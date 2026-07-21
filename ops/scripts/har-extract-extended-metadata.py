#!/usr/bin/env python3
"""Extract extended-metadata request/response bodies from a Fiddler HAR export."""

import base64
import gzip
import json
import re
import sys
from pathlib import Path

try:
    import zstandard as zstd
except ImportError:
    print("pip install zstandard", file=sys.stderr)
    raise

ZSTD_MAGIC = b"\x28\xb5\x2f\xfd"
GZIP_MAGIC = b"\x1f\x8b"


def har_text_to_bytes(text: str) -> bytes:
    # Fiddler JSON may mangle opaque bytes as \uXXXX; keep low byte per code unit.
    return bytes(ord(c) & 0xFF for c in text)


def maybe_decompress(body: bytes) -> tuple[bytes, str]:
    if body.startswith(ZSTD_MAGIC):
        dctx = zstd.ZstdDecompressor()
        return dctx.decompress(body), "zstd"
    if body.startswith(GZIP_MAGIC):
        return gzip.decompress(body), "gzip"
    return body, "raw"


def safe_name(s: str) -> str:
    s = re.sub(r"[^\w.\-]+", "_", s)
    return s[:80]


def main() -> int:
    har_path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(r"c:\Users\ChristosKarapasias\Documents\exx.har")
    out_dir = Path(sys.argv[2]) if len(sys.argv) > 2 else har_path.parent / (har_path.stem + "_extracted")
    out_dir.mkdir(parents=True, exist_ok=True)

    har = json.loads(har_path.read_text(encoding="utf-8-sig"))
    entries = har["log"]["entries"]
    manifest = []

    for i, entry in enumerate(entries):
        req = entry["request"]
        resp = entry["response"]
        url = req["url"]
        if "extended-metadata" not in url:
            continue

        feature = next((h["value"] for h in req["headers"] if h["name"].lower() == "client-feature-id"), f"entry{i:02d}")
        prefix = f"{i:02d}_{safe_name(feature)}"

        # Request body
        req_raw = b""
        if "postData" in req and "text" in req["postData"]:
            req_raw = har_text_to_bytes(req["postData"]["text"])
        req_body, req_enc = maybe_decompress(req_raw)
        (out_dir / f"{prefix}_request_wire.bin").write_bytes(req_raw)
        (out_dir / f"{prefix}_request.bin").write_bytes(req_body)

        # Response body (HAR stores base64)
        resp_wire = base64.b64decode(resp["content"]["text"])
        resp_body, resp_enc = maybe_decompress(resp_wire)
        (out_dir / f"{prefix}_response_wire.bin").write_bytes(resp_wire)
        (out_dir / f"{prefix}_response.bin").write_bytes(resp_body)

        manifest.append({
            "index": i,
            "feature": feature,
            "status": resp["status"],
            "request_wire_bytes": len(req_raw),
            "request_bytes": len(req_body),
            "request_encoding": req_enc,
            "response_wire_bytes": len(resp_wire),
            "response_bytes": len(resp_body),
            "response_encoding": resp_enc,
            "request_file": f"{prefix}_request.bin",
            "response_file": f"{prefix}_response.bin",
        })

    (out_dir / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"Extracted {len(manifest)} extended-metadata entries -> {out_dir}")
    for m in manifest:
        print(f"  [{m['index']:02d}] {m['feature']}: req {m['request_bytes']}B ({m['request_encoding']}) -> resp {m['response_bytes']}B ({m['response_encoding']})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
