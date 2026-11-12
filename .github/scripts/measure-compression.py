#!/usr/bin/env python3
"""Measure a codec or the real chunk-upload stack in a fresh process, including native peak RSS."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("codec", choices=("previous", "framework", "upload"))
    parser.add_argument("workload", choices=("text", "mixed", "random"))
    parser.add_argument("--encrypted", action="store_true", help="Use encryption for the upload scenario")
    parser.add_argument("--assembly", type=Path,
                        default=Path("src/Arius.Benchmarks/bin/Release/net11.0/Arius.Benchmarks.dll"))
    args = parser.parse_args()
    if args.encrypted and args.codec != "upload":
        parser.error("--encrypted requires the upload scenario")
    command = (["compression-upload", args.workload, "encrypted" if args.encrypted else "plaintext"]
               if args.codec == "upload" else ["compression", args.codec, args.workload])
    result = subprocess.run(
        ["dotnet", str(args.assembly), *command],
        stdout=subprocess.PIPE, text=True, check=True,
    )
    measurement = json.loads(result.stdout)
    if os.name != "nt":
        import resource
        peak = resource.getrusage(resource.RUSAGE_CHILDREN).ru_maxrss
        measurement["PeakWorkingSetBytes"] = peak if sys.platform == "darwin" else peak * 1024
    if not measurement["PeakWorkingSetBytes"]:
        raise RuntimeError("The operating system did not report peak resident memory")
    print(json.dumps(measurement))


if __name__ == "__main__":
    main()
