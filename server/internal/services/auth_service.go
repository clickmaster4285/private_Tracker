package services

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"log"
	"time"

	"github.com/alpha-ai-tracker/server/internal/config"
	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/golang-jwt/jwt/v5"
	"golang.org/x/crypto/bcrypt"
)

// AuthService handles authentication and company admin initialization.
type AuthService struct {
	repo        *repository.UserRepo
	rbacRepo    *repository.RBACRepo
	refreshRepo *repository.RefreshTokenRepo
	jwtConfig   config.JWTConfig
	adminCfg    config.AdminConfig
}

// NewAuthService creates a new AuthService.
func NewAuthService(repo *repository.UserRepo, rbacRepo *repository.RBACRepo, refreshRepo *repository.RefreshTokenRepo, jwtCfg config.JWTConfig, adminCfg config.AdminConfig) *AuthService {
	return &AuthService{
		repo:        repo,
		rbacRepo:    rbacRepo,
		refreshRepo: refreshRepo,
		jwtConfig:   jwtCfg,
		adminCfg:    adminCfg,
	}
}

// Claims represents JWT claims.
type Claims struct {
	UserID string `json:"userId"`
	jwt.RegisteredClaims
}

// deriveKey derives a 32-byte AES key from the JWT secret using SHA-256.
func deriveKey(secret string) []byte {
	hash := sha256.Sum256([]byte(secret))
	return hash[:]
}

// encryptToken encrypts a signed JWT string using AES-GCM.
func encryptToken(signedToken string, secret string) (string, error) {
	key := deriveKey(secret)

	block, err := aes.NewCipher(key)
	if err != nil {
		return "", fmt.Errorf("create cipher: %w", err)
	}

	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return "", fmt.Errorf("create GCM: %w", err)
	}

	nonce := make([]byte, gcm.NonceSize())
	if _, err := io.ReadFull(rand.Reader, nonce); err != nil {
		return "", fmt.Errorf("generate nonce: %w", err)
	}

	ciphertext := gcm.Seal(nonce, nonce, []byte(signedToken), nil)
	return base64.URLEncoding.EncodeToString(ciphertext), nil
}

// decryptToken decrypts an AES-GCM encrypted token back to a signed JWT string.
func decryptToken(encrypted string, secret string) (string, error) {
	key := deriveKey(secret)

	data, err := base64.URLEncoding.DecodeString(encrypted)
	if err != nil {
		return "", fmt.Errorf("base64 decode: %w", err)
	}

	block, err := aes.NewCipher(key)
	if err != nil {
		return "", fmt.Errorf("create cipher: %w", err)
	}

	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return "", fmt.Errorf("create GCM: %w", err)
	}

	nonceSize := gcm.NonceSize()
	if len(data) < nonceSize {
		return "", fmt.Errorf("ciphertext too short")
	}

	nonce, ciphertext := data[:nonceSize], data[nonceSize:]
	plaintext, err := gcm.Open(nil, nonce, ciphertext, nil)
	if err != nil {
		return "", fmt.Errorf("decrypt: %w", err)
	}

	return string(plaintext), nil
}

// EnsureCompanyAdmin checks if a user on the system role exists, and if not, creates one.
func (s *AuthService) EnsureCompanyAdmin(ctx context.Context) error {
	count, err := s.repo.CountUsersWithRole(ctx, SystemRoleName)
	if err != nil {
		return fmt.Errorf("check company admins: %w", err)
	}

	if count > 0 {
		log.Printf("[auth] company admin exists, skipping initialization")
		return nil
	}

	log.Printf("[auth] no company admin found — auto-initializing with credentials from .env")

	role, err := s.rbacRepo.GetRoleByName(ctx, SystemRoleName)
	if err != nil {
		return fmt.Errorf("find system role: %w", err)
	}
	if role == nil {
		return fmt.Errorf("system role %q is missing — run RBAC seed first", SystemRoleName)
	}

	hashedPassword, err := bcrypt.GenerateFromPassword([]byte(s.adminCfg.Password), bcrypt.DefaultCost)
	if err != nil {
		return fmt.Errorf("hash admin password: %w", err)
	}

	admin := &models.User{
		Name:            s.adminCfg.Name,
		Email:           s.adminCfg.Email,
		PasswordHash:    string(hashedPassword),
		RoleID:          role.ID,
		Shift:           "Day",
		TrackingEnabled: false,
		TrackingStatus:  "untracked",
		IsOnline:        false,
	}

	created, err := s.repo.Create(ctx, admin)
	if err != nil {
		return fmt.Errorf("create company admin: %w", err)
	}

	log.Printf("[auth] company admin created: email=%s employeeId=%s", created.Email, created.EmployeeID)
	return nil
}

// ErrAccountSuspended is returned by Login and RefreshSession when the account
// status is not 'active'. Handlers map it to HTTP 403 so the web can explain
// the refusal (as opposed to a 401 for bad credentials).
var ErrAccountSuspended = errors.New("Account suspended. Contact your administrator.")

// Login authenticates a user and returns JWT token + user info.
func (s *AuthService) Login(ctx context.Context, req *dto.LoginRequest) (*dto.LoginResponse, error) {
	user, err := s.repo.GetByEmail(ctx, req.Email)
	if err != nil {
		return nil, fmt.Errorf("find user: %w", err)
	}
	if user == nil {
		return nil, fmt.Errorf("invalid email or password")
	}

	if err := bcrypt.CompareHashAndPassword([]byte(user.PasswordHash), []byte(req.Password)); err != nil {
		return nil, fmt.Errorf("invalid email or password")
	}

	// Account status is checked AFTER the password so the API never reveals to
	// an unauthenticated caller whether an account exists / is suspended.
	if user.Status == "suspended" {
		return nil, ErrAccountSuspended
	}

	token, err := s.generateToken(user)
	if err != nil {
		return nil, fmt.Errorf("generate token: %w", err)
	}

	resp := &dto.LoginResponse{
		User:  userToResponse(user),
		Token: token,
	}
	s.attachPermissions(ctx, user.ID, &resp.User)

	refreshRaw, refreshExpiresAt, err := s.issueRefreshToken(ctx, user.ID)
	if err != nil {
		return nil, fmt.Errorf("issue refresh token: %w", err)
	} else {
		resp.RefreshToken = refreshRaw
		resp.RefreshExpiresAt = refreshExpiresAt
	}

	return resp, nil
}

// issueRefreshToken mints a fresh opaque refresh token, persists its SHA-256 hash
// and returns the raw value (for the cookie) with its expiry.
func (s *AuthService) issueRefreshToken(ctx context.Context, userID string) (string, time.Time, error) {
	if s.refreshRepo == nil {
		return "", time.Time{}, fmt.Errorf("refresh token repository unavailable")
	}

	rawBytes := make([]byte, 32)
	if _, err := rand.Read(rawBytes); err != nil {
		return "", time.Time{}, fmt.Errorf("generate refresh token: %w", err)
	}
	raw := base64.URLEncoding.EncodeToString(rawBytes)
	hash := hashRefreshToken(raw)

	expiresAt := time.Now().Add(s.jwtConfig.RefreshExpiry)
	if err := s.refreshRepo.Create(ctx, userID, hash, expiresAt); err != nil {
		return "", time.Time{}, err
	}
	return raw, expiresAt, nil
}

// RefreshSession validates a presented refresh token, ROTATES it (old row revoked,
// a new one issued) and returns a fresh access JWT + replacement refresh token.
func (s *AuthService) RefreshSession(ctx context.Context, rawRefreshToken string) (*dto.LoginResponse, error) {
	if s.refreshRepo == nil {
		return nil, fmt.Errorf("refresh token repository unavailable")
	}
	if rawRefreshToken == "" {
		return nil, fmt.Errorf("refresh token missing")
	}

	stored, err := s.refreshRepo.GetValidByHash(ctx, hashRefreshToken(rawRefreshToken))
	if err != nil {
		return nil, fmt.Errorf("look up refresh token: %w", err)
	}
	if stored == nil {
		return nil, fmt.Errorf("invalid or expired refresh token")
	}

	user, err := s.repo.GetByID(ctx, stored.UserID)
	if err != nil {
		return nil, fmt.Errorf("find user: %w", err)
	}
	if user == nil || user.DeletedAt != nil {
		return nil, fmt.Errorf("user no longer exists")
	}
	// A suspended account must not keep minting access tokens from a 30-day
	// refresh cookie.
	if user.Status == "suspended" {
		return nil, ErrAccountSuspended
	}

	access, err := s.generateToken(user)
	if err != nil {
		return nil, fmt.Errorf("generate token: %w", err)
	}

	newRefreshRaw, refreshExpiresAt, err := s.issueRefreshToken(ctx, user.ID)
	if err != nil {
		return nil, fmt.Errorf("rotate refresh token: %w", err)
	}

	// Revoke only after the replacement exists — a crash between the two writes
	// leaves the old row valid rather than locking the user out.
	if _, err := s.refreshRepo.RevokeByHash(ctx, hashRefreshToken(rawRefreshToken)); err != nil {
		log.Printf("[auth] WARNING: could not revoke rotated refresh token for %s: %v", user.ID, err)
	}

	resp := &dto.LoginResponse{
		User:             userToResponse(user),
		Token:            access,
		RefreshToken:     newRefreshRaw,
		RefreshExpiresAt: refreshExpiresAt,
	}
	s.attachPermissions(ctx, user.ID, &resp.User)

	return resp, nil
}

// RevokeRefreshToken revokes the presented refresh token (logout).
func (s *AuthService) RevokeRefreshToken(ctx context.Context, rawRefreshToken string) error {
	if s.refreshRepo == nil || rawRefreshToken == "" {
		return nil
	}
	_, err := s.refreshRepo.RevokeByHash(ctx, hashRefreshToken(rawRefreshToken))
	return err
}

// hashRefreshToken derives the at-rest identifier for a raw refresh token.
func hashRefreshToken(raw string) string {
	sum := sha256.Sum256([]byte(raw))
	return hex.EncodeToString(sum[:])
}

// attachPermissions resolves the granted submodule keys for the user's role and
// embeds them into the response so the web client can guard navigation.
func (s *AuthService) attachPermissions(ctx context.Context, userID string, out *dto.UserResponse) {
	keys, err := s.rbacRepo.PermissionKeysForUser(ctx, userID)
	if err != nil {
		log.Printf("[auth] WARNING: could not resolve permissions for %s: %v", userID, err)
		return
	}
	out.Permissions = keys
}

// ValidateToken decrypts and validates a JWT token, returning the claims.
func (s *AuthService) ValidateToken(tokenString string) (*Claims, error) {
	signedToken, err := decryptToken(tokenString, s.jwtConfig.Secret)
	if err != nil {
		return nil, fmt.Errorf("decrypt token: %w", err)
	}

	token, err := jwt.ParseWithClaims(signedToken, &Claims{}, func(token *jwt.Token) (interface{}, error) {
		if _, ok := token.Method.(*jwt.SigningMethodHMAC); !ok {
			return nil, fmt.Errorf("unexpected signing method: %v", token.Header["alg"])
		}
		return []byte(s.jwtConfig.Secret), nil
	})
	if err != nil {
		return nil, fmt.Errorf("parse token: %w", err)
	}

	claims, ok := token.Claims.(*Claims)
	if !ok || !token.Valid {
		return nil, fmt.Errorf("invalid token")
	}

	return claims, nil
}

// GetUserByID returns a full user model (for middleware use).
func (s *AuthService) GetUserByID(ctx context.Context, id string) (*models.User, error) {
	return s.repo.GetByID(ctx, id)
}

// GetUserResponseByID returns the user response with role permissions attached.
func (s *AuthService) GetUserResponseByID(ctx context.Context, id string) (*dto.UserResponse, error) {
	user, err := s.repo.GetByID(ctx, id)
	if err != nil {
		return nil, err
	}
	if user == nil {
		return nil, nil
	}
	resp := userToResponse(user)
	s.attachPermissions(ctx, user.ID, &resp)
	return &resp, nil
}

// GenerateEmployeeToken generates a JWT token for an employee desktop client session.
// Deliberately long-lived: employees send it in sync request bodies and have no
// refresh mechanism — the 15-minute web-admin TTL must never apply here.
func (s *AuthService) GenerateEmployeeToken(emp *models.Employee) (string, error) {
	now := time.Now()
	claims := &Claims{
		// DeviceAuth consumers query telemetry by the public EMP-XXXXX key.
		// Using the employees UUID here made the legacy Bearer fallback
		// authenticate successfully but return no employee/schedule data.
		UserID: emp.EmployeeID,
		RegisteredClaims: jwt.RegisteredClaims{
			ExpiresAt: jwt.NewNumericDate(now.Add(s.jwtConfig.EmployeeAccessExpiry)),
			IssuedAt:  jwt.NewNumericDate(now),
			NotBefore: jwt.NewNumericDate(now),
			Issuer:    "alpha-ai-tracker-employee",
			Subject:   emp.EmployeeID,
		},
	}

	token := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	signedToken, err := token.SignedString([]byte(s.jwtConfig.Secret))
	if err != nil {
		return "", fmt.Errorf("sign token: %w", err)
	}

	return encryptToken(signedToken, s.jwtConfig.Secret)
}

func (s *AuthService) generateToken(user *models.User) (string, error) {
	now := time.Now()
	claims := &Claims{
		UserID: user.ID,
		RegisteredClaims: jwt.RegisteredClaims{
			ExpiresAt: jwt.NewNumericDate(now.Add(s.jwtConfig.AccessExpiry)),
			IssuedAt:  jwt.NewNumericDate(now),
			NotBefore: jwt.NewNumericDate(now),
			Issuer:    "alpha-ai-tracker",
			Subject:   user.ID,
		},
	}

	token := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	signedToken, err := token.SignedString([]byte(s.jwtConfig.Secret))
	if err != nil {
		return "", fmt.Errorf("sign token: %w", err)
	}

	return encryptToken(signedToken, s.jwtConfig.Secret)
}
