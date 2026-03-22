# Arius

Content-addressable archival to Azure Blob Storage. Deduplicated, optionally encrypted, with Archive-tier support.

## Installation

### Windows (installer)

Download and run `Arius-win-x64-Setup.exe` from the
[latest release](https://github.com/woutervanranst/Arius7/releases/latest).

Velopack handles installation and future updates. To update later:

```
arius update
```

### Windows / Linux (portable)

Download the zip for your platform from the
[latest release](https://github.com/woutervanranst/Arius7/releases/latest)
and extract it to a directory on your `PATH`.

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

If installed via the Windows installer, run:

```
arius update
```

This checks GitHub Releases for a newer version, downloads it, and restarts.

## License

[MIT](LICENSE)
