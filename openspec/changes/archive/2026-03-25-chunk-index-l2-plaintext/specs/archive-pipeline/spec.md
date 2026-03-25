## MODIFIED Requirements

### Requirement: Index shard merge and upload
The system SHALL collect all new index entries (content-hash → chunk-hash, original-size, compressed-size) and upload updated chunk index shards once at the end of the archive run. For each modified shard prefix, the system SHALL download the existing shard (if cached or from Azure), merge new entries, and upload.

The L3 wire format (Azure blobs) SHALL be: plaintext lines → gzip compressed → optionally AES-256-CBC encrypted. Content type SHALL be `application/aes256cbc+gzip` (encrypted) or `application/gzip` (plaintext).

The L2 local disk cache SHALL store shards as **plaintext lines only** — no gzip compression, no encryption. This makes the local cache human-readable and avoids unnecessary CPU overhead on every cache read/write. The L2 format SHALL NOT change based on whether a passphrase is provided.

The shard entry format SHALL be: `<content-hash> <chunk-hash> <original-size> <compressed-size>\n`.

#### Scenario: New entries merged into existing shard
- **WHEN** 50 new files have content hashes with prefix `a1`
- **THEN** the system SHALL download/load the `a1` shard, append 50 entries, and upload the merged shard to Azure in gzip+encrypt format

#### Scenario: First archive (no existing shards)
- **WHEN** archiving to an empty repository
- **THEN** the system SHALL create new shards for each prefix that has entries

#### Scenario: L2 cache stores plaintext
- **WHEN** a shard is saved to the local L2 disk cache
- **THEN** the file SHALL contain raw plaintext lines with no compression or encryption, regardless of whether a passphrase is configured

#### Scenario: L3 upload uses wire format
- **WHEN** a shard is uploaded to Azure
- **THEN** the blob SHALL be gzip-compressed and AES-256-CBC encrypted if a passphrase is provided, or gzip-compressed only if no passphrase is provided

#### Scenario: Stale L2 file is self-healing
- **WHEN** an L2 cache file cannot be parsed as plaintext lines (e.g., it contains old encrypted bytes from a prior version)
- **THEN** the system SHALL treat it as a cache miss, fall through to L3, and re-cache the shard in plaintext format
