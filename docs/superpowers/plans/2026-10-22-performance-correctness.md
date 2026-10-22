# Performance and Correctness Implementation Plan

> **For agentic workers:** Execute the independent file-owned slices below; the integration owner runs verification and commits. Do not run build, lint, tests or formatters while workers are editing.

**Goal:** Fix the confirmed transparency, interactive persistence and scheduling defects, reduce measured image-processing costs, and deliver verified changes on `dev-ai-denoising`.

**Architecture:** Keep the existing Core/Engine/RAW/shared-editor boundaries and native backends. Use transient editor state until a gesture commits, bounded image ownership, and behavior-preserving computation reuse; do not replace decoders or introduce a second rendering pipeline.

**Tech Stack:** .NET 10, Avalonia, Magick.NET, the in-tree RAW decoder, ONNX Runtime, WebGPU/Dawn WGSL.

**Spec:** The accepted review contract in this document. Baseline: `b32cd4c`. The user authorized implementation, evidence recording, commit and push after the performance review.

## Review contract and constraints

1. Box and Lanczos downsampling must not mix invisible RGB into visible colors. Keep straight-alpha outputs, transparent PNG/TIFF and opaque-image precision.
2. Slider changes must preview without writing the project on every tick. Commit once at gesture completion; preserve undo grouping, project ownership and save-failure atomicity. Interrupted edits must end explicitly.
3. Expensive preview callbacks and RAW histograms must not hold the scheduler submission lock. Preserve cancellation, generation and epoch guards through final UI delivery.
4. Thumbnail decoded sources and UI bitmaps must have bounded ownership, including offscreen items; scrolling back must reload them. Preserve foreground priority, loading completion and background pacing.
5. Reuse AHD scratch buffers without changing samples; avoid RAF preview extraction copying unrelated RAW payload; do not allocate a duplicate file-sized sparse buffer for whole-file fallback.
6. Cache full-resolution neural results for export, but blend at preview resolution when serving a preview. Preserve settings, alpha, source metadata, model identity and invalidation.
7. Enable bounded JPEG decoder sizing only for preview requests and verify orientation, reference dimensions, ICC and image differences against full decoding. Full export remains full decode.
8. Optimize large-radius GPU Gaussian work without changing its kernel, edge clamp or alpha semantics. Compare production Metal output and time; do not claim CPU fallback as GPU success.

No new packages, persistence format, user preference, decoder replacement, approximate blur or background-save service. Existing RAW numerical and export contracts remain authoritative. Windows and iOS results require their own execution and are not implied by Mac results.

## File ownership and implementation slices

### A. Alpha and JPEG (integration owner)

- `src/FilmBatch.Engine/Imaging/SourceImage.cs`: box resize accumulates RGB times coverage, divides by accumulated coverage and separately averages alpha; zero coverage produces zero RGB.
- `src/FilmBatch.Engine/Exporting/BatchExporter.cs`: Lanczos intermediate RGB is premultiplied, final RGB is unpremultiplied using filtered coverage, with zero/negative coverage cleared.
- `src/FilmBatch.Engine/Imaging/MagickImageCodec.cs`: use existing JPEG `size` define for finite previews, retain original raster/reference dimensions and full-resolution path.
- Regression: opaque red beside invisible blue remains red at half coverage through both resizers and encoded PNG. Include all-transparent, opaque, odd dimensions and cancellation cases where applicable.

### B. Interactive persistence

- `src/FilmBatch.App.Shared/ViewModels/InspectorViewModel.cs`, `ParamViewModels.cs`, `MainWindowViewModel.cs`, existing gesture controls/lifecycle callers; Core changes only if necessary for atomic commit.
- Reuse the editor's existing transient-settings pattern. End-edit commits one real `ProjectService` command; do not mutate committed photo entries before successful save.
- Regression: multiple ticks produce transient previews and unchanged disk; release produces one durable change and one undo step; interrupted selection/project switches cannot retarget a commit; failed save leaves committed settings and undo unchanged.

### C. Scheduler callbacks

- `src/FilmBatch.Engine/Scheduling/RenderScheduler.cs`, `src/FilmBatch.App.Shared/Services/PreviewService.cs`, existing scheduler tests.
- Snapshot eligible completion under the lock, invoke expensive callbacks after releasing it, and retain delivery guards after computation and queued UI dispatch.
- Regression: a gated completion callback cannot block submission; cancellation, reconfiguration and a newer request prevent stale delivery.

### D. Thumbnail ownership

- `src/FilmBatch.App.Shared/Services/ImageCache.cs`, `ViewModels/FilmstripViewModel.cs`, thumbnail view model and existing viewport view hooks; loading/cache tests.
- Bound completed source retention and offscreen bitmap ownership using existing viewport/pump information; eviction must not cancel another consumer's shared in-flight decode or destroy current display resources.
- Regression: loading well beyond the budget releases old images, scrolling back reloads, selected/visible work wins, folder change and failures still complete accurately.

### E. RAW allocations

- `src/FilmBatch.Raw/Develop/Ahd.cs`, `Vendors/Fujifilm/Raf.cs`, `RawSparse.cs`, associated RAW tests.
- Reuse per-worker tile scratch; slice borrowed container memory instead of copying preview-unrelated data; reuse whole-file sparse buffers or decide fallback before allocating.
- Verify decoded sensor and developed RGB samples against baseline plus malformed/range cases; measure allocations separately from I/O bytes and peak RSS.

### F. Denoise preview

- `src/FilmBatch.Engine/Denoising/DenoiseProcessor.cs`, `DenoiseTiling.cs` if needed; denoise tests.
- Retain the full neural result. Cache the corresponding original/denoised preview inputs before repeated strength blending; exports still materialize full resolution.
- Regression: preview blend matches the former full-blend-then-downsample result within existing numerical tolerance, alpha/masks survive, and changing settings never reruns inference unnecessarily.

### G. GPU Gaussian

- `src/FilmBatch.Engine/Rendering/GpuEffectPipeline.cs`, existing Gaussian shader and dispatch support if required; GPU regression cases.
- Reuse exact separable weights and edge clamp; cooperative workgroup loads are preferred over changing the blur kernel. Keep a correct path for every supported radius and image boundary.
- Verify constant fields, impulses, odd dimensions, large radii and transparent edges against the existing kernel and production renderer. Measure full-image halation/clarity independently from the synthetic spike.

## Verification sequence

- [x] Add narrow consumer-behavior regressions before each implementation; integration owner executes failing-before checks against the untouched baseline and records the actual result.
- [x] Apply file-owned fixes; integration owner resolves interfaces and runs the affected tests, then the full Release solution suite once integration is complete.
- [x] Run a disposable before/after harness with identical inputs and settings: persistence writes/latency, lock submission, cache retention, RAW samples/allocations, denoise materialization, JPEG errors, GPU image parity/time.
- [x] Build and run the actual Apple Silicon application using `dotnet run build/Build.cs -- --rid osx-arm64 --smoke`; inspect the smoke receipt and visual output. Do not substitute headless output for native UI proof.
- [x] Update existing architecture/build/verification documentation and append observed evidence here. Remove disposable measurement scaffolding after evidence is recorded.
- [x] Commit and push the verified implementation files with conventional subject/body style, without force; include the observed receipts in the final evidence-documentation commit.

## Evidence ledger

| Acceptance | State | Evidence |
|---|---|---|
| 1 alpha | verified | Six new downsampling cases failed on untouched `b32cd4c`; repaired box/Lanczos and real PNG16 round-trip preserve visible red and half coverage. Full suite passes. |
| 2 persistence | verified | Four baseline gesture regressions failed on per-tick saves, save atomicity and selection/project retargeting. Repaired regressions and native pointer drag/release/undo/project-switch pass. |
| 3 scheduler | verified | Gated callback no longer blocks submission; completion, failure, notice and device-ready carry their originating epoch through UI delivery. Scheduler and delivery regressions pass. |
| 4 thumbnails | verified | Decoded-source retention measured after GC; native 400-image scrolling/reload retained 64 sources and bitmaps without changing terminal counts. App regressions pass. |
| 5 RAW allocations | verified | AHD, RAF extraction and sparse fallback allocate less; paired sensor/developed/preview hashes agree. No blanket RAW speedup claim. |
| 6 denoise | verified | Both allocation regressions fail on untouched `b32cd4c` and pass after repair. Paired preview precision and real NAFNet CPU inference/cache-hit smoke pass; detailed domain boundary below. |
| 7 JPEG | verified, not bit-exact previews | Orientation/reference size/Adobe RGB, narrow images and no-enlargement cases pass. Full decode/export unchanged; measured preview differences below. |
| 8 GPU | verified on Metal | All four production comparisons are RGBA16-identical, no CPU fallback; Gaussian behavior regressions and all 36 expanded Naga entry points pass. |
| Native smoke | verified on macOS | Rebuilt self-contained app/CLI; 25 package checks pass, actual gesture and thumbnail native-window smoke passes. Windows/iOS not inferred. |
| Implementation commit / push | verified | `a5c8266`, `7211c44`, `a7f22c2`, `35f4373` committed; Git acknowledged normal `b32cd4c..35f4373` update to `origin/dev-ai-denoising`. This evidence documentation is recorded separately. |

Integration checks: initial suite compiled App (235 passed) but exposed three test compilation issues (RAF helper/type name shadowing, untyped tuple collection and missing test cancellation token); corrected without product changes. Next run: RAW 629 passed / 293 skipped; App 235 passed; Core 648 passed / 27 skipped / 3 failed. The three newly authored test failures were check defects: decimal rounding was used instead of absolute scene tolerance (observed difference 2.4e-7), a hand-calculated SDR red value used 37666 instead of `round(0.5748×65535)=37670`, and a Gaussian check pinned invisible RGB rather than visible color/coverage. Corrected arithmetic/tolerance and removed the invisible-RGB assertion; product output was not changed to satisfy them. Subsequent real codec smoke exposed two product issues: PNG selected palette output and lost 16-bit alpha, and an unconditional JPEG size hint enlarged a small image. Added failing regressions, selected PNG64, and restricted the hint using original JPEG dimensions; final results below include these fixes.

Consumer coverage: desktop shared editor/native gestures, CLI/package GPU discovery, GUI export, cached sources, sidecar persistence, undo and documentation are covered by the checks below. Source metadata, model identity and persisted settings retain their existing contracts. Windows/iOS consumers share code but their runtime behavior is explicitly untested in this round; no schema migration, new dependency or alternate renderer was introduced.

## Observed results — 2026-10-22

### Method and inputs

- Baseline is the untouched `b32cd4c` source snapshot; repaired source is the delivery containing this record. Same Apple M3, 8 logical processors, 16 GiB, .NET SDK 10.0.402 / runtime 10.0.13, Release, Magick.NET Q16-HDRI 14.17.2, Dawn/WebGPUSharp 0.5.7 and Metal.
- Each timed stage ran once cold, then three warm iterations; the table reports the warm median. Managed allocation is `GC.GetTotalAllocatedBytes(true)` delta, **not** peak RSS, native allocation or GPU memory. Retained cache memory instead uses forced GC and `GC.GetTotalMemory(true)`.
- Public RAW inputs are pinned in [sony.tsv](../../../tests/FilmBatch.Raw.Tests/Corpus/sony.tsv) (`arw-sony-a7s-shade.arw`, SHA-256 `7cc338a0abc8fdad32d61006f1f8f412297b93e040c7efba572eb2bd8f8e8be2`) and [vendors.tsv](../../../tests/FilmBatch.Raw.Tests/Corpus/vendors.tsv) (`raf-fuji-xt20-compressed.raf`, SHA-256 `a23045101b2912e64f154100d646c228c0d16585d21e95e0d1a006e537f7784c`). These rows include the public download URLs.
- JPEG inputs were created once using baseline JPEG quality 95: `GpuSpikeBenchmark.Synthetic(6000, 4000)` and the baseline full Sony A7S image (4240×2832). The same files were reused. GPU preview comparisons use saved baseline RGBA16 input, preventing JPEG decode changes from confounding shader parity.
- Production GPU calls are `GpuSpikeRenderer.Render16`, halation amount 40, radius setting 30 or 100, clarity 30; these radius settings are not literal Gaussian tap counts. Both runs report `Apple M3 · Metal`, `IsSoftware=false`, `UsingCpu=false`.
- Denoise timing isolates materialization after inference: use full Sony scene RGB multiplied by 0.98 as a deterministic cached neural result, with amount/luminance/color 70/80/40 and a 1600×1069 preview. It is **not** a neural-inference benchmark. A separate smoke uses the installed real NAFNet-SIDD model.

### Paired measurements

| Scenario / metric | Before | After |
|---|---:|---:|
| Native drag: durable sidecar revisions during 30 pointer moves | 30 | 0 |
| Native drag: median UI tick, ms | 4.021 | 0.375 |
| Native drag: maximum UI tick, ms | 14.068 | 1.349 |
| Submission while completion callback holds for 200 ms, ms | 202.127 | 0.004 |
| 1,000 thumbnails: retained managed memory after GC, MiB | 204.841 | 13.313 |
| 1,000 thumbnails: retained decoded sources | 1,000 | 64 |
| Sony A7S AHD: managed allocation, MiB | 1,345.226 | 323.912 |
| Sony A7S AHD: warm time, ms | 402.837 | 380.057 |
| Fuji RAF embedded 708,575-byte JPEG extraction: allocation, MiB | 24.557 | 0.677 |
| Fuji RAF embedded JPEG extraction: warm time, ms | 1.821 | 0.020 |
| Fuji full RAW decode: allocation, MiB | 320.546 | 296.605 |
| Fuji full RAW decode: warm time, ms | 271.488 | 330.167 |
| Sparse whole-file fallback: allocation for 1,085,348 bytes, MiB | 2.072 | 1.038 |
| Cached RAW denoise preview: allocation, MiB | 209.000 | 19.577 |
| Cached RAW denoise preview: warm time, ms | 56.919 | 5.615 |
| 6000×4000 synthetic JPEG → 1600 preview: warm time, ms | 414.950 | 280.532 |
| 4240×2832 natural JPEG → 1600 preview: warm time, ms | 251.016 | 253.415 |
| Metal 1600×1067, radius setting 30: warm time, ms | 25.595 | 21.036 |
| Metal 1600×1067, radius setting 100: warm time, ms | 32.842 | 27.926 |
| Metal 6000×4000, radius setting 30: warm time, ms | 612.647 | 380.017 |
| Metal 6000×4000, radius setting 100: warm time, ms | 1,353.329 | 658.035 |

Fuji full decode was slower in this run despite allocating less; this is not reported as a decode speedup. AHD warm timings varied substantially. The natural JPEG does not meet the safe size-hint threshold, so its unchanged path is not a speedup claim. These small single-host samples are observations, not throughput guarantees.

### Correctness and actual surfaces

- Sony and Fuji sensor hashes match baseline; Sony AHD developed RGB and the extracted RAF JPEG also match their baseline hashes. Sparse fallback returns identical bytes.
- All four Metal comparisons have maximum RGB16 and alpha16 difference **zero** across the entire image. Tests additionally exercise constant fields, impulses, odd dimensions, large radii and transparent edges; the optimization keeps tap order, exact weights and clamp boundaries.
- Cached RAW denoise preview differs by at most 1/65535 in displayed RGB, RMS 0.039763 code values, with alpha unchanged. RAW affine mixing permits cached proxy inputs. SDR retains full-resolution per-pixel clipping/quantization and alpha-weighted accumulation while avoiding full-image intermediate buffers; it does not claim preview-sized arithmetic. New allocation regressions fail on baseline (scene 3,348,920 bytes; SDR 1,774,184 bytes) and pass after repair.
- Real NAFNet-SIDD CPU smoke processed 128×96, one tile, model SHA-256 `8b437280db2f9f0ef5c733fa0fec70fc10012a62d19dac24a25b80ec8c529230`; first call inferred, second changed strength and reported `CacheHit=true` with different output. This proves the actual ONNX path, not quality on a natural photograph or performance on CoreML/DirectML.
- JPEG preview is deliberately not bit-exact to full decode: the synthetic image's maximum RGB16 difference is 1,417, RMS 37.108 code values; alpha is unchanged. The natural image is identical. Orientation, original reference dimensions, Adobe RGB conversion, narrow-image behavior and no enlargement pass. Full-resolution requests never use the hint; these samples do not establish universal perceptual equivalence.
- Opaque red beside invisible blue downsamples to `[65535,0,0,32767]` through box and `[65535,0,0,32768]` through Lanczos. Actual encoded/reloaded PNG returns the latter; IHDR is 16-bit RGBA (color type 6), not palette alpha.
- Native Avalonia window (`Avalonia.Native.WindowImpl`, Metal): the preview changes during drag while committed exposure stays 0.25; release commits 1.5 once, undo restores 0.25. Switching project mid-gesture saves the old project's 1.5, leaves the new project at 0, and a late release does not retarget it.
- Native 400-image folder: scrolling away and revisiting reloads an evicted thumbnail; resident sources/bitmaps are 64 each, `CompletedThumbnails=400`, `FailedThumbnails=0`. Screenshots show the transient editor and reloaded strip; this is not headless UI evidence.
- Rebuilt package `dist/osx-arm64/FilmBatch.app` passes all 25 smoke checks in 4,795 ms, including Metal preview, native menus, persistence/undo, GUI 16-bit export, ICC, unchanged source, Dock restore and gallery. Actual app and context-menu captures were inspected. Packaged CLI `gpu-info` reports Apple M3 / Metal. This is an ad-hoc local-development bundle, not a notarized release.

### Final automated checks and replay

| Final Release suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| FilmBatch.Raw.Tests | 629 | 0 | 293 |
| FilmBatch.Core.Tests | 655 | 0 | 27 |
| FilmBatch.App.Tests | 235 | 0 | 0 |

Skipped corpus/oracle and platform-specific cases remain unverified; a green run is not evidence that those cases executed. All 36 expanded shader entry points passed Naga 30.0.0. The first packaging invocation failed before launch because an unquoted PATH contained spaces; only that failed packaging step was rerun with quoted PATH and passed.

```bash
dotnet test FilmBatch.sln -c Release --nologo --logger 'console;verbosity=minimal' --logger trx
dotnet run build/CheckShaders.cs -- --naga /path/to/naga
dotnet run build/Build.cs -- --rid osx-arm64 --smoke
```

The disposable C# drivers, before/after JSON receipts, full-buffer comparison summary, native captures and final TRX results are retained outside the repository in the verification archive; they are not shipped in the product. Product/test entry points remain in the repository. No Windows/iOS runtime, broad camera corpus, independent decoder oracle, human perceptual ranking or Sigma BF performance result is inferred from this run.

### Separate Sigma BF investigation

The user-reported BF slowdown remains a valid observation, not a disproved claim. The separate `research/sigma-bf-raw-performance` branch was committed and pushed as `f059b78`; its [research record](https://github.com/minexo79/FilmBatch/blob/f059b78/docs/sigma-bf-raw-performance.md) contains actual Sony stage timings, DNG-path hypotheses and provenance limits. No BF DNG was present locally, and publicly linked original downloads returned HTTP 403 even through ordinary Chromium navigation. A readable original Sigma BF DNG/local file path is still required to reproduce and attribute that specific slowdown. No speculative BF-specific decoder change was applied or merged.

Local verification archive: `artifacts/verification/2026-10-22-performance-correctness.tar.gz` (ignored, not pushed), SHA-256 `8cb72d8ecfb5cda5d51c75afed50d2602a666e03f59a465c28a1886aa0efde2d`; gzip integrity checked. It includes baseline/repaired/native/Sigma driver source, stage receipts, image-comparison summary, final TRX and package captures. Large RGBA16 buffers remain separate local evidence, not Git content. No original user photos or model weights are added to the repository.

Cleanup / documentation check: archived driver source before removing the five temporary driver projects, expanded baseline and temporary Naga installation. The separate Sigma worktree and measurement receipts remain available. All 127 local Markdown path targets in the six changed documentation files resolve; the new evidence-section anchor also matches.

TWINS: searched `(ushort)(r / n)` and `dst[o] = ... r / n` - found 0 other sites: none. Both `Raf.PreviewJpeg` consumers were migrated: `RawPreview` intentionally owns the selected JPEG result, while `RawSourceLoader` borrows the underlying slice for EXIF parsing.

### Implementation delivery receipts

| Commit | Scope |
|---|---|
| `a5c8266` | Alpha-weighted resizing, PNG16 precision and bounded JPEG preview decode |
| `7211c44` | Transient gesture persistence, bounded thumbnails and originating-epoch scheduler delivery |
| `a7f22c2` | AHD scratch reuse, borrowed RAF preview data and sparse fallback allocation |
| `35f4373` | Denoise preview materialization, exact tiled Gaussian and shader-entry validation |

All four implementation commits were pushed successfully to `origin/dev-ai-denoising` without force. GitHub additionally reported two existing default-branch dependency alerts (one high, one moderate); those alerts were not investigated or changed by this scoped performance repair. The evidence/documentation commit follows these observed implementation receipts; its final push acknowledgement is the delivery receipt, not an inference from this text.
