package scale

import (
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"os"
)

// ResolveInstanceID returns INSTANCE_ID from config, or generates a stable-enough
// process id: hostname-pid-random6.
func ResolveInstanceID(configured string) string {
	if configured != "" {
		return configured
	}
	host, _ := os.Hostname()
	if host == "" {
		host = "node"
	}
	var b [3]byte
	_, _ = rand.Read(b[:])
	return fmt.Sprintf("%s-%d-%s", host, os.Getpid(), hex.EncodeToString(b[:]))
}
