package queue

// Task type names for Asynq (low-priority maintenance).
const (
	TaskSessionLifecycle = "jobs:session_lifecycle"
	TaskRetention        = "jobs:retention"
	TaskStaleness        = "jobs:staleness"

	QueueLow = "low"
)
