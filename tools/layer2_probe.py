import json, base64, hashlib, hmac, sys, urllib.request

DATA = r"C:\AA-PROJECT\src\Assessment.Api\data\dataset.bin"
OUT = r"C:\AA-PROJECT\tools\layer2-probe-out.txt"
API_KEY = "sa_90291610bea54baafd7c03676b33c31380315ea49c5289de4ed5955b27d0fe00"
CONTENT_HASH = "ca765b1e5464555b22a2ebd3e733c9f90b4f150ba7d89083cd0d2aaf8d2bf149"
BASE = "https://ca-seassessment-api-dev.happywater-190f264d.northcentralus.azurecontainerapps.io"

lines = []
def log(s):
    lines.append(s)
    print(s)

with open(DATA, "r", encoding="utf-8") as f:
    samples = json.load(f)["data"][:3]

log("=== CIPHERTEXT ===")
for i, s in enumerate(samples):
    b = base64.b64decode(s)
    log(f"sample {i}: len={len(b)} head={b[:32].hex()}")

keys = {
    "apiKeyHex": bytes.fromhex(API_KEY[3:]),
    "sha256ApiKey": hashlib.sha256(API_KEY.encode()).digest(),
    "contentHash": bytes.fromhex(CONTENT_HASH),
    "hmac": hmac.new(bytes.fromhex(API_KEY[3:]), bytes.fromhex(CONTENT_HASH), hashlib.sha256).digest(),
}

try:
    from nacl.secret import SecretBox
    log("PyNaCl available")
    for name, key in keys.items():
        box = SecretBox(key)
        for i, s in enumerate(samples):
            try:
                plain = box.decrypt(base64.b64decode(s))
                log(f"WIN secretbox key={name} sample={i} plain={plain[:120]!r}")
            except Exception as e:
                log(f"fail secretbox key={name} sample={i}: {type(e).__name__}")
except ImportError:
    log("PyNaCl NOT installed - pip install pynacl")

log("=== API ===")
for path in ["api/v1/key", "api/v1/transcript", "api/v1/stats"]:
    req = urllib.request.Request(BASE + "/" + path, headers={"Authorization": f"Bearer {API_KEY}"})
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            body = resp.read(500)
            log(f"GET {path} -> {resp.status} {body[:200]!r}")
            for k, v in resp.headers.items():
                if "key" in k.lower() or k.lower() in ("link", "etag") or k.lower().startswith("x-"):
                    log(f"  {k}: {v}")
    except Exception as ex:
        log(f"GET {path} ERR {ex}")

with open(OUT, "w", encoding="utf-8") as f:
    f.write("\n".join(lines))
