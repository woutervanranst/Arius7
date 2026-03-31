#!/usr/bin/env python3
"""
recover-chunk.py — Emergency recovery for Arius AES-256-GCM encrypted chunks (ArGCM1 format).

Usage:
  python3 recover-chunk.py <encrypted-chunk-file> <passphrase> [output-file]

If output-file is omitted, the decrypted+decompressed content is written to stdout.

Prerequisites:
  - Python 3.7+
  - 'cryptography' package  (pip install cryptography)
  - stdlib only otherwise (hashlib, struct, zlib, sys, argparse)

Format (ArGCM1):
  HEADER (38 bytes):
    [0..5]   Magic: "ArGCM1" (6 bytes)
    [6..21]  Salt (16 bytes, for PBKDF2)
    [22..25] PBKDF2 iterations (LE uint32)
    [26..37] Nonce₀ (12 bytes, base nonce)
  BLOCKS (repeated):
    [0..3]   Plaintext length (LE uint32, max 65536)
    [4..]    Ciphertext + GCM tag (length + 16 bytes)
  SENTINEL (final block):
    [0..3]   Length = 0x00000000
    [4..19]  GCM tag (16 bytes)

Nonce derivation: nonce_i = nonce₀ XOR little_endian_bytes(i, 12)
Block size: 64 KiB (fixed in ArGCM1)
Key derivation: PBKDF2-SHA256 (iterations from header, output 32 bytes)
"""

import argparse
import sys
import hashlib
import struct
import zlib

try:
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
except ImportError:
    print("Error: 'cryptography' package is required.", file=sys.stderr)
    print("  Install with: pip install cryptography", file=sys.stderr)
    sys.exit(1)

MAGIC      = b"ArGCM1"
SALT_SIZE  = 16
ITER_SIZE  = 4
NONCE_SIZE = 12
TAG_SIZE   = 16


def _derive_nonce(nonce0: bytes, block_index: int) -> bytes:
    """nonce_i = nonce₀ XOR little_endian_bytes(i, 12)"""
    counter = block_index.to_bytes(NONCE_SIZE, "little")
    return bytes(a ^ b for a, b in zip(nonce0, counter))


def decrypt_argcm1(chunk_path: str, passphrase: str, out_stream) -> None:
    """
    Parse and decrypt an ArGCM1 file, writing decompressed plaintext to out_stream.
    Uses a streaming zlib decompressor so no full chunk is ever buffered in memory.
    """
    passphrase_bytes = passphrase.encode("utf-8")
    # wbits=47 means gzip format auto-detect (16 | 15)
    decompressor = zlib.decompressobj(wbits=47)

    with open(chunk_path, "rb") as f:
        # ── Parse 38-byte header ───────────────────────────────────────────
        magic = f.read(len(MAGIC))
        if magic != MAGIC:
            print(f"Error: not an ArGCM1 file (magic={magic!r})", file=sys.stderr)
            sys.exit(1)

        salt       = f.read(SALT_SIZE)
        iterations = struct.unpack("<I", f.read(ITER_SIZE))[0]
        nonce0     = f.read(NONCE_SIZE)

        # ── Derive AES-256 key via PBKDF2-SHA256 ──────────────────────────
        key = hashlib.pbkdf2_hmac("sha256", passphrase_bytes, salt, iterations, dklen=32)
        aes = AESGCM(key)

        # ── Decrypt blocks, decompress incrementally ───────────────────────
        block_index = 0

        while True:
            len_bytes = f.read(4)
            if len(len_bytes) < 4:
                print("Error: truncated stream (missing block length)", file=sys.stderr)
                sys.exit(1)

            plain_len = struct.unpack("<I", len_bytes)[0]
            nonce_i   = _derive_nonce(nonce0, block_index)
            block_index += 1

            if plain_len == 0:
                # Sentinel — verify its GCM tag, then we're done
                tag = f.read(TAG_SIZE)
                if len(tag) < TAG_SIZE:
                    print("Error: truncated stream (missing sentinel tag)", file=sys.stderr)
                    sys.exit(1)
                try:
                    aes.decrypt(nonce_i, tag, None)
                except Exception:
                    print(
                        "Error: sentinel authentication failed "
                        "(file may be truncated or tampered)",
                        file=sys.stderr,
                    )
                    sys.exit(1)
                # Flush remaining decompressor output
                out_stream.write(decompressor.flush())
                break

            cipher_and_tag = f.read(plain_len + TAG_SIZE)
            if len(cipher_and_tag) < plain_len + TAG_SIZE:
                print(f"Error: truncated stream (block {block_index - 1})", file=sys.stderr)
                sys.exit(1)

            try:
                plaintext_block = aes.decrypt(nonce_i, cipher_and_tag, None)
            except Exception:
                print(
                    f"Error: authentication failed on block {block_index - 1} "
                    "(file may be tampered)",
                    file=sys.stderr,
                )
                sys.exit(1)

            # Feed decrypted bytes into the streaming decompressor
            out_stream.write(decompressor.decompress(plaintext_block))


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Recover an Arius AES-256-GCM encrypted chunk (ArGCM1 format).",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument("chunk_file",  help="Path to the encrypted chunk file")
    parser.add_argument("passphrase",  help="Encryption passphrase")
    parser.add_argument("output_file", nargs="?", help="Output file (default: stdout)")
    args = parser.parse_args()

    if args.output_file:
        with open(args.output_file, "wb") as out:
            decrypt_argcm1(args.chunk_file, args.passphrase, out)
        print(f"Recovered: {args.output_file}", file=sys.stderr)
    else:
        decrypt_argcm1(args.chunk_file, args.passphrase, sys.stdout.buffer)


if __name__ == "__main__":
    main()
