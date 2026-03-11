# Arius

Arius is a content-addressed, deduplicated backup tool for Azure Blob Storage.
Files are chunked with a rolling-hash chunker (GearHash), encrypted with AES-256-CBC, packed into compressed archives, and stored in Azure Blob Storage at any access tier (Hot / Cool / Cold / Archive).
Metadata (snapshots, index, trees, config) is always stored at the Cold tier; data packs default to Archive.

---

## Table of Contents

1. [Architecture overview](#architecture-overview)
2. [Installation](#installation)
3. [Configuration](#configuration)
4. [Commands](#commands)
   - [init](#init)
   - [backup](#backup)
   - [restore](#restore)
   - [snapshots](#snapshots)
   - [ls](#ls)
   - [find](#find)
   - [forget](#forget)
   - [prune](#prune)
   - [check](#check)
   - [diff](#diff)
   - [stats](#stats)
   - [tag](#tag)
   - [key](#key)
   - [repair](#repair)
   - [version](#version)
   - [cat](#cat)
5. [JSON output mode](#json-output-mode)
6. [Environment variables](#environment-variables)
7. [Local development secrets](#local-development-secrets)
8. [Access tiers and restore costs](#access-tiers-and-restore-costs)
9. [Manual recovery](#manual-recovery)

---

## Architecture overview

```
source files
     │  GearHash chunker (CDC)
     ▼
  chunks  ──HMAC-SHA256──▶  blob hashes
     │  PackerManager accumulates until pack-size threshold
     ▼
  pack  (TAR → gzip → AES-256-CBC)  ──SHA256──▶  pack ID
     │
     ▼
  Azure Blob Storage
  ├── data/packs/<packId>      (Archive by default)
  ├── metadata/index/<id>      (Cold)
  ├── metadata/snapshots/<id>  (Cold)
  ├── metadata/trees/<hash>    (Cold)
  ├── config                   (Cold)
  └── keys/<id>                (Cold)
```

Deduplication is content-addressed at the chunk level across all snapshots.
A local SQLite cache mirrors the index for fast lookups without Azure round-trips.

---

## Installation

```bash
dotnet build Arius.slnx -c Release
# Binary: src/Arius.Cli/bin/Release/net10.0/arius
```

Or run directly:

```bash
dotnet run --project src/Arius.Cli -- <command> [options]
```

---

## Configuration

Connection details can be supplied as command-line options or environment variables.
Environment variables take precedence only when the corresponding option is omitted.

| Environment variable | Equivalent option | Description |
|---|---|---|
| `ARIUS_REPOSITORY` | `--repo` | Azure Storage connection string |
| `ARIUS_CONTAINER` | `--container` | Blob container name |
| `ARIUS_PASSWORD` | *(passphrase)* | Repository passphrase (skips interactive prompt) |

If `ARIUS_PASSWORD` is not set and `--password-file` is not given, Arius prompts for the passphrase interactively (input hidden).

---

## Commands

### Global options

Every command accepts the following options in addition to its own:

| Option | Alias | Description |
|---|---|---|
| `--repo <string>` | `-r` | Azure Storage connection string |
| `--container <string>` | `-c` | Blob container name |
| `--password-file <path>` | | Read passphrase from file instead of prompting |
| `--json` | | Output results as JSON (where supported) |
| `--yes` | `-y` | Skip confirmation prompts |
| `--verbose` | `-v` | Verbose output |

---

### init

Initialize a new Arius repository in an Azure Blob Storage container.
Creates a `config` blob and the first key file.

```
arius init [options]
```

**Options**

| Option | Default | Description |
|---|---|---|
| `--repo`, `--container`, `--password-file` | | See global options |
| `--pack-size <bytes>` | `10485760` (10 MB) | Target pack file size in bytes |
| `--json` | | Output result as JSON |

**Example**

```bash
arius init \
  --repo "DefaultEndpointsProtocol=https;AccountName=..." \
  --container mybackups \
  --pack-size 104857600
```

Interactive passphrase prompt will appear. Supply `ARIUS_PASSWORD` or `--password-file` to skip it.

**Output**

```
Repository initialized!
  Repo ID    : 3f9a1c82-...
  Config blob: config
  Key blob   : keys/default
```

---

### backup

Create a new snapshot by backing up one or more files or directories.

```
arius backup [options] <paths...>
```

**Arguments**

| Argument | Required | Description |
|---|---|---|
| `<paths...>` | Yes | One or more source paths (files or directories) |

**Options**

| Option | Default | Description |
|---|---|---|
| `--repo`, `--container`, `--password-file` | | See global options |
| `--tier <tier>` | `archive` | Azure access tier for data packs: `hot`, `cool`, `cold`, `archive` |
| `--json` | | Output each event as a JSON line |

**Examples**

```bash
# Back up a directory to Archive tier (default)
arius backup --repo "$CONN" --container mybackups /home/user/documents

# Back up multiple paths
arius backup -r "$CONN" -c mybackups /etc /home /var/log

# Back up with Hot tier (instant restore, higher storage cost)
arius backup -r "$CONN" -c mybackups --tier hot /data/critical

# JSON output (one event per line)
arius backup -r "$CONN" -c mybackups --json /data | jq .
```

**Progress output**

A live progress bar shows files processed / total, current file name, and a spinner.
Final line:

```
Backup complete! 1 234 files processed, 890 deduplicated.
```

**Tier guidance**

| Tier | Storage cost | Restore latency | Retrieval cost |
|---|---|---|---|
| `archive` | Lowest | 1–15 hours rehydration | Yes |
| `cold` | Low | Minutes | Yes |
| `cool` | Medium | Immediate | Small |
| `hot` | Highest | Immediate | None |

---

### restore

Restore files from a snapshot to a local directory.

```
arius restore [options] <snapshot-id>
```

**Arguments**

| Argument | Required | Description |
|---|---|---|
| `<snapshot-id>` | Yes | Full or prefix snapshot ID |

**Options**

| Option | Default | Description |
|---|---|---|
| `--repo`, `--container`, `--password-file` | | See global options |
| `--target <path>` | | **Required.** Destination directory |
| `--include <substring>` | | Only restore paths containing this substring |
| `--yes` | | Skip the pre-restore plan display |
| `--json` | | Output each event as a JSON line |

**Examples**

```bash
# Restore entire snapshot
arius restore -r "$CONN" -c mybackups --target /mnt/restore abc12345

# Restore only .pdf files
arius restore -r "$CONN" -c mybackups --target /tmp/pdfs --include .pdf abc12345

# Non-interactive restore in a script
arius restore -r "$CONN" -c mybackups --target /restore --yes latest
```

> If data packs are at the Archive or Cold tier, restore will block until Azure completes
> rehydration. Use `--tier hot` during backup to avoid this for time-sensitive data.

**Progress output**

```
Plan: 452 file(s), 1.2 GB
Restoring... 120 file(s) — documents/report.pdf
Done: 452 file(s) restored (1.2 GB)
Restore complete! 452 files restored to /mnt/restore
```

---

### snapshots

List all snapshots in the repository.

```
arius snapshots [options]
```

**Options**

| Option | Description |
|---|---|
| `--repo`, `--container`, `--password-file` | See global options |
| `--json` | Output as JSON array |

**Example**

```bash
arius snapshots -r "$CONN" -c mybackups
```

**Output**

```
 ID         Time                  Paths              Host    Tags
─────────────────────────────────────────────────────────────────
 3f9a1c82   2026-03-10 14:22:01   /home/user/docs    host1
 a1b2c3d4   2026-03-09 08:00:00   /etc, /home        host1   weekly
```

---

### ls

List files and directories inside a snapshot.

```
arius ls [options] <snapshot-id> [path]
```

**Arguments**

| Argument | Required | Default | Description |
|---|---|---|---|
| `<snapshot-id>` | Yes | | Full or prefix snapshot ID |
| `[path]` | No | `/` | Sub-path within the snapshot to list |

**Options**

| Option | Default | Description |
|---|---|---|
| `--repo`, `--container`, `--password-file` | | See global options |
| `--recursive`, `-R` | `false` | Recursively list all entries under path |
| `--json` | | Output as JSON array |

**Examples**

```bash
# List root of a snapshot
arius ls -r "$CONN" -c mybackups 3f9a1c82

# List a subdirectory
arius ls -r "$CONN" -c mybackups 3f9a1c82 /home/user/documents

# List all files recursively
arius ls -r "$CONN" -c mybackups --recursive 3f9a1c82
```

**Output**

```
 Mode        Type   Size       Modified          Name
──────────────────────────────────────────────────────
 drwxr-xr-x  dir              2026-03-10 14:20  documents
 -rw-r--r--  file   124 357    2026-03-09 11:05  notes.md
 lrwxrwxrwx  lnk              2026-03-08 09:00  link -> target
```

---

### find

Search for files by name pattern across all snapshots (or a specific one).

```
arius find [options] <pattern>
```

**Arguments**

| Argument | Required | Description |
|---|---|---|
| `<pattern>` | Yes | Glob pattern, e.g. `*.pdf`, `report_202?_*.xlsx` |

Pattern supports `*` (any characters) and `?` (single character). Matching is case-insensitive.

**Options**

| Option | Default | Description |
|---|---|---|
| `--repo`, `--container`, `--password-file` | | See global options |
| `--snapshot`, `-s` | | Limit search to a single snapshot ID |
| `--path` | | Limit search to a path prefix |
| `--json` | | Output as JSON array |

**Examples**

```bash
# Find all PDFs across all snapshots
arius find -r "$CONN" -c mybackups "*.pdf"

# Find in a specific snapshot only
arius find -r "$CONN" -c mybackups --snapshot 3f9a1c82 "report_*.xlsx"

# Find within a path prefix
arius find -r "$CONN" -c mybackups --path /home/user "*.conf"
```

**Output**

```
 Snapshot   Path                          Type   Size
──────────────────────────────────────────────────────────
 3f9a1c82   /home/user/docs/report.pdf   file   245 120
 a1b2c3d4   /home/user/docs/report.pdf   file   240 000
```

---

### forget

Remove snapshots according to a retention policy.
Always performs a dry-run preview first, then prompts for confirmation.

```
arius forget [options]
```

**Options**

| Option | Default | Description |
|---|---|---|
| `--repo`, `--container`, `--password-file` | | See global options |
| `--keep-last <N>` | | Keep the N most recent snapshots |
| `--keep-hourly <N>` | | Keep one snapshot per clock-hour, for N hours |
| `--keep-daily <N>` | | Keep one snapshot per calendar day, for N days |
| `--keep-weekly <N>` | | Keep one snapshot per ISO week, for N weeks |
| `--keep-monthly <N>` | | Keep one snapshot per calendar month, for N months |
| `--keep-yearly <N>` | | Keep one snapshot per calendar year, for N years |
| `--keep-within <duration>` | | Keep all snapshots within a duration: `30d`, `4w`, `12h`, `6m`, `1y` |
| `--keep-tag <tag>` | | Always keep snapshots with this tag (repeatable) |
| `--dry-run` | `false` | Preview only — no changes made |
| `--yes`, `-y` | | Skip confirmation prompt after preview |

Multiple `--keep-*` options can be combined; a snapshot is retained if it satisfies **any** rule.

**Examples**

```bash
# Keep the last 7 daily and 4 weekly snapshots
arius forget -r "$CONN" -c mybackups --keep-daily 7 --keep-weekly 4

# Keep everything from the past 30 days, plus the last 10
arius forget -r "$CONN" -c mybackups --keep-within 30d --keep-last 10

# Always keep snapshots tagged "important"
arius forget -r "$CONN" -c mybackups --keep-daily 7 --keep-tag important

# Preview only
arius forget -r "$CONN" -c mybackups --keep-daily 7 --dry-run

# Non-interactive (CI)
arius forget -r "$CONN" -c mybackups --keep-last 30 --yes
```

**Output (preview)**

```
keep    3f9a1c82  2026-03-10 14:22  (last)
keep    a1b2c3d4  2026-03-09 08:00  (daily)
remove  b5e6f7a8  2026-03-01 12:00  (not in policy)
```

Followed by a confirmation prompt (skipped with `--yes` or `--dry-run`).

> `forget` only removes snapshot metadata. To free storage space, run `prune` afterwards.

---

### prune

Remove unreferenced data packs left over after `forget`.
Always previews what would be deleted, then prompts for confirmation.

```
arius prune [options]
```

**Options**

| Option | Default | Description |
|---|---|---|
| `--repo`, `--container`, `--password-file` | | See global options |
| `--dry-run` | `false` | Preview only — no changes made |
| `--yes`, `-y` | | Skip confirmation prompt |

**Examples**

```bash
# Preview what prune would delete
arius prune -r "$CONN" -c mybackups --dry-run

# Run prune non-interactively
arius prune -r "$CONN" -c mybackups --yes
```

**Typical workflow**

```bash
# 1. Forget snapshots outside the retention window
arius forget -r "$CONN" -c mybackups --keep-daily 7 --yes

# 2. Delete the now-unreferenced packs
arius prune -r "$CONN" -c mybackups --yes
```

**Output (preview)**

```
delete  pack 3f9a1c82  (fully unreferenced)
repack  pack a1b2c3d4  (partially referenced — live blobs will be preserved)
```

> Repacking archive-tier packs requires rehydration first. The current version emits a
> warning and skips the repack in that case. Rehydrate the pack manually (via the Azure
> portal or `az storage blob set-tier`) and re-run `prune`.

---

### check

Verify repository metadata integrity.
Optionally also verifies data pack contents (requires rehydration of Archive-tier packs).

```
arius check [options]
```

**Options**

| Option | Default | Description |
|---|---|---|
| `--repo`, `--container`, `--password-file` | | See global options |
| `--read-data` | `false` | Also verify data pack contents (warns about rehydration cost) |

**Examples**

```bash
# Metadata-only check (fast, no rehydration)
arius check -r "$CONN" -c mybackups

# Full data integrity check (slow, may trigger rehydration)
arius check -r "$CONN" -c mybackups --read-data
```

**Output**

Messages are colour-coded: errors in red, warnings in yellow, info in grey.

```
[INFO]  Snapshots: 12 OK
[INFO]  Index entries: 45 231 OK
[WARN]  Pack a1b2c3d4 is at Archive tier — skipped (use --read-data)
Check passed.
```

---

### diff

Show differences between two snapshots.

```
arius diff [options] <snapshot1> <snapshot2>
```

**Arguments**

| Argument | Required | Description |
|---|---|---|
| `<snapshot1>` | Yes | ID (or prefix) of the first snapshot |
| `<snapshot2>` | Yes | ID (or prefix) of the second snapshot |

**Options**

| Option | Description |
|---|---|
| `--repo`, `--container`, `--password-file` | See global options |

**Example**

```bash
arius diff -r "$CONN" -c mybackups 3f9a1c82 a1b2c3d4
```

**Output**

```
+ /home/user/newfile.txt           (added)
- /home/user/oldfile.txt           (removed)
M /home/user/documents/report.pdf  (modified)
T /home/user/link                  (type changed)
```

If there are no differences:

```
No differences.
```

---

### stats

Display repository statistics.

```
arius stats [options]
```

**Options**

| Option | Description |
|---|---|
| `--repo`, `--container`, `--password-file` | See global options |
| `--json` | Output as JSON |

**Example**

```bash
arius stats -r "$CONN" -c mybackups
```

**Output**

```
┌─────────────────────────────────────┐
│     Repository Statistics           │
├─────────────────────┬───────────────┤
│ Snapshots           │ 12            │
│ Packs               │ 340           │
│ Total stored        │ 48.2 GB       │
│ Unique blobs        │ 92 450        │
│ Unique data         │ 31.7 GB       │
│ Dedup ratio         │ 1.52x         │
└─────────────────────┴───────────────┘
```

---

### tag

Add, remove, or replace tags on a snapshot.

```
arius tag [options] <snapshot-id>
```

**Arguments**

| Argument | Required | Description |
|---|---|---|
| `<snapshot-id>` | Yes | Full or prefix snapshot ID |

**Options** — exactly one of `--set`, `--add`, or `--remove` is required.

| Option | Description |
|---|---|
| `--repo`, `--container`, `--password-file` | See global options |
| `--set <tag>...` | Replace all existing tags with the supplied list |
| `--add <tag>...` | Add one or more tags (keeps existing tags) |
| `--remove <tag>...` | Remove one or more tags |

**Examples**

```bash
# Set tags (replaces all existing tags)
arius tag -r "$CONN" -c mybackups --set weekly important 3f9a1c82

# Add a tag without removing others
arius tag -r "$CONN" -c mybackups --add verified 3f9a1c82

# Remove a specific tag
arius tag -r "$CONN" -c mybackups --remove weekly 3f9a1c82
```

**Output**

```
Tags updated.  Tags: important, verified
```

---

### key

Manage repository keys (passphrases).
A repository can hold multiple keys; any of them can unlock it.

```
arius key <subcommand> [options]
```

#### key list

List all key IDs stored in the repository.

```
arius key list [options]
```

#### key add

Add a new key with a different passphrase.

```
arius key add [options]
```

| Option | Description |
|---|---|
| `--repo`, `--container`, `--password-file` | See global options (existing passphrase) |
| `--new-password <pass>` | New passphrase for the key being added |

#### key remove

Remove a key by ID. The last key cannot be removed.

```
arius key remove [options]
```

| Option | Description |
|---|---|
| `--repo`, `--container`, `--password-file` | See global options (existing passphrase) |
| `--key-id <id>` | ID of the key to remove |

#### key passwd

Change the passphrase of an existing key.

```
arius key passwd [options]
```

| Option | Default | Description |
|---|---|---|
| `--repo`, `--container`, `--password-file` | | See global options (current passphrase) |
| `--new-password <pass>` | | New passphrase |
| `--key-id <id>` | `"default"` | ID of the key to change |

**Examples**

```bash
# List keys
arius key list -r "$CONN" -c mybackups

# Add a recovery key
arius key add -r "$CONN" -c mybackups --new-password "recovery-passphrase"

# Rotate the default key's passphrase
arius key passwd -r "$CONN" -c mybackups --new-password "new-secret"

# Remove a specific key
arius key remove -r "$CONN" -c mybackups --key-id a1b2c3d4
```

---

### repair

Repair corrupted or inconsistent repository metadata.

```
arius repair <subcommand> [options]
```

#### repair index

Rebuild the index by scanning existing pack headers.

```
arius repair index [options]
```

#### repair snapshots

Find and remove snapshot documents whose tree blobs are missing or unreadable.

```
arius repair snapshots [options]
```

**Options (both subcommands)**

| Option | Description |
|---|---|
| `--repo`, `--container`, `--password-file` | See global options |

**Examples**

```bash
# Rebuild the index
arius repair index -r "$CONN" -c mybackups

# Remove broken snapshot documents
arius repair snapshots -r "$CONN" -c mybackups
```

---

### version

Print the Arius version.

```
arius version
```

---

### cat

Print raw internal repository objects as indented JSON.
Intended for diagnostics and manual recovery.

```
arius cat <subcommand> [options]
```

#### cat config

Print the repository config blob.

```
arius cat config [options]
```

#### cat snapshot

Print a full snapshot document.

```
arius cat snapshot [options] <snapshot-id>
```

#### cat tree

Print the nodes of a tree blob by its hash.

```
arius cat tree [options] <hash>
```

**Options (all subcommands)**

| Option | Description |
|---|---|
| `--repo`, `--container`, `--password-file` | See global options |

**Examples**

```bash
# Print the config
arius cat config -r "$CONN" -c mybackups

# Print a snapshot document
arius cat snapshot -r "$CONN" -c mybackups 3f9a1c82

# Print a tree by its hash
arius cat tree -r "$CONN" -c mybackups e3b0c44298fc...
```

---

## JSON output mode

Commands that support `--json` serialise every result object as JSON.
For streaming commands (backup, restore, ls, find, snapshots, diff), each event or item
is emitted as a **single JSON line** — suitable for `jq` piping.

```bash
# Stream backup events and filter with jq
arius backup -r "$CONN" -c mybackups --json /data \
  | jq 'select(."$type" == "BackupCompleted")'

# List snapshots as JSON
arius snapshots -r "$CONN" -c mybackups --json | jq '.[].id'
```

---

## Environment variables

| Variable | Description |
|---|---|
| `ARIUS_REPOSITORY` | Azure Storage connection string (replaces `--repo`) |
| `ARIUS_CONTAINER` | Blob container name (replaces `--container`) |
| `ARIUS_PASSWORD` | Repository passphrase (skips interactive prompt) |

Setting all three allows fully non-interactive operation in scripts and CI:

```bash
export ARIUS_REPOSITORY="DefaultEndpointsProtocol=https;AccountName=myaccount;AccountKey=..."
export ARIUS_CONTAINER="backups"
export ARIUS_PASSWORD="my-passphrase"

arius backup /data
arius snapshots
```

---

## Local development secrets

Storing connection strings and passwords in environment variables works fine in CI and
production, but is cumbersome during local development. Both `Arius.Cli` and `Arius.Api`
support the [.NET User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets)
mechanism as an additional configuration source so credentials never have to be committed
to source control or set globally in the shell.

User secrets are stored in a per-user, per-project JSON file outside the repository tree
and are never included in a build output or container image.

### How it works

Both projects already have a `UserSecretsId` configured in their `.csproj` files.
The lookup priority for every credential key is:

```
environment variable  (highest — always wins)
  ↓
.NET user secrets     (Development only)
  ↓
--password-file / interactive prompt
```

### CLI — set secrets

```bash
cd src/Arius.Cli

dotnet user-secrets set ARIUS_REPOSITORY "DefaultEndpointsProtocol=https;AccountName=myaccount;AccountKey=..."
dotnet user-secrets set ARIUS_CONTAINER  "mybackups"
dotnet user-secrets set ARIUS_PASSWORD   "my-passphrase"
```

After that you can run any command without supplying credentials:

```bash
dotnet run --project src/Arius.Cli -- snapshots
dotnet run --project src/Arius.Cli -- backup /home/user/documents
```

### API — set secrets

```bash
cd src/Arius.Api

dotnet user-secrets set ARIUS_REPOSITORY "DefaultEndpointsProtocol=https;AccountName=myaccount;AccountKey=..."
dotnet user-secrets set ARIUS_CONTAINER  "mybackups"
dotnet user-secrets set ARIUS_PASSWORD   "my-passphrase"
```

Then start the API without any environment variables:

```bash
dotnet run --project src/Arius.Api
```

### Manage secrets

```bash
# List all secrets for a project
dotnet user-secrets list --project src/Arius.Cli

# Remove a specific secret
dotnet user-secrets remove ARIUS_PASSWORD --project src/Arius.Cli

# Clear all secrets
dotnet user-secrets clear --project src/Arius.Cli
```

> User secrets are stored in:
> - **Windows:** `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json`
> - **macOS / Linux:** `~/.microsoft/usersecrets/<UserSecretsId>/secrets.json`

---

## Access tiers and restore costs

Azure Blob Storage access tiers affect storage cost and restore latency:

| Tier | Monthly cost | Restore latency | Early-deletion minimum |
|---|---|---|---|
| Hot | High | Immediate | None |
| Cool | Medium | Immediate | 30 days |
| Cold | Lower | Immediate | 90 days |
| Archive | Lowest | 1–15 hours | 180 days |

Arius stores all **metadata** at Cold tier. **Data packs** default to Archive; use `--tier` on backup to override.

When restoring from Archive-tier packs, Arius submits a rehydration request and waits.
Standard rehydration takes up to 15 hours; High-priority rehydration (not yet a CLI flag)
takes under 1 hour but costs more.

To avoid rehydration latency on critical data, back it up with `--tier cold` or `--tier hot`.

---

## Manual recovery

Every pack file is a standard pipeline: `AES-256-CBC (OpenSSL format) → gunzip → tar`.
No custom tooling is required to recover data if Arius itself is unavailable.

1. **Get the master key** from the key file:

   ```bash
   # Read the key file JSON
   arius cat config -r "$CONN" -c mybackups   # find the key blob name

   # Or fetch raw from Azure:
   az storage blob download \
     --connection-string "$ARIUS_REPOSITORY" \
     --container-name "$ARIUS_CONTAINER" \
     --name "keys/default" \
     --file key.json

   # Decrypt the master key with openssl
   jq -r '.encryptedKey' key.json | base64 -d > encrypted_key.bin
   jq -r '.salt' key.json | base64 -d > salt.bin
   ITERATIONS=$(jq '.iterations' key.json)

   openssl enc -d -aes-256-cbc -pbkdf2 -iter "$ITERATIONS" \
     -pass "pass:YOUR_PASSPHRASE" \
     -S "$(xxd -p salt.bin)" \
     -in encrypted_key.bin -out master_key.bin
   ```

2. **Download a pack file**:

   ```bash
   az storage blob download \
     --connection-string "$ARIUS_REPOSITORY" \
     --container-name "$ARIUS_CONTAINER" \
     --name "data/packs/<packId>" \
     --file pack.enc
   ```

3. **Decrypt, decompress, and extract**:

   ```bash
   MASTER_KEY_HEX=$(xxd -p -c 256 master_key.bin)

   openssl enc -d -aes-256-cbc \
     -K "$MASTER_KEY_HEX" \
     -iv <first-16-bytes-of-pack-as-hex> \
     -in pack.enc \
   | gunzip \
   | tar -x -f - -C ./recovered/
   ```

   Each blob is stored in the TAR as a file named by its HMAC-SHA256 hash.
   A `manifest.json` inside the TAR maps hashes back to their original metadata.

4. **Identify files from the index**:

   ```bash
   arius cat snapshot -r "$CONN" -c mybackups <snapshot-id>
   arius cat tree -r "$CONN" -c mybackups <tree-hash>
   ```

   These commands output the tree structure showing which blob hashes correspond to which file paths.
