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

**Later (not this pass):**
- [ ] Video playback + sync in the review scene, if/when needed.
- [ ] Progressive/animated replay instead of instant full reveal.
- [ ] Dual heatmap (expert + trainee shown together), once single-recording playback works.
- [ ] `SurgeryHeatmapOverlayLayer.SetVisible(bool)` - hide heatmap during a silent first pass.
- [ ] Persist calibration validation residual into the saved session data.
- [ ] Split watch vs. review into the real sequential flow (separate scene/step), rather than
      both living in `EyeTrackingDemo.unity` and racing at the same launch.

**Blocked / needs input:**
- [ ] Structure list for per-structure dwell time (anatomical/instrument regions) - needs
      clinical contact's input.
- [ ] Expert recording: captured once and reused, or recaptured per trainee session?

**Deferred:**
- [ ] Consolidate duplicated calibration write-up further up this file (the narrative section vs.
      the review-feedback checklist cover a lot of the same ground).

