## ADDED Requirements

### Requirement: Azure Blob Storage backend
The system SHALL use Azure Blob Storage as the sole storage backend, accessed via the Azure.Storage.Blobs SDK.

#### Scenario: Repository connection
- **WHEN** user provides an Azure connection string or account URL with credentials
- **THEN** the system connects to the specified container and can perform all repository operations

### Requirement: Upload blobs
The system SHALL upload blobs to Azure with the correct tier assignment (Cold for metadata, Archive for data packs).

#### Scenario: Upload pack file
- **WHEN** a pack file is uploaded
- **THEN** it is stored in `data/{prefix}/{hash}` with Archive access tier

#### Scenario: Upload metadata
- **WHEN** a snapshot, index, tree, or key blob is uploaded
- **THEN** it is stored with Cold access tier

### Requirement: Download blobs
The system SHALL download blobs from Cold tier immediately. For Archive tier blobs, the system SHALL rehydrate first.

#### Scenario: Download cold-tier blob
- **WHEN** a cold-tier blob (snapshot, index, tree) is requested
- **THEN** it is downloaded immediately via streaming

#### Scenario: Download archive-tier blob without rehydration
- **WHEN** an archive-tier blob is requested that has not been rehydrated
- **THEN** the system reports the blob requires rehydration

### Requirement: Rehydration management
The system SHALL initiate rehydration of archive-tier blobs to a temporary hot-tier copy, poll rehydration status, and download once available.

#### Scenario: Initiate rehydration
- **WHEN** the system needs data from archive-tier packs
- **THEN** it calls the Azure Set Blob Tier API to rehydrate to Hot tier with the chosen priority (standard or high)

#### Scenario: Poll rehydration status
- **WHEN** rehydration has been initiated for a set of packs
- **THEN** the system can query the archive status of each blob to determine if rehydration is complete

#### Scenario: Standard priority rehydration
- **WHEN** standard priority rehydration is requested
- **THEN** rehydration completes within 15 hours

#### Scenario: High priority rehydration
- **WHEN** high priority rehydration is requested
- **THEN** rehydration completes within 1 hour

### Requirement: Blob lease locking
The system SHALL use Azure Blob Storage lease mechanism for concurrency control instead of file-based locks.

#### Scenario: Exclusive lock for prune
- **WHEN** a prune operation starts
- **THEN** the system acquires a 60-second lease on the lock blob, auto-renewed by a background task

#### Scenario: Lock contention
- **WHEN** a second operation attempts to acquire a lease that is already held
- **THEN** the system reports the repository is locked and identifies the lock holder if possible

#### Scenario: Crash recovery
- **WHEN** a process holding a lease crashes without releasing it
- **THEN** the lease auto-expires after the lease duration (60 seconds) and the repository becomes available

### Requirement: Blob listing
The system SHALL list blobs by prefix to discover repository contents (snapshots, index files, trees, packs).

#### Scenario: List snapshots
- **WHEN** the system needs to discover all snapshots
- **THEN** it lists blobs with prefix `snapshots/` and returns their names and metadata

### Requirement: Blob deletion
The system SHALL delete blobs from Azure for prune and forget operations.

#### Scenario: Delete pack file
- **WHEN** prune determines a pack is fully unreferenced
- **THEN** the pack blob is deleted from Azure

#### Scenario: Delete snapshot
- **WHEN** forget removes a snapshot
- **THEN** the snapshot blob is deleted from `snapshots/`
