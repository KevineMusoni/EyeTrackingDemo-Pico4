# Eye Tracking - Notes

## Main project crash (Surgical VR)
- SIGSEGV/null pointer in Vulkan driver, ~130ms after XR session FOCUSED.
- Likely cause: two foveation features enabled at once (`FoveatedRenderingFeature` +
  `MetaXRFoveationFeature`, both drive `XR_FB_foveation_vulkan`) - suspected conflict on first
  swapchain submit.
- Ruled out: eye tracking (OS-level), video teardown, missing scene scripts.

## Demo project setup (EyeTrackingDemo, Pico 4 Enterprise)
Cloned `picoxr/EyeTrackingDemo`. Fixes to get it running:
1. `Packages/manifest.json` - `com.unity.xr.picoxr` pointed to a dead path → repointed to the
   embedded package folder.
2. Missing OpenJDK module → installed via Unity Hub.
3. Gradle failed on a corrupted `debug.keystore` → killed stuck Gradle daemons, renamed keystore
   aside, tooling regenerated it.
4. Installed on headset (`PA8E50MGH2020230D`, package `com.DefaultCompany.PICOEyeTracking`).

## Eye tracking not working → fixed
- `GetEyeTrackingDevice devices.Count` = 0.
- `gd32ipdservice` (native eye/IPD driver) stuck failing → fixed by full headset reboot.
- Still failed after reboot: `com.picovr.permission.EYE_TRACKING` never requested by the app, so
  stayed denied. Fixed via `adb shell pm grant com.DefaultCompany.PICOEyeTracking com.picovr.permission.EYE_TRACKING`.
- **Confirmed working.**

## Vector vs Point (gaze data)
- `GetCombineEyeGazeVector` = gaze direction (updates continuously). `GetCombineEyeGazePoint` =
  ray origin near the eyes (barely changes, head-relative not gaze-target).
- Real "what am I looking at" = raycast from origin along vector (`GazeTargetControl()`,
  `Physics.SphereCast`). Added as a "Target" line in `EyeTrackingManager.cs`'s text panel.

---
# ✅ EYE TRACKING CORE SETUP - DONE
---

## Heatmap prototype
- `HeatMapTestObject` (dup of `Mad Flower`) got a Mesh Collider (mesh manually assigned, Convex
  unchecked - required for `RaycastHit.textureCoord`).
- `MeshGazeHeatmap.cs` (on `MadFlower`): accumulates heat into a runtime 512x512 texture, soft
  circular brush, blue→green→yellow→red gradient.
- Hook: `EyeTrackingManager.GazeTargetControl()` calls `heatmap.StampAt(uv)`. `SphereCast` (main
  gaze hit-test) has no UV data - a supplementary `Physics.Raycast` along the same ray gets it.
- Display: `MadFlower_HeatOverlay` (dup, no collider, ~1.01 scale to avoid z-fighting).
  **Bug**: URP `Unlit` shader rendered magenta (project has no SRP assigned, still Built-in RP
  despite URP installed) → **fixed with `Unlit/Transparent`**.

## Brush size auto-scales to object size
- Fixed pixel radius looked bigger/smaller depending on object's texel density. Replaced with
  `brushRadiusWorldMeters` (real-world size) + `ComputeBrushRadiusPixels()`, which compares a hit
  triangle's world-space area vs UV-space area to convert world radius → correct pixel radius per
  spot/object.
- `StampAt()` now takes the full `RaycastHit` (needs `triangleIndex`). Script needs
  `[RequireComponent(typeof(MeshCollider))]`.

## Mesh Read/Write Enabled required
- `ComputeBrushRadiusPixels()` reads `mesh.triangles/.vertices/.uv` in C# - Unity strips that by
  default. Fix: `Mad Flower.fbx` → Model import → check **Read/Write Enabled**.

## Gaze dwell-time report
- Live "Target" readout has a problem: looking at the report changes what it's reporting. Fixed
  by switching to **accumulated dwell time**, always shown on the text panel (no button/blink
  toggle - blink-select never registered reliably on-device, dropped).

## 5-point rotational gaze calibration (`CalibrationManager.cs`, `Calibration.unity`)
- Corrects *angular* bias (the built-in `combineEyeGazeOriginOffset` only fixes ray *position*).
- **Phase 1 (train)**: 5 points, settle 1s → average raw gaze → `Quaternion.FromToRotation` gives
  each point's correction. Reject if >20°. Gate 1: needs 5/5 accepted or retries.
- **Phase 2 (validate)**: correction frozen, 4 held-out points (top/bottom/left/right), measures
  residual error after correction vs. uncorrected - avoids the optimism of grading on training
  data. Gate 2: only Excellent/Good (≤3°) proceeds; no retry cap (precision matters more than
  speed, confirmed intentional).
- Scoring: residual → Excellent ≤1.5°/Good ≤3°/Fair ≤5°/Poor >5° (a judgment call, not
  independently validated for this device - citations removed after finding the sources weren't
  strong enough).
- Fit uses `AverageQuaternions` (Markley's method) over all accepted per-point corrections, not
  sequential `Slerp`. Retry is per-point, not full-sequence. On-screen result is a plain
  pass/fail message; degree measurements logged only, not shown to the user.
- `CalibrationCorrection`/`IsCalibrated` are `static`, reset every launch (shared headset, no
  persistence). Every PXR call's return value is now checked - failed reads used to return
  zero-valued data that could get counted as a valid sample.
- **Verified on-device**: real pass/fail screenshots in `Docs/Screenshots/`.

## Position-guide step (`PositionGuideManager.cs`, `PositionGuide.unity`, first scene loaded)
- Uses `GetLeftEyePositionGuide`/`GetRightEyePositionGuide` - SDK doc comment says "Neo3 Pro Eye
  only" but a newer SDK build's source confirms Pico 4 Pro/Enterprise are supported too (comment
  just stale).
- Worked once, then every session since reads stuck at exactly `(0,0,0)`. Ruled out script
  changes, reboot, permission, SDK reinstall. **Root cause (in `adb logcat`, outside the app
  entirely)**: `gd32ipdservice` failing UART comms with the physical sensor in a retry loop -
  hardware/driver fault, not fixable from app code. Kept in Build Settings (implementation
  verified correct once) pending a hardware fix.

## 3D stereo surgery video on `SurgeryVideoScreen`
- **Goal**: play a 3840x1080 side-by-side stereo H.264 video with the gaze heatmap overlaid.
- **Attempt 1 (Unity `VideoPlayer`+`RenderTexture`)** and **Attempt 2 (AVPro Video)**: both
  decoded cleanly but never displayed a frame on Android+Vulkan (known `RenderTexture`
  format/sampling issue; AVPro's `Resampler.cs` uses a non-OES-aware `Graphics.Blit`). Both fully
  reverted.
- **Attempt 3 (working)**: PICO compositor-layer External Surface - bypasses Unity's render
  pipeline entirely, hands the decoder's raw Android `Surface` to PICO's compositor via
  `PXR_OverLay`. Playback driven by PICO's own ExoPlayer-backed `playvideo.jar`
  (`com.pico.exoplayerdemo.PlayVideo`), called via raw `AndroidJNI`. New
  `SurgeryVideoOverlayPlayer.cs` sets `overlayShape=Quad`, `isExternalAndroidSurface=true`,
  `externalAndroidSurface3DType=LeftRight`. **Confirmed working.**
- **Bug: heatmap invisible once video showing.** `PXR_OverLay.overlayType` defaults to `Overlay`,
  compositing in front of the *entire* eye-buffer render, burying the heatmap regardless of its
  3D depth. `Underlay` (tried, reverted) requires eye-buffer alpha=0 hole-punch pixels, which
  `PXR_OverLay` doesn't provide, so it broke the video entirely (black).
  **Real fix**: gave the heatmap its own second `PXR_OverLay` (`SurgeryHeatmapOverlayLayer.cs`,
  `TextureType.DynamicTexture`, `layerDepth=-1`) - two independently-stacked compositor layers.
  Still invisible until a second bug was found: the heatmap's `PXR_OverLay` had
  `isExternalAndroidSurface` stuck at `1` in the saved scene (external-surface layers skip the
  texture-upload path entirely) - fixed in the scene data and defensively in `Awake()`.
- **Video pillarboxed**: `SurgeryVideoScreen`'s scale was sized for the *combined* double-wide
  frame, but `Surface3DType.LeftRight` already splits it into per-eye 16:9 halves. Fixed by
  resizing both screen and overlay quad to `{x: 1.7066667, y: 0.96}`.

## Gaze recording + review (`MeshGazeHeatmap.cs`, `GazeReviewLoader.cs`)
- Samples appended inside `StampAt()`, autosaved every 5s (`InvokeRepeating`, not
  `OnApplicationQuit` - never fires on `adb shell am force-stop`).
- `GazeReviewLoader.cs` loads the most recent recording and paints it instantly via
  `PaintAt()`/`StampAt(RaycastHit)` split (live vs. replay share the same paint call).
- Auto-resolved (unnamed) loads are deleted after reading, so each recording shows once, next
  launch only - fixes stale data surviving rebuilds via `persistentDataPath`.
- Load was scheduled via `Invoke(..., 22s)` from `Start()`, not immediate - otherwise it could
  only ever show a *previous* session's leftovers within the same session.
- `autoStopAfterSeconds` (`20` on `SurgeryVideoScreen`) stops recording once the clip's likely
  done - a fixed wall-clock number, not tied to real playback state.

## Heatmap painting performance - two passes
- **Pass 1**: `PaintPixels()` used `Texture2D.GetPixel/SetPixel` per brush pixel (~16,600 calls at
  max radius, every frame) - real per-call overhead. Fixed with a plain `Color[]` backing array
  per texture (`heatPixels`/`comparisonPixels`/`combinedPixels`), read/written by direct indexing.
- **Pass 2**: replaying a full session (~1,300+ samples) called `SetPixels`+`Apply()` (a full GPU
  upload) once per sample in a tight loop - a multi-hundred-upload main-thread stall showed as a
  black compositor frame. Fixed with batched paint variants (`PaintAtBatched` etc., array-only) +
  one `FlushToGpu()` call after the loop. Live per-frame painting (`PaintAt`/`PaintAtColor`)
  untouched - immediate upload was never the problem there.

## Specialist vs trainee comparison
- **Role select** (`RoleSelect.unity`, dup of `Calibration.unity`): needed 3 fixes the source
  scene never had (no controller interaction before this) - missing `XR Interaction Manager`,
  plain `StandaloneInputModule` instead of `XRUIInputModule`, plain `GraphicRaycaster` instead of
  `TrackedDeviceGraphicRaycaster`, missing `InputActionManager` (without it, input actions stay
  wired but never `.Enable()`d, so the controller sits frozen at its default pose). Also disabled
  the (unused) `LeftHand Controller` `LineRenderer` project-wide - only the right hand is ever
  read for input anywhere in this project.
- **Recording**: `MeshGazeHeatmap.Start()` branches on `SessionRoleManager.IsSpecialist` -
  specialist always overwrites `specialist_reference.json`; trainee keeps the normal
  timestamped/auto-consumed behavior.
- **Comparison texture**: `useComparisonBuffer` flag paints specialist + trainee into separate
  buffers, merged per-pixel (higher alpha wins) by `CombineComparisonBuffers()` into what's
  actually displayed. `ComparisonLoader.cs` loads both sources. Built `ReportScreen`/
  `ReportScreen_Overlay` directly in scene YAML - final confirmed position `(3.2, 1.3, 1)`,
  `Y: 90°` rotation. **Lesson**: for placement/look, iterate from a screenshot, not from reading
  collider transforms out of the scene file - repeatedly missed real obstacles a picture caught
  immediately.
- **Race condition found in review**: `GazeReviewLoader` and `ComparisonLoader` both
  auto-resolved + deleted the same trainee file with no ordering guarantee. Fixed by making
  `GazeReviewLoader` read-only and bumping `ComparisonLoader`'s load delay so it always runs
  (and deletes) second.
- **Specialist-only gating**: both loaders skip entirely if `SessionRoleManager.IsSpecialist`; a
  specialist session now fully hides both review/report screens, showing only the video.
- **Legend**: colored swatch quads + TMP labels below `ReportScreen`, colors matching
  `ComparisonLoader`'s `specialistColor`/`traineeColor` exactly (won't auto-update if those
  change).
- **Return-to-role-select button**: `ReturnToRoleSelectButton.cs`, self-hides unless specialist,
  `SceneManager.LoadScene("RoleSelect")`. Built by mirroring `RoleSelect.unity`'s own working
  button structure byte-for-byte rather than a fresh standalone element.

## Second video screen reticle (`ReticleDemoVideoScreen`)
- **Attempt 1 (abandoned)**: a standalone `PXR_OverLay` for a reticle sphere, mirroring the
  heatmap's own working layer setup - never rendered in front of the video despite an extensive,
  systematically-ruled-out pass (4 different `layerDepth` values, z-fighting offsets, adding a
  MeshRenderer back, fixing an unrelated scene-load crash). Root cause left as undocumented SDK
  compositor behavior with no source access - abandoned rather than keep guessing. Dead code
  (`GazeReticle`, `GazeReticleOverLayer.cs`) left in place, pending cleanup.
- **Attempt 2 (working)**: reused `MeshGazeHeatmap` directly instead of a custom layer, plus a new
  `instantReticleMode` bool - instead of accumulating heat over time, `StampAt()` clears the
  texture and paints one fresh dot per stamp, a live marker instead of a trail. `Update()` clears
  the dot once a full frame passes with no `StampAt()` call (compares `lastStampFrame + 1`, not
  `!=`, since `EyeTrackingManager`/`MeshGazeHeatmap` have no defined execution order - a plain
  `!=` would clear-then-repaint every frame).

## Review/report load timing - two race conditions found and fixed
- **Bug**: timers (`autoStopAfterSeconds`, `initialLoadDelaySeconds`) counted from each script's
  own `Start()`, not from when the video actually began playing - `SurgeryVideoOverlayPlayer`
  creates its Android Surface asynchronously, so real playback can start a beat or more later.
  **Fix**: `SurgeryVideoOverlayPlayer` exposes `PlaybackStarted`; dependent scripts start their
  timers from that event instead, via a new `videoPlayer` field (optional - unassigned objects
  keep the old `Start()`-based timing).
- **Regression 1**: `PlaybackStarted` was firing before subscribers had reached their own
  `Start()` (Unity doesn't guarantee `Start()` order between scripts, even on the same
  GameObject) - the event fired to zero listeners and was lost. First fix (defer surface creation
  one frame via a coroutine) wasn't reliable either, since real `Start()` calls were observed
  spread across ~300ms, not one frame.
  **Real fix**: `SubscribeOrFireImmediately(Action)` - fires the callback immediately if playback
  already started, otherwise subscribes normally. Removes the ordering dependency entirely
  instead of trying to out-guess it.

## Custom XR slider drag for replay video scrubbing (`Visualisation.unity`)
- **Goal**: a draggable slider on `DivergenceReplayScreen`'s replay UI that seeks the actual
  native video via `SurgeryVideoOverlayPlayer.SeekTo()`.
- **Built from scratch** - `IBeginDragHandler`/`IDragHandler`/`IEndDragHandler` never fire at all
  through this project's XR ray setup for World Space UI (confirmed on-device); `IPointerUpHandler`
  only fires as a discrete click, can't scrub. New `CustomSliderDragHandler.cs` polls raw
  controller/ray state every frame instead of using Unity's UI event system. Three real bugs:
  1. **`TryGetCurrentUIRaycastResult()` only worked after the video ended** (its own internal
     cache, driven by XRI's hover bookkeeping, not directly controllable) - replaced with a
     hand-rolled ray-plane intersection against the slider's `RectTransform`.
  2. **The slider's live-playback sync fought the drag unpredictably** - a `SetSliderSyncSuppressed`
     flag looked correct but wasn't reliable, since Unity doesn't guarantee `Update()` order
     between different scripts. Fixed with `LateUpdate()` (guaranteed to run after every
     `Update()` in the scene), which re-applies the drag value unconditionally every frame while
     dragging - always wins regardless of ordering.
  3. **`XRRayInteractor.isSelectActive` never reflects this rig's trigger at all** (the real bug).
     UI clicks and 3D-object selection are driven by two *separate* input actions on
     `ActionBasedController` - `isUISelectActive`/`uiPressAction` (what UI clicks actually use)
     vs. `isSelectActive`/`selectAction` (for grabbing 3D objects, never wired to anything here).
     Fixed by reading `ActionBasedController.uiPressAction.action.IsPressed()` directly.
- **Logcat "chatty" collapsing** wasted several rounds of "add a log, check logcat, see nothing" -
  Android's `logd` was silently collapsing this app's high-volume `UnityMain` log lines into
  `chatty: expire N lines` summaries *before* any client-side filter ever saw the stream. Fixed
  by `adb logcat -G 16M` (bigger device buffer) + dump-then-search (`adb logcat -d > file.txt`)
  instead of a live filtered pipe, which also buffered unreliably over PowerShell on Windows.
- **Confirmed working on-device.**

## Tailed reticle - gaze dots trail their recent movement (`DivergenceReplayScreenOverlay.cs`)
- **Goal**: specialist/trainee dots show a short fading, tapering trail instead of jumping between
  positions each frame.
- **Design**: no new runtime state - `specialistSamples`/`traineeSamples` already hold the full
  session, so a tail is just several lookups at earlier timestamps against that same data. Stays
  correct across slider seeks since it recomputes from `elapsed` every frame. `DrawDot()` gained a
  `radius` param; `DrawTail()` loops back `tailDurationSeconds`, tapering size/alpha with age;
  `DrawLine()` connects consecutive tail points with a stamped stroke (isolated dots left visible
  gaps whenever gaze moved fast enough).
- **Real bug**: the new `FindInterpolatedSample()` (blends between real neighbors instead of
  snapping to one) used a linear scan assuming a sorted list - never actually guaranteed, since
  samples are deserialized straight from JSON. Symptom: the tail rendered as a thick, "dancing"
  blob (long strokes connecting far-apart, temporally-unrelated points). **Fix**: sort both lists
  once in `Start()` right after loading.
- **Residual shakiness left as-is, on purpose**: likely genuine saccade motion + pixel-rounding.
  A smoothing pass (averaging nearby timestamps) was designed but deliberately not built - for a
  feature meant to compare specialist vs. trainee attention, averaging away real saccades would
  hide genuine behavioral differences. Accuracy prioritized over visual calm.
- **Texture resolution bump (512→1024, radii doubled to match) - tried, reverted**: caused a real
  regression on-device ("too fast and not visible," likely the 4x bigger buffer too expensive
  per-frame - not confirmed). Reverted to `512`/`12`/`4`/`20`.
- **Confirmed working on-device** at that baseline - stable, continuous threads, no blob artifacts.

## Known but unfixed
- SDK bug: `PXR_BuildProcessor.cs` writes a manifest value as the literal string `"false/true"`
  instead of a real bool. Not a blocker.
- Dead code pending cleanup: `GazeReticle`, `GazeReticleOverLayer.cs`, `EyeTrackingManager.cs`'s
  now-unused `GazeReticle`/`ReticleTargetObject` fields.

## Repo
- Fork: `github.com/KevineMusoni/EyeTrackingDemo-Pico4`. `origin` = fork, `upstream` =
  `picoxr/EyeTrackingDemo` (read-only).
- `.gitignore` updated for Burst debug output/per-machine editor state that wasn't being ignored.
- README overhauled: fork attribution, calibration explanation + Mermaid flowchart + geometry
  diagram, real on-device screenshots.

## Later / blocked
- [ ] Video playback + sync in the review scene.
- [ ] Progressive/animated replay instead of instant reveal.
- [ ] `SurgeryHeatmapOverlayLayer.SetVisible(bool)`.
- [ ] Persist calibration validation residual into saved session data.
- [ ] Split watch vs. review into a real sequential flow (separate scene).
- [ ] Structure list for per-structure dwell time - needs clinical contact's input.
- [ ] Expert recording: captured once and reused, or recaptured per trainee?
- [ ] Remove dead Attempt-1 reticle code.
