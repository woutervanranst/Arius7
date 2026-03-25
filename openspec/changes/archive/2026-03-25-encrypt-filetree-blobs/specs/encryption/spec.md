## ADDED Requirements

### Requirement: Filetree blob body encryption
When a passphrase is provided, filetree blob bodies SHALL be gzip-compressed and encrypted (AES-256-CBC) before upload to Azure Blob Storage, using the same `IEncryptionService.WrapForEncryption` pipeline as chunks, snapshots, and chunk index shards. Without a passphrase, filetree blob bodies SHALL be gzip-compressed only (no encryption). The local disk cache SHALL store filetree blobs in plaintext (no compression or encryption).

#### Scenario: Filetree encrypted when passphrase provided
- **WHEN** archiving with `--passphrase` and a filetree blob is uploaded to Azure
- **THEN** the blob body SHALL be gzip-compressed and AES-256-CBC encrypted (not plaintext)

#### Scenario: Filetree compressed but not encrypted without passphrase
- **WHEN** archiving without `--passphrase` and a filetree blob is uploaded to Azure
- **THEN** the blob body SHALL be gzip-compressed but not encrypted

#### Scenario: Filetree roundtrip through encryption
- **WHEN** a filetree blob is uploaded with a passphrase and then downloaded and deserialized with the same passphrase
- **THEN** the deserialized tree entries SHALL be identical to the original

#### Scenario: Filetree disk cache remains plaintext
- **WHEN** a filetree blob is written to the local disk cache at `~/.arius/{account}-{container}/filetrees/`
- **THEN** the cached file SHALL be plaintext UTF-8 text (no compression or encryption)

### Requirement: Worst-case recovery of filetree blobs
Encrypted filetree blobs SHALL be recoverable using only standard tools. For an encrypted filetree: download blob, pipe through `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase>`, pipe through `gunzip`, producing the plaintext tree blob text. For an unencrypted filetree: download blob, pipe through `gunzip`.

#### Scenario: Manual recovery of encrypted filetree
- **WHEN** the Arius software is unavailable and a user has the passphrase and tree hash
- **THEN** downloading `filetrees/<hash>` and running `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase> | gunzip` SHALL produce the plaintext tree blob text

#### Scenario: Manual recovery of unencrypted filetree
- **WHEN** the Arius software is unavailable and a user has the tree hash (no passphrase)
- **THEN** downloading `filetrees/<hash>` and running `gunzip` SHALL produce the plaintext tree blob text
