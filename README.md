# Arius

Content-addressable archival to Azure Blob Storage. Deduplicated, optionally encrypted, with Archive-tier support.

## Installation

### Windows

Download and run **`Arius-win-x64-Setup.exe`** from the
[latest release](https://github.com/woutervanranst/Arius7/releases/latest).
This installs Arius and enables auto-updating via `arius update`.

### Linux

Download **`Arius-linux-x64.AppImage`** from the
[latest release](https://github.com/woutervanranst/Arius7/releases/latest),
make it executable, and run it:

```bash
chmod +x Arius-linux-x64.AppImage
./Arius-linux-x64.AppImage archive ./photos --account myaccount --container mycontainer
```

Or move it to a directory on your `PATH`:

```bash
mv Arius-linux-x64.AppImage /usr/local/bin/arius
```

### macOS

Download **`Arius-osx-arm64-Setup.pkg`** or **`Arius-osx-arm64-Portable.zip`** from the
[latest release](https://github.com/woutervanranst/Arius7/releases/latest).

Since the binaries are not Apple-notarized, macOS Gatekeeper will block them.
To work around this:

- **Setup.pkg**: Right-click → **Open**, or go to System Settings → Privacy & Security → **Open Anyway**.
- **Portable (.app)**: Remove the quarantine attribute after extracting:
  ```bash
  xattr -cr Arius.app
  ```

### Portable (all platforms)

Every release also includes a portable zip per platform
(`Arius-win-x64-Portable.zip`, `Arius-osx-arm64-Portable.zip`).
Linux uses AppImage as its portable format.

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
