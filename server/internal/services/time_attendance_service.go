package services

import (
	"context"
	"fmt"
	"math"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

type TimeAttendanceService struct {
	repo *repository.TimeAttendanceRepo
}

func NewTimeAttendanceService(repo *repository.TimeAttendanceRepo) *TimeAttendanceService {
	return &TimeAttendanceService{repo: repo}
}

func (s *TimeAttendanceService) GetSchedule(ctx context.Context, employeeID string) (*dto.ScheduleResponse, error) {
	schedule, err := s.repo.GetScheduleForEmployee(ctx, employeeID)
	if err != nil || schedule == nil {
		return nil, err
	}
	location, err := time.LoadLocation(schedule.Timezone)
	if err != nil {
		return nil, fmt.Errorf("invalid shift timezone %q: %w", schedule.Timezone, err)
	}
	now := time.Now().In(location)
	holidays, err := s.repo.ListHolidays(ctx, now.AddDate(0, -1, 0), now.AddDate(1, 0, 0))
	if err != nil {
		return nil, err
	}
	return &dto.ScheduleResponse{
		ID:            strconv.Itoa(schedule.ShiftID),
		Timezone:      schedule.Timezone,
		GraceMinutes:  schedule.GraceMinutes,
		WeeklyPattern: weeklyPattern(schedule),
		Holidays:      holidayResponses(holidays),
		ValidFrom:     schedule.ValidFrom.In(location).Format("2006-01-02"),
		ValidTo:       nil,
	}, nil
}

func (s *TimeAttendanceService) ListHolidays(ctx context.Context) ([]dto.HolidayResponse, error) {
	rows, err := s.repo.ListAllHolidays(ctx)
	if err != nil {
		return nil, err
	}
	return holidayResponses(rows), nil
}

func (s *TimeAttendanceService) CreateHoliday(ctx context.Context, input dto.HolidayInput) (*dto.HolidayResponse, error) {
	date, label, status, err := validateHoliday(input, true)
	if err != nil {
		return nil, err
	}
	row, err := s.repo.CreateHoliday(ctx, date, label, status)
	if err != nil {
		return nil, err
	}
	result := holidayResponse(*row)
	return &result, nil
}

func (s *TimeAttendanceService) UpdateHoliday(ctx context.Context, id int, input dto.HolidayInput) (*dto.HolidayResponse, error) {
	date, label, status, err := validateHoliday(input, false)
	if err != nil {
		return nil, err
	}
	row, err := s.repo.UpdateHoliday(ctx, id, date, label, status)
	if err != nil || row == nil {
		return nil, err
	}
	result := holidayResponse(*row)
	return &result, nil
}

func (s *TimeAttendanceService) DeleteHoliday(ctx context.Context, id int) (bool, error) {
	return s.repo.DeleteHoliday(ctx, id)
}

func (s *TimeAttendanceService) AttendanceToday(ctx context.Context, employeeID string) (dto.AttendanceResponse, error) {
	schedule, err := s.repo.GetScheduleForEmployee(ctx, employeeID)
	if err != nil {
		return dto.AttendanceResponse{}, err
	}
	timezone := "UTC"
	if schedule != nil {
		timezone = schedule.Timezone
	}
	location, err := time.LoadLocation(timezone)
	if err != nil {
		return dto.AttendanceResponse{}, fmt.Errorf("invalid schedule timezone: %w", err)
	}
	return s.attendanceForDay(ctx, employeeID, time.Now().In(location), location, schedule)
}

// AttendanceDayCounts buckets TODAY's attendance status across employeeIDs.
// "Today" is resolved per employee from their shift timezone — the same rule as
// AttendanceToday / GET /attendance/today — so the numbers line up with the
// /attendance page. An employee with no schedule falls back to UTC and lands in
// "unknown". Intended for the bounded, department-filtered dashboard summary; it
// runs one attendance computation per employee (the same work the /attendance
// page currently fans out over HTTP, done here in a single request).
func (s *TimeAttendanceService) AttendanceDayCounts(
	ctx context.Context, employeeIDs []string,
) (dto.DashboardAttendance, error) {
	out := dto.DashboardAttendance{
		Date:  time.Now().UTC().Format("2006-01-02"),
		Total: len(employeeIDs),
	}
	now := time.Now()
	for _, employeeID := range employeeIDs {
		schedule, err := s.repo.GetScheduleForEmployee(ctx, employeeID)
		if err != nil {
			return out, err
		}
		location := time.UTC
		if schedule != nil {
			if loaded, err := time.LoadLocation(schedule.Timezone); err == nil {
				location = loaded
			}
		}
		record, err := s.attendanceForDay(ctx, employeeID, now.In(location), location, schedule)
		if err != nil {
			return out, err
		}
		switch record.Status {
		case "present":
			out.Present++
		case "late":
			out.Late++
		case "absent":
			out.Absent++
		case "half_day":
			out.HalfDay++
		case "off_shift":
			out.OffShift++
		default:
			out.Unknown++
		}
	}
	return out, nil
}

func (s *TimeAttendanceService) AttendanceRange(
	ctx context.Context,
	employeeID, fromValue, toValue string,
	page, perPage int,
) (*dto.AttendanceRangeResponse, error) {
	schedule, err := s.repo.GetScheduleForEmployee(ctx, employeeID)
	if err != nil {
		return nil, err
	}
	timezone := "UTC"
	if schedule != nil {
		timezone = schedule.Timezone
	}
	location, err := time.LoadLocation(timezone)
	if err != nil {
		return nil, fmt.Errorf("invalid schedule timezone: %w", err)
	}
	from, to, err := parseAttendanceRange(fromValue, toValue, location)
	if err != nil {
		return nil, err
	}
	if to.Before(from) {
		return nil, fmt.Errorf("to must not be before from")
	}
	total := 0
	for day := from; !day.After(to); day = day.AddDate(0, 0, 1) {
		total++
	}
	if total > 366 {
		return nil, fmt.Errorf("attendance range cannot exceed 366 days")
	}
	if page < 1 {
		page = 1
	}
	if perPage < 1 || perPage > 100 {
		perPage = 31
	}
	startIndex := (page - 1) * perPage
	if startIndex > total {
		startIndex = total
	}
	endIndex := startIndex + perPage
	if endIndex > total {
		endIndex = total
	}

	days := make([]time.Time, 0, endIndex-startIndex)
	for i := startIndex; i < endIndex; i++ {
		days = append(days, to.AddDate(0, 0, -i))
	}
	result := make([]dto.AttendanceResponse, 0, len(days))
	for _, day := range days {
		row, err := s.attendanceForDay(ctx, employeeID, day, location, schedule)
		if err != nil {
			return nil, err
		}
		result = append(result, row)
	}
	totalPages := (total + perPage - 1) / perPage
	return &dto.AttendanceRangeResponse{
		Data: result, Total: total, Page: page, PerPage: perPage, TotalPages: totalPages,
	}, nil
}

func (s *TimeAttendanceService) DaySessionEvents(
	ctx context.Context, employeeID, dateValue string,
) (*dto.DaySessionEventsResponse, error) {
	schedule, err := s.repo.GetScheduleForEmployee(ctx, employeeID)
	if err != nil {
		return nil, err
	}
	timezone := "UTC"
	if schedule != nil {
		timezone = schedule.Timezone
	}
	location, err := time.LoadLocation(timezone)
	if err != nil {
		return nil, fmt.Errorf("invalid schedule timezone: %w", err)
	}
	day, err := time.ParseInLocation("2006-01-02", strings.TrimSpace(dateValue), location)
	if err != nil {
		return nil, fmt.Errorf("date must be YYYY-MM-DD")
	}
	dayStart := time.Date(day.Year(), day.Month(), day.Day(), 0, 0, 0, 0, location)
	dayEnd := dayStart.AddDate(0, 0, 1)
	events, err := s.repo.ListDaySessionEvents(ctx, employeeID, dayStart.UTC(), dayEnd.UTC())
	if err != nil {
		return nil, err
	}
	out := make([]dto.SessionEventResponse, 0, len(events))
	for _, e := range events {
		out = append(out, dto.SessionEventResponse{
			ID:         e.ID,
			EmployeeID: e.EmployeeID,
			EventType:  e.EventType,
			OsUsername: e.OsUsername,
			EventAt:    e.EventAt,
			Count:      e.EventCount,
			FirstAt:    e.FirstAt,
			LastAt:     e.LastAt,
			SyncedAt:   e.SyncedAt,
		})
	}
	return &dto.DaySessionEventsResponse{
		EmployeeID: employeeID,
		WorkDate:   dayStart.Format("2006-01-02"),
		Timezone:   timezone,
		Data:       out,
		Total:      len(out),
	}, nil
}

func parseAttendanceRange(fromValue, toValue string, location *time.Location) (time.Time, time.Time, error) {
	fromRaw := strings.TrimSpace(fromValue)
	toRaw := strings.TrimSpace(toValue)
	// Empty bounds ("all time" on the web) map to the last 366 inclusive days —
	// attendance is computed per calendar day and cannot span more than a year.
	if fromRaw == "" || toRaw == "" {
		now := time.Now().In(location)
		to := time.Date(now.Year(), now.Month(), now.Day(), 0, 0, 0, 0, location)
		from := to.AddDate(0, 0, -365)
		return from, to, nil
	}
	from, err := time.ParseInLocation("2006-01-02", fromRaw, location)
	if err != nil {
		return time.Time{}, time.Time{}, fmt.Errorf("from must be YYYY-MM-DD")
	}
	to, err := time.ParseInLocation("2006-01-02", toRaw, location)
	if err != nil {
		return time.Time{}, time.Time{}, fmt.Errorf("to must be YYYY-MM-DD")
	}
	return from, to, nil
}

func (s *TimeAttendanceService) attendanceForDay(
	ctx context.Context,
	employeeID string,
	day time.Time,
	location *time.Location,
	schedule *repository.ScheduleRecord,
) (dto.AttendanceResponse, error) {
	dayStart := time.Date(day.Year(), day.Month(), day.Day(), 0, 0, 0, 0, location)
	dayEnd := dayStart.AddDate(0, 0, 1)
	events, err := s.repo.ListSessionEvents(ctx, employeeID, dayStart.UTC(), dayEnd.UTC())
	if err != nil {
		return dto.AttendanceResponse{}, err
	}
	holidays, err := s.repo.ListHolidays(ctx, dayStart, dayStart)
	if err != nil {
		return dto.AttendanceResponse{}, err
	}

	var first *time.Time
	for _, event := range events {
		if event.EventAt.Before(dayStart.UTC()) || !isActiveMarker(event.EventType) {
			continue
		}
		at := event.FirstAt
		if first == nil || at.Before(*first) {
			copy := at
			first = &copy
		}
	}

	var last *time.Time
	if first != nil {
		for _, event := range events {
			at := event.LastAt
			if at.Before(*first) || !at.Before(dayEnd.UTC()) {
				continue
			}
			if last == nil || at.After(*last) {
				copy := at
				last = &copy
			}
		}
		// Alive signals keep Last Seen / Active moving while the employee works
		// without new lock/idle/power events. Heartbeat can stall after reboot
		// while DeviceAuth sync still refreshes employee_devices.last_seen_at —
		// take the freshest of both inside the work-day window.
		heartbeat, heartbeatErr := s.repo.GetLastHeartbeat(ctx, employeeID)
		if heartbeatErr != nil {
			return dto.AttendanceResponse{}, heartbeatErr
		}
		extendLastActive(&last, heartbeat, dayStart.UTC(), dayEnd.UTC())

		deviceSeen, deviceErr := s.repo.GetLastDeviceSeen(ctx, employeeID)
		if deviceErr != nil {
			return dto.AttendanceResponse{}, deviceErr
		}
		extendLastActive(&last, deviceSeen, dayStart.UTC(), dayEnd.UTC())
	}

	presence := 0.0
	idle := 0.0
	if first != nil && last != nil && last.After(*first) {
		presence = last.Sub(*first).Seconds()
		idle = inactiveSeconds(events, *first, *last)
		if idle > presence {
			idle = presence
		}
	}
	active := math.Max(0, presence-idle)
	status := "unknown"
	lateMinutes := 0
	offShift := 0.0

	if schedule != nil {
		shiftValue, scheduled := weeklyPattern(schedule)[weekdayKey(dayStart.Weekday())]
		if len(holidays) > 0 || !scheduled {
			status = "off_shift"
			offShift = presence
		} else {
			parts := strings.SplitN(shiftValue, "-", 2)
			shiftStart, _ := time.ParseInLocation("15:04", parts[0], location)
			shiftEnd, _ := time.ParseInLocation("15:04", parts[1], location)
			shiftStart = time.Date(day.Year(), day.Month(), day.Day(), shiftStart.Hour(), shiftStart.Minute(), 0, 0, location)
			shiftEnd = time.Date(day.Year(), day.Month(), day.Day(), shiftEnd.Hour(), shiftEnd.Minute(), 0, 0, location)
			if !shiftEnd.After(shiftStart) {
				shiftEnd = shiftEnd.AddDate(0, 0, 1)
			}
			if first == nil {
				if time.Now().In(location).Before(shiftEnd) {
					status = "unknown"
				} else {
					status = "absent"
				}
				return dto.AttendanceResponse{
					EmployeeID: employeeID, WorkDate: dayStart.Format("2006-01-02"),
					Timezone: schedule.Timezone,
					Status:   status,
				}, nil
			}
			firstLocal := first.In(location)
			lastLocal := firstLocal
			if last != nil {
				lastLocal = last.In(location)
			}
			overlapStart := maxTime(firstLocal, shiftStart)
			overlapEnd := minTime(lastLocal, shiftEnd)
			inShift := math.Max(0, overlapEnd.Sub(overlapStart).Seconds())
			offShift = math.Max(0, presence-inShift)
			if !time.Now().In(location).Before(shiftEnd) && inShift < shiftEnd.Sub(shiftStart).Seconds()/2 {
				status = "half_day"
			} else if firstLocal.After(shiftStart.Add(time.Duration(schedule.GraceMinutes) * time.Minute)) {
				status = "late"
				lateMinutes = int(firstLocal.Sub(shiftStart.Add(time.Duration(schedule.GraceMinutes) * time.Minute)).Minutes())
			} else {
				status = "present"
			}
		}
	}

	return dto.AttendanceResponse{
		EmployeeID: employeeID, WorkDate: dayStart.Format("2006-01-02"),
		Timezone: scheduleTimezone(schedule),
		FirstActiveAt: first, LastActiveAt: last,
		ActiveSeconds: int(math.Round(active)), IdleSeconds: int(math.Round(idle)),
		OffShiftSeconds: int(math.Round(offShift)), Status: status, LateMinutes: lateMinutes,
	}, nil
}

func weeklyPattern(schedule *repository.ScheduleRecord) map[string]string {
	result := make(map[string]string)
	if schedule == nil {
		return result
	}
	value := shortTime(schedule.StartTime) + "-" + shortTime(schedule.EndTime)
	for _, raw := range strings.Split(schedule.WorkingDays, ",") {
		key := strings.ToLower(strings.TrimSpace(raw))
		if len(key) >= 3 {
			result[key[:3]] = value
		}
	}
	return result
}

func shortTime(value string) string {
	if len(value) >= 5 {
		return value[:5]
	}
	return value
}

func weekdayKey(day time.Weekday) string {
	return strings.ToLower(day.String()[:3])
}

func scheduleTimezone(schedule *repository.ScheduleRecord) string {
	if schedule == nil {
		return ""
	}
	return schedule.Timezone
}

func isActiveMarker(eventType string) bool {
	switch eventType {
	case "power_on", "resume", "tracker_login", "screen_unlock", "idle_end":
		return true
	default:
		return false
	}
}

// extendLastActive bumps last when candidate falls inside [dayStart, dayEnd) and
// is strictly after the current last (or last is unset).
func extendLastActive(last **time.Time, candidate *time.Time, dayStart, dayEnd time.Time) {
	if candidate == nil {
		return
	}
	at := candidate.UTC()
	if at.Before(dayStart) || !at.Before(dayEnd) {
		return
	}
	if *last == nil || at.After(**last) {
		copy := at
		*last = &copy
	}
}

func inactiveSeconds(events []models.SessionEvent, start, end time.Time) float64 {
	sort.Slice(events, func(i, j int) bool { return events[i].EventAt.Before(events[j].EventAt) })
	idle, locked := false, false
	var inactiveStart *time.Time
	total := 0.0
	for _, event := range events {
		if event.EventAt.After(end) {
			break
		}
		wasInactive := idle || locked
		switch event.EventType {
		case "idle_start":
			idle = true
		case "idle_end":
			idle = false
		case "screen_lock":
			locked = true
		case "screen_unlock":
			locked = false
		default:
			continue
		}
		isInactive := idle || locked
		if !wasInactive && isInactive {
			at := maxTime(event.EventAt, start)
			inactiveStart = &at
		} else if wasInactive && !isInactive && inactiveStart != nil {
			until := minTime(event.EventAt, end)
			if until.After(*inactiveStart) {
				total += until.Sub(*inactiveStart).Seconds()
			}
			inactiveStart = nil
		}
	}
	if (idle || locked) && inactiveStart != nil && end.After(*inactiveStart) {
		total += end.Sub(*inactiveStart).Seconds()
	}
	return math.Max(0, total)
}

func validateHoliday(input dto.HolidayInput, isCreate bool) (time.Time, string, string, error) {
	date, err := time.Parse("2006-01-02", input.Date)
	if err != nil {
		return time.Time{}, "", "", fmt.Errorf("date must be YYYY-MM-DD")
	}
	label := strings.TrimSpace(input.Label)
	if label == "" {
		return time.Time{}, "", "", fmt.Errorf("holiday label is required")
	}
	status := strings.ToLower(strings.TrimSpace(input.Status))
	if status == "" {
		if isCreate {
			status = "pending"
		} else {
			return time.Time{}, "", "", fmt.Errorf("status is required")
		}
	}
	switch status {
	case "pending", "approved", "rejected":
	default:
		return time.Time{}, "", "", fmt.Errorf("status must be pending, approved, or rejected")
	}
	return date, label, status, nil
}

func holidayResponses(rows []repository.HolidayRecord) []dto.HolidayResponse {
	result := make([]dto.HolidayResponse, 0, len(rows))
	for _, row := range rows {
		result = append(result, holidayResponse(row))
	}
	return result
}

func holidayResponse(row repository.HolidayRecord) dto.HolidayResponse {
	status := row.Status
	if status == "" {
		status = "approved"
	}
	return dto.HolidayResponse{
		ID: row.ID, Date: row.Date.Format("2006-01-02"), Label: row.Label, Status: status,
	}
}

func maxTime(a, b time.Time) time.Time {
	if a.After(b) {
		return a
	}
	return b
}

func minTime(a, b time.Time) time.Time {
	if a.Before(b) {
		return a
	}
	return b
}
