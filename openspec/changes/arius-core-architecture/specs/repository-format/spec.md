## ADDED Requirements

### Requirement: Repository initialization
The system SHALL create a new repository in an Azure Blob Storage container with the standard directory layout: `config`, `keys/`, `snapshots/`, `index/`, `trees/`, `data/`.

#### Scenario: Initialize new repository
- **WHEN** user runs `init` against an empty Azure Blob container with a passphrase
- **THEN** the system creates an encrypted `config` blob containing repo ID, version, and chunker parameters, and a key file in `keys/` containing the master key encrypted with the passphrase-derived key

#### Scenario: Initialize already-initialized container
- **WHEN** user runs `init` against a container that already has a `config` blob
- **THEN** the system SHALL reject the operation with an error message

### Requirement: Repository config
The `config` blob SHALL be an encrypted JSON document containing: repository ID (UUID), format version, gear hash seed (uint64), and default pack size.

#### Scenario: Config content
- **WHEN** a repository is initialized
- **THEN** the `config` blob contains a randomly generated repo ID, format version `1`, a randomly generated gear hash seed, and the configured pack size (default 10 MB)

### Requirement: Content-addressable storage
All blobs (packs, trees, index files, snapshots) SHALL be named by the SHA-256 hash of their content (after encryption where applicable).

#### Scenario: Pack file naming
- **WHEN** an encrypted pack file is uploaded
- **THEN** its blob name in Azure is the SHA-256 hex digest of the encrypted content

### Requirement: Storage tiering
The system SHALL store metadata blobs (`config`, `keys/`, `snapshots/`, `index/`, `trees/`) in Cold tier and data packs (`data/`) in Archive tier.

#### Scenario: Metadata in cold tier
- **WHEN** a snapshot, index, tree, or key blob is uploaded
- **THEN** its access tier is set to Cold

#### Scenario: Data in archive tier
- **WHEN** a pack file is uploaded to `data/`
- **THEN** its access tier is set to Archive

### Requirement: Pack file format
Pack files SHALL be TAR archives compressed with gzip and encrypted with AES-256-CBC. Each TAR archive SHALL contain blob files named by their SHA-256 hash (with `.bin` extension) and a `manifest.json` file listing all contained blobs with their hash, type, and size.

#### Scenario: Pack structure
- **WHEN** a pack file is created with N blobs
- **THEN** the file is a TAR archive containing `{hash1}.bin, {hash2}.bin, ..., {hashN}.bin, manifest.json`, compressed with gzip, then encrypted with AES-256-CBC in OpenSSL-compatible format

#### Scenario: Manual extractability
- **WHEN** a pack file is downloaded from Azure and the master key is known
- **THEN** it can be decrypted with `openssl enc -d ...`, decompressed with `gunzip`, and extracted with `tar x` to recover the individual blobs

### Requirement: Configurable pack size
The default pack size SHALL be 10 MB. Users SHALL be able to override this at repository initialization or per-command with `--pack-size`.

#### Scenario: Custom pack size
- **WHEN** user initializes a repo with `--pack-size 64m`
- **THEN** the pack size is stored in `config` and used for all subsequent backup operations

#### Scenario: Default pack size
- **WHEN** user initializes a repo without specifying pack size
- **THEN** the default pack size of 10 MB is used

### Requirement: Data prefix directories
Pack files in `data/` SHALL be organized into subdirectories based on the first two hex characters of the pack hash (256 subdirectories).

#### Scenario: Pack storage path
- **WHEN** a pack with hash `a3f2...` is uploaded
- **THEN** it is stored at `data/a3/a3f2...`
