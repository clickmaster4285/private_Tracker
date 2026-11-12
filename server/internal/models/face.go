package models

import "time"

// FaceEnrollmentJob tracks async server-side embedding generation.
type FaceEnrollmentJob struct {
	ID               string     `json:"id"`
	EmployeeID       string     `json:"employeeId"`
	ProfileID        *string    `json:"profileId,omitempty"`
	Status           string     `json:"status"`
	AttemptCount     int        `json:"attemptCount"`
	LockedBy         string     `json:"lockedBy,omitempty"`
	LockedAt         *time.Time `json:"lockedAt,omitempty"`
	ErrorCode        string     `json:"errorCode,omitempty"`
	ErrorMessage     string     `json:"errorMessage,omitempty"`
	CreatedBy        string     `json:"createdBy,omitempty"`
	CreatedAt        time.Time  `json:"createdAt"`
	FinishedAt       *time.Time `json:"finishedAt,omitempty"`
	SampleCount      int        `json:"sampleCount,omitempty"`
	ProcessedSamples int        `json:"processedSamples,omitempty"`
	RejectedSamples  int        `json:"rejectedSamples,omitempty"`
}

// FaceEnrollmentSample indexes one original enrollment image on disk.
type FaceEnrollmentSample struct {
	ID             string    `json:"id"`
	JobID          string    `json:"jobId"`
	EmployeeID     string    `json:"employeeId"`
	PoseID         string    `json:"poseId"`
	RelPath        string    `json:"relPath"`
	ContentType    string    `json:"contentType"`
	ByteSize       int64     `json:"byteSize"`
	Width          int       `json:"width"`
	Height         int       `json:"height"`
	QualityScore   float64   `json:"qualityScore"`
	ClientSampleID string    `json:"clientSampleId,omitempty"`
	ProcessStatus  string    `json:"processStatus,omitempty"`
	RejectReason   string    `json:"rejectReason,omitempty"`
	FaceCount      int       `json:"faceCount,omitempty"`
	CreatedAt      time.Time `json:"createdAt"`
	// ImageURL is filled by the API for admin gallery/dialog (not stored).
	ImageURL string `json:"imageUrl,omitempty"`
}

// EmployeeFaceProfile is the authoritative biometric profile header.
type EmployeeFaceProfile struct {
	ID                 string     `json:"id"`
	EmployeeID         string     `json:"employeeId"`
	Status             string     `json:"status"`
	ProfileVersion     int        `json:"profileVersion"`
	EnrollmentVersion  int        `json:"enrollmentVersion"`
	ModelID            string     `json:"modelId"`
	ModelVersion       string     `json:"modelVersion"`
	PreprocessVersion  string     `json:"preprocessVersion"`
	EmbeddingDims      int        `json:"embeddingDims"`
	ActivatedAt        *time.Time `json:"activatedAt,omitempty"`
	RevokedAt          *time.Time `json:"revokedAt,omitempty"`
	LastError          string     `json:"lastError,omitempty"`
	CreatedBy          string     `json:"createdBy,omitempty"`
	UpdatedBy          string     `json:"updatedBy,omitempty"`
	CreatedAt          time.Time  `json:"createdAt"`
	UpdatedAt          time.Time  `json:"updatedAt"`
	SampleCount        int        `json:"sampleCount,omitempty"`
	EmbeddingCount     int        `json:"embeddingCount,omitempty"`
}

// FaceModelVersion describes a distributable ONNX (or related) package.
type FaceModelVersion struct {
	ID                 string    `json:"id"`
	ModelID            string    `json:"modelId"`
	ModelVersion       string    `json:"modelVersion"`
	Role               string    `json:"role"`
	EmbeddingDims      *int      `json:"embeddingDims,omitempty"`
	PreprocessVersion  string    `json:"preprocessVersion"`
	Normalization      string    `json:"normalization"`
	PackageSHA256      string    `json:"packageSha256"`
	Signature          string    `json:"signature"`
	PackageRelPath     string    `json:"packageRelPath"`
	CreatedAt          time.Time `json:"createdAt"`
}
