# Eye Tracking Unity Demo

This project is a fork of [Pico's official EyeTrackingDemo sample](https://github.com/picoxr/EyeTrackingDemo)
(their native Pico SDK eye-tracking demo, not OpenXR), extended here for standalone eye-tracking learning and prototyping on a PICO 4 Enterprise headset.

## What was added in this project

- **5-point rotational gaze calibration**, with pass/fail validation, automatic retry, and a
  plain-language quality score - runs in its own `Calibration.unity` scene before the main demo
  loads. Full write-up in [User Calibration](#user-calibration) below, code in
  [`CalibrationManager.cs`](Assets/Scripts/CalibrationManager.cs).
- **Gaze-driven heatmap prototype**, stamping heat onto an object's surface wherever the user
  looks, with brush size auto-scaled to that object's real physical size:
  [`MeshGazeHeatmap.cs`](Assets/Scripts/MeshGazeHeatmap.cs).

  <img src="Docs/Screenshots/heatmap-cube.jpeg" width="32%"> <img src="Docs/Screenshots/heatmap-madflower.jpeg" width="32%">
- **Per-object dwell-time report**, replacing the original live gaze-vector readout with an
  accumulating "seconds looked at each object" report, in
  [`EyeTrackingManager.cs`](Assets/Scripts/EyeTrackingManager.cs).
- Fixes required to get the original sample building, deploying, and eye-tracking correctly on a
  PICO 4 Enterprise headset (stale package reference, corrupted Android debug keystore, runtime
  eye-tracking permission grant, Built-in vs URP shader mismatch, mesh Read/Write import setting).

Everything from here down is the original Pico sample's documentation, with the **User
Calibration** section rewritten to describe the rebuilt system above; **3D models** and **Avatar**
are unmodified from the original sample.

## Environment：

- PUI 5.4.0
- Unity 2021.3.13f1
- Pico Unity Integration SDK 2.1.4

## Applicable devices:

- PICO 4 Pro
- PICO 4 Enterprise

## Description：
To enable eye tracking feature you need to mark the Eye Tracking check box on PXR_Manager:
![Screenshot](https://github.com/picoxr/EyeTrackingDemo/blob/eb8677aca7d30c2506d2e8ab0b0ed992c00e9d8a/Screenshots/PXR_Manager.png)

- There are 3 parts in this project. A spot light is used to show an approximate eye gaze area.

### User Calibration
*Rebuilt for this project - see [What was added](#what-was-added-in-this-project) above.*

**What this does:** every headset's eye tracking has a small natural offset, before you can use
the app, it asks you to look at 5 dots shown one at a time. It measures how far off your gaze was
from each dot and learns a correction for it - then checks its own work by testing the correction
against 4 more dots (top, bottom, left, right) it never used to learn from. If that re-check comes back too imprecise, it
automatically tries the whole thing again on its own - you don't need to do anything. Only once
it genuinely passes that quality check do you see a green pass message with a score from 0-100%
(higher is better), and get taken straight into the main demo.

There's no limit on how many times it will retry - this project favors an accurate result over a
fast one, so seeing several orange "retrying" messages in a row is expected, not a sign anything
is broken. If it retries for an unusually long time, that's more likely a headset fit issue (try
readjusting it) than a software problem.

**Technical details** (implementation): runs in a dedicated `Calibration.unity` scene, before the
main demo loads, correcting for ANGULAR bias in the raw eye-tracking data (the tracker's estimate
of gaze direction being consistently off by a small rotation). Code:
[`Assets/Scripts/CalibrationManager.cs`](Assets/Scripts/CalibrationManager.cs).

**Flow:**

```mermaid
flowchart TD
    A([Calibration.unity loads]) --> B[Marker moves to point 1 of 5 training points]
    B --> C{Update: past settle time?}
    C -->|still settling| C
    C -->|settled| D[Average raw gaze samples]
    D --> E{Dwell time reached?}
    E -->|no| C
    E -->|yes| F[Compute this point's correction angle]
    F --> G{Angle greater than 20 degrees?}
    G -->|reject: likely not looking at marker| H[Advance to next point]
    G -->|accept| I[Blend into CalibrationCorrection]
    I --> H
    H --> J{More training points left?}
    J -->|yes| B
    J -->|no| K{All 5 training points accepted?}
    K -->|no| L["Show 'Calibration Failed - Retrying' (red)"]
    L --> M[Reset state] --> B
    K -->|yes| P[Freeze CalibrationCorrection - marker moves to validation point 1 of 4]
    P --> Q{Update: dwell + settle, same as above}
    Q --> RG{Raw angle greater than 20 degrees?}
    RG -->|reject: likely not looking at marker| S
    RG -->|accept| R[Apply frozen CalibrationCorrection to raw gaze, measure residual angle to true point]
    R --> S{More validation points left?}
    S -->|yes| P
    S -->|no| T{Residual error 3 degrees or under?}
    T -->|no: Fair/Poor| U["Show 'Calibration Quality Too Low - Retrying' (orange)"]
    U --> M
    T -->|yes: Excellent/Good| N["Show 'Calibration Passed - label (percent)' (green)"]
    N --> O([Load EyeTrackingDemo.unity])
```

![Calibration geometry - point spacing and bias angle explained](Docs/calibration-geometry.svg)

**Key thresholds:**

| Value | Purpose | Basis |
|---|---|---|
| ~16°-28° | Spacing between the 5 target points | Geometry of `calibrationPointLocalOffsets` - a fact about the layout, not a tuned threshold |
| 20° (`maxPlausiblePointCorrectionDegrees`) | Per-point rejection - is this point's data even usable? | Heuristic: comfortably above real tracking bias/noise, comfortably below "not looking at the marker at all" |
| 1.5° / 3° / 5° | Quality label bands (Excellent/Good/Fair/Poor); **3° also gates retry** - only Excellent/Good proceed | Judgment call, not yet independently validated for this device - see [Open question](#open-question-validating-the-quality-bands-scientifically) below |

Two separate gates - failing either one retries from scratch, no app restart needed:

**Gate 1 - training data validity:** requires all 5 training points to collect valid data
(`RecordCurrentPointCorrection` rejects a point outright if its correction angle exceeds 20°,
same as an eye-tracking dropout). Fail here shows red `"Calibration Failed - Retrying..."`.

**Gate 2 - measured quality** (the interesting one): on passing gate 1, `CalibrationCorrectionLocal`
is frozen and the marker moves through 4 more points - top, bottom, left, right
(`validationPointLocalOffsets`) - that were **never** used to build the correction. Each offset
sits at the same ~16.26° angular distance from center as the training corners (same difficulty,
just spent on one axis instead of split across two - derived, not copied, from
`sqrt(0.5² + 0.3²) = 0.583 = 2·tan(16.26°)`), and together they cover the horizontal/vertical axes
the old 2-point diagonal pair never directly tested.

*Why staying under ~20-25° matters, not just "why 16.26°":* every point's fitting/measurement
depends on the head staying still while only the eyes move to it - if a point sits far enough off
center that a user's head unconsciously starts turning toward it too, that assumption silently
breaks for that point's data. Published oculomotor research on eye-head coordination gives a real
number for this: gaze shifts start recruiting head movement (not just the eyes) once amplitude
passes roughly 20-25°, based on the classic effective-oculomotor-range (EOMR) work by Guitton &
Volle (1987), as summarized in
[Freedman, "Coordination of the Eyes and Head during Visual Orienting" (2008)](https://pmc.ncbi.nlm.nih.gov/articles/PMC2605952/):
*"As gaze shift amplitude increased beyond 20°, saccade amplitude continued to increase linearly,
but the slope of the eye amplitude - gaze amplitude relationship was less than 1"* - i.e. beyond
that point, the head starts doing part of the work. This project's ~16.26° figure wasn't chosen
with that research in mind (it was derived purely to match training-corner difficulty, see above),
but it happens to sit comfortably under this real, independently-sourced safety margin - a
reassuring cross-check, not the original justification. Two caveats worth being honest about: the
20-25° figure is specifically studied for *horizontal* gaze shifts (the same source notes vertical/
oblique eye-head coordination is less studied, so applying it to the up/down/diagonal points here
is an extrapolation), and it marks where head involvement *starts*, not a hard wall - the same
paper separately notes eye-only saccades functionally saturate around ~35°, with the eye's hard
anatomical rotation limit around ~45° (rarely reached). The conservative ~20-25° onset figure is
the relevant one here, since even partial, early head involvement during a ~1-2 second dwell sample
would be enough to quietly bias that point's data.

Each one first passes through the *same* 20°
"were-they-looking-at-it" rejection check the training points use (checked on the raw, uncorrected
angle - a glance-away during validation shouldn't corrupt the very number this feature exists to
make honest); if accepted, `RecordValidationResidual` applies the now-frozen correction to that
point's raw gaze and measures how far off the *corrected* result still is from the true direction
- a train/test split, not a number measured on the same points used to fit it (which would be
optimistically biased). **All validation points must succeed** - if even one is rejected or drops
out, there's no fallback and no partial credit, it's treated as a failure and retried, the same
as a gate 1 failure (`"Calibration Validation Incomplete - Retrying..."`).

Each accepted point also gets a **precision** measurement, separate from that accuracy/residual
number: the average angular deviation of its individual raw samples from their own mean direction
(how tightly clustered the samples were around each other), as opposed to how far off that cluster
was from the true point. A correction can be precise-but-biased (tight cluster, wrong spot) or
accurate-but-imprecise (centered right, noisy) - tracking only one, as the original code did, hides
that distinction. Precision is logged per-point and as a session average, but does not currently
gate pass/fail - only the accuracy/residual number does.

**The correction has to earn its use.** A fitted correction is only ever an estimate built from a
short, noisy sample - if it happened to capture one-off sample noise rather than real, persistent
tracking bias, applying it can leave gaze *worse* off than doing nothing. So alongside the
corrected residual above, `HandleValidationComplete` also computes the *uncorrected* residual (raw
gaze vs. the true point, `CalibrationCorrectionLocal` never applied - the same `rawAngle` already
computed for the rejection gate, just also summed this time) across all 4 points. If the corrected
average isn't actually better than the uncorrected average, the fitted correction is discarded -
`CalibrationCorrectionLocal` resets to identity (PICO's raw output) and the reported/gated quality
number switches to the uncorrected residual - so the quality label, percentage, and pass/fail gate
always reflect whichever result is actually about to ship, not an assumed-good fitted correction.

**A good average can hide one bad region.** Three excellent points and one bad one can still
average out to "Good" overall - the mean has no way to flag that a specific direction (say, the
right side of the field) is unreliable while the rest is fine. So the pass/fail gate now checks
two things, not one: the mean residual (as before) AND the single WORST-performing point among the
4, both against the same 3° "Good" ceiling. Every point has to independently qualify as Good, not
just the average of all 4 - a bad region can no longer hide behind three good ones. `adb logcat`
reports which point index was worst and its value on every validation run, whether it passed or
failed the check, for diagnosability.

That residual error becomes the label + 0-100% score
(`GetBiasQualityLabel`/`GetBiasQualityPercent`, linearly mapped from 0° = 100% to the 5° "Poor"
ceiling, `PoorBiasCeilingDegrees`) - and gates progression: only Excellent/Good (residual ≤3°,
`GoodBiasCeilingDegrees`) proceeds. Fair/Poor shows orange `"Calibration Quality Too Low -
Retrying..."` and restarts from point 1, uncapped by design - for a precision-sensitive use case,
an accurate result matters more than a fast one. `adb logcat` shows both the residual and the
training-set bias side by side (e.g. `Residual error=2.1° (Good, 58%) - training-set bias was
0.9° for comparison`) so you can see how much the training-only number would have overstated
accuracy.

Both outcomes, captured on-device:

<img src="Docs/Screenshots/calibration-quality-too-low.jpeg" width="45%"> <img src="Docs/Screenshots/calibration-passed-excellent.jpeg" width="45%">

**Cross-scene handoff:** `CalibrationCorrectionLocal`/`IsCalibrated` are `static` fields - held in
memory only, reset every launch (multiple people may share the headset). On pass,
`SceneManager.LoadScene()` replaces `Calibration.unity` with `EyeTrackingDemo.unity`, and
`EyeTrackingManager.cs` reads `CalibrationManager.CalibrationCorrectionLocal` directly each frame,
applying it to the local gaze vector before converting to world space (not after, as it used to -
a world-space correction only stays accurate for as long as the head is in the same orientation
it was in during calibration, which doesn't hold up once the user turns their head).

#### Open question: validating the quality bands scientifically

The 1.5°/3°/5° bands themselves are still a judgment call - but the *number they're applied to*
is now real, device-specific measured data instead of a borrowed benchmark. Status:

1. **Holdout validation points - implemented.** The 4 validation points above measure residual
   error on data `CalibrationCorrection` was never fit on, on this actual headset, every time
   someone calibrates. This is what makes the quality score trustworthy as a *relative* signal
   (better/worse between runs) even before the *absolute* band cutoffs are independently checked.
2. **Precision (self-consistency) check - implemented.** Alongside residual/bias, each validation
   point now also measures the *spread* of its raw samples around their own mean direction, not
   just the average. Logged per-point and as a session average; a tight cluster is evidence of a
   stable, trustworthy correction independently of any external accuracy benchmark - but this
   number is diagnostic only so far, it doesn't yet gate pass/fail.
3. **Derive the bar from actual task requirements - not yet implemented.** Instead of asking
   "what accuracy do other headsets achieve," compute the angular size of the smallest object
   this project needs the user to reliably select (using the same object-size logic already in
   [`MeshGazeHeatmap.cs`](Assets/Scripts/MeshGazeHeatmap.cs)), and set "good enough" relative to
   that - a functional requirement instead of a hardware comparison.
4. **Accumulate real first-party data - not yet implemented.** Now that every calibration run
   produces a real residual-error measurement (#1 above), logging and aggregating those across
   multiple people/sessions on this actual headset would let the 1.5°/3°/5° cutoffs themselves
   be set from a real, device-specific distribution instead of a judgment call.

### 3D Models
*Original Pico sample, unmodified.*

**What this does:** two 3D objects in the scene (a cube and an animated character) react when you
look at them - they visually highlight to show they're "focused," and un-highlight the moment
you look away. Implementation: derive from `ETObject` and implement `IsFocused()`/`UnFocused()`.

![Screenshot](https://github.com/picoxr/EyeTrackingDemo/blob/eb8677aca7d30c2506d2e8ab0b0ed992c00e9d8a/Screenshots/3DModels.png)

### Avatar
*Original Pico sample, unmodified.*

**What this does:** a virtual avatar's eyes blink and open/close in sync with your own real
blinking, read directly from the headset's eye-tracking sensors. Implementation:
`PXR_EyeTracking.GetLeftEyeGazeOpenness`/`GetRightEyeGazeOpenness`.

![One eye closed to test openness tracking](Docs/Screenshots/avatar-eye-openness.jpeg)
