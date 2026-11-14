package facecrypto

import (
	"math"
	"testing"
)

func TestEncryptDecryptRoundTrip(t *testing.T) {
	key, err := KeyFromSecret("lab-face-embedding-key")
	if err != nil {
		t.Fatal(err)
	}
	in := make([]float32, 512)
	for i := range in {
		in[i] = float32(math.Sin(float64(i) * 0.01))
	}
	ct, nonce, err := EncryptVector(key, in)
	if err != nil {
		t.Fatal(err)
	}
	if len(ct) == 0 || len(nonce) == 0 {
		t.Fatalf("empty ciphertext/nonce")
	}
	out, err := DecryptVector(key, ct, nonce)
	if err != nil {
		t.Fatal(err)
	}
	if len(out) != len(in) {
		t.Fatalf("len %d != %d", len(out), len(in))
	}
	for i := range in {
		if math.Abs(float64(out[i]-in[i])) > 1e-6 {
			t.Fatalf("mismatch at %d: %v vs %v", i, out[i], in[i])
		}
	}
}
