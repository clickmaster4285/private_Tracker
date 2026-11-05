package services

import (
	"context"
	"fmt"

	"github.com/alpha-ai-tracker/server/internal/repository"
)

// DepartmentService handles business logic for departments.
type DepartmentService struct {
	repo         *repository.DepartmentRepo
	employeeRepo *repository.EmployeeRepo
}

// NewDepartmentService creates a new DepartmentService.
func NewDepartmentService(repo *repository.DepartmentRepo, employeeRepo *repository.EmployeeRepo) *DepartmentService {
	return &DepartmentService{repo: repo, employeeRepo: employeeRepo}
}

// DepartmentInfo is the public department info.
type DepartmentInfo struct {
	ID            int    `json:"id"`
	Name          string `json:"name"`
	EmployeeCount int    `json:"employeeCount"`
}

// List returns all departments.
func (s *DepartmentService) List(ctx context.Context) (*DepartmentListResponse, error) {
	depts, err := s.repo.List(ctx)
	if err != nil {
		return nil, fmt.Errorf("list departments: %w", err)
	}

	info := make([]DepartmentInfo, len(depts))
	for i, d := range depts {
		info[i] = DepartmentInfo{
			ID:            d.ID,
			Name:          d.Name,
			EmployeeCount: d.EmployeeCount,
		}
	}

	return &DepartmentListResponse{
		Departments: info,
		Total:       len(info),
	}, nil
}

// Create creates a new department.
func (s *DepartmentService) Create(ctx context.Context, name string) (*DepartmentInfo, error) {
	if name == "" {
		return nil, fmt.Errorf("department name is required")
	}

	dept, err := s.repo.Create(ctx, name)
	if err != nil {
		return nil, fmt.Errorf("create department: %w", err)
	}

	return &DepartmentInfo{
		ID:            dept.ID,
		Name:          dept.Name,
		EmployeeCount: 0,
	}, nil
}

// Update updates a department name.
func (s *DepartmentService) Update(ctx context.Context, id int, name string) (*DepartmentInfo, error) {
	if name == "" {
		return nil, fmt.Errorf("department name is required")
	}

	dept, err := s.repo.Update(ctx, id, name)
	if err != nil {
		return nil, fmt.Errorf("update department: %w", err)
	}

	return &DepartmentInfo{
		ID:            dept.ID,
		Name:          dept.Name,
		EmployeeCount: dept.EmployeeCount,
	}, nil
}

// Delete removes a department.
func (s *DepartmentService) Delete(ctx context.Context, id int) error {
	return s.repo.Delete(ctx, id)
}

// DepartmentListResponse is the response for listing departments.
type DepartmentListResponse struct {
	Departments []DepartmentInfo `json:"departments"`
	Total       int              `json:"total"`
}
