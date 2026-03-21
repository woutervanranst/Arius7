I want to make an archival tool, loosely inspired on Restic in C#.

Arius is specifically made for the Azure Blob Archive tier: cheap long term offline storage for the bulk of the binaries. However: archive tier blobs cannot be read without rehydration, which takes hours and costs money per transaction.

It is a content addressable backup system with file level deduplication, optional client side encryption (AES-256/openssl-compatible). Encryption is enabled by providing a `--passphrase`; without it, data is stored as plaintext.

I want to start with a solid foundation and three use cases.

# Use cases

## CLI Syntax

most CLI commands (since local system is stateless) will require:
- accountname
- accountkey
- passphrase (optional — omit for plaintext mode)
- container

## arius archive

CLI capability:
- archive a folder and all its content (subfolders and files). This is the 'local root' of the repository.

CLI switches:
--tier (default to Archive, but can be Hot/Cool/Cold as well)
--remove-local (delete local files after successful archive, only keep the pointer files)


File level deduplication: every file is hashed (SHA-256 seeded with the passphrase when provided, plain SHA-256 when not, to avoid hash collision attacks when encrypted)

Large files should be encrypted and then uploaded

Small files (use a configurable boundary eg. under 1 MB, `--small-file-threshold`) should be added to a TAR file and then GZIPped and then encrypted and then uploaded (because of the prohibitive cost of rehydrating small files from archive storage). The TAR is sealed when it reaches a configurable target size (`--tar-target-size`, default 64 MB). The chunk name for a tar bundle is the hash of the tar file itself.

To have local visibility on what files are in an archive (ie. when i search in file explorer) i want to have pointer files (<original filename>.pointer.arius) that just contain the content hash. it lives alongside the binary

Should store the RELATIVE path of files

The enumeration of the local file system should be graceful, eg. system protected folders. If a file cannot be read, it should be skipped with a warning and the process should continue.

## arius restore

CLI capabilities:
arius restore one file
arius restore multiple files
arius restore a directory and everything under it
arius restore a full snapshot

CLI switches:
-v specify the snapshot (v for version), optional. default to the latest version

Cost estimation

Chunks in Archive tier should not be rehydrated in place but in blob storage under `chunks-rehydrated`, then downloaded, decrypted, and reassembled locally

When an restore is requested, we should first check which files are already rehydrated and only rehydrate the missing ones. Then we can start downloading and restoring the already rehydrated ones while waiting for the others to be ready.
You can keep a local cache of downloaded chunks to avoid re-downloading if the same chunk is needed again (eg. a chunk with TAR small files)
After the full restore is done, the local cache and the `chunks-rehydrated` can be deleted

## arius ls

List all files in a snapshot

CLI capabilities:
- Filter on path/filename prefix (use case: to find all files in a directory) (optional)
- Filter on path/filename part (eg. find a filename in the whole snapshot) (optional)
-v to specify the snapshot (optional, default to latest)



# Domain concepts

## Local Concepts

- Binary File: another name for the original file that holds the actual content.
- Pointer File: `<filename>.pointer.arius` containing the hex content hash. Pointer files can be renamed/duplicated alongside their binaries and the snapshot will capture the new paths.

## Azure Blob concepts

- Chunk (actual content blob), stored in the storage container under `chunks/`. Content type is set to `application/aes256cbc+tar+gzip` (or `application/tar+gzip` without encryption) for a chunk with tarred small files and `application/aes256cbc+gzip` (or `application/gzip`) for a chunk with a single large file.
- File Tree Blob: an encrypted (when passphrase provided) JSON blob representing a single directory's listing (name, type, hash + metadata (size, created date, modified date) for each entry). Stored under `filetrees/<file-tree-hash>.enc`. Content-addressed: unchanged directories reuse the same tree blob across snapshots. Tree hashes are passphrase-seeded (when encrypted) to prevent structural leakage.
- Snapshot: a 'version' of the repository at a point in time. A small encrypted manifest stored under `snapshots/<UTC-timestamp>.enc` (e.g., `2026-03-21T140000.000Z`) containing the root tree hash and metadata (timestamp, file count, total size, Arius version). Snapshots are never deleted.
- Chunk Index: a set of shards under `chunk-index/<2-byte-prefix>/index.bin` mapping content-hash → chunk-hash for ALL content (both tar-bundled small files and large files). Stored as plaintext (contains only opaque hash-to-hash mappings). Used for existence checks during archive (especially when pointer files are missing) and for locating tar bundles during restore.
- Repository: the whole collection of snapshots, chunks, tree blobs, chunk index, and local cache.

# Non Functionals

It should be designed to be highly scalable. Think a 1 TB archive consisting of 2 KB files (500 million files). The design should be able to handle this scale without breaking a sweat.

Archive/restore should be paralallized/concurrent (use Channels): avoid going file by file, hashing one, then uploading one etc. this will take way too long and is not time efficient. Be careful with concurrency (eg. is a file already being uploaded by another thread, is a chunk already being rehydrated by another thread etc.)

It should run in a Docker container (on my Synology)

For `restore` or `ls`, the local file system should not know anything: all knowledge about the repository should live in blob storage. The local file system can be used as a cache but should be fully restoreable from blob storage.

For archive, the local file system is the source of truth. if a binary is hashed and the pointer file already exists but is out of sync, it should be updated

Worst case, files should be recoverable using open source tools (openssl, gzip, tar), eg. use the hash in the pointer file to locate the chunk, download it, decrypt it, and extract the file. So compatiblity is a must.

The chunks should be backwards compatible with a previous version of Arius (they are alreayd in archive storage, we can't break them). We will make a snapshot migration separately. As long as it uses openssl/tar/gzip it should be fine (`Salted__` prefix, PBKDF2 with SHA-256 and 10K iterations key derivation

Use streaming/IAsyncEnumerable where appropriate

Everything in azure blob storage should be encrypted when a passphrase is provided: the chunks, tree blobs, and snapshots. The chunk index is always plaintext (it only contains opaque hashes). When no passphrase is provided, everything is plaintext. 

All blob names should be opaque passphrase-seeded hashes when encrypted (no file names or directory structure leakage).

The local file system can be trusted: plaintext files can be stored

Archives should be operating system neutral (ie cross windows/linux) - take care of '/' and '\' in paths, reserved characters in filenames etc.'

# Testing

This is for my childhood backups so it should be properly tested.

Make unit tests for the critical parts

Otherwise, treat the system as black box: start from the Mediator command that archives or restores files, execute it and see whether a restore contains the correct data. Think through all the scenarios here: a file is updated in place (ie. same filename but different hash, both versions should be in the achive and depending on the restore command the correct version should be restored, ...)

think through the edge cases, eg what if the pointer file hash and the binary hash are out of sync

Use Azure Test Containers here as well as the option to use a real Azure Blob storage account (eg. a test account with a small budget).

# Architecture

As a design, use Arius.Core that uses Mediator (not MediatR) for easy future reuse between the CLI and the API.
Make an Arius.Cli project (using System.CommandLine). I m doubting between Spectre.Console and Terminal.Gui.
The Core should send streaming updates to the CLI as files go through the archive/restore phase (hashed, uploaded, downloaded, decrypted etc)
Arius.AzureBlob should contain all blob storage specific implementations as an abstraction. Core should not know anything about Azure Blob (imagine i want to add another backend later, eg. S3 or local filesystem)

There should be extensive logging throughout to follow the trace of every file through the archival/restore paths to troubleshoot.

In the future (out of scope for now), I ll want a File Explorer-alike web interface as a docker container that can browse repositories and perform the same actions as the CLI



# State Storage Design

The state lives in blob storage in Cool tier, using a Git-style content-addressed merkle tree:

- Each directory becomes an individual encrypted tree blob (one per directory, content-addressed)
- Snapshots are small manifests pointing to the root tree hash
- A chunk index (65,536 shards by 2-byte hash prefix) maps every content hash to its chunk hash, serving as a global registry of all archived content
- `ls` traverses the tree on demand (only downloads tree blobs for the requested path — fast for directory listings, slower for full-text search on cold cache)
- The chunk index shards are downloaded lazily to check existence (10 GB total at 500M scale, cached locally after first download)
- File Tree blobs are cached locally and valid indefinitely (content-addressed = immutable)
- Tree node metadata is extensible (versioned JSON format, currently: name, type, hash, size, created, modified)

Container layout:
```
chunks/              ← Archive tier (configurable)
chunks-rehydrated/   ← Hot tier, temporary
filetrees/               ← Cool tier
snapshots/           ← Cool tier
chunk-index/         ← Cool tier (always plaintext)
```