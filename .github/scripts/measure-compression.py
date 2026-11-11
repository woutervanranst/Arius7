#!/usr/bin/env python3
"""Run one codec comparison in a fresh process, including its native peak resident memory."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("codec", choices=("previous", "framework"))
    parser.add_argument("workload", choices=("text", "mixed", "random"))
    args = parser.parse_args()
    assembly = Path("src/Arius.Benchmarks/bin/Release/net11.0/Arius.Benchmarks.dll")
    result = subprocess.run(
        ["dotnet", str(assembly), "compression", args.codec, args.workload],
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
