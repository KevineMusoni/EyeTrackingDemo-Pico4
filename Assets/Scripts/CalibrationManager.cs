using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.XR.PXR;
using TMPro;

// Runs a 5-point rotational gaze calibration before normal tracking begins. Corrects for
// ANGULAR bias in the eye-tracking data (the tracker's estimate of gaze direction being
// consistently off by a small rotation) - a different, more accurate error model than the
// main scene's pre-existing joystick offset (EyeTrackingManager.combineEyeGazeOriginOffset),
// which only shifts the gaze ray's starting POSITION, not its direction.
//
// Flow: shows a visible marker at 5 known world positions in sequence (center + 4 corners of
// the field of view). At each point, the user just looks at it naturally - after a short
// settle time, raw gaze samples are averaged, and the rotation needed to make that raw
// direction match the TRUE direction to the known point is recorded. The 5 per-point
// corrections are blended into one final CalibrationCorrection quaternion, which
// EyeTrackingManager (in the main scene, once loaded) applies to its own gaze vector.
//
// MULTI-SCENE SETUP (true sequential, not additive): this script lives in a dedicated
// "Calibration" scene with its OWN copy of the XR rig (camera + PXR_Manager, duplicated from
// the main scene) - so it can render and read real gaze data completely independently,
// before the main scene has even loaded. Calibration runs entirely in isolation; only once
// all 5 points are done does it call a normal (non-additive) SceneManager.LoadScene(), which
// automatically replaces this scene with the main one - no manual unload needed, and no
// overlap between the two scenes at any point (unlike an earlier additive-loading version).
//
// CalibrationCorrection/IsCalibrated are STATIC and never written to disk - they live purely
// in memory for as long as the app process is running. Recalibrating from scratch on every
// launch is intentional here (not a limitation): this headset may be used by different
// people between sessions, and a saved/reused calibration could silently apply the wrong
// person's correction. Static fields also let EyeTrackingManager (in the OTHER scene file)
// read the result directly by class name - an Inspector-wired reference can't cross scene
// files, and wouldn't survive this GameObject being destroyed when its scene unloads anyway.
public class CalibrationManager : MonoBehaviour
{
    [Header("Multi-Scene Setup")]
    // Name of the main scene to load (replacing this one) once calibration finishes.
    [SerializeField] private string mainSceneName = "EyeTrackingDemo";

    [Header("References")]
    [SerializeField] private Transform calibrationMarker;
    // ADDED: this scene's OWN copy of the XR Origin (duplicated from the main scene, with
    // Camera Offset/Main Camera/PXR_Manager nested under it) - used the same way
    // EyeTrackingManager.Origin is used there, to transform head-local gaze data into world
    // space. Needed now because there's no EyeTrackingManager instance to borrow this from
    // during calibration - this scene has to do the transform itself.
    [SerializeField] private Transform xrOrigin;
    // ADDED: shows the pass/fail result before transitioning (on pass) or retrying (on fail).
    // A separate TextMeshPro element in the Calibration scene, distinct from the existing
    // "Eyetracking Calibration" title text.
    [SerializeField] private TMP_Text statusText;

    [Header("Calibration Points")]
    // Local offsets from this GameObject's own transform, in meters - e.g. place this
    // GameObject at the player's start position facing forward, and these define a simple
    // cross/diamond pattern spanning the visual field at ~2m distance.
    [SerializeField]
    private Vector3[] calibrationPointLocalOffsets = new Vector3[]
    {
        new Vector3(0f, 0f, 2f),        // center
        new Vector3(-0.5f, 0.3f, 2f),   // up-left
        new Vector3(0.5f, 0.3f, 2f),    // up-right
        new Vector3(-0.5f, -0.3f, 2f),  // down-left
        new Vector3(0.5f, -0.3f, 2f),   // down-right
    };

    [Header("Validation Points")]
    // ADDED: shown AFTER the 5 calibration points pass, using the same dwell/settle/sample
    // mechanism, but these are held out of CalibrationCorrection entirely (see
    // RecordValidationResidual). Measuring accuracy on points the correction was never fit on
    // (a train/test split) is what actually justifies a quality score - reusing the training
    // points themselves would optimistically overstate how well the correction generalizes.
    [SerializeField]
    private Vector3[] validationPointLocalOffsets = new Vector3[]
    {
        new Vector3(0.25f, 0.15f, 2f),
        new Vector3(-0.25f, -0.15f, 2f),
    };

    [Header("Timing")]
    // Total time spent on each point before moving to the next.
    [SerializeField] private float dwellDurationPerPoint = 2f;
    // Only samples raw gaze data during the LAST portion of the dwell window, letting the
    // initial saccade (the fast eye movement jumping to the new point) settle first -
    // sampling too early would average in the "still moving toward the target" data.
    [SerializeField] private float settleTimeBeforeSampling = 1f;
    // How long the "Calibration Passed"/"Calibration Failed" message stays on screen before
    // the scene transitions (pass) or the 5-point sequence restarts (fail).
    [SerializeField] private float resultDisplayDuration = 2f;

    [Header("Validation")]
    // A point's raw-to-true correction angle above this is treated as the user not actually
    // looking at the marker (distracted, looked elsewhere, blinked through the whole window)
    // rather than a genuine tracking bias this large - see RecordCurrentPointCorrection.
    [SerializeField] private float maxPlausiblePointCorrectionDegrees = 20f;

    // STATIC - see class comment above for why. Defaults to identity/false so that opening
    // the main scene directly (skipping calibration entirely, e.g. for a quick test in the
    // Editor) still behaves safely - EyeTrackingManager just applies a no-op correction.
    public static bool IsCalibrated { get; private set; } = false;
    public static Quaternion CalibrationCorrection { get; private set; } = Quaternion.identity;

    private Matrix4x4 originPoseMatrix;
    private Matrix4x4 headPoseMatrix;

    private int currentPointIndex = 0;
    private float pointTimer = 0f;
    private Vector3 sampleSum = Vector3.zero;
    private int sampleCount = 0;
    private int pointsCollected = 0;
    // ADDED: running sum of each accepted point's pointCorrectionAngle (bias, in degrees) -
    // used to compute the average bias shown as a plain-language quality label after passing.
    // See GetBiasQualityLabel for why the degree thresholds are set where they are.
    private float biasAngleSum = 0f;
    // ADDED: sum/count of residual errors measured against the held-out validation points,
    // AFTER applying the already-finalized CalibrationCorrection - this is the honest,
    // train/test-split accuracy number, as opposed to biasAngleSum above (the training-set
    // number, which is optimistic since those same points were used to fit the correction).
    private float validationResidualSum = 0f;
    private int validationPointsMeasured = 0;

    // ADDED: which stage of the flow Update() is currently driving. Calibrating fits
    // CalibrationCorrection from the 5 training points; Validating measures accuracy against
    // the held-out points using that now-frozen correction; Finished blocks Update() entirely
    // during the pass/fail result pause (whether about to load the main scene or retry).
    private enum Phase { Calibrating, Validating, Finished }
    private Phase phase = Phase.Calibrating;

    private void Awake()
    {
        // Reset static state in case this scene gets loaded again later (e.g. a future
        // "recalibrate" flow) - without this, a second run would start already "calibrated"
        // from the previous run's leftover static values.
        IsCalibrated = false;
        CalibrationCorrection = Quaternion.identity;
    }

    private void Start()
    {
        if (xrOrigin != null)
        {
            originPoseMatrix = xrOrigin.localToWorldMatrix;
        }

        if (calibrationMarker != null && calibrationPointLocalOffsets.Length > 0)
        {
            calibrationMarker.position = transform.TransformPoint(calibrationPointLocalOffsets[0]);
        }
    }

    // Which point array Update()/AdvanceToNextPoint are currently walking through.
    private Vector3[] CurrentPointSet => phase == Phase.Calibrating ? calibrationPointLocalOffsets : validationPointLocalOffsets;

    private void Update()
    {
        if (phase == Phase.Finished || calibrationMarker == null || xrOrigin == null)
        {
            return;
        }

        // ADDED: read and transform raw gaze data directly, the same way
        // EyeTrackingManager.Update() does - this scene has to do it itself now, since there's
        // no EyeTrackingManager instance around to borrow it from during calibration.
        PXR_EyeTracking.GetHeadPosMatrix(out headPoseMatrix);
        PXR_EyeTracking.GetCombineEyeGazeVector(out Vector3 rawGazeVector);
        PXR_EyeTracking.GetCombineEyeGazePoint(out Vector3 rawGazeOrigin);
        Vector3 rawGazeVectorWorld = originPoseMatrix.MultiplyVector(headPoseMatrix.MultiplyVector(rawGazeVector));
        Vector3 rawGazeOriginWorld = originPoseMatrix.MultiplyPoint(headPoseMatrix.MultiplyPoint(rawGazeOrigin));

        pointTimer += Time.deltaTime;

        // Only start averaging raw gaze samples after the settle time has passed, so the
        // initial saccade toward the new point doesn't skew the average.
        if (pointTimer >= settleTimeBeforeSampling)
        {
            sampleSum += rawGazeVectorWorld;
            sampleCount++;
        }

        if (pointTimer >= dwellDurationPerPoint)
        {
            if (phase == Phase.Calibrating)
            {
                RecordCurrentPointCorrection(rawGazeOriginWorld);
            }
            else
            {
                RecordValidationResidual(rawGazeOriginWorld);
            }
            AdvanceToNextPoint();
        }
    }

    private void RecordCurrentPointCorrection(Vector3 gazeOrigin)
    {
        if (sampleCount == 0)
        {
            // Didn't get a sample window (e.g. eye tracking dropped out) - skip this
            // point rather than corrupting the blended correction
            return;
        }

        Vector3 averageRawDirection = (sampleSum / sampleCount).normalized;
        Vector3 truePointDirection = (calibrationMarker.position - gazeOrigin).normalized;

        // The rotation that would take the raw (uncorrected) direction and align it exactly
        // with the true direction to the known calibration point - this IS the correction.
        Quaternion pointCorrection = Quaternion.FromToRotation(averageRawDirection, truePointDirection);

        // ADDED: reject implausibly large per-point corrections. Quaternion.Angle gives the
        // single overall rotation magnitude (handles axis wraparound correctly, unlike reading
        // eulerAngles directly) - if this point alone would require a huge correction, the
        // averaged raw direction almost certainly isn't the user looking at the marker at all,
        // and blending it in would corrupt the final CalibrationCorrection with garbage data.
        float pointCorrectionAngle = Quaternion.Angle(Quaternion.identity, pointCorrection);
        if (pointCorrectionAngle > maxPlausiblePointCorrectionDegrees)
        {
            Debug.Log($"[CalibrationManager] Point {currentPointIndex} rejected - correction angle {pointCorrectionAngle:F1}° exceeds {maxPlausiblePointCorrectionDegrees}° threshold (likely not looking at the marker).");
            return;
        }

        if (pointsCollected == 0)
        {
            CalibrationCorrection = pointCorrection;
        }
        else
        {
            // Running-average blend across points via incremental Slerp - a standard simple
            // approximation for averaging multiple rotations without needing a full
            // quaternion-averaging algorithm (appropriate here since all 5 corrections should
            // be small, broadly similar rotations representing one consistent tracking bias).
            CalibrationCorrection = Quaternion.Slerp(CalibrationCorrection, pointCorrection, 1f / (pointsCollected + 1));
        }

        biasAngleSum += pointCorrectionAngle;
        pointsCollected++;
    }

    // ADDED: measures accuracy against a held-out point - one CalibrationCorrection was never
    // fit on - instead of trusting the optimistic training-set number above.
    private void RecordValidationResidual(Vector3 gazeOrigin)
    {
        if (sampleCount == 0)
        {
            // Dropout - skip this validation point rather than counting it as a bad measurement.
            return;
        }

        Vector3 averageRawDirection = (sampleSum / sampleCount).normalized;
        Vector3 truePointDirection = (calibrationMarker.position - gazeOrigin).normalized;

        // Same "were they even looking at this point" gate the training points use, checked on
        // the RAW angle - a glance-away during validation would otherwise inject a huge, bogus
        // residual into the very number this feature exists to make more honest.
        float rawAngle = Vector3.Angle(averageRawDirection, truePointDirection);
        if (rawAngle > maxPlausiblePointCorrectionDegrees)
        {
            Debug.Log($"[CalibrationManager] Validation point {currentPointIndex} rejected - raw angle {rawAngle:F1}° exceeds {maxPlausiblePointCorrectionDegrees}° threshold (likely not looking at the marker).");
            return;
        }

        // Apply the ALREADY-FINALIZED CalibrationCorrection (fit only on the 5 training points)
        // and measure how far the corrected direction still is from this held-out point's true
        // direction - this is real calibration accuracy, not a number optimistically measured
        // on the same points used to build the correction.
        Vector3 correctedDirection = CalibrationCorrection * averageRawDirection;
        float residualAngle = Vector3.Angle(correctedDirection, truePointDirection);

        validationResidualSum += residualAngle;
        validationPointsMeasured++;
    }

    private void AdvanceToNextPoint()
    {
        currentPointIndex++;
        pointTimer = 0f;
        sampleSum = Vector3.zero;
        sampleCount = 0;

        Vector3[] points = CurrentPointSet;
        if (currentPointIndex >= points.Length)
        {
            if (phase == Phase.Calibrating)
            {
                HandleCalibrationComplete();
            }
            else
            {
                HandleValidationComplete();
            }
            return;
        }

        calibrationMarker.position = transform.TransformPoint(points[currentPointIndex]);
    }

    // ADDED: pass requires ALL 5 points to have collected real samples - pointsCollected only
    // increments in RecordCurrentPointCorrection when sampleCount > 0 there, so a dropout during
    // any point's sampling window (eye tracking glitch, blink, etc.) leaves it short of 5 and
    // fails the check, rather than silently shipping a correction built from mostly-missing data.
    private void HandleCalibrationComplete()
    {
        bool passed = pointsCollected >= calibrationPointLocalOffsets.Length;

        if (passed)
        {
            // The correction itself is now final - only the QUALITY SCORE still needs
            // measuring, against the held-out validation points next.
            IsCalibrated = true;
            phase = Phase.Validating;
            currentPointIndex = 0;
            calibrationMarker.position = transform.TransformPoint(validationPointLocalOffsets[0]);
        }
        else
        {
            calibrationMarker.gameObject.SetActive(false);
            Debug.Log($"[CalibrationManager] Calibration FAILED - only {pointsCollected}/{calibrationPointLocalOffsets.Length} points collected valid data. Retrying.");
            ShowResult("Calibration Failed - Retrying...", Color.red);
            phase = Phase.Finished;
            Invoke(nameof(RetryCalibration), resultDisplayDuration);
        }
    }

    // ADDED: the quality score shown to the user is now based on validationResidualSum (the
    // honest, train/test-split number), not biasAngleSum (the optimistic training-set number,
    // still logged below for comparison).
    private void HandleValidationComplete()
    {
        phase = Phase.Finished;
        calibrationMarker.gameObject.SetActive(false);

        float averageResidualDegrees;
        if (validationPointsMeasured > 0)
        {
            averageResidualDegrees = validationResidualSum / validationPointsMeasured;
        }
        else
        {
            // No validation data at all (rare - every held-out point dropped) - fall back to
            // the training-set bias rather than showing a misleading 0°/100% result.
            averageResidualDegrees = biasAngleSum / pointsCollected;
        }

        string qualityLabel = GetBiasQualityLabel(averageResidualDegrees);
        int qualityPercent = GetBiasQualityPercent(averageResidualDegrees);
        float averageTrainingBiasDegrees = biasAngleSum / pointsCollected;

        // ADDED: quality gate - Fair/Poor no longer silently proceeds to the main scene. For a
        // precision-sensitive use case, a correction this imprecise defeats the point of
        // calibrating at all, so it's treated the same as a data-validity failure: retry from
        // scratch. Deliberately uncapped (no max attempts) - the goal is an actually-good
        // calibration, not a best-effort one after N tries; if this proves too strict in
        // practice (e.g. genuinely stuck retrying), that's a signal to revisit the bands
        // themselves via the validation data now being collected, not to add a silent cap.
        bool qualityAcceptable = averageResidualDegrees <= GoodBiasCeilingDegrees;

        if (qualityAcceptable)
        {
            Debug.Log($"[CalibrationManager] Validation complete: {validationPointsMeasured}/{validationPointLocalOffsets.Length} points measured. Residual error={averageResidualDegrees:F1}° ({qualityLabel}, {qualityPercent}%) - training-set bias was {averageTrainingBiasDegrees:F1}° for comparison. CalibrationCorrection={CalibrationCorrection.eulerAngles}");
            // MODIFIED: on-screen text shows the plain-language label plus a percentage (e.g.
            // "Good (82%)") - meant to be read by non-technical users without needing degrees
            // explained; the precise degree value is still in the log line above.
            ShowResult($"Calibration Passed - {qualityLabel} ({qualityPercent}%)", GetBiasQualityColor(averageResidualDegrees));
            // MODIFIED: delayed via Invoke (was immediate) so the pass message is actually
            // visible before the scene transitions.
            Invoke(nameof(LoadMainScene), resultDisplayDuration);
        }
        else
        {
            Debug.Log($"[CalibrationManager] Validation complete but quality below the {GoodBiasCeilingDegrees}° Good threshold - retrying. Residual error={averageResidualDegrees:F1}° ({qualityLabel}, {qualityPercent}%), training-set bias was {averageTrainingBiasDegrees:F1}° for comparison.");
            ShowResult($"Calibration Quality Too Low - {qualityLabel} ({qualityPercent}%) - Retrying...", GetBiasQualityColor(averageResidualDegrees));
            Invoke(nameof(RetryCalibration), resultDisplayDuration);
        }
    }

    // ADDED: NOT yet validated against real-world data for this project - see README's "Open
    // question: validating the quality bands scientifically" section. Deliberately kept strict
    // (favoring precision-sensitive use cases over matching "typical" hardware performance)
    // pending real evidence from the validation points above, which is what these bands will
    // eventually be checked against once enough calibration runs have accumulated.
    // Single source of truth for the "Poor" ceiling - used both as the bottom of the label
    // bands below AND as the 0% anchor for GetBiasQualityPercent, so the label and the
    // percentage can never drift out of sync with each other. Deliberately NOT the same value
    // as maxPlausiblePointCorrectionDegrees (20°) above - that threshold answers a different
    // question (is this one point's data even usable?, a heuristic) from this one (how good is
    // an already-valid result?, currently a judgment call).
    private const float PoorBiasCeilingDegrees = 5f;
    // ADDED: extracted from GetBiasQualityLabel's inline "Good" cutoff so HandleValidationComplete
    // can gate on the EXACT same value the label uses, rather than a second, possibly-drifting
    // copy of "3f" - see the quality gate note in HandleValidationComplete for why this now
    // matters (it didn't when the quality score was purely informational).
    private const float GoodBiasCeilingDegrees = 3f;

    private static string GetBiasQualityLabel(float averageBiasDegrees)
    {
        if (averageBiasDegrees <= 1.5f) return "Excellent";
        if (averageBiasDegrees <= GoodBiasCeilingDegrees) return "Good";
        if (averageBiasDegrees <= PoorBiasCeilingDegrees) return "Fair";
        return "Poor";
    }

    private static Color GetBiasQualityColor(float averageBiasDegrees)
    {
        if (averageBiasDegrees <= GoodBiasCeilingDegrees) return Color.green;
        if (averageBiasDegrees <= PoorBiasCeilingDegrees) return Color.yellow;
        return new Color(1f, 0.5f, 0f); // orange - Fair/Poor, now retried rather than shown as a pass
    }

    // ADDED: 0-100 score for non-technical users, linearly mapped from 0° (100%) to the same
    // PoorBiasCeilingDegrees (5°, 0%) the labels use - a familiar "battery/signal bar" format
    // that needs no unit explanation, and shares the one ceiling constant rather than a
    // separately-invented number.
    private static int GetBiasQualityPercent(float averageBiasDegrees)
    {
        float percent = 100f * (1f - averageBiasDegrees / PoorBiasCeilingDegrees);
        return Mathf.RoundToInt(Mathf.Clamp(percent, 0f, 100f));
    }

    private void ShowResult(string message, Color color)
    {
        if (statusText == null)
        {
            return;
        }
        statusText.text = message;
        statusText.color = color;
    }

    // MOVED here from the inline call: SceneManager.LoadScene automatically replaces/unloads
    // the current scene as part of the transition, no manual UnloadSceneAsync needed.
    private void LoadMainScene()
    {
        SceneManager.LoadScene(mainSceneName);
    }

    // ADDED: resets all per-run state and starts the 5-point sequence over from point 1 - used
    // when the pass check above fails, so the user doesn't have to restart the whole app just
    // because eye tracking glitched on one point.
    private void RetryCalibration()
    {
        currentPointIndex = 0;
        pointsCollected = 0;
        biasAngleSum = 0f;
        validationResidualSum = 0f;
        validationPointsMeasured = 0;
        pointTimer = 0f;
        sampleSum = Vector3.zero;
        sampleCount = 0;
        CalibrationCorrection = Quaternion.identity;
        // ADDED: must reset this too now - retry can now happen AFTER IsCalibrated was set true
        // (a quality-gated retry, following validation) as well as before it (a training-phase
        // failure). Previously harmless to skip since only the latter case existed.
        IsCalibrated = false;
        phase = Phase.Calibrating;

        ShowResult(string.Empty, Color.white);
        calibrationMarker.gameObject.SetActive(true);
        calibrationMarker.position = transform.TransformPoint(calibrationPointLocalOffsets[0]);
    }
}
