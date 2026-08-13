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
against 2 more dots it never used to learn from. If that re-check comes back too imprecise, it
automatically tries the whole thing again on its own - you don't need to do anything. Only once
it genuinely passes that quality check do you see a green pass message with a score from 0-100%
(higher is better), and get taken straight into the main demo.

**A note if you're watching someone go through this:** there's no limit on how many times it will
retry - this project intentionally favors getting an accurate result over a fast one, so seeing
several orange "retrying" messages in a row is expected behavior, not a sign anything is broken.
If it keeps retrying for an unusually long time, that's more likely a headset fit issue (try
readjusting it) than a software problem.

<details>
<summary><b>Technical details</b> (implementation, for developers)</summary>

Before the main demo scene loads, a dedicated `Calibration.unity` scene runs a 5-point rotational calibration to correct for ANGULAR bias in the raw eye-tracking data (the tracker's estimate of gaze direction being consistently off by a small rotation). This is separate from - and more accurate than - the simpler joystick-based offset adjustment described below, which only shifts the gaze ray's starting *position*, not its *direction*.

Implementation: [`Assets/Scripts/CalibrationManager.cs`](Assets/Scripts/CalibrationManager.cs).

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
    K -->|yes| P[Freeze CalibrationCorrection - marker moves to validation point 1 of 2]
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

**Why degrees, not distance:** each calibration point's error is measured as the angle between
the *true* direction to that point and the *raw measured* gaze direction (`Quaternion.Angle`
between two direction rays), not a distance in meters - the same angular gap means a bigger or
smaller real-world miss depending on how far away you're looking, so every eye-tracking spec
(Tobii, HTC) is published in degrees for the same reason.

![Calibration geometry - point spacing and bias angle explained](Docs/calibration-geometry.svg)

**Key thresholds in the code, and where each one comes from:**

| Value | Purpose | Basis |
|---|---|---|
| ~16°-28° | Spacing between the 5 target points | Geometry of `calibrationPointLocalOffsets` - a fact about the layout, not a tuned threshold |
| 20° (`maxPlausiblePointCorrectionDegrees`) | Per-point rejection - is this point's data even usable? | Heuristic: comfortably above real tracking bias/noise, comfortably below "not looking at the marker at all" |
| 1.5° / 3° / 5° | Quality label bands (Excellent/Good/Fair/Poor); **3° (`GoodBiasCeilingDegrees`) also gates retry** - Fair/Poor now retries automatically, only Excellent/Good actually proceed | Judgment call, kept deliberately strict for precision-sensitive use cases - not yet independently validated for this device (see note below) |

Note the 20° rejection threshold and the 1.5°/3°/5° quality bands answer two different
questions - "is this one point's data even usable?" (heuristic) vs. "how good is an already-valid
result?" (currently a judgment call) - and are deliberately kept as separate constants in the
code so they can't drift into meaning the same thing.

**On the quality bands specifically:** these are not yet backed by device-specific measurement.
Third-party research on other VR headsets exists, but doesn't transfer reliably to this project -
different hardware (dedicated eye-tracking chips vs. Pico 4 Enterprise's in-house sensors) and, in
the case that came closest to being relevant, a small pilot study (n=11) whose own authors caution
against generalizing. Treat 1.5°/3°/5° as a reasonable starting point pending real validation on
this headset, not a settled number - see [Open question: validating the quality bands](#open-question-validating-the-quality-bands-scientifically)
below for how we plan to close that gap.

There are now two separate gates - failing either one retries from scratch, no app restart needed:

**Gate 1 - training data validity:** requires all 5 training points to collect valid data
(`RecordCurrentPointCorrection` rejects a point outright if its correction angle exceeds 20°,
same as an eye-tracking dropout). Fail here shows red `"Calibration Failed - Retrying..."`.

**Gate 2 - measured quality** (the interesting one): on passing gate 1, `CalibrationCorrection`
is frozen and the marker moves through 2 more points (`validationPointLocalOffsets`) that were
**never** used to build the correction. Each one first passes through the *same* 20°
"were-they-looking-at-it" rejection check the training points use (checked on the raw, uncorrected
angle - a glance-away during validation shouldn't corrupt the very number this feature exists to
make honest); if accepted, `RecordValidationResidual` applies the now-frozen correction to that
point's raw gaze and measures how far off the *corrected* result still is from the true direction
- a train/test split, not a number measured on the same points used to fit it (which would be
optimistically biased). If both validation points get rejected (rare), the quality gate below
falls back to the training-set bias rather than showing a misleading 0°/100%.

That residual error becomes the plain-language label plus 0-100% score
(`GetBiasQualityLabel`/`GetBiasQualityPercent`, percentage linearly mapped from 0° = 100% to the
same 5° "Poor" ceiling the label uses, `PoorBiasCeilingDegrees`, so both numbers always agree) -
**and it also gates progression**: only Excellent/Good (residual ≤3°, `GoodBiasCeilingDegrees`)
actually proceeds to the main scene. Fair/Poor shows orange `"Calibration Quality Too Low -
Retrying..."` and restarts from point 1, same as a gate 1 failure. This retry has **no attempt
cap by design** - for a precision-sensitive use case, an actually-good calibration matters more
than a fast one, so it keeps retrying indefinitely rather than settling for a best-effort result
after N tries.

`adb logcat` shows both numbers side by side regardless of outcome (e.g. `Residual error=2.1°
(Good, 58%) - training-set bias was 0.9° for comparison`) so you can see how much the
training-only number would have overstated accuracy.

**Cross-scene handoff:** `CalibrationCorrection` and `IsCalibrated` are `static` fields - held in
memory only, never written to disk (recalibrating from scratch every launch is intentional, since
multiple people may share the headset). Once calibration passes, `SceneManager.LoadScene()`
replaces `Calibration.unity` with `EyeTrackingDemo.unity` (sequential, not additive - no scene
overlap), and `EyeTrackingManager.cs` reads `CalibrationManager.CalibrationCorrection` directly by
class name each frame to rotate its gaze vector.

You can also adjust eye tracking offset with the trigger button on the right controller, which
shifts the gaze ray's starting position independently of the calibration correction above.

![Screenshot](https://github.com/picoxr/EyeTrackingDemo/blob/bd7e1f592971bd35fc4fca292f05afb7add51ab5/Screenshots/Calibration.png)

#### Open question: validating the quality bands scientifically

The 1.5°/3°/5° bands themselves are still a judgment call - but the *number they're applied to*
is now real, device-specific measured data instead of a borrowed benchmark. Status:

1. **Holdout validation points - implemented.** The 2 validation points above measure residual
   error on data `CalibrationCorrection` was never fit on, on this actual headset, every time
   someone calibrates. This is what makes the quality score trustworthy as a *relative* signal
   (better/worse between runs) even before the *absolute* band cutoffs are independently checked.
2. **Precision (self-consistency) check - not yet implemented.** Alongside bias, track the
   *spread* of raw samples collected during each point's settle window, not just their average.
   A tight cluster of samples is evidence of a stable, trustworthy correction independently of
   any external accuracy benchmark.
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
you look away.

<details>
<summary><b>Technical details</b> (implementation, for developers)</summary>

This part shows you how to detect if a 3D model with animation is focused or unfocused by eye-tracking. To create your own eye tracking interactive game object, you can simply derive from ETObject and implement IsFocused() and UnFocused().

![Screenshot](https://github.com/picoxr/EyeTrackingDemo/blob/eb8677aca7d30c2506d2e8ab0b0ed992c00e9d8a/Screenshots/3DModels.png)

</details>

### Avatar
*Original Pico sample, unmodified.*

**What this does:** a virtual avatar's eyes blink and open/close in sync with your own real
blinking, read directly from the headset's eye-tracking sensors.

<details>
<summary><b>Technical details</b> (implementation, for developers)</summary>

This part shows you how to get and apply eye openness to an avatar by calling PXR_EyeTracking.GetLeftEyeGazeOpenness(out leftEyeOpenness) and PXR_EyeTracking.GetRightEyeGazeOpenness(out rightEyeOpenness).

![Screenshot](https://github.com/picoxr/EyeTrackingDemo/blob/eb8677aca7d30c2506d2e8ab0b0ed992c00e9d8a/Screenshots/Avatar.png)

</details>
