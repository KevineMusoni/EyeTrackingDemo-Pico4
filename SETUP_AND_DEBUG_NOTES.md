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

## 5-point rotational gaze calibration (new)

**Why**: gaze felt slightly inaccurate. The demo's pre-existing joystick offset
(`combineEyeGazeOriginOffset`) only corrects the gaze ray's starting *position* - real
eye-tracking bias is usually *angular* (direction is off by a small rotation), which a
position-only fix can't properly correct. Built a proper calibration step instead.

**New file**: `Assets/Scripts/CalibrationManager.cs`.
- Shows one visible marker at 5 known positions in sequence (center + 4 corners of the field
  of view, defined as local offsets from the CalibrationManager's own transform).
- At each point: waits `settleTimeBeforeSampling` (default 1s) for the initial eye saccade to
  settle, then averages raw gaze direction samples (`EyeTrackingManager.RawGazeVectorWorld`)
  for the rest of the `dwellDurationPerPoint` window (default 2s total per point).
- Computes the rotation needed to align the averaged raw direction with the TRUE direction to
  that known point (`Quaternion.FromToRotation`), and blends all 5 per-point rotations into
  one `CalibrationCorrection` via incremental Slerp (running-average approximation,
  appropriate since all 5 corrections should represent one consistent underlying bias).
- `IsCalibrated` flips true and the marker hides once all 5 points are done.

**`EyeTrackingManager.cs` changes**:
- Added `RawGazeOriginWorld`/`RawGazeVectorWorld` public read-only properties, exposing the
  UNCORRECTED world-space gaze data each frame for `CalibrationManager` to sample from.
- Added optional `calibrationManager` reference (Inspector-assigned). If assigned, its
  `CalibrationCorrection` quaternion is applied to the gaze DIRECTION (not origin) every
  frame before it's used for `SpotLight` rotation and `GazeTargetControl` (hit detection,
  heatmap stamping, dwell tracking all inherit the correction automatically, since they all
  go through the same corrected vector). Safe/backward-compatible if left unassigned -
  falls back to raw uncorrected data exactly like before this feature existed.

**Still needed (Editor setup)**:
1. Create a `CalibrationManager` GameObject, position it at the player start location facing
   forward (or just reference the XR Origin's forward direction appropriately).
2. Create a visible calibration marker object (e.g. a small sphere), assign it to the
   `Calibration Marker` field.
3. Add the `Calibration Manager` component, assign `Eye Tracking Manager` (drag in the scene's
   `EyeTrackingManager` object) and `Calibration Marker` fields.
4. On `EyeTrackingManager`'s own component, assign its new `Calibration Manager` field to
   point at this new object, so the correction actually gets applied.
5. Tune `calibrationPointLocalOffsets` in the Inspector if the default 5-point spread doesn't
   match the scene's scale/distance well.

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

## Next (focus: standalone eye tracking only, main project crash on hold)
- [ ] Add real runtime permission request to demo (so it works without manual `adb grant`).
- [ ] Build standalone gaze-display feature (numeric readout + reticle).
