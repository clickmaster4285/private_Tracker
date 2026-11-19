package services

import (
	"encoding/base64"
	"strings"
	"testing"
)

func TestDecodeAppIcon(t *testing.T) {
	png := append(append([]byte{}, pngSignature...), []byte("rest-of-png")...)
	if got := decodeAppIcon(base64.StdEncoding.EncodeToString(png)); len(got) != len(png) {
		t.Fatalf("valid png rejected")
	}
	if decodeAppIcon(base64.StdEncoding.EncodeToString([]byte("<svg onload=x>"))) != nil {
		t.Fatal("non-PNG accepted")
	}
	if decodeAppIcon("not base64!!") != nil || decodeAppIcon("") != nil {
		t.Fatal("garbage accepted")
	}
	big := append(append([]byte{}, pngSignature...), []byte(strings.Repeat("x", maxAppIconBytes))...)
	if decodeAppIcon(base64.StdEncoding.EncodeToString(big)) != nil {
		t.Fatal("oversized icon accepted")
	}
}
