#!/usr/bin/env python3
"""
recover-chunk.py — Emergency recovery for Arius encrypted chunks.

Supports:
  - AES-256-GCM (ArGCM1 format, default since v2)
  - AES-256-CBC (Salted__ format, legacy)

The format is auto-detected from the file's magic bytes.

Usage:
  python3 recover-chunk.py <encrypted-chunk-file> <passphrase> [output-file]

If output-file is omitted, the decrypted+decompressed content is written to stdout.

Prerequisites:
  - Python 3.7+
  - 'cryptography' package  (pip install cryptography)
  - stdlib only otherwise (hashlib, struct, zlib, sys, argparse)

GCM Format (ArGCM1):
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

CBC Format (Salted__, legacy):
  [0..7]   Magic: "Salted__" (8 bytes)
  [8..15]  Salt (8 bytes)
  [16..]   Ciphertext (AES-256-CBC, PKCS7 padding)
  Key derivation: PBKDF2-SHA256, 10,000 iterations, 48-byte output
    bytes  0–31 = AES-256 key
    bytes 32–47 = IV

GCM nonce derivation: nonce_i = nonce₀ XOR little_endian_bytes(i, 12)
GCM block size: 64 KiB (fixed in ArGCM1)
"""

import argparse
import sys
import hashlib
import struct
import zlib

try:
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
    from cryptography.hazmat.primitives.padding import PKCS7
except ImportError:
    print("Error: 'cryptography' package is required.", file=sys.stderr)
    print("  Install with: pip install cryptography", file=sys.stderr)
    sys.exit(1)

# ── Format constants ──────────────────────────────────────────────────────────

GCM_MAGIC      = b"ArGCM1"
GCM_SALT_SIZE  = 16
GCM_ITER_SIZE  = 4
GCM_NONCE_SIZE = 12
GCM_TAG_SIZE   = 16

CBC_MAGIC      = b"Salted__"
CBC_SALT_SIZE  = 8
CBC_KEY_SIZE   = 32   # AES-256
CBC_IV_SIZE    = 16   # AES block size
CBC_PBKDF2_ITER = 10_000


# ── GCM recovery ──────────────────────────────────────────────────────────────

def _gcm_derive_nonce(nonce0: bytes, block_index: int) -> bytes:
    """nonce_i = nonce₀ XOR little_endian_bytes(i, 12)"""
    counter = block_index.to_bytes(GCM_NONCE_SIZE, "little")
    return bytes(a ^ b for a, b in zip(nonce0, counter))


def _recover_gcm(f, passphrase_bytes: bytes, out_stream) -> None:
    """
    Decrypt and decompress an ArGCM1 stream (file object already at position 0),
    writing plaintext to out_stream. Uses a streaming zlib decompressor — no full
    chunk is ever buffered in memory.
    """
    magic = f.read(len(GCM_MAGIC))
    if magic != GCM_MAGIC:
        print(f"Error: not an ArGCM1 file (magic={magic!r})", file=sys.stderr)
        sys.exit(1)

    salt       = f.read(GCM_SALT_SIZE)
    iterations = struct.unpack("<I", f.read(GCM_ITER_SIZE))[0]
    nonce0     = f.read(GCM_NONCE_SIZE)

    key = hashlib.pbkdf2_hmac("sha256", passphrase_bytes, salt, iterations, dklen=32)
    aes = AESGCM(key)

    # wbits=47 → gzip format auto-detect (16 | 15)
    decompressor = zlib.decompressobj(wbits=47)
    block_index  = 0

    while True:
        len_bytes = f.read(4)
        if len(len_bytes) < 4:
            print("Error: truncated stream (missing block length)", file=sys.stderr)
            sys.exit(1)

        plain_len = struct.unpack("<I", len_bytes)[0]
        nonce_i   = _gcm_derive_nonce(nonce0, block_index)
        block_index += 1

        if plain_len == 0:
            # Sentinel — verify its GCM tag, then we're done
            tag = f.read(GCM_TAG_SIZE)
            if len(tag) < GCM_TAG_SIZE:
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
            out_stream.write(decompressor.flush())
            break

        cipher_and_tag = f.read(plain_len + GCM_TAG_SIZE)
        if len(cipher_and_tag) < plain_len + GCM_TAG_SIZE:
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

        out_stream.write(decompressor.decompress(plaintext_block))


# ── CBC recovery ──────────────────────────────────────────────────────────────

def _recover_cbc(f, passphrase_bytes: bytes, out_stream) -> None:
    """
    Decrypt and decompress a legacy Salted__ AES-256-CBC stream (file object already
    at position 0), writing plaintext to out_stream.
    """
    magic = f.read(len(CBC_MAGIC))
    if magic != CBC_MAGIC:
        print(f"Error: not a Salted__ file (magic={magic!r})", file=sys.stderr)
        sys.exit(1)

    salt = f.read(CBC_SALT_SIZE)

    # Derive 48 bytes: first 32 = AES-256 key, last 16 = IV
    derived = hashlib.pbkdf2_hmac(
        "sha256", passphrase_bytes, salt, CBC_PBKDF2_ITER,
        dklen=CBC_KEY_SIZE + CBC_IV_SIZE,
    )
    key = derived[:CBC_KEY_SIZE]
    iv  = derived[CBC_KEY_SIZE:]

    cipher      = Cipher(algorithms.AES(key), modes.CBC(iv))
    decryptor   = cipher.decryptor()
    unpadder    = PKCS7(128).unpadder()
    # wbits=47 → gzip format auto-detect (16 | 15)
    decompressor = zlib.decompressobj(wbits=47)

    while True:
        chunk = f.read(64 * 1024)
        if not chunk:
            break
        decrypted = decryptor.update(chunk)
        out_stream.write(decompressor.decompress(unpadder.update(decrypted)))

    # Finalise decryption + PKCS7 unpadding
    try:
        final_decrypted = decryptor.finalize()
        final_unpadded  = unpadder.update(final_decrypted) + unpadder.finalize()
    except Exception as e:
        print(f"Error: decryption/unpadding failed — wrong passphrase? ({e})", file=sys.stderr)
        sys.exit(1)

    out_stream.write(decompressor.decompress(final_unpadded))
    out_stream.write(decompressor.flush())


# ── Auto-detect and dispatch ──────────────────────────────────────────────────

def recover(chunk_path: str, passphrase: str, out_stream) -> None:
    """Open the chunk file, detect its format, and write plaintext to out_stream."""
    passphrase_bytes = passphrase.encode("utf-8")

    with open(chunk_path, "rb") as f:
        header = f.read(8)
        f.seek(0)

        if header[:6] == GCM_MAGIC:
            _recover_gcm(f, passphrase_bytes, out_stream)
        elif header == CBC_MAGIC:
            _recover_cbc(f, passphrase_bytes, out_stream)
        else:
            print(
                f"Error: unrecognised format (first 8 bytes: {header!r}). "
                "Expected 'ArGCM1' (GCM) or 'Salted__' (CBC).",
                file=sys.stderr,
            )
            sys.exit(1)


# ── CLI ───────────────────────────────────────────────────────────────────────

def main() -> None:
    parser = argparse.ArgumentParser(
        description="Recover an Arius encrypted chunk (ArGCM1 or Salted__ format).",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument("chunk_file",  help="Path to the encrypted chunk file")
    parser.add_argument("passphrase",  help="Encryption passphrase")
    parser.add_argument("output_file", nargs="?", help="Output file (default: stdout)")
    args = parser.parse_args()

    if args.output_file:
        with open(args.output_file, "wb") as out:
            recover(args.chunk_file, args.passphrase, out)
        print(f"Recovered: {args.output_file}", file=sys.stderr)
    else:
        recover(args.chunk_file, args.passphrase, sys.stdout.buffer)


if __name__ == "__main__":
    main()
