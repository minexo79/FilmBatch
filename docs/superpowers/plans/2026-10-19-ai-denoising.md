# AI Denoising Implementation Plan

> Agent execution uses `reflective-implement`, `test-driven-development`, `writing-plans` and matching UI/platform skills. Implement the complete runnable path; the integration owner runs checks after owned slices settle. Implementation remained uncommitted as initially requested. The subsequent user request, **“Commit in commits”**, authorizes grouped local commits only; do not push, publish or add remote credentials.

**Goal:** Deliver real, offline, non-destructive photographic AI denoising for RAW scene-linear and ordinary RGB sources, prioritizing verified image quality and a maintainable cross-platform implementation.

**Architecture:** Use one source-stage ONNX Runtime implementation before crop, creative effects and film grain. Select RawNIND linear for developed float Rec.2020 and NAFNet-SIDD for ordinary sRGB only after the actual model artifacts, licenses and native execution are verified. Denoise at full source resolution, cache the neural result independently of artistic controls, and derive preview pixels from that result. Explicit model installation is separate from rendering; errors never produce a successful identity result.

**Tech stack:** Existing .NET 10 / Avalonia 12 / CommunityToolkit.Mvvm, existing CPU and Dawn renderers; one ONNX Runtime native dependency. No Python runtime or second GPU/network-operator implementation.

**Spec / authority:** This document implements the user's request to plan and implement AI denoising until human intervention is genuinely required. `docs/ai-denoise-survey.md` supplies historical source evidence, not permission to copy GPL code or claim benchmark results. The initial no-commit/no-push constraint was respected through implementation and verification; the later explicit request authorizes logical local commits, not a push or release.

## Brief and acceptance contract

- **Why / intended outcome:** Remove camera noise before adding a film look or grain, with adjustable non-destructive strength and preserved highlight/detail information.
- **Inputs:** Decoded scene-linear Rec.2020 RAW with existing metadata; ordinary 16-bit sRGB RGBA; immutable resolved photo settings; verified model files.
- **Outputs:** Same geometry, color domain and metadata, changed only by the selected denoising amount; normal preview and full-resolution export; truthful model/provider/progress/error state.
- **Assumptions:** Source photos are immutable. Existing RAW development remains authoritative. Model files are optional user data, not silently fetched or embedded as unverified binary blobs. Clear permissive/attribution model terms can be adopted with their required notices; unclear rights stop adoption of that artifact.
- **Failure conditions:** HDR clipping to satisfy an RGB model; unsupported model/data-domain combinations; fake inference or filter substitution; preview-only integration; in-place source mutation; stale/cancelled inference published as current; silent provider or model substitution; unbounded activation/full-image caching; source-file overwrite; automatic commit/push.
- **Falsifiability:** Compare actual native model output against noisy/clean photographic samples and held-out structural fixtures; show measurable noise reduction, inspect edges/texture/color and tile seams, and verify source hashes plus metadata/geometry. Reject a candidate or provider that produces non-finite output or materially changes the data contract.
- **Human-review triggers:** Missing lawful/obtainable weights, private credentials, paid/cloud execution, destructive changes to user data, or a platform/quality decision not resolvable from local experiments. Finish all independently reachable work before reporting a blocker.

## Global constraints

- Grouped local commits are explicitly authorized by the subsequent user request. No `git push`, release publishing, credential creation or auth/permission changes.
- Preserve CPU/GPU rendering equations and existing pixel tolerances outside denoising.
- Preserve RAW float headroom, scene metadata, semantic masks, source reference dimensions, alpha and orientation.
- Native model execution is bounded by fixed-size overlapping tiles, one inference flight at a time, reusable input/output tensors and bounded retained full-image results.
- Disabled/zero-strength processing must not load a model, allocate image buffers or change source identity.
- Artistic settings and denoising mix controls do not rerun a cached neural result; source/options/model changes invalidate it.
- Explicit install/import verifies fixed model identity and SHA-256 before activation. Render never accesses the network.
- Cancellation interrupts inference, does not poison the model/session/cache, and cannot be mistaken for GPU loss or trigger retries.
- UI, CLI, preview, thumbnails and full-resolution export must have a stated, exercised consumer contract. No unmarked approximate preview.

## File and ownership map

| Slice | Files / responsibilities | Boundary |
| --- | --- | --- |
| Model/runtime evidence | Official model/runtime artifacts and isolated native probes under ignored evidence | Primary source, exact pin/hash/license, observed inference; no repository edits during exploration |
| Engine/model runtime | `src/FilmBatch.Engine/Denoising/` (new); Engine package references | Known model catalog and installer, verified ONNX sessions, tile processing, gain/domain handling, cancellation, full-source result caching, strength mixing |
| Shared rendering/source integration | Existing `SourceImage`, CPU/GPU renderer entry points, scheduler, batch export | One denoised source shared by rendering backends; full-resolution-before-preview order; metadata and alpha; cancellation and error classification |
| Schema/UI | Existing `ModuleCatalog`, Inspector/Views/localization plus focused denoising view model | Photo-edit controls, visible install/progress/provider/error/cancel actions, undo/save/reopen through existing settings architecture |
| CLI/deployment | Existing CLI command registration and native publish flow | Model installation/status and actual AI export without Python; package includes correct RID native runtime, not build-machine dependencies |
| Verification/docs | Meaningful existing-style tests, ignored photographic/native smoke evidence, existing ADR/survey/building/notices | Deterministic edge/transition regressions; actual model, actual UI and export proof; exact limits and license provenance |

## Task 1 — Establish real model and runtime prerequisites

- [x] Read current official model artifacts, domain/preprocessing/output gain and tile contracts, and map exact weights to license/attribution.
- [x] Download selected artifacts into ignored/local model storage and record byte lengths plus SHA-256. Keep GPL tools outside the shipping runtime and do not copy their implementation.
- [x] Run an isolated .NET native inference probe on the host with actual fixed-shape tensors; record available/selected providers, finite output, latency and memory. Confirm CPU cancellation and session reuse.
- [x] Choose the highest-effect candidate that meets the data, licensing and execution gates, not an estimated speed ranking. Record source evidence and rejected alternatives.

## Task 2 — Implement the bounded source-stage denoiser

- [x] Before implementation, characterize a missing consumer-visible behavior with a failing check against the untouched baseline. Keep permanent tests for boundaries/transitions, not copies or default constants.
- [x] Add the known-model catalog/store and streaming atomic install with fixed-hash verification, cancellation cleanup and readable missing/corrupt-model errors.
- [x] Implement channel-planar FP32 packing, edge padding, overlap/context handling and seamless reconstruction using the verified model contract; keep HDR range and model-specific gain correction explicit.
- [x] Preserve alpha and RAW metadata; blend the denoised/original result in the correct color domain. Do not pass RAW/HDR through the sRGB model.
- [x] Reuse sessions/tensors, bound inference concurrency and cache lifetime. Cache the neural output independently of mix controls and creative settings; cancelled/failed work is not cached.
- [x] Exercise real model output, tiny/odd dimensions, tile boundaries, finite/HDR values, alpha, corrupt models, cancellation and reuse. Use native execution evidence as well as tests.

## Task 3 — Integrate normal application entry points

- [x] Add an opt-in `denoise` schema module in the Basic/source section, with default-off state and model-appropriate blend controls. Existing project commands own edits, undo, presets, copy/paste and persistence.
- [x] Integrate the same prepared source before both CPU and GPU effects. Full-resolution denoise precedes downscaling; cropped/rotated preview and full export must retain their existing geometry contract.
- [x] Propagate meaningful cancellation through active inference and queued preview/source changes without regressing same-source interactive preview delivery. Foreground work must not wait behind a whole background denoising job.
- [x] Show model installed/missing/downloading, inference phase/tile progress, actual provider and errors in the native UI; expose explicit model download/import and cancel actions. Never report unprocessed output as denoised.
- [x] Integrate CLI model management and export, and report AI processing provenance in existing export evidence.
- [x] Run native UI workflows including actual AI preview, strength adjustment without reinference, cancel/photo switch, undo, save/reopen and a real 16-bit export. Inspect the actual surface.

## Task 4 — Quality, packaging and closure

- [x] Measure real paired photographic noise reduction where lawful samples are available; inspect detail, color, saturated highlights and tile seams. Synthetic checks supplement, not replace, photographic evidence.
- [x] Run changed-path regressions, complete solution checks and native publish/executable smoke. Match each platform/provider claim to actual execution; cross-build is not Windows runtime proof.
- [x] Update `docs/adr/0002-ai-denoise.md`, `docs/ai-denoise-survey.md`, architecture/build/user documentation and `THIRD-PARTY-NOTICES.txt` from verified implementation and model provenance.
- [x] Remove owned throwaway runtime scaffolds after retaining compact evidence and reproducible inputs under ignored `dist/ai-denoise-evidence/`.
- [x] Keep implementation uncommitted/unpushed until explicitly authorized. The subsequent user request authorizes grouped local commits only; report actual checks, remaining human prerequisites and unexercised provider/platform bounds without pushing or publishing.

## Review criteria

1. Real licensed model and native inference, not a wiring-only scaffold or hidden classical filter.
2. Correct photographic data domain, exposure/gain normalization, headroom, alpha and metadata.
3. Tile seams, tiny/odd images, finite output and cancellation verified through the consuming source/render/export paths.
4. A slider is not accepted until its output is observed; persisted settings survive reopen; stale work cannot replace the current photo.
5. Model/network failures are actionable errors, never success plus original pixels. Model installation is explicit and integrity checked.
6. No second inference framework, speculative plugin surface, unnecessary per-pixel allocations or unrelated renderer/CI changes.
7. No unsupported Lightroom-quality, ANE/GPU-placement, throughput, calibrated noise or cross-platform claims.

## State ledger

| Item | Status | Evidence / current boundary |
| --- | --- | --- |
| User delivery authority | confirmed | Implementation and verification remained uncommitted/unpushed; subsequent “Commit in commits” authorizes grouped local commits only, with no push or release |
| Existing RAW/source/render/cache/UI contracts | inspected | Float Rec.2020 scene plus as-shot metadata; source-reference geometry; existing schema-driven Basic section and export path |
| Model artifact and runtime capability | verified prerequisites | Official `nightly-5.7.0` RawNIND linear / NAFNet-SIDD packages matched published archive hashes; author independently permits RawNIND weights under CC-BY-4.0; no GPL tooling copied |
| Native inference | verified prerequisites | Actual ONNX Runtime 1.24.4 CPU and CoreML MLProgram runs; finite HDR outputs, synthetic quality improvement; CPU native cancellation followed by successful reuse. RawNIND uses one signed global mean gain, not per-tile normalization |
| Baseline consumer failure | reproduced | Untouched baseline CPU renderer silently returned original processing for active denoise with missing models; isolated `red-probe` returned exit 1 |
| Engine and UI/export integration | verified | Source-stage CPU/Metal paths, exact full/proxy pixels, real model download/import and shipped CLI export; native application completed 25 checks with no failures |
| Photographic quality and native UI | verified within sample | NIND ISO200/6400 texture and subject-detail crops; actual CPU/CoreML models, source hashes, HDR/alpha, cancellation/reuse and 16-bit ICC exports; no independent sensor-RAW benchmark claim |
| Final verification and notices | verified on available platforms | Release suite: App 194 passed; Core 532 passed/27 skipped; Raw 618 passed/293 skipped; zero failures. Apple Silicon published GUI smoke 25 passed; Windows x64 cross-publish and DirectML native/license closure checked, Windows execution not available |

## Observed acceptance evidence — 2026-10-19

The real NIND droid ISO6400/ISO200 pair is CC-BY-4.0 by Trougnouf (Benoit Brummer). Two 865×577 crops exercise background texture and the green subject's edges, reflections and surface detail. The linear input is inverse-sRGB decoded and converted to Rec.2020; it is **not** original sensor RAW and is not an independent held-out RAW benchmark. PSNR is measured in each model's own input domain, so the two models' values are not a cross-model quality ranking.

| Input domain / crop | Noisy PSNR (dB) | Denoised PSNR (dB) |
| --- | ---: | ---: |
| sRGB / texture | 29.81 | 33.91 |
| sRGB / subject detail | 25.82 | 31.20 |
| Derived linear Rec.2020 / texture | 30.61 | 33.62 |
| Derived linear Rec.2020 / subject detail | 31.59 | 35.67 |

- Actual CoreML + CPU and forced CPU runs both completed. Inspected denoised/reference images retain the subject's silhouette, eyes, reflections and wall texture; 100% strength also smooths some fine surface marks, so strength remains a blend control rather than a detail-reconstruction guarantee.
- Full-size processing then proxy resizing is pixel-exact against the existing downscale path. A failing-before/passing-after regression caught quantizing after rather than before the SDR resize. Tiny/odd images, reflection padding, transparent alpha, float HDR headroom and fresh native inference after cancellation passed.
- Real CPU/Metal effect consumers matched within 1/65535 on these prepared sources, including crop/rotation; existing wider pipeline tolerances were not changed.
- The actual macOS application exercised visible module toggles, all three mix sliders, cached pixel changes, undo/redo, save/reopen, actionable missing-model errors, cancellation/photo switch, RAW/RGB model selection and native TIFF export. All 25 checks passed; English/zh-TW and export surfaces were inspected. Automation peers and actual native rendering were used, not physical mouse/keyboard or an OS focus-switch claim.
- A separate UI regression reproduced an obsolete RGB model download completing after switching to a RAW source. Model-domain changes now cancel that operation; posted progress is fenced to its source context. Installation/cancellation do not write photograph settings.
- Published macOS CLI explicitly downloaded both pinned models, imported a verified ONNX, then exported both source domains to genuine 16-bit PNG with ICC and model/provider provenance while HTTP(S) proxy endpoints were deliberately unavailable. Source SHA-256 values were unchanged.
- The self-contained macOS application passed all 25 existing executable smoke checks. Initial published launches could not create the native RenderTimer while the display was asleep; after separately establishing and observing an active display, the unchanged executable passed. No product workaround, permission change or unlock was used.
- The macOS archive is ad-hoc signed for local use, with minimum deployment declaration 14.0 derived by the existing Mach-O packaging check. Windows x64 archives contain the selected DirectML runtime and redistribution terms; no Windows execution or GPU-placement claim is made. Developer ID signing/notarization requires the user's identity/profile and was not attempted.
- Compact ignored evidence: `dist/ai-denoise-evidence/verification.json`, `quality*/quality.json`, `native-ui-verified/native.json`, `native-ui-verified/*.png`, `cli-verification.json`, regression TRX files and retained reproduction inputs. These are evidence, not product dependencies.

## Consumer map

| Consumer class | Coverage |
| --- | --- |
| Direct source preparation / cache / model store | Actual CPU/CoreML models plus real-ORT tiny-graph boundary tests; missing/corrupt imports fail closed |
| Alternate CPU/GPU render entry points | Prepared-source CPU/Metal parity, native preview and full 16-bit export |
| Preview, thumbnails and cancellation generations | Scheduler transition regressions, UI context regression and native cancel/photo-switch workflow |
| Persisted settings / undo / presets | Existing schema/command storage; native undo, redo and project reopen; no parallel settings store |
| CLI / export logs / native deployment | Shipped CLI install/import/offline export, parsed PNG/ICC/source hashes and actual model reports; native macOS smoke and Windows cross-publish |
| Documentation / redistribution | Current ADR, survey boundary, build instructions, architecture, history and bundled ONNX/DirectML terms |
| Unexercised boundaries | Actual Windows DirectML execution, physical OS focus/input while console locked, Developer ID/notarization and independent multi-camera sensor-RAW quality benchmark |
