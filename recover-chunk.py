#!/usr/bin/env python3
"""
recover-chunk.py — Emergency recovery for Arius encrypted chunks.

Each chunk is compressed and then encrypted, so recovery decrypts first and
decompresses the result.

Encryption (auto-detected from the file's leading magic bytes):
  - AES-256-GCM (ArGCM1 format, default since v2)
  - AES-256-CBC (Salted__ format, legacy)

Compression (auto-detected from the decrypted stream's leading magic bytes):
  - zstd (RFC 8878 frame, default since the zstd migration)
  - gzip (legacy)

Usage:
  python3 recover-chunk.py [--no-decompress] <encrypted-chunk-file> <passphrase> [output-file]

If output-file is omitted, the recovered content is written to stdout.

Prerequisites:
  - Python 3.7+
  - 'cryptography' package        (pip install cryptography)
  - For zstd chunks, one of:      pip install zstandard   (preferred)
                            or:   pip install pyzstd
    gzip chunks need no extra package (stdlib zlib).
    No zstd package available? Re-run with --no-decompress and pipe the raw
    (still-compressed) output through the zstd CLI yourself:
      python3 recover-chunk.py --no-decompress CHUNK PASS | zstd -d > out

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
import os
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

GCM_MAGIC       = b"ArGCM1"
GCM_SALT_SIZE   = 16
GCM_ITER_SIZE   = 4
GCM_NONCE_SIZE  = 12
GCM_TAG_SIZE    = 16
GCM_BLOCK_SIZE  = 64 * 1024   # ArGCM1 plaintext block size; block lengths never exceed this

CBC_MAGIC      = b"Salted__"
CBC_SALT_SIZE  = 8
CBC_KEY_SIZE   = 32   # AES-256
CBC_IV_SIZE    = 16   # AES block size
CBC_PBKDF2_ITER = 10_000

# Compression frame magic bytes (little-endian on disk), matching ZstdCompressionService.
ZSTD_MAGIC = b"\x28\xb5\x2f\xfd"   # RFC 8878 frame; XXH64 content checksum enabled
GZIP_MAGIC = b"\x1f\x8b"           # legacy gzip blobs


# ── Compression: auto-detecting decompressor (mirrors ZstdCompressionService) ──

def _gzip_decompressor():
    """gzip decompressor. wbits=47 → gzip-format auto-detect (16 | 15)."""
    return zlib.decompressobj(wbits=47)


def _zstd_decompressor():
    """
    Returns an incremental zstd decompressor (whichever backend is installed) exposing
    .decompress(), .flush(), and .eof. libzstd validates the frame's XXH64 content checksum,
    so corrupt bytes inside a complete frame raise; .eof additionally lets the caller reject a
    frame that was truncated before its end marker, where the checksum trailer never arrives.
    Exits with an actionable hint if no zstd backend is available.
    """
    try:
        import zstandard
        return zstandard.ZstdDecompressor().decompressobj()  # exposes .decompress/.flush/.eof
    except ImportError:
        pass
    try:
        import pyzstd
        return _PyzstdAdapter(pyzstd.ZstdDecompressor())
    except ImportError:
        pass

    print(
        "Error: this chunk is zstd-compressed, but no Python zstd backend is installed.\n"
        "  Install one with:  pip install zstandard      (preferred)\n"
        "               or:   pip install pyzstd\n"
        "  Or re-run with --no-decompress to emit the raw zstd stream and decompress it\n"
        "  with the zstd CLI yourself:\n"
        "    python3 recover-chunk.py --no-decompress CHUNK PASS | zstd -d > out",
        file=sys.stderr,
    )
    sys.exit(1)


class _PyzstdAdapter:
    """Adapts pyzstd.ZstdDecompressor to the .decompress()/.flush()/.eof shape of zlib/zstandard."""

    def __init__(self, decompressor):
        self._decompressor = decompressor

    def decompress(self, data: bytes) -> bytes:
        return self._decompressor.decompress(data)

    def flush(self) -> bytes:
        # pyzstd emits everything as it is fed; a complete frame leaves nothing to flush.
        return b""

    @property
    def eof(self) -> bool:
        # True only once the frame's end marker has been decoded (False for a truncated frame).
        return self._decompressor.eof


class _AutoDecompressor:
    """
    Incremental decompressor that auto-detects gzip vs zstd from the leading magic bytes
    of the (decrypted) compressed stream, then delegates to the matching backend. Exposes
    the same .decompress()/.flush() shape as zlib.decompressobj so it drops straight into
    the recovery loops.

    The format signal is intrinsic to the bytes, so no metadata/content-type is needed —
    matching the self-describing read path in Arius' ZstdCompressionService:
      zstd: 28 B5 2F FD   gzip: 1F 8B
    An empty (0-byte) compressed stream decodes to empty content (legacy gzip wrote empty
    payloads, e.g. an empty filetree, as a 0-byte body).
    """

    def __init__(self):
        self._inner = None        # backend decompressor, once the format is known
        self._is_zstd = False     # whether the active backend is zstd (gated checks below)
        self._buffer = b""        # bytes held until enough have arrived to detect

    def decompress(self, data: bytes) -> bytes:
        if self._inner is not None:
            return self._run(lambda: self._inner.decompress(data))

        self._buffer += data
        # gzip is identifiable from 2 bytes, zstd needs 4.
        if self._buffer[:2] == GZIP_MAGIC:
            return self._activate(_gzip_decompressor(), is_zstd=False)
        if len(self._buffer) >= len(ZSTD_MAGIC):
            if self._buffer[:len(ZSTD_MAGIC)] == ZSTD_MAGIC:
                return self._activate(_zstd_decompressor(), is_zstd=True)
            _fail_unrecognized(self._buffer)
        return b""  # not enough bytes yet (and not gzip) — keep buffering

    def flush(self) -> bytes:
        if self._inner is None:
            if not self._buffer:
                return b""  # empty stream → empty content
            # A real gzip/zstd frame is never shorter than the magic; treat as corruption.
            _fail_unrecognized(self._buffer)

        out = self._run(self._inner.flush)

        # A zstd frame truncated before its end marker decodes to a silent partial prefix (its
        # XXH64 trailer is never reached). libzstd reports this as eof=False — reject it loudly,
        # matching the C# read path, which raises "Premature end of stream" on the same input.
        if self._is_zstd and not self._inner.eof:
            print(
                "Error: incomplete zstd frame — the chunk is truncated, so any recovered "
                "data is only a partial prefix. Discard it.",
                file=sys.stderr,
            )
            sys.exit(1)

        return out

    def _activate(self, inner, is_zstd: bool) -> bytes:
        self._inner = inner
        self._is_zstd = is_zstd
        buffered, self._buffer = self._buffer, b""
        return self._run(lambda: inner.decompress(buffered))

    @staticmethod
    def _run(call):
        """
        Invoke a backend decompress/flush call, turning a decoder error (corrupt frame, bad
        XXH64/CRC checksum, invalid bitstream) into a clean message + non-zero exit rather than
        letting a raw library traceback escape — an operator should not have to tell a corrupt
        chunk apart from a tool bug.
        """
        try:
            return call()
        except Exception as e:
            print(
                f"Error: decompression failed — the chunk is corrupt or truncated ({e}).",
                file=sys.stderr,
            )
            sys.exit(1)


class _Passthrough:
    """No-op decompressor for --no-decompress: emits the decrypted (still-compressed) stream verbatim."""

    def decompress(self, data: bytes) -> bytes:
        return data

    def flush(self) -> bytes:
        return b""


def _fail_unrecognized(header: bytes) -> None:
    print(
        f"Error: unrecognised compression format (first bytes: {header[:4]!r}). "
        "Expected a zstd (28 B5 2F FD) or gzip (1F 8B) frame.",
        file=sys.stderr,
    )
    sys.exit(1)


# ── GCM recovery ──────────────────────────────────────────────────────────────

def _gcm_derive_nonce(nonce0: bytes, block_index: int) -> bytes:
    """nonce_i = nonce₀ XOR little_endian_bytes(i, 12)"""
    counter = block_index.to_bytes(GCM_NONCE_SIZE, "little")
    return bytes(a ^ b for a, b in zip(nonce0, counter))


def _recover_gcm(f, passphrase_bytes: bytes, decompressor, out_stream) -> None:
    """
    Decrypt and decompress an ArGCM1 stream (file object already at position 0),
    writing plaintext to out_stream. Streams block-by-block — no full chunk is ever
    buffered in memory.
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

    block_index = 0

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
                    "Error: GCM authentication failed on the end-of-stream sentinel — "
                    "wrong passphrase, or the file is truncated/tampered.",
                    file=sys.stderr,
                )
                sys.exit(1)
            out_stream.write(decompressor.flush())
            break

        if plain_len > GCM_BLOCK_SIZE:
            print(
                f"Error: block length {plain_len} exceeds the 64 KiB maximum "
                "(corrupt or tampered header).",
                file=sys.stderr,
            )
            sys.exit(1)

        cipher_and_tag = f.read(plain_len + GCM_TAG_SIZE)
        if len(cipher_and_tag) < plain_len + GCM_TAG_SIZE:
            print(f"Error: truncated stream (block {block_index - 1})", file=sys.stderr)
            sys.exit(1)

        try:
            plaintext_block = aes.decrypt(nonce_i, cipher_and_tag, None)
        except Exception:
            print(
                f"Error: GCM authentication failed on block {block_index - 1} — "
                "wrong passphrase, or the file is truncated/tampered.",
                file=sys.stderr,
            )
            sys.exit(1)

        out_stream.write(decompressor.decompress(plaintext_block))


# ── CBC recovery ──────────────────────────────────────────────────────────────

def _recover_cbc(f, passphrase_bytes: bytes, decompressor, out_stream) -> None:
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

    cipher    = Cipher(algorithms.AES(key), modes.CBC(iv))
    decryptor = cipher.decryptor()
    unpadder  = PKCS7(128).unpadder()

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

    if final_unpadded:
        out_stream.write(decompressor.decompress(final_unpadded))
    out_stream.write(decompressor.flush())


# ── Auto-detect and dispatch ──────────────────────────────────────────────────

def recover(chunk_path: str, passphrase: str, out_stream, decompress: bool = True) -> None:
    """Open the chunk file, detect its encryption format, and write the recovered content to out_stream."""
    passphrase_bytes = passphrase.encode("utf-8")
    decompressor = _AutoDecompressor() if decompress else _Passthrough()

    with open(chunk_path, "rb") as f:
        header = f.read(8)
        f.seek(0)

        if header[:6] == GCM_MAGIC:
            _recover_gcm(f, passphrase_bytes, decompressor, out_stream)
        elif header == CBC_MAGIC:
            _recover_cbc(f, passphrase_bytes, decompressor, out_stream)
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
        description="Recover an Arius encrypted chunk (ArGCM1 or Salted__ encryption; zstd or gzip compression).",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument("chunk_file",  help="Path to the encrypted chunk file")
    parser.add_argument("passphrase",  help="Encryption passphrase")
    parser.add_argument("output_file", nargs="?", help="Output file (default: stdout)")
    parser.add_argument(
        "--no-decompress",
        action="store_true",
        help="Decrypt only; emit the raw (still-compressed) stream. Pipe it through 'zstd -d' or 'gunzip'.",
    )
    args = parser.parse_args()

    decompress = not args.no_decompress

    if args.output_file:
        # Recover into a sibling .partial file and rename only on success, so a failure
        # (wrong passphrase, truncation, corruption — all of which sys.exit non-zero) never
        # leaves a partial, unverified file where the final output should be.
        tmp = args.output_file + ".partial"
        try:
            with open(tmp, "wb") as out:
                recover(args.chunk_file, args.passphrase, out, decompress)
            os.replace(tmp, args.output_file)
        except BaseException:
            try:
                os.remove(tmp)
            except OSError:
                pass
            raise
        print(f"Recovered: {args.output_file}", file=sys.stderr)
    else:
        recover(args.chunk_file, args.passphrase, sys.stdout.buffer, decompress)


if __name__ == "__main__":
    main()
