# Grain, neutral-point and Zone implementation plan

**Goal:** Continue the merged `dev-filmlike-fix` implementation and deliver the feasible interactive workflows from [the survey](../../grain-white-balance-survey.md), with real CPU/GPU rendering, reversible edits and honest data-domain limits.

**Authority:** User requested committing the current repairs, merging the feature branch, reviewing its implementation, then implementing all feasible survey plans. Later delivery order: complete and commit the survey before repairing `paste-10` and `paste-11`; verify and commit those deferred fixes, then push all task-related commits only when the fixes are ready. This plan records implementation scope, not a new license or camera-calibration claim.

**Architecture:** Reuse the existing parameter schema, `ModuleParams`, CPU/WGSL passes, `PreviewCanvas.ActiveTool`, and `ProjectService.SetValues`. A bounded analysis service samples the developed source, not the displayed bitmap; a photo-tools panel previews proposals without persisting them until acceptance. No inference framework, model download or new dependency.

**Tech stack:** .NET10, C#, Avalonia12, CommunityToolkit.Mvvm8.4.2, WebGPUSharp0.5.7/Dawn, WGSL. Existing RAW2-code and film/grain64-code pixel tolerances remain unchanged.

## Reviewed incoming implementation

Incoming commit `2b28a173ab948b055830485154ba68f3eef4b4df` replaces blocky grain with signed soft discs, integer clump density and correlated chroma; adds size-dependent softness; and prevents clarity from amplifying shadow chroma. It does not implement white-balance sampling, local skin assistance or Zone tools. Its grain coordinates and radius still depend on cropped output size; no selectable correlated-field/optical model or seed UI is present. These are continuation work, not grounds to discard the branch.

## Feasibility and acceptance

| ID | Survey plan | Delivery / acceptance |
| --- | --- | --- |
| G1 | Continuous / multi-scale correlated grain | Selectable deterministic model, spatially smooth isotropic field, shared CPU/WGSL math; seed and shadow/midtone/highlight response controls. Existing clump model remains selectable. |
| G2 | Optical Poisson / Boolean model | Independent mathematical implementation (no GPL reference-code copying), explicit aperture/sample-quality controls, bounded deterministic sampling, CPU/GPU parity; describe it as a 2D optical-statistical model, not complete film chemistry. |
| G3 | Reproducible preview/export/crop grain | Source-relative coordinates survive preview downscaling, crop, orientation and readback bands; finite pixel footprint is considered rather than merely resizing the cell. Softness and source geometry have observable regressions. |
| G4 | Calibrated stock scans / exact stock identity | **Blocked by input:** no owned/licensed, calibrated multi-exposure stock scans were supplied. Do not bundle invented/proprietary textures or label procedural presets as exact stock/ISO reconstructions. Existing generic models remain usable without these assets. |
| W1 | Neutral patch | Click or drag a region; bounded robust statistics reject invalid/outlier/processed clipping proxies, report sample count/dispersion; forward-fit `RawWhiteBalance.MatrixFor` in2500–9900K / Tint±150; warn at bounds or residual. One accept = one undo; cancel changes nothing. |
| W2 | Automatic candidates | Present multiple spatial candidates and uncertainty; no automatic brightest-is-white rule and no forced high-confidence answer for monochromatic/mixed-light scenes. User accepts or resamples. |
| W3 | Gray-card / reference workflow | Neutral-card patch uses W1; capture a reference patch chromaticity and solve another patch to it, clearly relative to the developed-image domain. No claim of sensor-domain or spectrally calibrated illumination recovery. |
| W4 | Independent skin assistance | User-selected feathered local region, independent warmth/tint/strength plus optional reference chromaticity; real CPU/GPU pass, no global-WB mutation, no fixed universal skin color, race-safe preview/accept/cancel. |
| Z1 | Zone measurement and placement | Scene-linear RAW sample, `Zone=5+log2(Y/0.18)`, III/V/VIII placement through exposure; negative/nonfinite/black samples yield a clear unavailable result. No WB changes. |
| Z2 | Shadow/highlight span | Retain two sample anchors, show current/target span; optional monotonic log-luminance curve fits the requested placements while preserving chromaticity. Reject degenerate/reversed anchors and warn when bounded parameter range cannot satisfy them. |
| U1 | Real interaction and persistence | Sampling view explicitly pauses creative geometry/effects so displayed positions correspond to the developed source plus current crop/orientation. Restore normal preview afterward. Focus/source changes invalidate proposals; native Escape/cancel/tool replacement cannot apply stale edits. Accepted settings survive save/reopen and one undo restores all changed fields. |
| V1 | Verification | Deterministic math/boundary regressions, real Metal CPU/GPU + crop/band/export smoke, existing solution suite, expanded-WGSL validation, real desktop UI screenshots/interactions; Windows driver execution remains unverified without a runner. |

## Data and safety floor

- `Scene.Image` already includes as-shot WB, camera/look transforms, possible HueSat/highlight reconstruction/lens warp and resampling. Forward-fit for rendered consistency; do not pretend its inverse recovers clean sensor samples.
- No sensor saturation mask exists at this boundary. Any clipping warning is explicitly a processed-image proxy; unknown sensor clipping stays unknown.
- Sampling uses a temporary view without creative warps. The crop affine maps displayed output back to the immutable developed source, including source orientation already applied during decode.
- Grain is late creative output, separate from RAW noise/ISO. Physical paper reflectance, scene exposure, negative density and display percentage are not interchangeable.
- Source files are read-only. All persistent values pass through the existing schema and one `SetValues` command; no hidden calibration assets, irreversible migration or new project format.

## Slices and shared integration

1. **Shared contracts (integration owner):** schema/parameter/source-reference fields and CPU/GPU pass integration; preserve all call sites.
2. **Grain:** extend incoming implementation with models, controls, coordinate/footprint handling and meaningful regressions. Do not edit other photo-analysis algorithms.
3. **Analysis:** bounded source sampling, forward WB/reference solving, automatic candidates and Zone arithmetic/curve fitting; no UI or rendering edits.
4. **Local rendering:** skin-region and Zone luminance CPU/WGSL passes; no global-WB changes or classifier dependencies.
5. **App:** one integrated photo-tools panel and region canvas tool, proposal lifecycle, reversible acceptance, references, source-change invalidation and localization. No direct source-array mutation.
6. **Integration/verification:** run checks once implementation slices settle; repair actual failures, update survey status/user docs from observed results, retain ignored evidence and remove owned throwaway drivers.

Every implementation worker skips build/lint/tests/formatters mid-flight. Parent owns fail-before characterization and final integrated execution. Permanent tests cover consumer-visible boundaries and transitions, not source text, forwarding or incidental default constants.

## State ledger

| Item | State | Evidence |
| --- | --- | --- |
| Survey/CI repair commits | done | `9db099e`, `4abd094`; no push |
| Requested branch merge | done | `git merge --no-edit origin/dev-filmlike-fix` succeeded with ort; grain pipeline auto-merged |
| Incoming grain runtime | verified | 9 grain/clarity checks passed against merge; real Metal RAW+Portra+crop/uncrop/export smoke now max1/65535 |
| G1/G2/G3 | verified on available host | Source-relative three-model CPU/WGSL implementation, bounded Poisson/Wendland correlated field and optical coverage; Metal render/crop/band/proxy smoke and native model/seed/quality controls exercised |
| G4 | blocked | Missing licensed calibrated stock data; no fabricated replacement |
| W1/W2/W3 | verified on available host | Bounded forward fit, three distinct spatial cast candidates, en/zh-TW candidate cards, relative cross-photo references, transient preview/accept/undo; repeated skin-reference acceptance regression failed before/passed after |
| W4 | verified on available host | Real local CPU/WGSL adjustment and native region/slider/acceptance, independent of global WB |
| Z1/Z2 | verified on available host | Native III/V/VIII placements, shadow/highlight span, one undo and stable repeated curve fit |
| U1/V1 | verified reachable paths; OS focus blocked | Release build 0 warnings/errors; solution 1307 passed, 319 skipped, 0 failed; 30 expanded WGSL units passed. Native window/automation/pointer-routing: 147 passed, 0 failed, source SHA-256 unchanged. Real OS activation/deactivation blocked by locked console; no unlock attempted |
| Older RAW+Portra+crop Metal divergence | resolved on merged path | Same preserved 64×48 fixture/program now passes all crop/uncrop transitions and full export at max1/65535; no Windows inference |
| Three Windows CI failures | external verification blocked | No local Windows/VM/SSH/authenticated CI; native removal reason still needed; do not claim closure from Metal |

### Baseline and consumer map

- Before new rendering implementations, `SurveyRenderingTests` failed on both missing behaviors: cropping changed the underlying grain (`28075` versus `24000`), and the selected local warmth adjustment did not alter the center pixel. Receipt: ignored `dist/survey-evidence/baseline/`.
- Shared parameters flow through the existing schema and `ModuleParams`; no new persistence format, dependency or source-file writes.

| Consumer | State | Required final observation |
| --- | --- | --- |
| CPU rendering | verified | Three models, source-coordinate crop/rotation and proxy attenuation; local region/chromaticity/alpha and scene-linear anchors |
| GPU rendering | verified on Metal | Actual Apple M3 Metal, all grain models, skin/Zone, RAW+Portra crop/uncrop and forced banded readback; existing tolerances retained |
| Image loading / proxy geometry | verified | Oriented TIFF/JPEG/DNG reference dimensions survive decode/downscale and actual TIFF export |
| App / native input | partial | Real native window and visible controls exercised via automation peers and PreviewCanvas pointer/key routing; OS focus/deactivation unavailable while console locked |
| Undo / save / reopen | verified | WB/local skin/Zone accept is one undo record; accepted multi-field and grain settings survive reopen |
| UI localization | verified | zh-TW/en photo tools, candidate confidence/residual and grain schema controls exercised; screenshots inspected |
| Existing fixtures / CI | verified available host | Final solution 1307 passed / 319 skipped / 0 failed; RAW 618 passed / 293 external-corpus skips, Core 506 passed / 26 platform/corpus skips, App 183 passed. Expanded WGSL 30 passed; CI configuration unchanged for survey |
| Stock calibration / Windows driver | blocked | Licensed calibrated scans and actual Windows runtime/CI remain unavailable |


### Current runtime observations

- Native survey driver used the production Avalonia macOS platform, actual Apple M3 Metal rendering, automation peers, and routed PreviewCanvas pointer/key events; it was not a headless UI suite or physical-mouse/OS-keyboard test. Of the reachable checks, 147 passed with no failed checks. Candidate-card, grain and 45° source-region surfaces were inspected from native-window render captures.
- `CGSessionCopyCurrentDictionary` reported `CGSSessionScreenIsLocked=1`, and the frontmost application was `loginwindow`. Real OS activation/deactivation could not be exercised. The driver reports this as blocked and its overall native result as partial, rather than fabricating a pass or changing product behavior.
- The repeated skin-reference scenario visibly reproduced a reversed correction before the fix; its permanent App regression failed with chromaticity error about 0.180, then passed after sampling without the replaced `skin` adjustment. The native repeated-acceptance scenario also passed after the fix.
- Final review reproduced a rounded-proxy grain coordinate error of 12247/65535 under quarter rotation; per-axis affine scaling was corrected and the new consumer pixel regression passed. A separate 45° drag omitted selected source corners; four-corner bounds and projected-region drawing now pass the regression and native surface check.
- Bounded analysis previously dropped a masked extreme-WB pixel whose actual CPU linear-stage luminance was positive. The regression failed before/passed after matching the existing renderer's scalar normalization. The masked forward-fit target now comes from actual CPU WB/semantic/local stages plus independently derived Basic gains, not from the sampler's own mirror.
- Actual Metal render/export differences were at most 1/65535, banded output was identical, and source pixels/files were unchanged. Isolated skin-only and Zone-only GPU passes, plus rounded-proxy GPU mapping, each matched CPU within 1/65535; this exercises the uniform's consumer rather than pinning field copies.
- Retained, ignored evidence lives under `dist/survey-evidence/`; executable throwaway drivers are not part of the product or a required future entry point.

