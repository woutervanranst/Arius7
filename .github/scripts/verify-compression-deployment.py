#!/usr/bin/env python3
"""Exercise a published CLI (or the CLI in the production runtime image) against a disposable repository."""

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import random
import shutil
import subprocess
import sys
import tarfile
import tempfile
import uuid

import zstandard


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", required=True, type=Path)
    parser.add_argument("--image", help="Run the framework-dependent CLI in this Linux runtime image")
    args = parser.parse_args()
    account = os.environ.get("ARIUS_E2E_ACCOUNT")
    key = os.environ.get("ARIUS_E2E_KEY")
    if not account or not key:
        parser.error("ARIUS_E2E_ACCOUNT and ARIUS_E2E_KEY are required; deployment compatibility is not verified")
    azure_cli = shutil.which("az")
    if not azure_cli:
        parser.error("Azure CLI is required to clean up disposable containers")

    cli = args.cli.resolve(strict=True)
    environment = dict(os.environ, ARIUS_ACCOUNT=account, ARIUS_KEY=key,
                       AZURE_STORAGE_ACCOUNT=account, AZURE_STORAGE_KEY=key)
    cli_environment = environment.copy()
    with tempfile.TemporaryDirectory(prefix="arius-zstd-deployment-") as temporary:
        root = Path(temporary)
        source = root / "source"
        source.mkdir()
        (root / "home").mkdir()
        (source / "empty.bin").write_bytes(b"")
        (source / "small.txt").write_bytes(b"Arius standard zstd compatibility\n" * 2048)
        (source / "large.bin").write_bytes(random.Random(220).randbytes(2 * 1024 * 1024))
        if args.image:
            relative_cli = cli.relative_to(Path.cwd())
            command = [
                "docker", "run", "--rm", "--user", f"{os.getuid()}:{os.getgid()}",
                "--env", "ARIUS_ACCOUNT", "--env", "ARIUS_KEY", "--env", "HOME=/work/home",
                "--mount", f"type=bind,source={root},target=/work",
                "--mount", f"type=bind,source={Path.cwd()},target=/workspace,readonly",
                "--entrypoint", "dotnet", args.image, f"/workspace/{relative_cli.as_posix()}",
            ]
            source_argument = "/work/source"
        else:
            command = [str(cli)]
            source_argument = str(source)
            cli_environment["HOME"] = str(root / "home")
            cli_environment["USERPROFILE"] = str(root / "home")

        for encrypted in (False, True):
            container = "zstd-gate-" + uuid.uuid4().hex
            passphrase = "zstd-compatibility-test" if encrypted else ""
            options = ["--container", container]
            if encrypted:
                options += ["--passphrase", passphrase]
            expected = {
                hashlib.sha256(passphrase.encode() + p.read_bytes()).hexdigest(): p.read_bytes()
                for p in source.iterdir()
            }
            try:
                subprocess.run(command + ["archive", source_argument, "--tier", "Hot"] + options,
                               env=cli_environment, check=True, timeout=180)
                listing = subprocess.run(command + ["ls"] + options, env=cli_environment, check=True,
                                         stdout=subprocess.PIPE, text=True, timeout=180).stdout
                if any(p.name not in listing for p in source.iterdir()):
                    raise RuntimeError("Published CLI could not list every archived binary file")

                chunks = json.loads(subprocess.run(
                    [azure_cli, "storage", "blob", "list", "--container-name", container,
                     "--prefix", "chunks/", "--include", "m", "--auth-mode", "key", "--output", "json",
                     "--only-show-errors"],
                    env=environment, check=True, stdout=subprocess.PIPE, text=True, timeout=120,
                ).stdout)
                actual = {}
                for chunk in chunks:
                    chunk_type = chunk["metadata"]["arius_type"]
                    if chunk_type == "thin":
                        continue
                    downloaded = root / "chunk.enc"
                    subprocess.run(
                        [azure_cli, "storage", "blob", "download", "--container-name", container,
                         "--name", chunk["name"], "--file", str(downloaded), "--overwrite", "true",
                         "--auth-mode", "key", "--only-show-errors"],
                        env=environment, check=True, stdout=subprocess.DEVNULL, timeout=120,
                    )
                    if encrypted:
                        recovered = root / "recovered.bin"
                        subprocess.run(
                            [sys.executable, "recover-chunk.py", str(downloaded), passphrase, str(recovered)],
                            check=True, timeout=120,
                        )
                        payload = recovered.read_bytes()
                    else:
                        decoder = zstandard.ZstdDecompressor().decompressobj()
                        payload = decoder.decompress(downloaded.read_bytes()) + decoder.flush()
                        if not decoder.eof:
                            raise RuntimeError("Published CLI wrote a truncated zstd frame")
                    if chunk_type == "large":
                        actual[hashlib.sha256(passphrase.encode() + payload).hexdigest()] = payload
                    elif chunk_type == "tar":
                        with tarfile.open(fileobj=io.BytesIO(payload), mode="r:") as archive:
                            for entry in archive:
                                actual[entry.name] = archive.extractfile(entry).read()
                    else:
                        raise RuntimeError(f"Unexpected chunk type: {chunk_type}")
                if actual != expected:
                    raise RuntimeError(f"Published CLI recovery produced different content (encrypted={encrypted})")
            finally:
                subprocess.run(
                    [azure_cli, "storage", "container", "delete", "--name", container, "--auth-mode", "key", "--only-show-errors"],
                    env=environment, check=True, stdout=subprocess.DEVNULL, timeout=120,
                )


if __name__ == "__main__":
    main()
