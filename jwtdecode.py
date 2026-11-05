import base64, json, hashlib
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

SECRET = "change-this-to-a-very-long-random-secret-key"
TOKEN = "s75nezKCQ48j_Llw_KtBRpsn4nFRcW0IfOqIkOiRia8QIJdjRha37Aa8G8DHIJWVviEuneg_mGoCaXABBXW7Y7wLgz2mKFDBn-uT7ga0gIAZQ8cprsmp_n8AnQHE689n3Lgz66nrAkc65PMt8dQifdLVpLaEqgeoCtH4VrmgmXq2Zq54Xyh3FmzgbjPdZpDfrk2Q_ijHCTpNBHgugFVCdLfojn3K7XvUu0k0C4XYsabZOlbf1AB6MTS32Qc18pjZKPhg3ZhyeVrr0zOhgbp2L4NhCue_bj1soMvay6MnZyN9LqiiJCI7iS_S8Xcwvvqn00IOdpj-wTAnPTCt4udqVVf85K8ecNFTw6UIDlT5hQDhcumqPriCohziX5fSFbcrEW94Lf2LEy5GdJEci0iuSBogA0Pz6kCxqbazxI7FcsEDlRIoAgvuxtXnOGgygDKm"

key = hashlib.sha256(SECRET.encode()).digest()
data = base64.urlsafe_b64decode(TOKEN + "==")
nonce, ciphertext = data[:12], data[12:]
signed_jwt = AESGCM(key).decrypt(nonce, ciphertext, None).decode()

payload = json.loads(base64.urlsafe_b64decode(signed_jwt.split(".")[1] + "=="))
print(json.dumps(payload, indent=2))
