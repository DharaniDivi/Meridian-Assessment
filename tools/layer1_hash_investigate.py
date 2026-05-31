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


def extract_raw_literals(batch_bytes: bytes):
    text = batch_bytes.decode("utf-8")
    doc = json.loads(text)
    raw = re.findall(r'"data"\s*:\s*\[(.*?)\]', text, re.S)
    if not raw:
        return []
    array_body = raw[0]
    return re.findall(r'"((?:\\.|[^"\\])*)"', array_body)


def build_data_only_from_raw_literals(literals):
    return ("{\"data\":[" + ",".join(f'"{lit}"' for lit in literals) + "]}").encode("utf-8")


index_path = os.path.join(ROOT, "dataset-index.bin")
dataset_path = os.path.join(ROOT, "dataset.bin")
batches_raw_path = os.path.join(ROOT, "dataset-batches.raw")

for label, path in [
    ("dataset.bin", dataset_path),
    ("dataset-batches.raw", batches_raw_path),
]:
    if not os.path.exists(path):
        print(f"{label}: missing")
        continue

    raw = open(path, "rb").read()
    print(f"\n=== {label} ({len(raw)} bytes) ===")
    print("contains \\u002B:", b"\\u002B" in raw)
    print("contains literal +:", b"+" in raw)

    objs = split_json_objects(raw)
    print("json objects:", len(objs))

    literals = []
    for ob in objs:
        text = ob.decode("utf-8")
        doc = json.loads(text)
        for item in doc.get("data", []):
            literals.append(item)

    escaped = json.dumps({"data": literals}, separators=(",", ":")).encode("utf-8")
    print("ciphertext count:", len(literals))
    print("escaped dataOnly sha256:", sha256_bytes(escaped))

    if objs and label == "dataset-batches.raw":
        raw_literals = []
        for ob in objs:
            text = ob.decode("utf-8")
            for match in re.finditer(r'"((?:\\.|[^"\\])*)"', text):
                pass
            doc_text = text
            data_match = re.search(r'"data"\s*:\s*\[', doc_text)
            if not data_match:
                continue
            start = data_match.end()
            depth = 1
            i = start
            in_str = False
            esc = False
            while i < len(doc_text) and depth:
                c = doc_text[i]
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
            array_slice = doc_text[start : i - 1]
            pos = 0
            while pos < len(array_slice):
                while pos < len(array_slice) and array_slice[pos] in " \t\n\r,":
                    pos += 1
                if pos >= len(array_slice):
                    break
                if array_slice[pos] != '"':
                    break
                end = pos + 1
                esc2 = False
                while end < len(array_slice):
                    ch = array_slice[end]
                    if esc2:
                        esc2 = False
                    elif ch == "\\":
                        esc2 = True
                    elif ch == '"':
                        break
                    end += 1
                raw_literals.append(array_slice[pos : end + 1])
                pos = end + 1

        if raw_literals:
            canonical = b"{\"data\":[" + ",".join(l.encode("utf-8") for l in raw_literals) + b"]}"
            print("raw-literal dataOnly sha256:", sha256_bytes(canonical))
            print("raw-literal sample has +:", b"+" in canonical and b"\\u002B" not in canonical)

if os.path.exists(index_path):
    index_bytes = open(index_path, "rb").read()
    print("\nindex sha256:", sha256_bytes(index_bytes))
