package services

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	goredis "github.com/alpha-ai-tracker/server/internal/redis"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

// EmployeeService handles business logic for employee operations.
type EmployeeService struct {
	repo        *repository.EmployeeRepo
	shiftRepo   *repository.ShiftRepo
	redisClient *goredis.Client
}

// NewEmployeeService creates a new EmployeeService. The shiftRepo is used
// only to look up the default "Day Shift" id when a create request omits
// shiftId; nil is tolerated (the create path then stores shift_id NULL).
func NewEmployeeService(repo *repository.EmployeeRepo, shiftRepo *repository.ShiftRepo, redisClient *goredis.Client) *EmployeeService {
	return &EmployeeService{repo: repo, shiftRepo: shiftRepo, redisClient: redisClient}
}

// List returns a paginated list of employees.
func (s *EmployeeService) List(ctx context.Context, params repository.EmployeeListParams) (*dto.EmployeeListResponse, error) {
	result, err := s.repo.List(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("list employees: %w", err)
	}

	emps := make([]dto.EmployeeResponse, len(result.Employees))
	for i, e := range result.Employees {
		emps[i] = employeeToResponse(&e)
	}

	return &dto.EmployeeListResponse{
		Data:       emps,
		Total:      result.Total,
		Page:       result.Page,
		PerPage:    result.PerPage,
		TotalPages: result.TotalPages,
	}, nil
}

// GetByID returns a single employee by ID.
func (s *EmployeeService) GetByID(ctx context.Context, id string) (*dto.EmployeeResponse, error) {
	emp, err := s.repo.GetByID(ctx, id)
	if err != nil {
		return nil, fmt.Errorf("get employee: %w", err)
	}
	if emp == nil {
		return nil, nil
	}
	resp := employeeToResponse(emp)
	return &resp, nil
}

// Create creates a new employee.
func (s *EmployeeService) Create(ctx context.Context, req *dto.CreateEmployeeRequest) (*dto.EmployeeResponse, error) {
	// Check for duplicate email
	if req.Email != "" {
		existing, err := s.repo.GetByEmail(ctx, req.Email)
		if err != nil {
			return nil, fmt.Errorf("check email uniqueness: %w", err)
		}
		if existing != nil {
			return nil, fmt.Errorf("email already exists: %s", req.Email)
		}
	}

	employeeID, err := s.repo.GenerateEmployeeID(ctx)
	if err != nil {
		return nil, fmt.Errorf("generate employee id: %w", err)
	}

	// Resolve the shift assignment. nil → "no shift" (renders empty in UI).
	// An explicit 0 (rare) is treated as nil.
	var shiftID *int
	if req.ShiftID != nil && *req.ShiftID > 0 {
		shiftID = req.ShiftID
	}

	deptID := req.DepartmentID
	if deptID == 0 {
		deptID = 1 // Default to Engineering (ID 1)
	}

	// Validate department_id exists
	// (deptID defaults to 1 which is Engineering — always seeded)

	emp := &models.Employee{
		EmployeeID:      employeeID,
		Name:            req.Name,
		Email:           req.Email,
		DepartmentID:    deptID,
		ShiftID:         shiftID,
		TrackingEnabled: true,
		TrackingStatus:  "untracked",
		IsOnline:        false,
	}

	created, err := s.repo.Create(ctx, emp)
	if err != nil {
		return nil, fmt.Errorf("create employee: %w", err)
	}

	resp := employeeToResponse(created)
	return &resp, nil
}

// Update partial updates an employee.
func (s *EmployeeService) Update(ctx context.Context, id string, req *dto.UpdateEmployeeRequest) (*dto.EmployeeResponse, error) {
	existing, err := s.repo.GetByID(ctx, id)
	if err != nil {
		return nil, err
	}
	if existing == nil {
		return nil, nil
	}

	updates := make(map[string]interface{})

	if req.Name != nil {
		updates["name"] = *req.Name
	}
	if req.Email != nil {
		updates["email"] = *req.Email
	}
	if req.DepartmentID != nil {
		updates["department_id"] = *req.DepartmentID
	}
	if req.ShiftID != nil {
		// Accept 0 / negative as "clear" (NULL); positive as the FK.
		if *req.ShiftID > 0 {
			updates["shift_id"] = *req.ShiftID
		} else {
			updates["shift_id"] = nil
		}
	}
	if req.TrackingEnabled != nil {
		updates["tracking_enabled"] = *req.TrackingEnabled
	}
	if req.TrackingStatus != nil {
		updates["tracking_status"] = *req.TrackingStatus
	}
	if req.IsOnline != nil {
		updates["is_online"] = *req.IsOnline
	}

	updated, err := s.repo.Update(ctx, id, updates)
	if err != nil {
		return nil, fmt.Errorf("update employee: %w", err)
	}
	if updated == nil {
		return nil, nil
	}

	resp := employeeToResponse(updated)
	return &resp, nil
}

// Delete removes an employee by ID.
func (s *EmployeeService) Delete(ctx context.Context, id string) error {
	emp, err := s.repo.GetByID(ctx, id)
	if err != nil {
		return err
	}
	if emp == nil {
		return fmt.Errorf("employee not found")
	}
	return s.repo.Delete(ctx, id)
}

// ImportEmployees bulk-imports Excel rows. Departments are get-or-created by
// name (a missing department is created first, then attached), and every row
// is upserted by its exact employee_id from the spreadsheet. Returns a
// per-row outcome report for the web UI.
func (s *EmployeeService) ImportEmployees(ctx context.Context, req *dto.ImportEmployeesRequest) (*dto.ImportEmployeesResponse, error) {
	if len(req.Employees) == 0 {
		return &dto.ImportEmployeesResponse{Results: []dto.ImportRowResult{}}, nil
	}

	items := make([]repository.ImportEmployeeItem, 0, len(req.Employees))
	results := make([]dto.ImportRowResult, 0, len(req.Employees))
	seen := make(map[string]int) // lowercased employee_id → file row index

	for i, row := range req.Employees {
		res := dto.ImportRowResult{
			RowIndex:   i + 1,
			EmployeeID: strings.TrimSpace(row.EmployeeID),
			Name:       strings.TrimSpace(row.Name),
		}

		reason := ""
		switch {
		case res.EmployeeID == "":
			reason = "missing employee id"
		case res.Name == "":
			reason = "missing name"
		case len(res.EmployeeID) > 20:
			reason = "employee id exceeds 20 characters"
		}
		if reason != "" {
			res.Status = "skipped"
			res.Reason = reason
			results = append(results, res)
			continue
		}

		key := strings.ToLower(res.EmployeeID)
		if prev, dup := seen[key]; dup {
			res.Status = "skipped"
			res.Reason = fmt.Sprintf("duplicate in file (row %d)", prev+1)
			results = append(results, res)
			continue
		}
		seen[key] = i
		items = append(items, repository.ImportEmployeeItem{
			EmployeeID: res.EmployeeID,
			Name:       res.Name,
			Email:      strings.TrimSpace(row.Email),
			Department: strings.TrimSpace(row.Department),
			Shift:      strings.TrimSpace(row.Shift),
		})
		res.Status = "pending"
		results = append(results, res)
	}

	outcomes, err := s.repo.Import(ctx, items)
	if err != nil {
		return nil, fmt.Errorf("import employees: %w", err)
	}

	outcomeIdx := 0
	for i := range results {
		if results[i].Status != "pending" {
			continue
		}
		out := outcomes[outcomeIdx]
		outcomeIdx++
		results[i].Status = out.Status
		results[i].Reason = out.Reason
	}

	var imported, updated, skipped int
	for _, r := range results {
		switch r.Status {
		case "imported":
			imported++
		case "updated":
			updated++
		case "skipped":
			skipped++
		}
	}

	return &dto.ImportEmployeesResponse{
		Imported: imported,
		Updated:  updated,
		Skipped:  skipped,
		Results:  results,
	}, nil
}

// ExportEmployees returns every non-deleted employee as a flat row for the
// Excel download.
func (s *EmployeeService) ExportEmployees(ctx context.Context) ([]dto.EmployeeExportRow, error) {
	emps, err := s.repo.ListAll(ctx)
	if err != nil {
		return nil, fmt.Errorf("list all employees: %w", err)
	}
	rows := make([]dto.EmployeeExportRow, len(emps))
	for i, e := range emps {
		rows[i] = dto.EmployeeExportRow{
			EmployeeID: e.EmployeeID,
			Name:       e.Name,
			Email:      e.Email,
			Department: e.Department,
			Shift:      e.Shift,
		}
	}
	return rows, nil
}

// GenerateSecret generates a one-time login secret for an employee and stores it in Redis.
func (s *EmployeeService) GenerateSecret(ctx context.Context, employeeID string) (*dto.GenerateSecretResponse, error) {
	// Verify employee exists
	emp, err := s.repo.GetByID(ctx, employeeID)
	if err != nil {
		return nil, fmt.Errorf("get employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}

	// Generate random 6-byte hex secret (12 chars)
	secretBytes := make([]byte, 6)
	if _, err := rand.Read(secretBytes); err != nil {
		return nil, fmt.Errorf("generate secret: %w", err)
	}
	secret := hex.EncodeToString(secretBytes)

	// Store in Redis with 5 min TTL
	if s.redisClient == nil {
		return nil, fmt.Errorf("Redis is not available — employee secrets require a running Redis server")
	}
	if err := s.redisClient.StoreSecret(ctx, emp.EmployeeID, secret); err != nil {
		return nil, fmt.Errorf("store secret in redis: %w", err)
	}

	return &dto.GenerateSecretResponse{
		Secret:    secret,
		ExpiresIn: int(goredis.SecretTTL.Seconds()),
	}, nil
}

func employeeToResponse(e *models.Employee) dto.EmployeeResponse {
	return dto.EmployeeResponse{
		ID:              e.ID,
		EmployeeID:      e.EmployeeID,
		Name:            e.Name,
		Email:           e.Email,
		Department:      e.Department,
		DepartmentID:    e.DepartmentID,
		ShiftID:         e.ShiftID,
		Shift:           e.Shift,
		TrackingEnabled: e.TrackingEnabled,
		TrackingStatus:  e.TrackingStatus,
		IsOnline:        e.IsOnline,
		ClientVersion:   e.ClientVersion,
		Avatar:          e.Avatar,
		AvatarColor:     e.AvatarColor,
		HasUserLogin:    e.HasUserLogin,
		CreatedAt:       e.CreatedAt,
		UpdatedAt:       e.UpdatedAt,
		DeletedAt:       e.DeletedAt,
	}
}
