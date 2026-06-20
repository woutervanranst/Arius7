# Encryption golden files

`Hash` is the first 8 characters of the golden file name. The Arius version column describes the persisted format contract, not where the fixture was created.

| Hash | Arius version (v5 or v7) | Encryption algorithm | Compression algorithm | Pdkf2Iter | Purpose |
|---|---|---|---|---:|---|
| `2552b810` | v5 | AES-256-CBC (`Salted__`) | gzip | 10,000 | Real legacy tar chunk with `world` and `42` entries. |
| `9ffc39c1` | v5 | AES-256-CBC (`Salted__`) | gzip | 10,000 | Real legacy large chunk with Lena PNG bytes. |
| `680ccc69` | v5 | AES-256-CBC (`Salted__`) | gzip | 10,000 | Synthetic fixture with fixed salt, used as a small deterministic legacy-CBC sanity check. |
| `25948687` | v7 | AES-256-GCM (`ArGCM1`) | gzip | 100,000 | GCM compatibility fixture for `"Hello, ArGCM1 golden file!"`. |
| `b886c2f1` | v7 | AES-256-GCM (`ArGCM1`) | zstd | 100,000 | Current production-format fixture for `"Hello, zstd golden file!"`. |
