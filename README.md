# Arius

Content-addressable archival to Azure Blob Storage. Deduplicated, optionally encrypted, with Archive-tier support.

## Installation

Download the archive for your platform from the
[latest release](https://github.com/woutervanranst/Arius7/releases/latest)
and extract it.

### Windows

```powershell
Expand-Archive arius-*-win-x64.zip -DestinationPath C:\Tools\arius
# Add C:\Tools\arius to your PATH
```

### Linux

```bash
tar xzf arius-*-linux-x64.tar.gz -C ~/.local/bin
chmod +x ~/.local/bin/Arius.Cli
```

### macOS

```bash
tar xzf arius-*-osx-arm64.tar.gz -C /usr/local/bin
chmod +x /usr/local/bin/Arius.Cli
```

> **Note:** macOS may block the binary the first time. Run `xattr -cr /usr/local/bin/Arius.Cli` to clear the quarantine flag.

### Docker

```bash
docker run --rm \
  -v /path/to/data:/data \
  -v arius-cache:/root/.arius/cache \
  ghcr.io/woutervanranst/arius7 \
  archive /data --account <name> --key <key> --container <container>
```

## Usage

```
arius archive <path> --account <name> --key <key> --container <container> [options]
arius restore <path> --account <name> --key <key> --container <container> [options]
arius ls          --account <name> --key <key> --container <container> [options]
arius update
```

### Archive

```bash
arius archive ./photos \
  --account mystorageaccount \
  --container photos-backup \
  --tier Archive \
  --remove-local
```

### Restore

```bash
arius restore ./photos \
  --account mystorageaccount \
  --container photos-backup
```

### List snapshots

```bash
arius ls \
  --account mystorageaccount \
  --container photos-backup
```

### Account key

Pass `--key` on the command line or store it in
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
