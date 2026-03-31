#!/usr/bin/env bash
# recover-chunk.sh — Emergency recovery for Arius AES-256-GCM encrypted chunks (ArGCM1 format).
#
# Usage:
#   ./recover-chunk.sh <encrypted-chunk-file> <passphrase> [output-file]
#
# If output-file is omitted, the decrypted+decompressed content is written to stdout.
#
# Prerequisites:
#   - Python 3 (3.7+) with the 'cryptography' package installed
#       pip install cryptography
#   - gunzip (part of coreutils, available on all Linux/macOS systems)
#
# Format (ArGCM1):
#   HEADER (38 bytes):
#     [0..5]   Magic: "ArGCM1" (6 bytes)
#     [6..21]  Salt (16 bytes, for PBKDF2)
#     [22..25] PBKDF2 iterations (LE uint32)
#     [26..37] Nonce₀ (12 bytes, base nonce)
#   BLOCKS (repeated):
#     [0..3]   Plaintext length (LE uint32, max 65536)
#     [4..]    Ciphertext + GCM tag (length + 16 bytes)
#   SENTINEL (final block):
#     [0..3]   Length = 0x00000000
#     [4..19]  GCM tag (16 bytes)
#
# Nonce derivation: nonce_i = nonce₀ XOR little_endian_bytes(i, 12)
# Block size: 64 KiB (fixed in ArGCM1)
# Key derivation: PBKDF2-SHA256 (iterations from header, output 32 bytes)

set -euo pipefail

# ── Argument validation ────────────────────────────────────────────────────────

if [[ $# -lt 2 || $# -gt 3 ]]; then
    echo "Usage: $0 <encrypted-chunk-file> <passphrase> [output-file]" >&2
    exit 1
fi

CHUNK_FILE="$1"
PASSPHRASE="$2"
OUTPUT_FILE="${3:-}"

if [[ ! -f "$CHUNK_FILE" ]]; then
    echo "Error: file not found: $CHUNK_FILE" >&2
    exit 1
fi

# ── Dependency checks ─────────────────────────────────────────────────────────

if ! command -v python3 &>/dev/null; then
    echo "Error: python3 is required but not found on PATH." >&2
    exit 1
fi

if ! python3 -c "from cryptography.hazmat.primitives.ciphers.aead import AESGCM" 2>/dev/null; then
    echo "Error: Python 'cryptography' package is required." >&2
    echo "  Install with: pip install cryptography" >&2
    exit 1
fi

if ! command -v gunzip &>/dev/null; then
    echo "Error: gunzip is required but not found on PATH." >&2
    exit 1
fi

# ── Python decryption script ──────────────────────────────────────────────────
# Inline Python handles all ArGCM1 parsing and AES-256-GCM block decryption.
# Output is the raw concatenated plaintext (still gzip-compressed at this point).

DECRYPTED_RAW=$(mktemp)
trap 'rm -f "$DECRYPTED_RAW"' EXIT

python3 - "$CHUNK_FILE" "$PASSPHRASE" "$DECRYPTED_RAW" <<'PYEOF'
import sys
import struct
import hashlib
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

MAGIC     = b"ArGCM1"
SALT_SIZE = 16
ITER_SIZE = 4
NONCE_SIZE = 12
TAG_SIZE  = 16
BLOCK_SIZE = 65536

chunk_file = sys.argv[1]
passphrase = sys.argv[2].encode("utf-8")
out_file   = sys.argv[3]

def derive_nonce(nonce0: bytes, block_index: int) -> bytes:
    """nonce_i = nonce₀ XOR little_endian_bytes(i, 12)"""
    counter = block_index.to_bytes(NONCE_SIZE, "little")
    return bytes(a ^ b for a, b in zip(nonce0, counter))

with open(chunk_file, "rb") as f_in, open(out_file, "wb") as f_out:
    # ── Parse 38-byte header ───────────────────────────────────────────────
    magic = f_in.read(len(MAGIC))
    if magic != MAGIC:
        print(f"Error: not an ArGCM1 file (magic={magic!r})", file=sys.stderr)
        sys.exit(1)

    salt       = f_in.read(SALT_SIZE)
    iter_bytes = f_in.read(ITER_SIZE)
    iterations = struct.unpack("<I", iter_bytes)[0]
    nonce0     = f_in.read(NONCE_SIZE)

    # ── Derive AES-256 key via PBKDF2-SHA256 ──────────────────────────────
    key = hashlib.pbkdf2_hmac("sha256", passphrase, salt, iterations, dklen=32)
    aes = AESGCM(key)

    # ── Decrypt blocks ─────────────────────────────────────────────────────
    block_index = 0
    while True:
        len_bytes = f_in.read(4)
        if len(len_bytes) < 4:
            print("Error: truncated stream (missing block length)", file=sys.stderr)
            sys.exit(1)

        plain_len = struct.unpack("<I", len_bytes)[0]
        nonce_i   = derive_nonce(nonce0, block_index)
        block_index += 1

        if plain_len == 0:
            # Sentinel block — read and verify GCM tag
            tag = f_in.read(TAG_SIZE)
            if len(tag) < TAG_SIZE:
                print("Error: truncated stream (missing sentinel tag)", file=sys.stderr)
                sys.exit(1)
            try:
                aes.decrypt(nonce_i, tag, None)  # ciphertext=b"" + tag appended
            except Exception:
                print("Error: sentinel authentication failed (file may be truncated or tampered)", file=sys.stderr)
                sys.exit(1)
            break

        cipher_and_tag = f_in.read(plain_len + TAG_SIZE)
        if len(cipher_and_tag) < plain_len + TAG_SIZE:
            print(f"Error: truncated stream (block {block_index - 1})", file=sys.stderr)
            sys.exit(1)

        try:
            plaintext = aes.decrypt(nonce_i, cipher_and_tag, None)
        except Exception:
            print(f"Error: authentication failed on block {block_index - 1} (file may be tampered)", file=sys.stderr)
            sys.exit(1)

        f_out.write(plaintext)

PYEOF

# ── Decompress gzip ───────────────────────────────────────────────────────────

if [[ -n "$OUTPUT_FILE" ]]; then
    gunzip -c "$DECRYPTED_RAW" > "$OUTPUT_FILE"
    echo "Recovered: $OUTPUT_FILE" >&2
else
    gunzip -c "$DECRYPTED_RAW"
fi
