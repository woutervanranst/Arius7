## ADDED Requirements

### Requirement: Minimal API endpoints
The API SHALL expose RESTful endpoints using ASP.NET Core Minimal APIs for all repository operations: list snapshots, browse trees, initiate backup, initiate restore, forget, prune, check, stats.

#### Scenario: List snapshots endpoint
- **WHEN** a GET request is made to `/api/snapshots`
- **THEN** the API returns a JSON array of snapshot metadata

#### Scenario: Browse tree endpoint
- **WHEN** a GET request is made to `/api/snapshots/{id}/tree?path=/documents/`
- **THEN** the API returns the directory listing for that path in the snapshot

#### Scenario: Start backup endpoint
- **WHEN** a POST request is made to `/api/backup` with path and options
- **THEN** the API starts a backup operation and returns an operation ID for progress tracking

### Requirement: SignalR hub for streaming
The API SHALL expose a SignalR hub for real-time streaming of operation progress to connected web clients.

#### Scenario: Backup progress via SignalR
- **WHEN** a backup is in progress and a web client is connected to the SignalR hub
- **THEN** the client receives `BackupEvent` messages in real-time

#### Scenario: Restore progress via SignalR
- **WHEN** a restore is in progress
- **THEN** connected clients receive `RestoreEvent` messages including rehydration status and file restoration progress

### Requirement: IAsyncEnumerable streaming for list endpoints
Endpoints that return collections (snapshots, tree entries, find results) SHALL stream responses using `IAsyncEnumerable<T>` for progressive rendering.

#### Scenario: Large directory listing
- **WHEN** a tree endpoint returns 100,000 entries
- **THEN** the response is streamed as a JSON array, with entries serialized incrementally

### Requirement: Mediator dispatch
All API endpoints SHALL construct request objects and dispatch them through the Mediator, using the same handlers as the CLI.

#### Scenario: Shared handler
- **WHEN** the API processes a `GET /api/snapshots` request
- **THEN** it creates a `ListSnapshotsRequest`, dispatches via Mediator, and receives the same `IAsyncEnumerable<Snapshot>` stream as the CLI would

### Requirement: Operation cancellation
The API SHALL support cancelling long-running operations via SignalR messages or a DELETE endpoint.

#### Scenario: Cancel restore
- **WHEN** a client sends a cancel message via SignalR for a running restore operation
- **THEN** the operation's `CancellationToken` is triggered and the operation terminates gracefully

### Requirement: Repository configuration API
The API SHALL allow configuring which Azure repository to connect to, either via environment variables or a configuration endpoint.

#### Scenario: Configure repository
- **WHEN** the API starts with `ARIUS_REPOSITORY` and `ARIUS_PASSWORD` environment variables
- **THEN** it connects to the specified repository on startup

### Requirement: Docker packaging
The API and web UI SHALL be packaged together in a single Docker container with multi-stage build.

#### Scenario: Docker run
- **WHEN** user runs `docker run -e ARIUS_REPOSITORY=... -e ARIUS_PASSWORD=... -p 8080:8080 arius`
- **THEN** the container serves the web UI at port 8080 with the API backend connected to the specified repository
