package repository

import (
	"context"
	"fmt"

	"github.com/jackc/pgx/v5/pgxpool"
)

// DepartmentRepo handles database operations for departments.
type DepartmentRepo struct {
	pool *pgxpool.Pool
}

// NewDepartmentRepo creates a new DepartmentRepo.
func NewDepartmentRepo(pool *pgxpool.Pool) *DepartmentRepo {
	return &DepartmentRepo{pool: pool}
}

// Department represents a department.
type Department struct {
	ID            int    `json:"id" db:"id"`
	Name          string `json:"name" db:"name"`
	EmployeeCount int    `json:"employeeCount"`
}

// List returns all departments with employee count.
func (r *DepartmentRepo) List(ctx context.Context) ([]Department, error) {
	query := `
		SELECT d.id, d.name, COALESCE(e.count, 0) AS employee_count
		FROM departments d
		LEFT JOIN (
			SELECT department_id, COUNT(*) AS count
			FROM employees
			WHERE deleted_at IS NULL
			GROUP BY department_id
		) e ON d.id = e.department_id
		WHERE d.deleted_at IS NULL
		ORDER BY d.name
	`
	rows, err := r.pool.Query(ctx, query)
	if err != nil {
		return nil, fmt.Errorf("list departments: %w", err)
	}
	defer rows.Close()

	var depts []Department
	for rows.Next() {
		var d Department
		if err := rows.Scan(&d.ID, &d.Name, &d.EmployeeCount); err != nil {
			return nil, fmt.Errorf("scan department: %w", err)
		}
		depts = append(depts, d)
	}
	return depts, nil
}

// GetByID returns a department by ID.
func (r *DepartmentRepo) GetByID(ctx context.Context, id int) (*Department, error) {
	query := `
		SELECT d.id, d.name, COALESCE(e.count, 0) AS employee_count
		FROM departments d
		LEFT JOIN (
			SELECT department_id, COUNT(*) AS count
			FROM employees
			WHERE deleted_at IS NULL
			GROUP BY department_id
		) e ON d.id = e.department_id
		WHERE d.id = $1 AND d.deleted_at IS NULL
	`
	var d Department
	err := r.pool.QueryRow(ctx, query, id).Scan(&d.ID, &d.Name, &d.EmployeeCount)
	if err != nil {
		return nil, fmt.Errorf("get department: %w", err)
	}
	return &d, nil
}

// Create creates a new department.
func (r *DepartmentRepo) Create(ctx context.Context, name string) (*Department, error) {
	var id int
	err := r.pool.QueryRow(ctx, "INSERT INTO departments (name) VALUES ($1) RETURNING id", name).Scan(&id)
	if err != nil {
		return nil, fmt.Errorf("create department: %w", err)
	}
	return &Department{ID: id, Name: name, EmployeeCount: 0}, nil
}

// Update updates a department name.
func (r *DepartmentRepo) Update(ctx context.Context, id int, name string) (*Department, error) {
	_, err := r.pool.Exec(ctx, "UPDATE departments SET name = $1 WHERE id = $2", name, id)
	if err != nil {
		return nil, fmt.Errorf("update department: %w", err)
	}
	return r.GetByID(ctx, id)
}

// Delete removes a department by ID.
func (r *DepartmentRepo) Delete(ctx context.Context, id int) error {
	tag, err := r.pool.Exec(ctx, "UPDATE departments SET deleted_at = NOW() WHERE id = $1 AND deleted_at IS NULL", id)
	if err != nil {
		return fmt.Errorf("delete department: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("department not found")
	}
	return nil
}
