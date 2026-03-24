# Arius

[![CI](https://github.com/woutervanranst/Arius7/actions/workflows/ci.yml/badge.svg)](https://github.com/woutervanranst/Arius7/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/woutervanranst/Arius7/graph/badge.svg)](https://codecov.io/gh/woutervanranst/Arius7)

Content-addressable archival to Azure Blob Storage. Deduplicated, optionally encrypted, with Archive-tier support.

## Installation

Download the binary for your platform from the
[latest release](https://github.com/woutervanranst/Arius7/releases/latest).

### Windows

```powershell
# Download arius-win-x64.exe and add its directory to your PATH
```

### Linux (& Synology NAS)

```bash
curl -Lo arius https://github.com/woutervanranst/Arius7/releases/latest/download/arius-linux-x64
chmod +x arius
sudo mv arius /usr/local/bin/
```

### macOS

```bash
curl -Lo arius https://github.com/woutervanranst/Arius7/releases/latest/download/arius-osx-arm64
chmod +x arius
sudo mv arius /usr/local/bin/
```

> **Note:** macOS may block the binary. Run `xattr -c /usr/local/bin/arius` to clear the quarantine flag.

## Usage

```
arius archive <path> -a <name> -k <key> -c <container> [options]
arius restore <path> -a <name> -k <key> -c <container> [options]
arius ls          -a <name> -k <key> -c <container> [options]
arius update
```

### Archive

```bash
arius archive ./photos \
  -a mystorageaccount \
  -c photos-backup \
  -t Archive \
  --remove-local
```

### Restore

```bash
arius restore ./photos \
  -a mystorageaccount \
  -c photos-backup
```

### List snapshots

```bash
arius ls \
  -a mystorageaccount \
  -c photos-backup
```

### Account key

Pass `-k` on the command line, set `ARIUS_KEY` environment variable, or store it in
[.NET user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets):

```bash
dotnet user-secrets set "arius:<account>:key" "<key>"
```

## Updating

Run:

```
arius update
```

This checks GitHub Releases for a newer version, downloads it, and replaces the binary in-place.

## License

[MIT](LICENSE)
