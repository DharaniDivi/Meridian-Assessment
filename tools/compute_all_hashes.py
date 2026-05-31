import base64
import hashlib
import json
import os
import re

ROOT = r"C:\AA-PROJECT\src\Assessment.Api\data"


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


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


def extract_raw_string_literals(array_text: str):
    literals = []
    pos = 0
    while pos < len(array_text):
        while pos < len(array_text) and array_text[pos] in " \t\n\r,":
            pos += 1
        if pos >= len(array_text):
            break
        if array_text[pos] != '"':
            break
        end = pos + 1
        esc = False
        while end < len(array_text):
            ch = array_text[end]
            if esc:
                esc = False
            elif ch == "\\":
                esc = True
            elif ch == '"':
                break
            end += 1
        literals.append(array_text[pos : end + 1])
        pos = end + 1
    return literals


batches_path = os.path.join(ROOT, "dataset-batches.raw")
index_path = os.path.join(ROOT, "dataset-index.bin")
dataset_path = os.path.join(ROOT, "dataset.bin")

if not os.path.exists(batches_path):
    print("Missing dataset-batches.raw — run Layer 1 first")
    raise SystemExit(1)

batch_bytes_list = split_json_objects(open(batches_path, "rb").read())
index_bytes = open(index_path, "rb").read() if os.path.exists(index_path) else b""
dataset_bytes = open(dataset_path, "rb").read() if os.path.exists(dataset_path) else b""

ciphertexts = []
raw_literals = []
envelope_texts = []
for ob in batch_bytes_list:
    text = ob.decode("utf-8")
    envelope_texts.append(text)
    doc = json.loads(text)
    data_match = re.search(r'"data"\s*:\s*\[', text)
    if data_match:
        start = data_match.end()
        depth = 1
        i = start
        in_str = False
        esc = False
        while i < len(text) and depth:
            c = text[i]
            if in_str:
                if esc:
                    esc = False
                elif c == "\\":
                    esc = True
                elif c == '"':
                    in_str = False
                i += 1
                continue
            if c == '"':
                in_str = True
            elif c == "[":
                depth += 1
            elif c == "]":
                depth -= 1
            i += 1
        raw_literals.extend(extract_raw_string_literals(text[start : i - 1]))
    for item in doc.get("data", []):
        ciphertexts.append(item)

hashes = {}
hashes["rawConcat"] = sha256_bytes(b"".join(batch_bytes_list))
hashes["ndjsonBatches"] = sha256_bytes("\n".join(t.decode("utf-8") for t in batch_bytes_list).encode())
hashes["ndjsonBatchesTrailingNewline"] = sha256_bytes(("\n".join(t.decode("utf-8") for t in batch_bytes_list) + "\n").encode())
hashes["dataOnlyEnvelope"] = sha256_bytes(("{\"data\":[" + ",".join(raw_literals) + "]}").encode())
hashes["mergedEnvelope"] = sha256_bytes(json.dumps({"count": len(ciphertexts), "data": ciphertexts}, separators=(",", ":")).encode())
hashes["ciphertextArray"] = sha256_bytes(json.dumps(ciphertexts, separators=(",", ":")).encode())
hashes["ciphertextNdjson"] = sha256_bytes("\n".join(ciphertexts).encode())
hashes["ciphertextJoinedNoSep"] = sha256_bytes("".join(ciphertexts).encode())
hashes["envelopeArrayJson"] = sha256_bytes(("[" + ",".join(envelope_texts) + "]").encode())
hashes["decodedCipherConcat"] = sha256_bytes(b"".join(base64.b64decode(c) for c in ciphertexts))
hashes["indexPlusBatches"] = sha256_bytes(index_bytes + b"".join(batch_bytes_list))
hashes["canonicalFileBytes"] = sha256_bytes(dataset_bytes)
hashes["indexBody"] = sha256_bytes(index_bytes)

print(f"ciphertexts: {len(ciphertexts)}")
print(f"batches: {len(batch_bytes_list)}")
print()
for name, digest in sorted(hashes.items()):
    print(f"{name:32} {digest}")
