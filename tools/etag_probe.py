import hashlib
import json
import os
import re

ROOT = r"C:\AA-PROJECT\src\Assessment.Api\data"


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_hex_to_bytes(hex_str: str) -> bytes:
    return bytes.fromhex(hex_str)


def split_json_objects(data: bytes):
    objs = []
    depth = 0
    start = 0
    in_str = False
    esc = False
    for i, b in enumerate(data):
        c = chr(b)
        if in_str:
            if esc:
                esc = False
            elif c == "\\":
                esc = True
            elif c == '"':
                in_str = False
            continue
        if c == '"':
            in_str = True
            continue
        if c == "{":
            if depth == 0:
                start = i
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                objs.append(data[start : i + 1])
    return objs


batches_path = os.path.join(ROOT, "dataset-batches.raw")
etags_path = os.path.join(ROOT, "dataset-batch-etags.json")
index_path = os.path.join(ROOT, "dataset-index.bin")

batches = split_json_objects(open(batches_path, "rb").read())
index_bytes = open(index_path, "rb").read()
etags = json.load(open(etags_path))["batchEtags"]

print("=== per-batch body vs ETag ===")
for i, (batch, etag) in enumerate(zip(batches, etags)):
    body_hash = sha256_bytes(batch)
    match = "MATCH" if body_hash == etag else "MISMATCH"
    print(f"batch {i}: body={body_hash} etag={etag} {match}")

print("\n=== composite candidates ===")
digest_bytes = b"".join(sha256_hex_to_bytes(e) for e in etags)
print("sha256(concat batch digests as bytes):", sha256_bytes(digest_bytes))
print("sha256(concat batch digests hex ascii):", sha256_bytes("".join(etags).encode()))
print("sha256(batches 1-4 raw concat):", sha256_bytes(b"".join(batches[1:])))
print("sha256(batches 1-4 digests bytes):", sha256_bytes(b"".join(sha256_hex_to_bytes(e) for e in etags[1:])))
print("sha256(index + batches 1-4):", sha256_bytes(index_bytes + b"".join(batches[1:])))
print("xor batch digests:", sha256_bytes(bytes(a ^ b ^ c ^ d ^ e for a, b, c, d, e in zip(*[sha256_hex_to_bytes(e) for e in etags])))

# data only from batches using json.dumps default (escaped)
ciphertexts = []
for batch in batches:
    doc = json.loads(batch.decode("utf-8"))
    ciphertexts.extend(doc["data"])
escaped = json.dumps({"data": ciphertexts}, separators=(",", ":")).encode()
print("escaped dataOnly (old bug):", sha256_bytes(escaped))

# with spaces after colon like some APIs
spaced = json.dumps({"data": ciphertexts}, separators=(", ", ": ")).encode()
print("dataOnly spaced json:", sha256_bytes(spaced))

# merged with spaces
merged_spaced = json.dumps({"count": len(ciphertexts), "data": ciphertexts}, separators=(", ", ": ")).encode()
print("merged spaced json:", sha256_bytes(merged_spaced))
