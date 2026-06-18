 ▐▛███▜▌   Claude Code v2.1.178
▝▜█████▛▘  Opus 4.8 · Claude Team
  ▘▘ ▝▝    ~/.superset/worktrees/Arius7/zstd2


❯ /effort                                                                       
  ⎿  Set effort level to ultracode (this session only): xhigh + dynamic workflow
     orchestration

❯ clone https://github.com/oleg-st/ZstdSharp locally and review it s            
  implementation. is it suitable/hardened to serve as a replacement for the     
  gzip BCL-built-in compression algorithm? what is the risk that zstdsharp will 
  produce a corrupt stream that i will not be able to decompress years later?   
  what are the failure modes? is the risk different for small                   
  (kilobytes/megabytes) vs large (gigabytes/terabytes) files? produce a         
  comprehensive report, dont write any implementation plan yet                  

⏺ I'll start by cloning the repo and understanding how the current Arius
  codebase uses gzip, then orchestrate a comprehensive review workflow.

⏺ Bash(cd /tmp && rm -rf ZstdSharp && git clone --depth 50
      https://github.com/oleg-st/ZstdSharp.git 2>&1 | tail -20)
  ⎿  Cloning into 'ZstdSharp'...
  ⎿  Shell cwd was reset to /Users/wouter/.superset/worktrees/Arius7/zstd2

  Searched for 1 pattern (ctrl+o to expand)

⏺ The clone's working directory got reset. Let me verify the clone succeeded and
  look at how Arius currently uses compression.

⏺ Bash(ls -la /tmp/ZstdSharp 2>&1 | head -5; echo "---COMMIT---"; git -C
      /tmp/ZstdSharp log -1 --oneline 2>&1; echo "---TAGS---"; git -C
      /tmp/ZstdSharp tag 2>&1 | tail…)
  ⎿  total 32
     drwxr-xr-x@   7 wouter  wheel    224 Jun 16 05:08 .
     drwxrwxrwt  394 root    wheel  12608 Jun 16 05:08 ..
     … +11 lines (ctrl+o to expand)

  Searched for 1 pattern (ctrl+o to expand)

⏺ Clone is good (zstd master, latest tag 0.8.8). The critical Arius usage is
  clear: ChunkStorageService.cs compresses backup chunks with
  GZipStream(CompressionLevel.SmallestSize) wrapped in encryption — that's the
  gigabytes/terabytes path. Let me scout the ZstdSharp structure before
  launching the review workflow.

⏺ Bash(cd /tmp/ZstdSharp && echo "===STRUCTURE===" && ls -la && echo "===TOP
      DIRS===" && find . -maxdepth 2 -type d -not -path './.git*' | sort && echo
      "===CSPROJ==="…)
  ⎿  ===STRUCTURE===
     total 32
     drwxr-xr-x@   7 wouter  wheel    224 Jun 16 05:08 .
     … +191 lines (ctrl+o to expand)
  ⎿  Shell cwd was reset to /Users/wouter/.superset/worktrees/Arius7/zstd2

⏺ Confirmed key fact: ZstdSharp is a pure-C# port (NuGet ZstdSharp.Port), not a
  native binding — uses unsafe pointers and Fody IL-weaving. Notably its test
  suite references Zstd.Extern (native libzstd via P/Invoke), so it
  cross-validates the port against reference zstd, and there's a
  golden-compression/PR-3517-block-splitter-corruption-test regression file. Let
  me gather the source inventory and key API/test files.

⏺ Bash(cd /tmp/ZstdSharp && echo "===README===" && cat README.md && echo "" &&
      echo "===SRC FILE COUNT===" && find src/ZstdSharp -name "*.cs" | wc -l &&
      echo "===SRC T…)
  ⎿  ===README===
     # ZstdSharp
     … +200 lines (ctrl+o to expand)
  ⎿  Shell cwd was reset to /Users/wouter/.superset/worktrees/Arius7/zstd2

⏺ Bash(cd /tmp/ZstdSharp && echo "===TEST FILES===" && ls -la
      src/ZstdSharp.Test/ && echo "===PUBLIC API FILES===" && ls -la
      src/ZstdSharp/ | grep -iE "compress|decomp…)
  ⎿  ===TEST FILES===
     total 168
     drwxr-xr-x@ 10 wouter  wheel    320 Jun 16 05:08 .
     … +14 lines (ctrl+o to expand)
  ⎿  Shell cwd was reset to /Users/wouter/.superset/worktrees/Arius7/zstd2

⏺ I have a clear picture now. ZstdSharp is a pure-C# port of zstd v1.5.7 (the
  format is standardized as RFC 8878), with a public streaming API
  (CompressionStream/DecompressionStream) and a test suite that cross-validates
  against native libzstd. This is exactly the right shape to assess. Let me
  unshallow the repo for git-history forensics and peek at the two most
  safety-critical files (stream disposal/flush + checksum handling) before
  launching the review workflow.

⏺ Bash(cd /tmp/ZstdSharp && git fetch --unshallow --quiet 2>&1 | tail -3; echo
      "total commits: $(git rev-list --count HEAD)"; echo "first commit: $(git
      log --reverse -…)
  ⎿  total commits: 176
     first commit: 2021-04-03 18:00:44 +0300
     latest commit: 2026-06-10 11:51:31 +0300
  ⎿  Shell cwd was reset to /Users/wouter/.superset/worktrees/Arius7/zstd2

⏺ Bash(cd /tmp/ZstdSharp && echo "===CompressionStream.cs===" && cat
      src/ZstdSharp/CompressionStream.cs)
  ⎿  ===CompressionStream.cs===
     using System;
     using System.Buffers;
     … +232 lines (ctrl+o to expand)
  ⎿  Shell cwd was reset to /Users/wouter/.superset/worktrees/Arius7/zstd2

⏺ Full history confirms a mature, actively-maintained project: 176 commits, 
  2021→2026-06-10 (last commit 6 days ago). I also spotted a key failure-mode
  detail in CompressionStream.Dispose: the finalizer path (disposing == false)
  deliberately skips the end-of-frame flush — so a forgotten Dispose() yields a
  truncated frame (same bug class as the gzip BytesWritten note already in
  Arius).

  I have enough context to launch a comprehensive multi-dimension review
  workflow. It will investigate 7 dimensions in parallel against the real cloned
  source + git history + web research, adversarially verify the load-bearing
  claims, synthesize a report, and run a completeness critic.

⏺ Workflow(Assess ZstdSharp as a long-term-durable gzip replacement for Arius:
          corruption risk, failure modes, small-vs-large file risk)
  ⎿  /workflows to view dynamic workflow runs

⏺ The comprehensive review workflow is running in the background against the
  cloned source, full git history, and live web research. It investigates 7
  dimensions in parallel, adversarially verifies each dimension's load-bearing
  claims, synthesizes a decision-grade report, and runs a completeness critic
  over it.

  Dimensions under review:
  1. Format conformance & long-term decodability — is the output standard RFC
  8878 zstd that any decoder can read forever (the decisive long-term question)?
  2. Port fidelity — how the C#-transpilation stays byte-faithful and the
  cross-validation-against-native-libzstd mechanism
  3. Git-history forensics — empirical track record of corruption-class bugs
  (the best predictor of future risk)
  4. Bitstream/entropy core & platform determinism — the error-prone bit-I/O
  heart, intrinsics-vs-software fallback divergence
  5. Streaming API failure modes — truncated-frame risk from the dispose/flush
  contract (highest real-world probability for a backup tool)
  6. Small-vs-large file risk differential — multithreading, windowLog, >2GB/4GB
  size arithmetic
  7. Maintenance / adoption / bus-factor

  I'll wait for it to complete and then deliver the full report. You'll see live
  progress via /workflows in the meantime.

✻ Cogitated for 4m 35s

❯ /workflows   ⎿  Dynamic workflows dialog dismissed

⏺ Cannot open agents — 1 still running in the background. Use 
  /background to confirm abandoning them.

❯ /workflows 
  ⎿  Dynamic workflows dialog dismissed

⏺ Dynamic workflow "Assess ZstdSharp as a long-term-durable gzip replacement for
Arius: corruption risk, failure modes, small-vs-large file risk" completed · 
15m 19s

⏺ The workflow completed (30 agents, 648 tool calls). Let me read the full
  report and the completeness critique.

  Read 1 file (ctrl+o to expand)

⏺ The workflow produced a thorough report and a sharp completeness critique that
  caught several places where the draft over-claimed. I've folded the
  critique's corrections into the final version below — scoping the
  "byte-identical/frozen-standard" headline claims, adding the no-fuzzing gap,
  qualifying truncation detection as read-to-EOF-dependent, and noting
  Frame_Content_Size as a default-on partial protection.

  ---
  ZstdSharp as a long-term-durable gzip replacement for Arius — assessment

  Executive summary

  Verdict: Yes — ZstdSharp.Port is suitable to replace BCL 
  System.IO.Compression.GZipStream, provided three cheap, mandatory safeguards 
  are kept (inline write-time round-trip verify, frame content checksum,
  deterministic disposal). It is a faithful, widely-adopted, actively-maintained
  pure-C# port of reference zstd v1.5.7 that emits standard zstd frames (RFC
  8878), not a proprietary container.

  Risk that it produces a stream un-decompressable years later: LOW — not
  negligible, and the residual risk sits in exactly the class Arius fears most:
  silent, restore-time corruption. It is acceptable because it's heavily
  mitigable on your side, not because the library is infallible.

  The one point to internalize first

  Recoverability of Arius's data does not depend on ZstdSharp surviving. The
  zstd frame format (magic 0xFD2FB528) has been stable since zstd v0.8.0 (Aug
  2016) and is documented in RFC 8878. ZstdSharp emits that standard bitstream,
  and the suite proves the managed compressor's output is byte-for-byte
  identical to native libzstd 1.5.7 (ZstdValidationTest.cs:79:
  Assert.True(compressedNative.SequenceEqual(compressedSharp))) for the tested 
  scope (the ~10 MB dickens text corpus, single-shot, default params, levels
  −5..−1 and 1..22, on Windows). A blob written today is therefore decodable
  decades hence by the official zstd CLI, libzstd, the Linux kernel, Go
  (klauspost/compress), Rust (zstd-rs), Python 3.14 stdlib, 7-Zip, and others.
  If the library vanished tomorrow, the data is still readable — the same
  longevity posture as gzip (RFC 1952).

  Two honest qualifications on that backstop:
  - RFC 8878 is Informational, not Standards-Track, and reserves a
  Frame_Header_Descriptor bit for a future incompatible feature (conformant
  decoders must reject frames that set it). So "decodable forever" rests on the
  format's ubiquity and Meta's stability commitment plus the broad decoder
  ecosystem — a de facto freeze — rather than a formal IETF guarantee. For
  practical purposes this is as strong as gzip's, but it is not a mathematical
  certainty.
  - The byte-identity proof is real but narrowly scoped (one corpus, default
  params, Windows). Streaming/multi-frame output, dictionaries,
  binary/incompressible data, and >2 GB inputs are only self-round-tripped, not
  validated against native.

  The residual implementation risk — that the port emits a subtly non-conformant
  or wrong-but-self-consistent frame — must be kept separate from the format
  guarantee. That residual risk is the only thing that can actually hurt Arius,
  and it is the subject of the rest of this report.

  ---
  1. Is it hardened enough to replace BCL gzip?

  Yes, with caveats manageable at Arius's layer. The honest trade:

  What you give up vs. the BCL incumbent:
  - In-the-BCL status (shipped and security-patched by Microsoft with the
  runtime — effectively infinite bus factor).
  - ~30 years of zlib/gzip battle-testing vs. a ~5-year-old port (first commit
  2021-04).
  - A single maintainer — Oleg Stepanischev authored ~162 of ~176 commits. This
  is the one axis strictly worse than the incumbent.
  - No continuous fuzzing / sanitizer coverage. This is the most
  under-appreciated gap. Upstream zstd's primary correctness mechanism is
  continuous OSS-Fuzz + ASan/MSan/UBSan. The C# port has none of its own, and it
  runs different code (software-fallback intrinsics, hand-written IL, C#
  pointer arithmetic) that upstream fuzzing never touches. Upstream's own
  PR-3517 block-splitter corruption bug evaded continuous fuzzing for months —
  and the port has no fuzzing at all. This is the strongest single argument for
  keeping consumer-side verification authoritative.

  What you do not give up:
  - Format longevity — standard, broadly-decodable zstd, independent of this
  library (above).
  - A strong correctness mechanism — byte-identity cross-validation against
  native libzstd, which is a stronger conformance check than self-round-tripping
  (a divergent compressor fails immediately, not "round-trips its own
  mistake").
  - Upstream tracking — currently on the latest upstream (1.5.7); no
  version-staleness debt today.

  Adoption (strong signal): ~226 M total NuGet downloads (~165 K/day), pulled
  transitively by MongoDB.Driver, MySql.Data, Parquet.Net, IronCompress, and —
  most relevant — Duplicati, a backup tool that is Arius's closest analog.
  Database-driver usage continuously exercises the core against huge, diverse
  production data; a mainstream-path silent-corruption bug would surface as wire
  failures and get reported fast. This "many eyes" effect is the principal
  mitigant for the single-maintainer bus factor.

  The honest weakness is the assurance infrastructure, not the mechanism:
  - No CI has ever existed in the repo across all 176 commits (live GitHub API:
  contents/.github → 404, actions/workflows → total_count: 0). The byte-identity
  test runs only when the maintainer runs dotnet test locally.
  - The native libzstd reference is bundled as Windows DLLs only (x64/x86 PE; no
  .so/.dylib). The cross-validation therefore structurally cannot run on 
  Linux/macOS/arm64 — Zstd.Extern's P/Invoke throws DllNotFoundException there.
  (Relevant to your arm64/macOS dev and any Linux deployment.)
  - The byte-identity test uses one ~10 MB text corpus, single-shot, default
  params, levels −5..−1 and 1..22 (level 0 and the deep-negative range
  untested). Every other test is a managed-only self-round-trip, which by
  construction cannot catch a self-consistent wrong frame.
  - Track record blemish: NuGet 0.7.0 shipped a publicly-vulnerable
  (pre-PR-3517-fix) build that sat live for ~1 month. "Tracks upstream fast" did
  not prevent shipping a known-corruption release — which is precisely why
  pinning + consumer-side verification matter.

  Net: right mechanism, strong in kind; thin and maintainer-dependent in
  coverage and enforcement. For an archival bet, that is exactly why Arius's own
  verification is load-bearing, not optional.

  ---
  2. Risk of an un-decompressable-years-later stream — calibrated

  Rating: LOW (with one elevated but fully-mitigable sub-risk)

  (a) Format-conformance / "the official tool can't read it": VERY LOW

  Output is standard zstd; byte-identity to native 1.5.7 is proven within the
  tested scope; the format is de facto frozen with a large independent decoder
  ecosystem. The dangerous "valid-but-wrong frame" middle case is exactly what
  the byte-identity test targets. The reason this isn't "zero": the proof's
  scope is narrow (one corpus/Windows/default-params), so streaming, binary,
  dictionary, and >2 GB shapes lean on self-round-trip rather than native
  comparison.

  (b) Silent encoder/transpilation corruption — the real danger: LOW, but this 
  is where essentially all residual risk lives

  The feared mode: the port emits subtly-wrong-but-self-consistent bytes that
  round-trip through the matching managed decoder today, get faithfully
  encrypted (AES-GCM authenticates the ciphertext, tag passes), go to the cold
  tier, and only fail — or worse, silently mis-decode — on restore years later.

  This is not theoretical — the history has exactly this class, twice in the 
  data-producing path:
  - 2cee9c4 (2021) — ZstdFast compared raw pointers (match0 > prefixStart)
  instead of indices (matchIndex0 > prefixStartIndex), mis-selecting matches →
  wrong-but-decodable frame. A large-input bug (misbehaves as window/hash
  indices grow).
  - 33c1fc8 (2024-12) — HUF_CTableHeader copied sizeof(struct) = 8 bytes instead
  of sizeof(nuint), corrupting the Huffman table on 32-bit builds.

  Why nonetheless LOW (calibrated):
  1. Both are fixed; the second is 32-bit-only and cannot affect x64/arm64
  (sizeof(nuint)==8) — Arius's targets.
  2. Cadence is low (~1–2 correctness commits/yr) and trending toward AOT/32-bit
  edge cases, not mainstream x64/arm64 data correctness.
  3. Bugs are port-specific transpilation artifacts (pointer arithmetic, integer
  width, hand-IL, struct sizeof) — not algorithmic flaws in zstd or the frozen
  format.

  Why not negligible:
  - No CI, Windows-only native reference, one-file corpus, no fuzzing — so the
  exact mode Arius fears is not systematically exercised on Arius's data shapes
  (binary/incompressible/already-compressed), sizes (>2 GB), or runtimes
  (arm64/Linux software-fallback intrinsic paths).
  - Asserts are compiled out in Release ([Conditional("DEBUG")],
  UnsafeHelper.cs:90) and copies are unchecked (Unsafe.CopyBlockUnaligned). A
  wrong-but-in-bounds write yields silently-wrong bytes (the dangerous case)
  rather than a crash. (An out-of-bounds write more likely
  AccessViolation-crashes — the safe loud case.)
  - Don't over-trust the cross-validation as a catch-net: it predates both 
  shipped bugs, yet they shipped anyway (limited corpus/arch coverage). The
  mechanism exists but has demonstrably missed this bug class for months / a
  release.

  Two integrity-mechanism corrections (important, and widely misunderstood):
  - There is no per-block integrity check. The only in-frame content checksum is
  an optional per-frame XXH64, OFF by default.
  - But "by default nothing inside the frame detects encoder corruption" is too 
  absolute. By default contentSizeFlag=1, so the decoder enforces decoded length
  == stored Frame_Content_Size, and the format has ~40+ structural
  corruption_detected sites. So a wrong-length or structurally-invalid encoder
  bug is caught by default. What is not caught without action is a same-length, 
  structurally-valid content error.
  - Enabling XXH64 does not catch an encoder bug. The checksum is computed over
  the uncompressed input at compress time and the decoded output at decompress
  time. A buggy encoder hashes the correct input, stores that hash; if the
  matching managed decoder symmetrically reconstructs the original bytes from
  the wrong frame, the hash matches. XXH64's real value is against (i) at-rest
  bit-rot of the compressed bytes and (ii) decoder-side faults on an
  otherwise-valid frame — and it lets a third-party decoder (the zstd CLI) flag
  a content mismatch. The only reliable guard against an encoder bug is the 
  inline round-trip verify, ideally with a different decoder. Keep both; they
  cover different threats.

  ---
  3. Failure modes, ranked by real-world probability for a backup tool

  #: 1
  Failure mode: Truncated frame — forgotten/aborted Dispose, crash, or mid-write

    exception means ZSTD_e_end is never written. The finalizer (Dispose(false))
    deliberately skips ZSTD_e_end (CompressionStream.cs:88-96), so GC won't
    rescue a forgotten stream.
  Likelihood: Highest (operational, not codec)
  Visibility: Loud only if restore reads inner stream to EOF —
    DecompressionStream defaults checkEndOfStream=true and throws
    EndOfStreamException("Premature end of stream")
    (DecompressionStream.cs:136-139,195-198). But the throw is read-driven: a
    consumer that reads only a prefix, or reads a concatenated [full][truncated]

    stream and stops after the first frame, gets silent partial output, no 
    exception. Arius must read to  EOF for the guard to fire.
  Severity: Safe if EOF is read; silent data-loss otherwise.
  ────────────────────────────────────────
  #: 2
  Failure mode: End-of-frame bytes stranded in an outer buffer. Dispose writes
    ZSTD_e_end to the inner stream but does not flush it
    (CompressionStream.cs:88-121). In Arius's CompressionStream →  AES-GCM → 
  blob
     shape, the tail can sit buffered and never reach storage unless
    dispose/flush order is correct. Confirmed by DisposeFlushTest
    (ZstdNetSteamingTests.cs:500-512): after writer.Dispose() the backing stream

    is still empty. The AES-GCM tag is computed over the truncated ciphertext
  and
     still validates  — encryption gives zero protection. (Inner-stream flush on

    Flush() was only added Jan 2026, eb8416a.)
  Likelihood: High (your exact pipeline)
  Visibility: Silent at write, loud at restore (if EOF read).
  Severity: Data-loss if ordering wrong.
  ────────────────────────────────────────
  #: 3
  Failure mode: checkEndOfStream set to false → truncated frame silently
  accepted
     as clean EOF.
  Likelihood: Low (explicit misconfig)
  Visibility: Silent
  Severity: Silent data-loss.
  ────────────────────────────────────────
  #: 4
  Failure mode: Silent encoder transpilation bug (2cee9c4/33c1fc8 class). On
    x64/arm64 single-threaded default path current code is clean; risk is a
    future re-transpilation of a new zstd version, or an untested data shape.
  Likelihood: Low
  Visibility: Silent until restore (AES-GCM can't catch; XXH64 can't catch
    encoder bugs; only length/structural errors caught by default)
  Severity: Catastrophic if it reaches cold tier — caught by inline round-trip
    verify.
  ────────────────────────────────────────
  #: 5
  Failure mode: Multithreaded (ZSTDMT) corruption, only if nbWorkers>0. Fully
    ported (1554-line ZstdmtCompress.cs + Monitor-based sync) but youngest,
    least-tested subsystem; round-trip-tested only, never 
    byte-identity-validated vs native. OFF by default (nbWorkers=0).
  Likelihood: Very low (opt-in)
  Visibility: Could be silent
  Severity: Avoid: keep nbWorkers=0, parallelize at chunk level.
  ────────────────────────────────────────
  #: 6
  Failure mode: Non-default window/LDM making frames unrestorable. Decompressor
    defaults maxWindowSize=128 MiB+1 (ZstdDecompress.cs:194). Never hit at Arius

    defaults (level 3 → windowLog 21 = 2 MiB; even level 22 → 27 = 128 MiB
  fits).
     But an explicit ZSTD_c_windowLog > 27 would make a default-configured
    decoder reject the frame (frameParameter_windowTooLarge).
  Likelihood: Very low (only with tuning)
  Visibility: Loud at restore
  Severity: Mirror write-time windowLog via ZSTD_d_windowLogMax if tuned.
  ────────────────────────────────────────
  #: 7
  Failure mode: Loud internal errors (corruption_detected, checksum_wrong) — all

    native return codes checked via EnsureZstdSuccess → ZstdException; nothing
    swallowed on the streaming path.
  Likelihood: n/a
  Visibility: Loud
  Severity: Annoying but safe.

  Ranking takeaway: #1, #2, #6, #7 are loud (with the #1/#2 caveat that "loud"
  requires reading to EOF at restore). The genuinely dangerous,
  silent-until-restore ones are #3 (misconfig only), #4, and #5 (if MT enabled).
  Arius's architecture amplifies the silent class because encryption sits 
  outside compression — AES-GCM faithfully authenticates whatever (possibly
  wrong) compressed bytes it's handed and provides no protection against any 
  pre-encryption corruption.

  ---
  4. Small (KB/MB) vs large (GB/TB) files

  Mostly the same at Arius's defaults — with one real large-file lean and 
  several debunked fears.

  Same for both sizes (confirmed):
  - Multithreading is OFF and large files do not auto-enable it (nbWorkers=0;
  the only size-dependent MT line disables MT for small inputs,
  ZstdCompress.cs:6736-6739). Both sizes run the identical single-threaded path,
  avoiding the buggiest subsystem.
  - The (int) casts in the streaming wrappers do not overflow at >2/4 GB. They
  are bounded per-iteration buffer offsets ((int)output.pos, where output.pos ≤ 
  output.size ≤ the ~128 KB or user int buffer), not cumulative file positions.
  All cross-boundary size/position state is nuint/ulong; Length/Position throw
  NotSupportedException. A TB file is a loop of small bounded chunks.
  - The default 128 MiB+1 decompressor window rejects no default-config frame,
  any size.
  - Empty/tiny inputs are explicitly tested and low-risk.

  Marginally riskier for large files (real, low):
  - Large-size arithmetic is this transpilation's historically-demonstrated weak
  spot. The ≥2 GB allocation-truncation bug (9572796, 2022) and the ZstdFast
  pointer-vs-index bug (2cee9c4, 2021) are both large-input bugs, both fixed.
  They prove that if a future re-transpilation reintroduces a width/overflow
  error, large files are where it lands.
  - A latent assumption to re-check on every re-transpilation: the ported
  memcpy/memset still take a 32-bit uint size, safe only because zstd never
  copies more than one block/window per call (architecturally < 4 GB). Correct
  today; worth re-verifying if the core is re-generated against a future zstd.
  - More iterations, bigger buffers, larger windowLog → more code surface →
  marginally higher chance of tripping a latent bug than the well-covered small
  path.
  - Coverage gap, not a defect: byte-identity-vs-native runs only at ~10 MB; the
  dedicated ≥2 GB tests (ZstdNetTests.cs:547-585) are
  [Theory(Skip="Explicit/memory consuming")], round-trip-only, not in CI. The
  multi-GB single-threaded path is not byte-identity-validated against native
  anywhere automated — well-pinned at ~10 MB, weakly-pinned at multi-GB.

  Implication: apply the inline round-trip verify to all sizes, and keep working
  chunks well under 2 GB (chunk-level partitioning, which Arius already does).
  The small end is essentially solved; the large end is low-risk but is exactly
  where your own write-time verification should be uncompromising.

  ---
  Prioritized mitigations (advice, not a build plan)

  1. Keep the inline write-time round-trip verify — the single most important 
  guard. It is the only reliable defense against a silent encoder transpilation
  bug (neither AES-GCM nor XXH64 catches it). Strongly prefer decoding the
  just-written frame with native libzstd / the zstd CLI, not the managed
  decompressor — a different decoder catches a self-consistent managed
  wrong-frame the matching managed decoder would happily reconstruct. All sizes,
  including large chunks.
  2. Enable ZSTD_c_checksumFlag = 1 (CompressionStream.SetParameter, 4
  bytes/frame). It doesn't catch encoder bugs, but it's cheap, permanent,
  decoder-verifiable insurance against at-rest bit-rot and decoder-side faults,
  and lets a future zstd-CLI restore flag a content mismatch. Complements,
  doesn't replace, #1. (Frame_Content_Size already gives default-on
  length/structural protection.)
  3. Deterministic disposal + correct ordering. Always Dispose/DisposeAsync the
  CompressionStream (never rely on finalizer or Flush()) before finalizing the
  AES-GCM stream, then flush/dispose the outer stream so end-of-frame bytes
  reach storage. Assert the inner written-byte count is non-zero after disposal.
  4. Keep checkEndOfStream = true on restore and read every blob to EOF (the
  truncation guard is read-driven). Add a test that a deliberately truncated
  blob throws. Treat any EndOfStreamException/ZstdException at restore as fatal.
  5. Keep nbWorkers = 0. Parallelize at the chunk level — stays on the validated
  path, off the least-validated MT subsystem.
  6. Leave windowLog/LDM at defaults; if ever tuned large, mirror via
  ZSTD_d_windowLogMax on restore.
  7. Pin a known-good ZstdSharp.Port version (the ~1-month vulnerable 0.7.0
  window shows why) and re-run your inline verification — plus a one-time
  native-zstd-CLI decode of representative large/streamed chunks — on every
  upgrade and on each architecture you deploy (x64, arm64), since the library's
  own cross-validation never runs on non-Windows.
  8. Document the recovery path: Arius blobs are standard RFC 8878 zstd frames,
  decodable by the official zstd CLI and any conformant decoder. Record it so a
  future operator recovers data even if ZstdSharp is gone — the ultimate
  backstop.

  ---
  Closing calibration

  ZstdSharp is a good, defensible choice. The format risk is very low and the
  data is recoverable independent of the library. The implementation risk is
  low, dominated by silent-until-restore modes that Arius's inline round-trip
  verify — kept, and upgraded to verify with a native/independent decoder —
  neutralizes. The real caveats, named plainly: single maintainer, zero CI, no
  fuzzing of the managed unsafe code, Windows-only + single-corpus
  cross-validation, asserts stripped in Release, and a streaming wrapper still
  receiving correctness fixes as recently as Jan 2026. None touch the
  recoverability of already-written, write-time-verified data; all argue for
  keeping Arius's own verification as the authoritative safety net rather than
  trusting the library's test infrastructure. Adopt it, with mitigations #1–#3 
  treated as mandatory.

  ---
  Want me to save a short project memory recording this evaluation outcome
  (verdict + the three mandatory safeguards) so it's available when you move to
  implementation? I'll hold off on any implementation plan as you asked.

✻ Cogitated for 21m 38s

※ recap: Goal was assessing ZstdSharp as a long-term gzip replacement for Arius. I delivered the full report: verdict is adopt at LOW risk with three mandatory safeguards. Next action is your call on whether I save a short project memory of the outcome. (disable recaps in /config)