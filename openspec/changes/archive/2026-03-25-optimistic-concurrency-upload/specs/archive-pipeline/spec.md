## MODIFIED Requirements

### Requirement: Crash-recoverable archive (replaces existing)
The archive pipeline SHALL use optimistic concurrency for all chunk uploads: uploads are attempted unconditionally without a pre-flight HEAD check.

`OpenWriteAsync` and `UploadAsync(overwrite:false)` use create-if-not-exists semantics (IfNoneMatch=*). If the blob already exists, `BlobAlreadyExistsException` is raised.

On catching `BlobAlreadyExistsException`, the pipeline SHALL perform a HEAD check (GetMetadataAsync) to determine blob completeness using the `arius-type` metadata sentinel:
- `arius-type` present → blob is fully committed (body + metadata); recover ContentLength as compressedSize and continue without re-uploading
- `arius-type` absent → blob body was committed but metadata was not yet written (partial state); delete the blob and retry the upload from scratch (goto retry)

This pattern applies to all three upload sub-stages: large file upload (Stage 4a), tar blob upload (Stage 4c-tar), and thin chunk creation (Stage 4c-thin).

Scenarios:
- WHEN a crash-recovery re-run encounters a fully committed blob (BlobAlreadyExistsException + arius-type present) THEN the pipeline SHALL recover compressedSize from ContentLength and continue without re-uploading
- WHEN a crash-recovery re-run encounters a partially committed blob (BlobAlreadyExistsException + arius-type absent) THEN the pipeline SHALL delete the blob and retry the upload
- WHEN UploadAsync(overwrite:false) raises BlobAlreadyExistsException for a thin chunk and arius-type is present THEN skip silently (fully complete)
- WHEN UploadAsync(overwrite:false) raises BlobAlreadyExistsException for a thin chunk and arius-type is absent THEN delete the thin blob and retry
- WHEN no crash occurred (normal run) THEN dedup (Stage 2) filters known hashes so Stage 4 never encounters existing blobs; the BlobAlreadyExistsException path is never exercised
