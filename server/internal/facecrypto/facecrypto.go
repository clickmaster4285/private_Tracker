// Package facecrypto encrypts face embedding vectors at rest (AES-256-GCM).
package facecrypto

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/sha256"
	"encoding/binary"
	"fmt"
	"io"
	"math"
)

// KeyFromSecret derives a 32-byte AES key from any non-empty secret string.
func KeyFromSecret(secret string) ([]byte, error) {
	if secret == "" {
		return nil, fmt.Errorf("embedding encryption secret is empty")
	}
	sum := sha256.Sum256([]byte(secret))
	return sum[:], nil
}

// Float32ToBytes packs little-endian float32 values.
func Float32ToBytes(v []float32) []byte {
	out := make([]byte, 4*len(v))
	for i, f := range v {
		binary.LittleEndian.PutUint32(out[i*4:], math.Float32bits(f))
	}
	return out
}

// BytesToFloat32 unpacks little-endian float32 values.
func BytesToFloat32(b []byte) ([]float32, error) {
	if len(b)%4 != 0 {
		return nil, fmt.Errorf("embedding plaintext length %d not multiple of 4", len(b))
	}
	n := len(b) / 4
	out := make([]float32, n)
	for i := 0; i < n; i++ {
		out[i] = math.Float32frombits(binary.LittleEndian.Uint32(b[i*4:]))
	}
	return out, nil
}

// Encrypt seals plaintext under AES-GCM and returns (ciphertext, nonce).
func Encrypt(key, plaintext []byte) (ciphertext, nonce []byte, err error) {
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, nil, err
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, nil, err
	}
	nonce = make([]byte, gcm.NonceSize())
	if _, err := io.ReadFull(rand.Reader, nonce); err != nil {
		return nil, nil, err
	}
	ciphertext = gcm.Seal(nil, nonce, plaintext, nil)
	return ciphertext, nonce, nil
}

// Decrypt opens AES-GCM ciphertext with the given nonce.
func Decrypt(key, ciphertext, nonce []byte) ([]byte, error) {
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}
	return gcm.Open(nil, nonce, ciphertext, nil)
}

// EncryptVector encrypts a float32 embedding.
func EncryptVector(key []byte, vector []float32) (ciphertext, nonce []byte, err error) {
	return Encrypt(key, Float32ToBytes(vector))
}

// DecryptVector decrypts to float32 embedding.
func DecryptVector(key, ciphertext, nonce []byte) ([]float32, error) {
	plain, err := Decrypt(key, ciphertext, nonce)
	if err != nil {
		return nil, err
	}
	return BytesToFloat32(plain)
}
