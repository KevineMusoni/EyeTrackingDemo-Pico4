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
  // done -> pass message is now "Tracking ready" (was "{label} ({percent}%)"), retry message
  is "Calibration Quality Too Low - Retrying..." (dropped label+percent too, opt 1). Both
  qualityLabel/qualityPercent still computed and logged in adb logcat, only the on-screen
  ShowResult calls changed. Screenshots (Docs/Screenshots/calibration-*.jpeg) now stale, due
  for a recapture.

  <!-- Different scene -  on launch, before calibration -->
- [ ] Add a headset-position/fit guidance step before calibration, similar to Ocumen's
  "Position Guide" stage (see earlier research this session on `GetLeftEyePositionGuide`/
  `GetRightEyePositionGuide` - Neo3 Pro Eye only per SDK docs, unverified on Pico 4 Enterprise)

  

**Target: two modes**
1. **Rapid/investor mode** - ~8-12 seconds, 5 calibration points + 4 validation points.
2. **Full Validation mode** - more points/samples, detailed per-point bias + precision + validity.

**Reference data point**: calibration success criteria was **≤3° bias AND
≤1° precision** - notably a two-part criterion (both bias and precision), whereas today's system only measures bias/residual, precision isn't tracked at all yet

