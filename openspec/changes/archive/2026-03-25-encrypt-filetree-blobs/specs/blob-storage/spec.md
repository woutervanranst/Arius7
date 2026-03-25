## MODIFIED Requirements

### Requirement: Container layout
The blob storage SHALL organize blobs into the following virtual directories: `chunks/` (configurable tier), `chunks-rehydrated/` (Hot tier, temporary), `filetrees/` (Cool tier), `snapshots/` (Cool tier), `chunk-index/` (Cool tier).

Filetree blobs SHALL use content type `application/aes256cbc+gzip` when encrypted or `application/gzip` when not encrypted, matching the chunk index and snapshot content type convention.

#### Scenario: Chunk stored in correct path
- **WHEN** a large file with hash `abc123` is uploaded
- **THEN** the blob SHALL be at `chunks/abc123`

#### Scenario: Rehydrated chunk path
- **WHEN** a chunk is rehydrated for restore
- **THEN** the rehydrated copy SHALL be at `chunks-rehydrated/<chunk-hash>`

#### Scenario: Encrypted tree blob content type
- **WHEN** a tree blob is uploaded with a passphrase
- **THEN** the blob SHALL be at `filetrees/<hash>` with content type `application/aes256cbc+gzip`

#### Scenario: Plaintext tree blob content type
- **WHEN** a tree blob is uploaded without a passphrase
- **THEN** the blob SHALL be at `filetrees/<hash>` with content type `application/gzip`
