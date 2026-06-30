#!/bin/sh
# Arius is published as a single-file bundle that the runtime keeps memory-mapped
# while it executes. Replacing the on-disk binary in-process invalidates those
# mapped pages and crashes the running process (SIGBUS) on the next bundle read,
# so the swap is deferred to this helper, which waits for Arius to exit first.
#
# Arguments: <pid-to-wait> <source-path> <destination-path> <temp-dir>

pid="$1"
src="$2"
dest="$3"
tmp="$4"

# Wait for the Arius process to exit, bounded so we never hang forever.
i=0
while kill -0 "$pid" 2>/dev/null && [ "$i" -lt 600 ]; do
    sleep 0.1 2>/dev/null || sleep 1
    i=$((i + 1))
done

# Source and destination are siblings on the same filesystem, so this is an
# atomic rename over the (now exited) executable.
if mv -f "$src" "$dest"; then
    rm -rf "$tmp"
fi
