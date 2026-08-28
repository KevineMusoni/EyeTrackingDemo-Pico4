# Eye Tracking - Notes

## Main project crash (Surgical VR)
- SIGSEGV, null pointer, in Vulkan driver, ~130ms after XR session FOCUSED.
- Likely cause: two foveation features enabled at once in `OpenXRPackageSettings.asset`
  (Android) - Unity's `FoveatedRenderingFeature` + `MetaXRFoveationFeature`, both drive
  `XR_FB_foveation_vulkan`. Suspected conflict on first swapchain submit.
- Ruled out: eye tracking (OS-level, not app), video teardown (unrelated process), missing
  scripts in scene 

## Demo project setup (EyeTrackingDemo, Pico 4 Enterprise)
Cloned `picoxr/EyeTrackingDemo` → `E:\Unity Projects\EyeTrackingDemo`. Fixed to get it running:

1. `Packages/manifest.json` - `com.unity.xr.picoxr` pointed to a dead path on the original
   author's PC → repointed to the embedded package folder already in the repo.
2. `JDK not found` → installed missing OpenJDK module via Unity Hub (2021.3.13f1).
3. Gradle build failed on a corrupted `debug.keystore` → killed 2 stuck Gradle daemons, renamed
   the keystore aside, Android tooling regenerated it.
4. Build succeeded → installed on headset (`PA8E50MGH2020230D`, package
   `com.DefaultCompany.PICOEyeTracking`).

## Eye tracking not working → fixed
- Error: `GetEyeTrackingDevice devices.Count` = 0.
- Headset's low-level eye/IPD driver (`gd32ipdservice`) was stuck failing 100% → **fixed by a
  full headset reboot**.
- Still failed after reboot → real cause: `com.picovr.permission.EYE_TRACKING` is a
  runtime Android permission the demo never requests, so it stayed denied. **Fixed by granting
  it manually via adb** and relaunching:
  ```
  adb shell pm grant com.DefaultCompany.PICOEyeTracking com.picovr.permission.EYE_TRACKING
  ```

  
- **Confirmed working now.**

## Vector vs Point (gaze data display). 
- `GetCombineEyeGazeVector` = gaze **direction** - updates continuously as eyes move. //modified
- `GetCombineEyeGazePoint` = gaze ray **origin** (near the eyes), not where you're looking -
  barely changes since it's relative to head, not gaze target. Expected to look static.
- Actual "what am I looking at" coordinate = raycast from origin along vector
  (`GazeTargetControl()` already does this via `Physics.SphereCast`, drives `Greenpoint`).
- **Fixed**: `EyeTrackingManager.cs` now also shows this as a "Target" line in the text panel
  (`hasGazeTarget`/`gazeTargetPoint` fields added, set inside `GazeTargetControl()`, shows
  "none" when nothing is hit this frame). All changes marked with `// ADDED`/`// MODIFIED`/
  `// MOVED` comments in the script.

---

# ✅ EYE TRACKING CORE SETUP - DONE

---

## Heatmap prototype to test

**Test object setup:**
- `HeatMapTestObject` = duplicate of `Mad Flower`, moved to local `x:6` so it doesn't overlap
  the original (they share a parent, so this guarantees separation in world space too).
- Its child `MadFlower` (the actual `SkinnedMeshRenderer` mesh, not the empty `MadFlower_Rig`
  bone container) got a **Mesh Collider**: mesh manually assigned (Skinned Mesh Renderers
  don't auto-fill it like Mesh Filters do) and **Convex unchecked** (required for
  `RaycastHit.textureCoord` to return UV data).

**Data side - `Assets/Scripts/MeshGazeHeatmap.cs`** (new script, on `MadFlower`):
- Accumulates heat into a runtime 512x512 texture, stamped in UV space wherever gazed at.
- Soft circular brush (falloff at the edges), builds up over time (`heatPerSecond`), colored
  via a blue→green→yellow→red gradient based on accumulated intensity.
- Perf note: uses `GetPixel`/`SetPixel` per stamp - fine for prototyping, not production-speed
  (would need `SetPixels32`/a compute shader if this becomes a real feature).

**Hook - `EyeTrackingManager.GazeTargetControl()`:**
- Calls `heatmap.StampAt(uv)` whenever the gaze hits an object with `MeshGazeHeatmap`.
- `Physics.SphereCast` (used for the main gaze hit-test) doesn't populate
  `RaycastHit.textureCoord` - only `Physics.Raycast` does - so a small supplementary raycast
  along the same ray gets the UV data, with a sanity check it hit the same collider.

**Display side - `MadFlower_HeatOverlay`** (duplicate of `MadFlower`):
- No Mesh Collider (can't intercept the gaze raycast - only the original mesh should).
- Scaled slightly larger (~1.01) to sit just above the original surface without z-fighting.
- Material: `Assets/Materials/HeatOverlay_MAT.mat`. Fully transparent where heat = 0, so the
  original mesh shows through untouched; only gazed-at spots show color.
- **BUG FOUND on-device**: originally set to `Universal Render Pipeline/Unlit` - wrong, this
  project's `GraphicsSettings.asset` has `m_CustomRenderPipeline: {fileID: 0}` (no SRP
  assigned, still Built-in Render Pipeline despite URP being an installed package). URP shader
  rendered as solid magenta/purple (Unity's shader-error fallback) instead of transparent.
  **Fix: use `Unlit/Transparent`** (Built-in RP shader) instead.
- `MadFlower`'s `MeshGazeHeatmap.overlayRenderer` field points to this object's
  `SkinnedMeshRenderer` (`Start()` assigns the heat texture to its material there).


## Brush size now auto-scales to object size
- Problem: `brushRadiusPixels` (fixed pixel count) made the dot look bigger on bigger objects
  and smaller on smaller ones, since UV space is always 0-1 regardless of physical object size
  (texel density differs per object/per spot on the mesh).
- Fix in `MeshGazeHeatmap.cs`: replaced `brushRadiusPixels` with `brushRadiusWorldMeters` (a
  real-world size, default 0.03 = 3cm radius) + `minBrushRadiusPixels`/`maxBrushRadiusPixels`
  safety clamps. New `ComputeBrushRadiusPixels()` measures the actual hit triangle's world-space
  area vs its UV-space area (via `RaycastHit.triangleIndex` + the Mesh Collider's `sharedMesh`
  vertices/UVs/triangles) to get local texel density, then converts the desired world radius
  into the correct pixel radius for that exact spot on that exact object.
- `StampAt()` signature changed from `Vector2 uv` to the full `RaycastHit` (needs
  `triangleIndex`, not just `textureCoord`) - updated the call site in
  `EyeTrackingManager.GazeTargetControl()` to match (`heatmap.StampAt(uvHit)`).
- Script now has `[RequireComponent(typeof(MeshCollider))]` since it reads the collider's mesh.
- Old `brushRadiusPixels` Inspector value on existing objects is now orphaned/ignored (Unity
  keeps but ignores removed serialized fields) - no action needed, new fields just use their
  C# defaults unless changed in the Inspector.

## Mesh Read/Write Enabled required for the new brush-size calculation
- `ComputeBrushRadiusPixels()` reads `mesh.triangles`/`.vertices`/`.uv` directly in C# - Unity
  strips that CPU-readable mesh data from the build by default to save memory.
- Error seen: `Not allowed to access triangles/indices on mesh 'MadFlower' (isReadable is
  false; Read/Write must be enabled in import settings)`.
- Fix: select `Mad Flower.fbx` → Inspector → Model tab → check **Read/Write Enabled** → Apply.
- Unity's built-in primitive meshes (e.g. the Cube's) are typically Read/Write enabled by
  default already, so this is specifically a per-imported-model setting, not project-wide.

## Gaze dwell-time report (button/blink approach tried, then simplified)

**Why**: a live "Target" readout has an inherent problem - looking at a report to read it
changes what the report is about, since it's driven by current gaze. Fixed by switching to
**accumulated dwell time** (doesn't get erased by looking away) instead of a live single value.

**Tried and removed**: a gaze+blink-activated button (`ReportButton.cs`, blink detection via
`PXR_EyeTracking.GetLeftEyeGazeOpenness`/`GetRightEyeGazeOpenness`) to toggle the report view
on/off. Could not get the blink-select interaction to register reliably on-device (live
Vector/Point/Target fields kept updating fine, confirming the base tracking was healthy - the
gaze+blink trigger specifically wasn't firing). Decided it wasn't necessary and removed it
entirely (script deleted, GameObject deleted from scene, all related code stripped back out).

**Final, simpler design**: no button, no blink detection needed at all. The dwell report is
now **always shown** on the text panel, replacing the old live Vector/Point/Target readout.
Since it's an accumulating total (not a live snapshot), there's no "looking at the report
erases the data" problem - the panel just keeps growing the same running totals every frame.

## 5-point rotational gaze calibration (rebuilt into 2-phase: train + validate)

**Why**: gaze felt slightly inaccurate. The demo's pre-existing joystick offset
(`combineEyeGazeOriginOffset`) only corrects the gaze ray's starting *position* - real
eye-tracking bias is usually *angular*, which a position-only fix can't correct. Built a
calibration step instead - later hardened into two phases once it became clear a quality score
based only on training data would be misleading (grading a correction on the exact data used to
build it is always optimistic, it doesn't test whether the correction actually generalizes).

**File**: `Assets/Scripts/CalibrationManager.cs`. Lives in its own scene (`Calibration.unity`,
own copy of the XR rig), runs before `EyeTrackingManager` even exists - reads `PXR_EyeTracking`
directly, no dependency on `EyeTrackingManager` at any point during calibration itself.

**Phase 1 - Calibrating (5 training points)**:
- Marker shown at 5 known positions (center + 4 corners, `calibrationPointLocalOffsets`).
- Per point: settle (`settleTimeBeforeSampling`, 1s) → average raw gaze samples for the rest of
  `dwellDurationPerPoint` (2s total) → `Quaternion.FromToRotation(avgRawDir, trueDir)` →
  `Quaternion.Angle` gives that point's correction angle (the bias).
- Reject if correction angle > `maxPlausiblePointCorrectionDegrees` (20°, heuristic - "were they
  even looking at the marker"). Accepted points blended into `CalibrationCorrection` via
  incremental `Quaternion.Slerp`.
- **Gate 1**: needs all 5/5 points accepted. Fail → red "Calibration Failed - Retrying...",
  `RetryCalibration()` resets everything, restarts from point 1.

**Phase 2 - Validating (2 held-out points, added today)**:
- On gate 1 pass, `CalibrationCorrection` is FROZEN. Marker shows 2 new points
  (`validationPointLocalOffsets`) never used in phase 1.
- Same dwell/settle/sample + 20° rejection gate, but instead of blending, `RecordValidationResidual`
  applies the frozen correction to the raw gaze and measures `Vector3.Angle(correctedDir, trueDir)`
  - the RESIDUAL (error still left after correction), averaged over both points.
- This residual is the number that matters - a proper train/test split, not the optimistic
  training-set bias. Both numbers logged side by side in `adb logcat` for comparison.

**Scoring**: residual → `GetBiasQualityLabel` (Excellent ≤1.5°, Good ≤3°, Fair ≤5°, Poor >5°,
via `PoorBiasCeilingDegrees`/`GoodBiasCeilingDegrees` constants) + `GetBiasQualityPercent`
(linear, 0°=100%, 5°=0%). **Bands are a judgment call**, not independently validated for this
device - looked into citing published VR eye-tracker accuracy research to back them, found the
sources weren't good enough to lean on (one was just a manufacturer spec, never independently
verified; the other was a real measurement but n=11 and different hardware, a Tobii-based
headset, not Pico's own sensors) - removed the citations rather than keep weak sourcing dressed
up as evidence.

**Gate 2 (added today)**: quality now gates progression too, not just informational. Only
Excellent/Good (≤3°) proceeds to `LoadMainScene()`. Fair/Poor → orange "Calibration Quality Too
Low - Retrying...", `RetryCalibration()` fires again. NO retry cap by design - user confirmed
this is intentional (precision-sensitive use case, an accurate result matters more than a fast
one), not something to add later.

**Bug fixed today**: `RetryCalibration()` wasn't resetting `IsCalibrated` back to `false` - was
harmless before (retry only ever followed a phase-1 failure, where `IsCalibrated` hadn't been
set true yet), became a real bug once phase-2 quality-gate retries could happen *after*
`IsCalibrated = true` was already set.

**Verified on-device today**: captured real `"Poor (0%) - Retrying"` and `"Excellent (85%) -
Passed"` screenshots, confirming the gate genuinely rejects/accepts correctly, not just in
theory. Cropped versions in `Docs/Screenshots/`, embedded in README.

**Handoff unchanged**: `CalibrationCorrection`/`IsCalibrated` still `static`, reset every launch
(no persistence - multiple people may share the headset). `EyeTrackingManager.cs` reads
`CalibrationManager.CalibrationCorrection` directly by class name once the main scene loads.

**`EyeTrackingManager.cs`**:
- `dwellTimes` (`Dictionary<string, float>`) - accumulates seconds gazed per object, but
  ONLY for objects with `MeshGazeHeatmap` (reuses the same `TryGetComponent` check that
  already gates heatmap stamping) - i.e. just the two test objects, nothing else in scene.
- Text panel now always shows:
  ```
  Gaze Report (seconds looked at each object):

  MadFlower: 4.2s

  HeatmapTestObject_Cube: 2.7s
  ```

## Heatmap code, summarized

**`MeshGazeHeatmap.cs`**
- `Start()`: builds a blank, fully-transparent texture, hands it to the overlay renderer's
  material, logs whether setup succeeded.
- `StampAt(hit)`: converts the hit's UV to a pixel position, computes the correct brush size
  spot, paints a soft circular blob there, accumulating over time (capped at "hot").
- `ComputeBrushRadiusPixels()`: looks up the exact hit triangle, compares its real-world size
  to its UV size, converts the desired real-world brush radius (10cm default) into the right
  pixel count for that specific object/spot - same physical dot size everywhere.
- `HeatGradient()`: maps 0-1 intensity to a color, blue → green → yellow → red.

**`EyeTrackingManager.cs` hook**
- Only fires if the gazed-at object has `MeshGazeHeatmap`.
- `SphereCast` (normal gaze detection) has no UV data, so a second plain `Raycast` along the
  same ray gets it, with a same-object check before trusting the result.
- Passes that result to `StampAt()`.

Flow: hook (data) → texel-density sizing → StampAt (painting) → overlay material (display).

## Known but unfixed
- SDK bug: `PXR_BuildProcessor.cs` writes a manifest meta-data value as the literal string
  `"false/true"` instead of an actual bool. // not the blocker

## Repo
- Fork pushed to personal GitHub: `github.com/KevineMusoni/EyeTrackingDemo-Pico4`.
- `origin` = personal repo (push access). `upstream` = `picoxr/EyeTrackingDemo` (original
  sample, read-only, kept around in case future upstream updates are worth pulling).
- `.gitignore` updated - `ET_*_BackUpThisFolder.../ET_*_BurstDebugInformation.../UserSettings/
  /.vscode/` weren't being ignored before (would have bloated the repo with per-session Burst
  debug output and per-machine editor state).
- README overhauled: fork attribution, "what was added" summary up top, plain-language +
  technical explanation of calibration, Mermaid flowchart, geometry diagram
  (`Docs/calibration-geometry.svg`), real on-device screenshots (not Pico's originals) for
  calibration pass/fail, the heatmap, and avatar eye-openness. Collapsible sections removed
  (were easy to miss), em dashes removed project-wide, unreliable research citations removed.

## Calibration - review feedback (supersedes the speculative list below)

**Verdict on today's work**: holdout validation + freezing the correction before testing is a
good improvement, confirmed as the right direction.

**Priorities, in order:**
1. Fix PXR call return-value/validity checking - failed reads currently return zero-valued data
   that may be getting counted as a valid sample (potential real bug, not just a nice-to-have). // CalibrationManager.cs:164-166:return bool and capture it.  (GetHeadPosMatrix, GetCombineEyeGazeVector, GetCombineEyeGazePoint, and GetCombinedEyePoseStatus which isn't called at all right now), and if any of them come back false, skip that frame entirely.
   Tested: added a counter/log line (e.g., "rejected N invalid reads this session")
2. Move the correction into head/camera-local coordinates, applied *before* converting to worldspace - current world-space approach may not hold up across head movement or XR rig changes.
// done -> CalibrationCorrectionLocal (ln 111), lastValidHeadPoseMatrix = headPoseMatrix (tracked per successful frame)
3. Validation must require ALL points to have sufficient data - no training-data fallback (this
   directly contradicts the fallback addimed in `HandleValidationComplete`, revisit that).
   // edited so that now it requires 4/4 not just > 0.

4. Add 4-point validation (top/bottom/left/right) instead of 2 same-diagonal points, plus
   per-point precision (not just bias).
   //done


5. Compare corrected vs uncorrected performance during validation - if the correction doesn't
   actually help, fall back to PICO's raw gaze output rather than applying a bad correction.
   //done -> validationUncorrectedResidualSum (reuses rawAngle). Falls back to identity if
   corrected residual isn't better than uncorrected.



**Full list of changes requested:**
- [x] Validity-check every PXR eye-tracking call's return value, not just assume success.
  // done -> GazeReading.TryReadRawGaze
- [x] Fit/apply the correction in head-local coordinates, not world space.
  // done -> CalibrationCorrectionLocal
- [x] Validation requires every point valid - remove the training-data fallback.
  // done -> requires validationPointLocalOffsets.Length/Length, no fallback
- [x] Replace the 2 same-diagonal validation points with 4 points (top/bottom/left/right).
  // done -> item 4
- [x] Keep individual per-point samples (not just their average) - needed to compute PRECISION
  (consistency) separately from bias (accuracy), not bias alone.
  // done -> currentPointRawSamplesLocal, item 4
- [x] Gate on both the mean AND the worst-performing point - a bad region of the visual field
  shouldn't be hideable behind a good average.
  // done -> validationWorstResidualDegrees, strict 3° ceiling on worst point too
- [x] Compare corrected vs uncorrected gaze during validation; keep PICO's raw output if the
  correction doesn't actually improve things.
  // done -> item 5
- [x] Replace the sequential `Slerp` blend with a simultaneous least-squares rotation fit
  (once coordinate-frame/validation issues above are fixed - Slerp can stay temporarily).
  // done -> AverageQuaternions (Markley's method, 4x4 accumulator + power iteration).
  acceptedPointCorrections collected per-point, combined once in HandleCalibrationComplete.

- [x] Adaptive sampling instead of a fixed 2s dwell - more validation coverage, less total time.
  // done -> live running precision check in Update(), stableFrameCount. Point advances early
  once precision <=0.5deg for 10 consecutive frames; dwellDurationPerPoint stays as timeout
  ceiling. Untuned starting values.

- [x] Retry only the failed point, not the whole 5/7-point sequence and headset-fit guidance (this changes the "no cap, restart everything" design).
  // done -> per-point retry: RecordCurrentPointCorrection/RecordValidationResidual return
  bool, AdvanceToNextPoint(bool) only advances index on success. Kept no-cap (user confirmed
  again) - fit guidance (perPointFitGuidanceThreshold=3,
  fullSequenceFitGuidanceThreshold=3), never stops retrying. HandleCalibrationComplete/
  HandleValidationComplete's old failure branches removed as dead code (now unreachable by
  construction). RetryCalibration only fires for the quality-gate case now. ex: if closed eye at one point, only the failed point will repeat/ fail.

- [x] Remove the on-screen percentage entirely - called out as arbitrary/misleading Replace with a plain "Tracking ready" message; keep degree
  measurements in logs only, not user-facing.
  // done -> pass message is now "Calibration complete" (was "{label} ({percent}%)", briefly
  "Tracking ready" before user asked for this wording), retry message is "Calibration Quality
  Too Low - Retrying..." (dropped label+percent too, opt 1). Both qualityLabel/qualityPercent
  still computed and logged in adb logcat, only the on-screen ShowResult calls changed.
  Screenshots (Docs/Screenshots/calibration-*.jpeg) now stale, due for a recapture.

  <!-- Different scene -  on launch, before calibration -->
- [x] Add a headset-position/fit guidance step before calibration, similar to Ocumen's
  "Position Guide" stage (see earlier research this session on `GetLeftEyePositionGuide`/
  `GetRightEyePositionGuide` - Neo3 Pro Eye only per SDK docs, unverified on Pico 4 Enterprise)
  // done (implementation) -> New PositionGuideManager.cs + PositionGuide.unity scene
  (duplicated from Calibration.unity, own XR rig), loads FIRST in Build Settings before
  Calibration.unity. UI: Frame + LeftEyeIndicator + RightEyeIndicator (plain placeholder
  squares, no sprites) + Continue button.

  Device support corrected: bundled SDK (2.1.4, Mar 2023) doc comment says "Neo3 Pro Eye only"
  - checked a newer SDK build's source directly, comment updated to "Neo3 Pro Eye, PICO 4 Pro,
  and PICO 4 Enterprise" - bundled comment is just stale, API is officially supported here.

  Session 1: VERIFIED WORKING - real, distinct, stable per-eye values (left ~0.36/0.67, right
  ~0.58/0.62, jitter ~0.01-0.02 frame to frame).

  Every session SINCE: stuck at exactly (0,0,0) both eyes, valid=true. Ruled out one cause at a
  time: not any later script change (reverted to byte-for-byte original, still stuck); not
  fixed by full device reboot; not fixed by re-granting EYE_TRACKING permission; not fixed by
  SDK reinstall; not the frozen controller-ray issue (confirmed separate/pre-existing). ROOT
  CAUSE found in adb logcat, outside this app entirely: gd32ipdservice (native eye/IPD driver)
  failing UART comms with the physical sensor on a ~10s retry loop (uart_open -> immediate
  Uart_Close -> fixed "79 00 00 79" response -> retry_cnt=3 ret=4). Hardware/driver fault, not
  fixable from app code. NOT shelved - kept in active Build Settings, implementation is correct
  and verified once, this is "blocked on external hardware issue" not a design dead end.

  Still TODO once hardware issue clears: swap in real sprites (frame outline + dot/aim
  graphics, Tobii's sample has reference art not to be reused directly), tune
  movementMultiplier against real observed ranges, consider holding last-good position on a
  (0,0,0)-looking dropout frame instead of snapping the dot to the corner, re-add
  auto-stability-advance (was implemented then reverted during debugging, see git history).

  

**Target: two modes**
1. **Rapid/investor mode** - ~8-12 seconds, 5 calibration points + 4 validation points.
2. **Full Validation mode** - more points/samples, detailed per-point bias + precision + validity.

**Reference data point**: calibration success criteria was **≤3° bias AND
≤1° precision** - notably a two-part criterion (both bias and precision), whereas today's system only measures bias/residual, precision isn't tracked at all yet

## 3D stereo surgery video on `SurgeryVideoScreen` (with gaze heatmap over it)

**Goal**: replace the test objects on `SurgeryVideoScreen` with a real 3840x1080 side-by-side
stereo H.264 video (`LAR_Surgery_3D_Robot_SEALG_v01.mp4`), while keeping the existing
`MeshGazeHeatmap` overlay working on top of it, unchanged.

**Attempt 1 - Unity `VideoPlayer` + `RenderTexture` → material**: prepared/played cleanly
(`prepareCompleted`/`started` fired, correct 3840x1080 frame size, zero errors in logcat) but
never visibly displayed a frame on-device - screen stayed black, heatmap still worked fine on
top of it. Root cause not fully pinned down but matches known Unity Issue Tracker reports of
`RenderTexture` format/sampling problems on Android+Vulkan.

**Attempt 2 - AVPro Video v3**: same symptom (clean decode, nothing visible). Source-level
read of AVPro's `Resampler.cs` found it uses a plain `Graphics.Blit` with no OES-aware shader to
copy Android's external/YCbCr video texture into a regular `RenderTexture` - silently produces
blank output on this device's texture format. Disabling the resampler didn't fix display either.
**Abandoned and fully reverted** (all AVPro assets/scripts removed, confirmed via `git status`
and grep for dangling references) rather than keep debugging a third-party plugin against an
undocumented device-specific texture quirk.

**Attempt 3 - PICO compositor-layer External Surface (current, working)**: bypasses Unity's
render/texture/shader pipeline entirely - hands the video decoder's raw Android `Surface`
straight to PICO's system compositor via `PXR_OverLay` (`Unity.XR.PXR`, already in the bundled
SDK). Playback itself is driven by PICO's own reference ExoPlayer-backed plugin
(`playvideo.jar`, class `com.pico.exoplayerdemo.PlayVideo`, from
`github.com/picoxr/Overlay-Demo-UnityXR`), called via raw `AndroidJNI`.

- New: `Assets/Scripts/SurgeryVideoOverlayPlayer.cs` (`[RequireComponent(typeof(PXR_OverLay))]`
  on `SurgeryVideoScreen`) - sets `overlayShape = Quad`, `isExternalAndroidSurface = true`,
  `externalAndroidSurface3DType = LeftRight` (matches the side-by-side stereo source), calls
  `overlay.CreateExternalSurface()`, then on the `externalAndroidSurfaceObjectCreated` callback
  calls `PlayVideo.playVideo(activity, path, surface)` via JNI with
  `Application.persistentDataPath + "/" + videoFileName`.
- New: `Assets/Plugins/Android/` - `playvideo.jar` + 5 ExoPlayer 2.11.5 `.aar`s, downloaded
  directly from PICO's official repo (verified valid ZIP archives via magic-byte check).
- Removed: the old `VideoPlayer`/`VideoPlayerDebugLogger` components from `SurgeryVideoScreen`
  (would have decoded the same file twice for no reason).
- `PXR_OverLay` auto-disables its own GameObject's `MeshRenderer` on Android builds, so
  `SurgeryVideoMAT`/`SurgeryVideoRT`/the custom stereo shaders are now dead on-device (left in
  place only for Editor preview - not cleaned up yet).
- Video file must exist at `Application.persistentDataPath` on-device
  (`/storage/emulated/0/Android/data/com.DefaultCompany.PICOEyeTracking/files/`) -
  confirmed present via `adb shell ls -la`, pushed with `adb push` if missing.
- **Confirmed working on-device**: video decodes and displays correctly, stereo pair visible.

**Bug found: heatmap invisible once video started displaying**. `PXR_OverLay.overlayType`
defaults to `Overlay`, which composites the video **in front of the entire normal eye-buffer
render** - not just in front of objects behind it in 3D space. Since `SurgeryVideoScreen_HeatOverlay`
(the heatmap quad) still renders normally through Unity's regular pipeline into that eye buffer,
it was being drawn correctly but buried underneath the compositor's video layer regardless of
its actual depth. Dwell-time tracking on `SurgeryVideoScreen` kept working throughout (proves the
gaze raycast/UV pipeline was never broken - purely a compositing-order display issue).

**Fix attempt 1 - `Underlay`, reverted**: set `overlay.overlayType = PXR_OverLay.OverlayType.Underlay`
so the video composites *behind* the normal eye-buffer render instead, letting the heatmap quad
draw on top of it normally. Broke the video entirely (black screen) - `Underlay` requires the
app's own eye-buffer to have alpha=0 "hole-punch" pixels where the video should show through,
but `PXR_OverLay.Awake()` unconditionally disables the `MeshRenderer` on its own GameObject, so
nothing draws there at all - the region falls back to the camera's opaque clear (black), which
blocks the underlay completely. Reverted back to the `Overlay` default.

**Fix (actual) - second independent compositor layer for the heatmap**: gave the heatmap its own
`PXR_OverLay` (`Assets/Scripts/SurgeryHeatmapOverlayLayer.cs`, on `SurgeryVideoScreen_HeatOverlay`,
a separate GameObject from `MeshGazeHeatmap`/`SurgeryVideoScreen` so the data-recording path is
untouched), `TextureType.DynamicTexture` with `layerDepth = -1` (video stays at the default `0`),
fed via `PXR_OverLay.SetTexture(heatTexture, dynamic: true)` from `MeshGazeHeatmap.Start()` once
the runtime heat texture exists. Two independently-stacked compositor layers ordered by depth,
sidestepping Unity's render/alpha pipeline (and the `Underlay` hole-punch problem) entirely.

Still invisible after this, though - traced through `PXR_OverlayManager.cs`'s per-frame
submission loop and found the real bug: the heatmap's `PXR_OverLay` had `isExternalAndroidSurface`
stuck at `1` in the saved scene (confirmed by reading the raw YAML, not inferred from symptoms) -
likely inherited from copying/pattern-matching the video layer's component when the Editor
auto-added it. External-surface layers skip `CopyRT()` (the method that actually uploads a fed
texture's pixels to the compositor), so the layer existed and had a texture assigned, but nothing
ever reached the screen - independent of `layerDepth`, which is why testing `1` and `-1` both
"failed" identically and wasted a round of debugging before the real cause surfaced. Fixed both
the scene data (`isExternalAndroidSurface: 1` -> `0`) and defensively in
`SurgeryHeatmapOverlayLayer.Awake()` (`overlay.isExternalAndroidSurface = false` now set
explicitly), so a stray Inspector value can't cause this silently again.
**Confirmed working on-device**: heatmap renders correctly on top of the video, live.

**Video pillarboxed (black bars on sides) - fixed**: `SurgeryVideoScreen`'s `Transform.localScale`
was `{x: 3.4, y: 0.96}`, a ~3.56:1 aspect ratio matching the *combined* double-wide 3840x1080
stereo file. But `PXR_OverLay`'s `Surface3DType.LeftRight` already splits that in half internally
and shows each eye its own 1920x1080 (16:9) half, so the quad was sized for the wrong frame,
leaving the correctly-proportioned per-eye image centered with black bars on both sides. Fixed by
resizing both `SurgeryVideoScreen` and `SurgeryVideoScreen_HeatOverlay` (kept aligned, since the
latter is a duplicate offset slightly in Z to avoid z-fighting rather than scaled up) to
`{x: 1.7066667, y: 0.96}` - true 16:9, same height, matching the per-eye frame.

## TODO

**Step 1 - save gaze data - done.** `MeshGazeHeatmap.cs` extended directly (no separate recorder
class): `recordSamples`/`recordingId` fields, samples appended inside `StampAt()` right where
uv/radius are already computed, `SaveRecording()` writes to
`GazeRecordings/{recordingId}_{timestamp}.json`. Periodic autosave (`InvokeRepeating`, default
5s) rather than `OnApplicationQuit()` alone - that callback never fires on `adb shell am
force-stop`, which is how this app normally gets closed during testing, so relying
on it alone would silently lose every session. Enabled on `SurgeryVideoScreen` only.

**Step 2 - review display - done, in `EyeTrackingDemo.unity` (not a separate scene).**
`GazeReviewScreen`/`GazeReviewScreen_Overlay` added directly to the main scene rather than a new
`GazeReview.unity` - simpler for now, revisit if/when the real sequential watch-then-review flow
needs its own scene. `MeshGazeHeatmap.StampAt()` split into `StampAt(RaycastHit)` (live) +
`PaintAt(uv, radius, amount)` (the actual painting, shared by both live and replay) so replay
doesn't need a live raycast. New `GazeReviewLoader.cs` loads the most recent (or a named)
recording and calls `PaintAt()` for every sample - full heatmap appears instantly, no animation.

Setup bugs hit and fixed along the way: `Overlay Renderer` field left unassigned (silently
meant nothing ever displayed); overlay quad had Unity's default `Standard` material instead of
`Unlit/Transparent` (new `GazeReviewOverlay_MAT.mat` created, mirroring `HeatOverlay_MAT.mat`);
`GazeReviewScreen_Overlay`'s transform diverged from the base quad's position/scale (dragged via
gizmo at some point) - realigned to match exactly, offset only in Z; initial `x: 6` placement
(copying the `HeatMapTestObject` convention) put it outside the room, moved to `x: 3`;
`GazeReviewScreen`'s own `MeshCollider` was live, so looking directly at the review screen added
its own new heat on top of the replayed recording - disabled the collider (`[RequireComponent]`
only requires it to exist, not be enabled), review is now a non-interactive display only.

**Fresh-build staleness - done.** `GazeRecordings/` lives in `Application.persistentDataPath`,
so it survives normal rebuilds the same way the video file does - `GazeReviewLoader` kept
showing whatever was last saved, potentially from a much older test. Fixed: auto-resolved loads
(blank `recordingFileName`) now get deleted after being read, so each recording is shown at most
once, on the very next launch after it was saved. Explicitly-named recordings (a future expert
reference, say) are exempt - only "most recent" auto-loads are consumed.

**Review not updating within the same session - done.** `GazeReviewLoader.Start()` only ever ran
once, at frame 0 - before anyone had watched anything that session, so no amount of watching
made it show new data; it could only ever show a PREVIOUS session's leftovers (or nothing).
Fixed: `Start()` now schedules the load via `Invoke(nameof(LoadAndDisplay), initialLoadDelaySeconds)`
(default 22s - a bit past the ~20s test clip's length plus a beat past the 5s autosave interval),
instead of loading immediately. Still a one-time load, just delayed long enough for this
session's own recording to actually exist on disk first.

**Heatmap kept growing after the video ended - done.** Gaze hitting the now-static screen after
the clip finished was still counted as "watching," pointlessly growing the heatmap/recording
with meaningless data. Fixed: new `autoStopAfterSeconds` field on `MeshGazeHeatmap` (`0` =
never stop, the default - test objects like `MadFlower` have no video and no "finished" moment).
Set to `20` on `SurgeryVideoScreen`, matching the trimmed test clip. Once elapsed time since
`Start()` passes that, `StampAt()` does one final save, cancels the periodic autosave, and goes
permanently inert for the rest of the session. **Caveat**: this is a fixed wall-clock number,
not tied to the video's actual playback state (still no way to query the native ExoPlayer's
position - same gap noted earlier for `videoTime`) - needs manual updating if the video length
changes, and would fire early/late if playback ever buffers or stalls.

**Caught during a code review**: the `initialLoadDelaySeconds`/`Invoke` fix above had silently
reverted back to an immediate-load `Start()` at some point (cause unknown - not from any edit
made here), which fully explained a heatmap showing up at only ~3s into a session when it should
have been impossible before ~22s. Only found by re-reading the full file rather than trusting
memory of what had already been fixed - reapplied. Worth periodically re-reading a file's actual
current state during a long debugging session rather than assuming earlier fixes are still there,
and confirming an on-device test is actually running a freshly rebuilt APK, not a leftover one
from before the latest script change.

**Heatmap painting performance - fixed, two passes.**

Pass 1, reported as "heatmaps are a bit slow moving" (live gaze felt like it lagged behind actual
eye position): matched what the file's own top-of-class comment had already flagged -
`PaintPixels()` called `Texture2D.GetPixel`/`SetPixel` individually for every pixel in the brush
circle (up to ~16,600 calls at the 64px radius cap), every single frame while gazing at an object
- real per-call API overhead, done synchronously on the main thread, competing with the XR render
loop. Fixed by giving each texture (`heatTexture`, `comparisonTexture`, `combinedTexture`) its own
plain `Color[]` backing array (`heatPixels`/`comparisonPixels`/`combinedPixels`) that
`PaintPixels()` now reads/writes via direct array indexing instead of the Texture2D API.

One bug caught while making this change (not yet in any build, fixed before it could ship):
`CombineComparisonBuffers()` used to compute its result into a local array and `SetPixels()` it
straight onto `combinedTexture`, without touching the new `combinedPixels` backing array. Since
the peak-divergence marker (`PaintAtColor` onto `CombinedTexture`) now paints through
`combinedPixels`, not the texture directly, it would have painted onto a stale blank buffer and
then overwritten the just-combined green/yellow image with it - marker visible, everything else
gone. `CombineComparisonBuffers()` now reads from `heatPixels`/`comparisonPixels` and writes
`combinedPixels` directly, keeping it as the source of truth.

Pass 2, reported as a black flash appearing for a couple seconds right as `GazeReviewScreen`
loaded a session's replay: the array fix above only sped up the per-pixel math inside each
`PaintPixels()` call - it didn't touch how often that call's `SetPixels`+`Apply()` (a full
512x512 GPU texture upload) actually runs. `GazeReviewLoader`/`ComparisonLoader` both replay a
saved recording by calling a paint method once per sample in a tight `foreach` loop - for a real
session (~1,300+ samples, per earlier logcat output) that meant 1,300+ full texture uploads
happening back-to-back within a single method call/frame, which is the kind of main-thread stall
that shows up as a black compositor frame on a VR headset. Added batched variants -
`PaintAtBatched`/`PaintAtColorBatched` (paint into the backing array only, skip the upload) plus
`FlushToGpu(Texture2D)` (does the one real upload) - and switched both loaders' replay loops to
use them: `GazeReviewLoader` calls `FlushToGpu(heatmap.HeatTexture)` once after its loop (that
texture IS what's displayed there); `ComparisonLoader`'s `PaintSamples` needs no explicit flush at
all - `heatTexture`/`comparisonTexture` are never directly displayed on the comparison screen
(only `combinedTexture` is), and `CombineComparisonBuffers()` already reads those backing arrays
directly and does its own single upload. `PaintAt`/`PaintAtColor` (used by live gaze, one stamp
per frame) are untouched - uploading immediately every frame was never the problem there.

Considered instead: progressive/animated reveal (show samples appearing over time rather than all
at once) - a nicer way to *watch* a heatmap build up, but a bigger change (needs painting spread
across frames, likely a coroutine) that doesn't by itself fix the freeze, which was really about
upload *frequency* during a burst, not about revealing the result gradually. Left on the
"Later" list below as a separate idea, not pursued as the fix for this.

**Later (not this pass):**
- [ ] Video playback + sync in the review scene, if/when needed.
- [ ] Progressive/animated replay instead of instant full reveal.
- [ ] `SurgeryHeatmapOverlayLayer.SetVisible(bool)` - hide heatmap during a silent first pass.
- [ ] Persist calibration validation residual into the saved session data.

## Specialist vs trainee comparison (in progress)

Goal: record one constant "specialist" reference session, then show it overlaid (two distinct
colors, same frame) against a trainee's own session, with a labeled legend.

**Step 1 - role selection - done, including scene.** `RoleSelect.unity` built (duplicated from
`Calibration.unity`, calibration objects stripped, Canvas + Specialist/Trainee buttons wired to
`RoleSelectUI`), added to Build Settings between `Calibration` and `EyeTrackingDemo`. Needed
three additional fixes the duplicated scene didn't have (none related to role selection itself,
all pre-existing gaps in `Calibration.unity` that were simply never noticed since calibration
never needed controller interaction):
- Missing `XR Interaction Manager` (both controllers' Ray Interactor had `m_InteractionManager: {fileID: 0}`) - added the GameObject, wired both.
- `EventSystem` had the plain `StandaloneInputModule` (mouse/keyboard only) - replaced with XRI's `XRUIInputModule`.
- Canvas had a plain `GraphicRaycaster` - replaced with `TrackedDeviceGraphicRaycaster` (XR-ray-aware).
- Missing `InputActionManager` (never present in `Calibration.unity` either) - without it, input actions stay wired but never `.Enable()`d, so the controller just sits at its default pose forever ("stuck"). Added, referencing the same Input Action Asset already used elsewhere.
- Bonus fix, applied project-wide (`RoleSelect`/`Calibration`/`PositionGuide`/`EyeTrackingDemo`): `LeftHand Controller`'s `LineRenderer` disabled everywhere - only the right hand is ever read for input anywhere in this project's code, and the left ray was drawing a stray line from its default pose with no way to auto-hide (this XRI version's line visual doesn't check tracking state at all).

**Step 2 - role-aware recording - done.** `MeshGazeHeatmap.Start()` branches on
`SessionRoleManager.IsSpecialist`: specialist sessions always write to a fixed
`specialist_reference.json` (always overwritten, never auto-deleted); trainee sessions keep the
existing timestamped/auto-consumed behavior, unchanged.

**Step 3 - dual-color comparison texture - done, including scene, visually confirmed.**
- [x] `MeshGazeHeatmap.cs`: shared brush-loop extracted into `PaintPixels()`; `PaintAt()` and new
      `PaintAtColor(uv, radius, amount, fixedColor, target)` both call it. New
      `useComparisonBuffer` flag (off by default) allocates a second `comparisonTexture` +
      `combinedTexture`; `CombineComparisonBuffers()` merges both per-pixel (higher alpha wins)
      into what `overlayRenderer` actually displays - called once by the loader after both
      sources finish, not per-sample.
- [x] New `ComparisonLoader.cs` - loads `specialist_reference.json` (never deleted) into
      `HeatTexture` and the trainee's latest session (auto-resolved, deleted after use, matching
      `GazeReviewLoader`) into `ComparisonTexture`, in two fixed colors (orange/cyan).
- [x] `ComparisonOverlay_MAT.mat` created, mirroring `GazeReviewOverlay_MAT.mat`.
- [x] Built `ReportScreen`/`ReportScreen_Overlay` directly in the scene YAML (same established
      pattern as the other direct scene edits this session, verified against the known-good
      `GazeReviewScreen`/`GazeReviewScreen_Overlay` structure) - named "Report" rather than
      "Comparison" to match how this screen's actually been talked about throughout this feature
      (`ComparisonLoader.cs`/`ComparisonOverlay_MAT.mat` keep their original names - only the
      scene GameObjects were renamed, no reason to touch script/asset names too).
      Placement took several passes (chasing a good position purely by reading wall transforms
      out of the scene file kept missing things a screenshot caught immediately - a real room
      corner the flat side-wall colliders didn't account for, an Editor drag/paste that
      accidentally overwrote `ReportScreen`'s Transform with `Cube (3)` (the wall)'s own
      position/scale, and a toe-in angle computed to face the spawn point that turned out to be
      less readable in practice than a flat 90 degree turn). Final, in-headset-confirmed values:
      position `(3.2, 1.3, 1)` (nudged from `4` after the legend was added below it), rotation
      `90 degrees` around Y
      (`m_LocalRotation: {x: 0, y: 0.7071068, z: 0, w: 0.7071068}` - the same quaternion the
      room's own walls use for a quarter turn) on both `ReportScreen` and `ReportScreen_Overlay`.
      Lesson for next time: for anything about how a placement actually *looks*, get a screenshot
      and iterate visually rather than computing it from collider transforms - geometry read from
      the scene file repeatedly missed real obstacles/angles a picture caught immediately.
      `ReportScreen`'s `MeshGazeHeatmap` has `useComparisonBuffer: 1` and its `overlayRenderer`
      pointed at `ReportScreen_Overlay` (material `ComparisonOverlay_MAT`); `ComparisonLoader` (not
      `GazeReviewLoader`) is attached, wired to the same `MeshGazeHeatmap`. **Verified on-device** -
      placement and rotation confirmed by eye in-headset.

**Caught during the "review current code" pass**: `GazeReviewLoader` and `ComparisonLoader` both
defaulted `initialLoadDelaySeconds` to `22f` and both auto-resolved + deleted the *same* trainee
recording file, racing with no ordering guarantee - whichever ran first would delete the file out
from under the other, so the loser silently showed nothing. Fixed: `GazeReviewLoader` no longer
deletes anything it reads (read-only now); `ComparisonLoader` bumped to `23f` so it's guaranteed
to run second, making it the sole deleter of the trainee's auto-resolved file. Also gave
`GazeReviewLoader`'s auto-resolve the same `specialist_reference.json` exclusion
`ComparisonLoader` already had, closing the edge case where a zero-sample trainee session would
leave the specialist file as the most-recently-written `.json` and risk it being treated as an
ordinary trainee recording.

**Step 4 - trainee-only gating - done, expanded.** Both `GazeReviewLoader.Start()` and
`ComparisonLoader.Start()` return immediately if `SessionRoleManager.IsSpecialist` - neither
schedules its load, so nothing displays on either report screen for a specialist session. Originally
this just left the screens present but blank; expanded so a specialist session now fully
`SetActive(false)`s both `GazeReviewScreen`/`GazeReviewScreen_Overlay` and
`ReportScreen`/`ReportScreen_Overlay` - the specialist view now shows only `SurgeryVideoScreen`,
the one thing actually relevant to recording the reference, rather than two empty quads sitting in
the room. New `MeshGazeHeatmap.OverlayGameObject` property (`overlayRenderer.gameObject`) lets
each loader reach its sibling overlay object to hide both halves of the screen together.

**Step 5 - legend - done.** Went through two design ideas: a real 3D arrow pointing at
`ReportScreen` (dropped - too much unverified rotation geometry for a hand-authored scene edit,
matching the pattern of every other placement issue this session), then a simpler "colored swatch
+ label" legend below the screen instead (reference: a chart legend screenshot the user shared -
colored circle/square next to each label). Built:
- [x] `LegendSwatch_Specialist_MAT.mat`/`LegendSwatch_Trainee_MAT.mat` - simple opaque colored
      materials (mirroring the existing `Green.mat`'s structure), colors matching
      `ComparisonLoader`'s current `specialistColor`/`traineeColor` exactly (dark green
      `(0, 0.6, 0)`, gold `(0.85, 0.65, 0)` - **update these materials if those colors change
      again, they won't track automatically**).
- [x] `ReportScreen_LegendSwatch_Specialist`/`_Trainee` - small flat Quads (scale `0.08 x 0.08`),
      positioned below `ReportScreen` at `(3, 0.65, 0.7)` and `(3, 0.65, 1.3)`, same `Y: 90`
      rotation as `ReportScreen` so they face the same way. Hand-authored directly (plain colored
      Quad, same low-risk pattern as every other quad this session).
- [x] Text labels ("Specialist"/"Trainee") added by the user in the Editor - ended up as UI
      `TextMeshProUGUI` (`RectTransform`) children of an existing world-space Canvas already in
      the scene (the one `offsetText`/the calibration offset readout also uses), rather than
      standalone 3D `TextMeshPro` objects as originally described - not wrong, confirmed working
      correctly by screenshot, just a different component type than planned. **Not written into
      this file numerically** - their `RectTransform` values are relative to that Canvas's own
      scaled/rotated local space (scale `0.005`), not plain world units, so they're documented
      here as "placed and confirmed working," not as copyable coordinates.
      Known minor gap: `ReportScreen` ended up at `X: 3.2`, swatches at `X: 3` - a small
      unresolved offset, not confirmed to matter visually.
- [ ] Split watch vs. review into the real sequential flow (separate scene/step), rather than
      both living in `EyeTrackingDemo.unity` and racing at the same launch.

**Return-to-role-select button - done, not yet visually confirmed.** Specialist sessions had no
way back to `RoleSelect.unity` short of a full app relaunch. New
[ReturnToRoleSelectButton.cs](Assets/Scripts/ReturnToRoleSelectButton.cs) - `SetActive(false)`s
itself in `Start()` unless `SessionRoleManager.IsSpecialist`, same self-hiding pattern
`GazeReviewLoader`/`ComparisonLoader` use in reverse; its one public method,
`ReturnToRoleSelect()`, does `SceneManager.LoadScene("RoleSelect")`, wired to a new UI Button's
`OnClick()`.

Built the Button + child `Text (TMP)` directly in the scene YAML, safely this time - unlike a
standalone 3D `TextMeshPro` (no known-good reference to copy, real font-asset wiring risk), this
mirrors `RoleSelect.unity`'s own already-working `SpecialistButton` structure byte-for-byte (same
script/sprite/font guids), just re-targeted at the new script. Confirmed first that
`EyeTrackingDemo.unity`'s Canvas already has the same `TrackedDeviceGraphicRaycaster`/
`XRUIInputModule` guids `RoleSelect.unity` needed adding - so VR-ray clicks should work here with
no further setup, unlike that scene's original state.

Parented under the same Canvas the legend labels ended up on, anchored position `(0, 300)`,
sized `200x60`. **Position not confirmed by eye** - reused the legend labels' working
`m_LocalPosition.z: -5.115` as a starting depth (since that landed correctly near `ReportScreen`),
but the anchored X/Y is a guess at "somewhere else on the same canvas, clear of existing
elements," not something confirmed to land in a sensible, reachable spot. **Needs an in-headset
check** - both that the button is visible/reachable during a specialist session, and that it's
actually clickable (confirms the raycaster assumption above holds in practice, not just on paper).

**Blocked / needs input:**
- [ ] Structure list for per-structure dwell time (anatomical/instrument regions) - needs
      clinical contact's input.
- [ ] Expert recording: captured once and reused, or recaptured per trainee session?

**Gaze indicator on a second video screen - done, working, via a different mechanism than
originally attempted.** Went through two implementations:

**Attempt 1 (abandoned) - standalone `PXR_OverLay` reticle.** Built `ReticleDemoVideoScreen` by
duplicating `SurgeryVideoScreen` (position `(-2.684, 1.424, 0.679)`, rotation `Y: -90`, near
`HeatMapTestObject`/"Mad Flower" per the user's reference point), then tried to give a small
sphere (`GazeReticle`) its own independent compositor layer (`GazeReticleOverLayer.cs`, mirroring
`SurgeryHeatmapOverlayLayer.cs`) so it could render in front of the External-Surface video the
same way the heatmap does. Never got it to actually render in front despite an extensive,
systematically-ruled-out debugging pass:
- 4 different `layerDepth` values tried, including the exact value (`-1`) proven to work for
  `SurgeryHeatmapOverlayLayer` - no change.
- Confirmed via `hitinfo.normal` position offset (`0.01f` up to `0.1f`) - no change, ruling out
  simple z-fighting.
- Adding a `MeshRenderer`/`MeshFilter` back (in case `DynamicTexture` mode needs something to
  render from) - no change.
- Starting `GazeReticle` active-at-scene-load instead of activating it later - fixed a genuine
  scene-load crash (caused by calling `overlay.SetTexture()` inside `Awake()` instead of `Start()`,
  too early for the PXR runtime) but didn't fix the rendering-order problem itself.
- Added a diagnostic log confirming the actual C# gaze-matching/positioning logic was 100%
  correct the whole time (`Match: True`, sane world position, every frame) - proving the
  unresolved part was purely how PXR composites the layer, not anything fixable in this
  project's code.

Given the C# logic was proven correct and the rendering issue was in undocumented SDK behavior we
have no source access to, this approach was abandoned rather than continuing to guess at PXR
settings. `GazeReticle`, `GazeReticleOverLayer.cs`, and the `GazeReticle`/`ReticleTargetObject`
fields/logic in `EyeTrackingManager.cs` are dead code from this point on -
**pending cleanup, not yet removed**.

**Attempt 2 (working) - reused `MeshGazeHeatmap` directly.** Instead of a custom compositor layer,
gave `ReticleDemoVideoScreen` the same `MeshGazeHeatmap` + dedicated heat-overlay-quad setup
`SurgeryVideoScreen` already uses successfully (`recordSamples`/`useComparisonBuffer` off, so it
doesn't feed the specialist/trainee recording pipeline). Built `ReticleDemoVideoScreen_HeatOverlay`
by duplicating `SurgeryVideoScreen_HeatOverlay` (same X/Y as `ReticleDemoVideoScreen`, Z nudged
`0.01` closer to the viewer, matching rotation) and wired `MeshGazeHeatmap.overlayRenderer`/
`compositorLayer` to point to it.

One bug caught during review before it ever reached testing: `compositorLayer` was pointing at
the *original* `SurgeryVideoScreen_HeatOverlay`'s `SurgeryHeatmapOverlayLayer`
(fileID `2027327163`) instead of the new duplicate's own instance (fileID `1214010239`) - the two
screens' heatmaps would have fought over the same compositor layer, likely breaking the already-
working heatmap on `SurgeryVideoScreen`. Fixed before testing; confirmed working afterward - heat
now accumulates correctly on `ReticleDemoVideoScreen` when gazed at.

Confirmed working on-device (heat visibly rendering on `ReticleDemoVideoScreen`) - but the user
clarified the actual wanted outcome was a live reticle, not an accumulating heatmap. Rather than
reviving the abandoned standalone-compositor-layer approach, added the reticle behavior *on top
of* this now-proven rendering pipeline instead:

**`MeshGazeHeatmap.cs` - new `instantReticleMode`.** A per-object bool (off everywhere except
`ReticleDemoVideoScreen`) that changes what `StampAt()` does: instead of accumulating alpha into
the texture over time (the normal heatmap behavior, unchanged for every other object), it clears
the whole texture and paints one fresh dot at the current gaze position, every stamp - a live
marker that moves with gaze rather than a lingering trail. A new `Update()` handles the "gaze
moved away" case: since `StampAt()` only runs while this object is the actual gaze target, there's
no other hook for "nothing is being looked at right now" - `Update()` checks whether a full frame
has passed with no `StampAt()` call and clears the dot if so. That check deliberately compares
against `lastStampFrame + 1`, not a plain inequality - `EyeTrackingManager` and `MeshGazeHeatmap`
are different components with no defined execution order, so a plain `!=` check would clear-then-
immediately-repaint every single frame during continuous gaze whenever Unity happened to run this
`Update()` before that frame's `StampAt()` - wasteful, though not visibly broken. The `+ 1`
tolerance fixes that at the cost of the clear landing one frame later than the tightest possible
timing.

`ReticleDemoVideoScreen`'s heatmap: `instantReticleMode: true`, `reticleColor: cyan`.

**Pending**: remove the dead Attempt-1 code (`GazeReticle` GameObject, `GazeReticleOverLayer.cs`,
the `GazeReticle`/`ReticleTargetObject` fields and their usage in `EyeTrackingManager.cs`) - fully
superseded now, nothing depends on it.

**Review/report load timing now measured from actual video playback start, not scene load -
done.** Reported bug: the trimmed clip is 20s, but a user could spend part of that time looking
elsewhere in the room (e.g. 3s at "Mad Flower", 17s at the video) and `GazeReviewScreen`/
`ReportScreen` would still load once 20 seconds had passed *in the scene*, regardless of whether
the video itself had actually finished 20 seconds of playback. Root cause: every timer involved -
`MeshGazeHeatmap.autoStopAfterSeconds` on `SurgeryVideoScreen`, and both loaders'
`initialLoadDelaySeconds` - measured from each script's own `Start()`, not from when the video
actually began playing. Those aren't the same moment: `SurgeryVideoOverlayPlayer` creates its
Android Surface asynchronously (`overlay.CreateExternalSurface()` in `Start()`, then a later
`OnSurfaceCreated()` callback actually issues the `playVideo` JNI call) - real playback can start
a beat or more after `Start()` runs, and every downstream timer had been counting from the wrong
zero point the entire time.

Fix: `SurgeryVideoOverlayPlayer.cs` now exposes `public event Action PlaybackStarted`, fired from
`OnSurfaceCreated()` right when the surface is ready and playback is issued (fired unconditionally,
not just inside the `UNITY_ANDROID` block, so Editor Play Mode testing of the downstream timing
still works even without the JNI call itself running). `MeshGazeHeatmap.cs`,
`GazeReviewLoader.cs`, and `ComparisonLoader.cs` each got a new optional `videoPlayer` field -
when assigned, `startTime`/the load-delay countdown starts from this event instead of from
`Start()`; left unassigned (every non-video object) falls back to the original Start()-based
timing unchanged. All three wired to `SurgeryVideoScreen`'s own `SurgeryVideoOverlayPlayer`
(fileID `982759302`).

One correctness detail: `MeshGazeHeatmap.StampAt()` now also checks a new `waitingForVideoStart`
flag (true from `Start()` until `PlaybackStarted` fires) before doing anything at all - without
it, `startTime` would sit at its default `0`, and `Time.time - 0 >= 20` would look true from the
very first frame, stopping recording before it ever really began.

**Not yet tested on-device** - this is a real behavioral/timing change to the recording pipeline,
worth a full Specialist-then-Trainee pass to confirm the review/report screens now load at the
right moment relative to actual video completion, not just that it compiles.

**Deferred:**
- [ ] Consolidate duplicated calibration write-up further up this file (the narrative section vs.
      the review-feedback checklist cover a lot of the same ground).

