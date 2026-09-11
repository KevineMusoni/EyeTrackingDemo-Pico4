using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;

// 5-point rotational gaze calibration, validated against 4 held-out points, run in a dedicated
// Calibration scene before EyeTrackingDemo.unity loads. Corrects ANGULAR bias (the tracker's
// gaze direction being consistently off by a small rotation) - a different error model from
// EyeTrackingManager.combineEyeGazeOriginOffset, which only shifts the ray's start position.
//
// The fit happens in HEAD-LOCAL space, not world space: the bias is a property of the eye/
// sensor relative to the head, so a world-space correction would only stay valid for as long as
// the head stayed in the exact orientation it was calibrated in. Local-to-world conversion is
// always the last step, after the correction has already been applied.
//
// CalibrationCorrectionLocal/IsCalibrated are static and never persisted - recalibrating from
// scratch every launch is intentional (the headset may be shared between people). Static also
// lets EyeTrackingManager, in the other scene file, read the result by class name - an
// Inspector reference can't cross scene files.
public class CalibrationManager : MonoBehaviour
{
    // A small static 3D marker sitting at the point's own real world position - positioned via
    // the exact same transform.TransformPoint(offset) call the real calibrationMarker uses, so
    // it is exactly where that point is, not an approximation. Hidden entirely for whichever
    // index is currently active, since the real (larger, green) marker already occupies that
    // spot - no separate indicator is needed there.
    [System.Serializable]
    private struct PointMarkerView
    {
        public MeshRenderer markerRenderer;
        // Small world-space Canvas Image, same technique as markerProgressRing - only shown
        // once this point is Done, so it never competes with the amber "look here now" marker.
        public Image checkmark;
    }

    [Header("Multi-Scene Setup")]
    // Goes to role selection first now, not straight into the demo - see RoleSelectUI.cs /
    // SessionRoleManager.cs.
    [SerializeField] private string mainSceneName = "RoleSelect";

    [Header("References")]
    [SerializeField] private Transform calibrationMarker;
    // This scene's own copy of the XR Origin - no EyeTrackingManager instance exists yet to
    // borrow one from during calibration.
    [SerializeField] private Transform xrOrigin;
    [SerializeField] private TMP_Text statusText;
    // Fixed on screen regardless of which corner the marker is currently at - the bias model
    // assumes the head stays still through the whole point sequence, so a plain static position
    // stays in view the entire time without needing to be head-locked.
    [SerializeField] private TMP_Text progressText;

    [Header("Marker Visuals")]
    // The marker's own sphere - used for the idle-pulse breathing scale and the retry flash.
    // A separate reference from calibrationMarker so a marker prefab swap without a
    // MeshRenderer at the root still degrades gracefully (visuals just no-op).
    [SerializeField] private MeshRenderer markerRenderer;
    // World-space Canvas Image parented under the marker - a UI Image.fillAmount ring can't
    // attach to calibrationMarker directly since it's a 3D mesh, not a RectTransform.
    [SerializeField] private Image markerProgressRing;

    [Header("Live Point Map")]
    // Small static markers at each point's own real 3D position, showing overall progress
    // (done/pending) at a glance instead of only ever seeing the single point currently active.
    // Positioned once in Start() from the same offsets/transform the real marker itself uses -
    // deliberately NOT a 2D screen-space overlay, since a screen-space approximation can never
    // reliably line up with where these real 3D points project (the camera moves with head
    // tracking; a static canvas position can't track that). Being real 3D objects at the real
    // coordinates makes "where is this dot" a non-question by construction.
    //
    // NOT gaze targets - PXR only measures against calibrationMarker itself. Never share
    // markerBaseColor's green with these, and always hide the marker at currentPointIndex (the
    // real marker already sits there) - confusing the two is what caused a viewer to look at
    // the wrong thing and fail a point for real, earlier in this feature's life.
    // Same order as calibrationPointLocalOffsets (center, up-left, up-right, down-left, down-right).
    [SerializeField] private PointMarkerView[] calibrationPointDots;
    // Same order as validationPointLocalOffsets (top, bottom, left, right) - recolored by
    // per-point residual once validation completes, see UpdateValidationQualityDots.
    [SerializeField] private PointMarkerView[] validationPointDots;

    [Header("Calibration Points")]
    // Local offsets from this GameObject's transform, in meters.
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
    // Held out of CalibrationCorrectionLocal entirely - measuring accuracy on data never used
    // to fit the correction is what makes the quality score meaningful (a train/test split).
    // Each offset sits at the same ~16.26deg angular distance from center as the training
    // corners: sqrt(0.5^2 + 0.3^2) = 0.583 = 2*tan(16.26deg), so validation is exactly as hard
    // as training, just along the horizontal/vertical axes instead of the diagonals.
    [SerializeField]
    private Vector3[] validationPointLocalOffsets = new Vector3[]
    {
        new Vector3(0f, 0.583f, 2f),    // top
        new Vector3(0f, -0.583f, 2f),   // bottom
        new Vector3(-0.583f, 0f, 2f),   // left
        new Vector3(0.583f, 0f, 2f),    // right
    };

    [Header("Timing")]
    // Timeout ceiling, not a fixed duration - most points converge early (see Adaptive
    // Sampling) and advance sooner; this is the fallback if a point never stabilizes.
    [SerializeField] private float dwellDurationPerPoint = 3.5f;
    // Sampling only starts after this long, letting the initial saccade to the new point settle.
    [SerializeField] private float settleTimeBeforeSampling = 2f;
    [SerializeField] private float resultDisplayDuration = 2f;

    [Header("Adaptive Sampling")]
    // A point finishes early once its samples converge, instead of always waiting out
    // dwellDurationPerPoint. Same angular-deviation-from-mean math as the precision metric
    // below, just computed live every frame. Starting values, not yet tuned against real data.
    [SerializeField] private float convergencePrecisionDegrees = 0.5f;
    [SerializeField] private int minSamplesBeforeConvergenceCheck = 5;
    [SerializeField] private int minStableFramesToConverge = 20;

    [Header("Validation")]
    // A point's raw-to-true angle above this is treated as not looking at the marker, rather
    // than a genuine bias this large.
    [SerializeField] private float maxPlausiblePointCorrectionDegrees = 20f;

    [Header("Fit Guidance")]
    // Diagnostic nudges only, never a retry cap - calibration retries indefinitely by design.
    // Shown when the SAME point fails repeatedly, or the WHOLE sequence's quality gate fails
    // repeatedly - both common symptoms of headset fit rather than a one-off glitch.
    [SerializeField] private int perPointFitGuidanceThreshold = 3;
    [SerializeField] private int fullSequenceFitGuidanceThreshold = 3;

    public static bool IsCalibrated { get; private set; } = false;
    public static Quaternion CalibrationCorrectionLocal { get; private set; } = Quaternion.identity;

    public IReadOnlyList<float> PerPointCorrectionAngles => perPointCorrectionAngles;
    public IReadOnlyList<float> PerPointValidationResiduals => perPointValidationResiduals;

    private Matrix4x4 originPoseMatrix;
    private Matrix4x4 headPoseMatrix;
    // Last successful read's world-space gaze origin/head pose - a dwell-completion frame can
    // land on a failed read, so these are the safe stand-in.
    private Vector3 lastValidGazeOriginWorld;
    private Matrix4x4 lastValidHeadPoseMatrix;

    private int currentPointIndex = 0;
    private float pointTimer = 0f;
    private Vector3 sampleSum = Vector3.zero;
    private int sampleCount = 0;
    private int pointsCollected = 0;
    private int stableFrameCount = 0;
    // Consecutive failures on the CURRENT point (not the whole sequence) - drives
    // perPointFitGuidanceThreshold, reset on that point's success.
    private int currentPointRetryCount = 0;
    // Consecutive full-sequence restarts from a quality-gate failure - drives
    // fullSequenceFitGuidanceThreshold, reset on eventual pass.
    private int consecutiveFullRetries = 0;
    // Each accepted training point's own correction, fit into one CalibrationCorrectionLocal
    // simultaneously in HandleCalibrationComplete - see AverageQuaternions.
    private List<Quaternion> acceptedPointCorrections = new List<Quaternion>();
    // Same data as biasAngleSum/validationResidualSum, kept per-point instead of only summed -
    // needed for a per-point quality readout (e.g. a result-screen dot per point) rather than
    // just the sequence-wide average.
    private List<float> perPointCorrectionAngles = new List<float>();
    private List<float> perPointValidationResiduals = new List<float>();
    // Parallel to perPointValidationResiduals but with CalibrationCorrectionLocal never
    // applied - lets the result-screen dots reflect whichever result actually shipped (see
    // correctionHelps in HandleValidationComplete), not always the corrected numbers.
    private List<float> perPointUncorrectedValidationResiduals = new List<float>();
    // Raw samples for the CURRENT point's settle window - sampleSum only gives the mean, this
    // is what precisionDegrees (spread around that mean) is computed from.
    private List<Vector3> currentPointRawSamplesLocal = new List<Vector3>();
    private float biasAngleSum = 0f;
    // Residual = accuracy after applying the frozen correction (train/test-split, honest).
    // biasAngleSum above is the training-set number, optimistic since it's the same data the
    // correction was fit on.
    private float validationResidualSum = 0f;
    private int validationPointsMeasured = 0;
    // Precision = consistency (how tightly clustered a point's raw samples were), a different
    // statistic from residual/bias (accuracy = how far the average sample is from the true
    // point). A correction can be precise-but-biased or accurate-but-imprecise.
    private float validationPrecisionSum = 0f;
    // Residual with CalibrationCorrectionLocal never applied - the baseline
    // validationResidualSum gets compared against, so a correction that's actually worse than
    // raw tracking can be caught and discarded instead of assumed good.
    private float validationUncorrectedResidualSum = 0f;
    // Worst single point's residual/index, not just the mean - a good average can hide one bad
    // region of the visual field. Tracked separately for corrected/uncorrected since whichever
    // result ships (see correctionHelps in HandleValidationComplete) decides which applies.
    private float validationWorstResidualDegrees = 0f;
    private int validationWorstResidualPointIndex = -1;
    private float validationWorstUncorrectedResidualDegrees = 0f;
    private int validationWorstUncorrectedResidualPointIndex = -1;

    private enum Phase { Calibrating, Validating, Finished }
    private Phase phase = Phase.Calibrating;

    // Cached once in Start so the pulse/flash coroutines have a known rest state to lerp back
    // to, instead of hardcoding a color/scale that would drift from whatever's on the marker.
    private Material markerMaterialInstance;
    private Color markerBaseColor;
    private Vector3 markerBaseScale;
    private Coroutine markerFlashCoroutine;

    private void Awake()
    {
        IsCalibrated = false;
        CalibrationCorrectionLocal = Quaternion.identity;
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
            markerBaseScale = calibrationMarker.localScale;
        }

        if (markerRenderer != null)
        {
            // .material (not .sharedMaterial) forces an instance, so recoloring this marker
            // (both the theme-color set here and the retry flash later) never touches the
            // shared material asset or any other renderer using it.
            markerMaterialInstance = markerRenderer.material;
            // Amber, not the app's theme green - this marker IS the "look at me, still
            // loading" signal, and green is reserved for the small Done breadcrumbs left
            // behind once a point is confirmed (see DoneDotColor) so the two are never
            // confusable with each other.
            markerBaseColor = new Color(0.94f, 0.62f, 0.15f, 1f);
            markerMaterialInstance.color = markerBaseColor;
        }

        if (markerProgressRing != null)
        {
            markerProgressRing.fillAmount = 0f;
            markerProgressRing.color = markerBaseColor;
        }

        // statusText otherwise keeps whatever placeholder was saved in the scene ("Result")
        // until a fit-guidance nudge or the final result overwrites it - which, on a clean run
        // with no retries, might never happen before validation completes.
        ShowResult(string.Empty, Color.white);
        UpdateProgressText();

        // Each point marker's position is fixed for the whole session - the point itself never
        // moves, only the real calibrationMarker travels between them - so this only needs to
        // run once, not every frame.
        PositionPointMarkers(calibrationPointDots, calibrationPointLocalOffsets);
        PositionPointMarkers(validationPointDots, validationPointLocalOffsets);
        RefreshPointDots();
    }

    private void PositionPointMarkers(PointMarkerView[] markers, Vector3[] offsets)
    {
        if (markers == null)
        {
            return;
        }

        for (int i = 0; i < markers.Length && i < offsets.Length; i++)
        {
            if (markers[i].markerRenderer != null)
            {
                markers[i].markerRenderer.transform.position = transform.TransformPoint(offsets[i]);
            }
        }
    }

    private Vector3[] CurrentPointSet => phase == Phase.Calibrating ? calibrationPointLocalOffsets : validationPointLocalOffsets;

    // Fixed "POINT X OF Y" readout, independent of which corner the marker is currently
    // travelling to - currentPointIndex already means "the point in progress" everywhere else
    // in this class, so it's used as-is rather than re-deriving a display index.
    private void UpdateProgressText()
    {
        if (progressText == null)
        {
            return;
        }

        if (phase == Phase.Finished)
        {
            progressText.text = string.Empty;
            return;
        }

        string phaseLabel = phase == Phase.Validating ? "CONFIRMING ACCURACY" : "CALIBRATING";
        int totalPoints = CurrentPointSet.Length;
        int displayIndex = Mathf.Min(currentPointIndex + 1, totalPoints);
        progressText.text = $"{phaseLabel} · POINT {displayIndex} OF {totalPoints}";
    }

    private void Update()
    {
        if (phase == Phase.Finished || calibrationMarker == null || xrOrigin == null)
        {
            return;
        }

        pointTimer += Time.deltaTime;

        bool isSettling = pointTimer < settleTimeBeforeSampling;
        UpdateMarkerIdlePulse(isSettling);
        UpdateMarkerProgressRing(isSettling);

        if (GazeReading.TryReadRawGaze(out headPoseMatrix, out Vector3 rawGazeVector, out Vector3 rawGazeOrigin))
        {
            lastValidHeadPoseMatrix = headPoseMatrix;
            lastValidGazeOriginWorld = originPoseMatrix.MultiplyPoint(headPoseMatrix.MultiplyPoint(rawGazeOrigin));

            if (pointTimer >= settleTimeBeforeSampling)
            {
                sampleSum += rawGazeVector;
                sampleCount++;
                currentPointRawSamplesLocal.Add(rawGazeVector);

                if (sampleCount >= minSamplesBeforeConvergenceCheck)
                {
                    Vector3 runningMeanDirection = (sampleSum / sampleCount).normalized;
                    float runningPrecisionDegrees = 0f;
                    foreach (Vector3 sample in currentPointRawSamplesLocal)
                    {
                        runningPrecisionDegrees += Vector3.Angle(sample.normalized, runningMeanDirection);
                    }
                    runningPrecisionDegrees /= currentPointRawSamplesLocal.Count;

                    stableFrameCount = runningPrecisionDegrees <= convergencePrecisionDegrees
                        ? stableFrameCount + 1
                        : 0;
                }
            }
        }

        bool dwellTimedOut = pointTimer >= dwellDurationPerPoint;
        bool sampleConverged = stableFrameCount >= minStableFramesToConverge;
        if (dwellTimedOut || sampleConverged)
        {
            string completionReason = sampleConverged ? "converged early" : "timed out";
            Debug.Log($"[CalibrationManager] Point {currentPointIndex} {completionReason} after {pointTimer:F2}s (vs {dwellDurationPerPoint:F2}s fixed timeout).");

            bool pointSucceeded = phase == Phase.Calibrating
                ? RecordCurrentPointCorrection(lastValidGazeOriginWorld, lastValidHeadPoseMatrix)
                : RecordValidationResidual(lastValidGazeOriginWorld, lastValidHeadPoseMatrix);
            AdvanceToNextPoint(pointSucceeded);
        }
    }

    // Only pulses while settling, not while sampling - a moving target during the sampling
    // window would bias the gaze-direction math the same way head movement would.
    private void UpdateMarkerIdlePulse(bool isSettling)
    {
        if (calibrationMarker == null)
        {
            return;
        }

        calibrationMarker.localScale = isSettling
            ? markerBaseScale * (1f + 0.08f * Mathf.Sin(Time.time * 4f))
            : markerBaseScale;
    }

    // Empty during settle (nothing to show progress on yet), then fills as samples converge -
    // stableFrameCount already IS a 0..minStableFramesToConverge progress value, computed for
    // the early-exit check in Update, so this reads it rather than tracking anything new.
    private void UpdateMarkerProgressRing(bool isSettling)
    {
        if (markerProgressRing == null)
        {
            return;
        }

        markerProgressRing.fillAmount = isSettling
            ? 0f
            : Mathf.Clamp01(stableFrameCount / (float)minStableFramesToConverge);
    }

    private void FlashMarkerRed()
    {
        if (markerMaterialInstance == null)
        {
            return;
        }

        if (markerFlashCoroutine != null)
        {
            StopCoroutine(markerFlashCoroutine);
        }
        markerFlashCoroutine = StartCoroutine(FlashMarkerRedRoutine());
    }

    private IEnumerator FlashMarkerRedRoutine()
    {
        const float halfDuration = 0.15f;

        float t = 0f;
        while (t < halfDuration)
        {
            t += Time.deltaTime;
            markerMaterialInstance.color = Color.Lerp(markerBaseColor, Color.red, t / halfDuration);
            yield return null;
        }

        t = 0f;
        while (t < halfDuration)
        {
            t += Time.deltaTime;
            markerMaterialInstance.color = Color.Lerp(Color.red, markerBaseColor, t / halfDuration);
            yield return null;
        }

        markerMaterialInstance.color = markerBaseColor;
        markerFlashCoroutine = null;
    }

    private static readonly Color PendingDotColor = new Color(0.3f, 0.3f, 0.3f, 1f);
    // Safe to reuse the app's theme green here now - markerBaseColor is amber while a point is
    // active, so a Done breadcrumb (green) and the live marker (amber) are never the same color
    // at the same time. The confusion this avoided earlier came from an amber-vs-amber or
    // green-vs-green clash, not from green appearing at all.
    private static readonly Color DoneDotColor = new Color(0f, 1f, 0.56078434f, 1f);

    private static void SetDotArrayVisible(PointMarkerView[] dots, bool visible)
    {
        if (dots == null)
        {
            return;
        }

        foreach (PointMarkerView dot in dots)
        {
            if (dot.markerRenderer != null)
            {
                dot.markerRenderer.gameObject.SetActive(visible);
            }
            if (!visible && dot.checkmark != null)
            {
                dot.checkmark.gameObject.SetActive(false);
            }
        }
    }

    // Redraws the whole map for the CURRENT phase's point set from currentPointIndex alone -
    // called at every point in the flow where currentPointIndex or phase changes, so the map
    // never needs its own separate bookkeeping of what's done vs pending.
    private void RefreshPointDots()
    {
        PointMarkerView[] activeSet = phase == Phase.Calibrating ? calibrationPointDots : validationPointDots;
        PointMarkerView[] otherSet = phase == Phase.Calibrating ? validationPointDots : calibrationPointDots;

        SetDotArrayVisible(otherSet, false);

        if (activeSet == null)
        {
            return;
        }

        for (int i = 0; i < activeSet.Length; i++)
        {
            if (activeSet[i].markerRenderer == null)
            {
                continue;
            }

            if (i == currentPointIndex)
            {
                // The real marker already sits at this exact point right now - a second
                // indicator here would just be a duplicate object in the same spot.
                activeSet[i].markerRenderer.gameObject.SetActive(false);
                if (activeSet[i].checkmark != null)
                {
                    activeSet[i].checkmark.gameObject.SetActive(false);
                }
                continue;
            }

            bool isDone = i < currentPointIndex;
            activeSet[i].markerRenderer.gameObject.SetActive(true);
            activeSet[i].markerRenderer.material.color = isDone ? DoneDotColor : PendingDotColor;
            if (activeSet[i].checkmark != null)
            {
                activeSet[i].checkmark.gameObject.SetActive(isDone);
            }
        }
    }

    // Dot order matches validationPointLocalOffsets (top, bottom, left, right). Takes whichever
    // residual list actually shipped (corrected vs uncorrected fallback - see correctionHelps
    // in HandleValidationComplete) so the dots never show a rosier number than what's live.
    // Only runs after RefreshPointDots has already marked every validation point Done (all 4
    // succeeded, by construction, to even reach here) - this just layers the quality tier on.
    private void UpdateValidationQualityDots(List<float> residuals)
    {
        if (validationPointDots == null)
        {
            return;
        }

        for (int i = 0; i < validationPointDots.Length; i++)
        {
            if (validationPointDots[i].markerRenderer == null || i >= residuals.Count)
            {
                continue;
            }

            validationPointDots[i].markerRenderer.material.color = GetBiasQualityColor(residuals[i]);
        }
    }

    private bool RecordCurrentPointCorrection(Vector3 gazeOriginWorld, Matrix4x4 headPose)
    {
        if (sampleCount == 0)
        {
            Debug.Log($"[CalibrationManager] Point {currentPointIndex} dropout - no valid samples collected this window.");
            return false;
        }

        Vector3 averageRawDirectionLocal = (sampleSum / sampleCount).normalized;

        // The true direction to the marker is a world-space fact, transformed backward into
        // the same local frame the raw gaze is already in - this is what makes the resulting
        // correction ride along with head rotation instead of only being valid at the exact
        // orientation calibration happened in.
        Vector3 truePointDirectionWorld = (calibrationMarker.position - gazeOriginWorld).normalized;
        Vector3 truePointDirectionLocal = headPose.inverse.MultiplyVector(originPoseMatrix.inverse.MultiplyVector(truePointDirectionWorld)).normalized;

        Quaternion pointCorrection = Quaternion.FromToRotation(averageRawDirectionLocal, truePointDirectionLocal);

        float pointCorrectionAngle = Quaternion.Angle(Quaternion.identity, pointCorrection);
        if (pointCorrectionAngle > maxPlausiblePointCorrectionDegrees)
        {
            Debug.Log($"[CalibrationManager] Point {currentPointIndex} rejected - correction angle {pointCorrectionAngle:F1}° exceeds {maxPlausiblePointCorrectionDegrees}° threshold (likely not looking at the marker).");
            return false;
        }

        acceptedPointCorrections.Add(pointCorrection);
        Debug.Log($"[CalibrationManager] Point {currentPointIndex} correction: {pointCorrection.eulerAngles} (angle={pointCorrectionAngle:F1}°) - accepted {acceptedPointCorrections.Count}/{calibrationPointLocalOffsets.Length} so far.");

        biasAngleSum += pointCorrectionAngle;
        perPointCorrectionAngles.Add(pointCorrectionAngle);
        pointsCollected++;
        return true;
    }

    private bool RecordValidationResidual(Vector3 gazeOriginWorld, Matrix4x4 headPose)
    {
        if (sampleCount == 0)
        {
            Debug.Log($"[CalibrationManager] Validation point {currentPointIndex} dropout - no valid samples collected this window.");
            return false;
        }

        Vector3 averageRawDirectionLocal = (sampleSum / sampleCount).normalized;
        Vector3 truePointDirectionWorld = (calibrationMarker.position - gazeOriginWorld).normalized;
        Vector3 truePointDirectionLocal = headPose.inverse.MultiplyVector(originPoseMatrix.inverse.MultiplyVector(truePointDirectionWorld)).normalized;

        float rawAngle = Vector3.Angle(averageRawDirectionLocal, truePointDirectionLocal);
        if (rawAngle > maxPlausiblePointCorrectionDegrees)
        {
            Debug.Log($"[CalibrationManager] Validation point {currentPointIndex} rejected - raw angle {rawAngle:F1}° exceeds {maxPlausiblePointCorrectionDegrees}° threshold (likely not looking at the marker).");
            return false;
        }

        Vector3 correctedDirectionLocal = CalibrationCorrectionLocal * averageRawDirectionLocal;
        float residualAngle = Vector3.Angle(correctedDirectionLocal, truePointDirectionLocal);

        float precisionDegrees = 0f;
        foreach (Vector3 sample in currentPointRawSamplesLocal)
        {
            precisionDegrees += Vector3.Angle(sample.normalized, averageRawDirectionLocal);
        }
        precisionDegrees /= currentPointRawSamplesLocal.Count;

        Debug.Log($"[CalibrationManager] Validation point {currentPointIndex} accepted - residual (accuracy)={residualAngle:F1}°, precision (sample spread)={precisionDegrees:F1}°, uncorrected (raw) residual={rawAngle:F1}°.");

        validationResidualSum += residualAngle;
        perPointValidationResiduals.Add(residualAngle);
        perPointUncorrectedValidationResiduals.Add(rawAngle);
        validationPrecisionSum += precisionDegrees;
        validationUncorrectedResidualSum += rawAngle;
        if (residualAngle > validationWorstResidualDegrees)
        {
            validationWorstResidualDegrees = residualAngle;
            validationWorstResidualPointIndex = currentPointIndex;
        }
        if (rawAngle > validationWorstUncorrectedResidualDegrees)
        {
            validationWorstUncorrectedResidualDegrees = rawAngle;
            validationWorstUncorrectedResidualPointIndex = currentPointIndex;
        }
        validationPointsMeasured++;
        return true;
    }

    // currentPointIndex only advances on success - a failed point leaves the marker put and
    // gets attempted again, with every earlier point's already-accepted data untouched.
    private void AdvanceToNextPoint(bool pointSucceeded)
    {
        pointTimer = 0f;
        sampleSum = Vector3.zero;
        sampleCount = 0;
        currentPointRawSamplesLocal.Clear();
        stableFrameCount = 0;

        if (pointSucceeded)
        {
            currentPointIndex++;
            if (currentPointRetryCount >= perPointFitGuidanceThreshold)
            {
                ShowResult(string.Empty, Color.white);
            }
            currentPointRetryCount = 0;
        }
        else
        {
            currentPointRetryCount++;
            FlashMarkerRed();
            if (currentPointRetryCount >= perPointFitGuidanceThreshold)
            {
                ShowResult("Having trouble tracking this point - try adjusting your headset fit", Color.yellow);
            }
        }

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
        UpdateProgressText();
        RefreshPointDots();
    }

    // A failed point retries itself instead of advancing, so reaching this method at all means
    // every training point already succeeded, by construction.
    private void HandleCalibrationComplete()
    {
        CalibrationCorrectionLocal = AverageQuaternions(acceptedPointCorrections);

        IsCalibrated = true;
        phase = Phase.Validating;
        currentPointIndex = 0;
        calibrationMarker.position = transform.TransformPoint(validationPointLocalOffsets[0]);
        UpdateProgressText();
        RefreshPointDots();
    }

    // Combines N rotation estimates into the single rotation closest to all of them
    // simultaneously (Markley et al., "Averaging Quaternions", 2007) - order-independent and
    // least-squares-optimal, unlike a sequential Slerp chain where later inputs get
    // progressively less influence purely from processing order.
    //
    // Each quaternion is a 4D unit vector. The 4x4 symmetric matrix M = sum(q_i * q_i^T) has
    // the property that its dominant eigenvector is that closest single rotation. Found here
    // via power iteration rather than a full eigendecomposition/SVD, since only the dominant
    // eigenvector is needed and M is small and well-conditioned for this use case.
    private static Quaternion AverageQuaternions(List<Quaternion> quaternions)
    {
        if (quaternions.Count == 0)
        {
            return Quaternion.identity;
        }
        if (quaternions.Count == 1)
        {
            return quaternions[0];
        }

        // q and -q are the same rotation but would partially cancel in the sum below if left
        // in opposite hemispheres - flip each to match the first quaternion's sign.
        Vector4 reference = QuaternionToVector4(quaternions[0]);
        float[,] accumulator = new float[4, 4];
        foreach (Quaternion q in quaternions)
        {
            Vector4 v = QuaternionToVector4(q);
            if (Vector4.Dot(v, reference) < 0f)
            {
                v = -v;
            }
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 4; col++)
                {
                    accumulator[row, col] += v[row] * v[col];
                }
            }
        }

        Vector4 estimate = reference;
        for (int iteration = 0; iteration < 30; iteration++)
        {
            Vector4 next = Vector4.zero;
            for (int row = 0; row < 4; row++)
            {
                float sum = 0f;
                for (int col = 0; col < 4; col++)
                {
                    sum += accumulator[row, col] * estimate[col];
                }
                next[row] = sum;
            }
            estimate = next.normalized;
        }

        return Vector4ToQuaternion(estimate);
    }

    private static Vector4 QuaternionToVector4(Quaternion q)
    {
        return new Vector4(q.x, q.y, q.z, q.w);
    }

    private static Quaternion Vector4ToQuaternion(Vector4 v)
    {
        return new Quaternion(v.x, v.y, v.z, v.w);
    }

    // A failed validation point retries itself, so reaching this method means all 4 points
    // succeeded, by construction. The only remaining path to a full RetryCalibration from here
    // is the quality gate below - a real accuracy problem with the fitted correction itself,
    // not any single point being unusable.
    private void HandleValidationComplete()
    {
        phase = Phase.Finished;
        calibrationMarker.gameObject.SetActive(false);
        UpdateProgressText();
        RefreshPointDots();

        float averageResidualDegrees = validationResidualSum / validationPointsMeasured;
        float averageTrainingBiasDegrees = biasAngleSum / pointsCollected;
        float averagePrecisionDegrees = validationPrecisionSum / validationPointsMeasured;
        float averageUncorrectedResidualDegrees = validationUncorrectedResidualSum / validationPointsMeasured;

        float worstResidualDegrees = validationWorstResidualDegrees;
        int worstResidualPointIndex = validationWorstResidualPointIndex;

        // A fitted correction is only an estimate from a short, noisy sample - if it isn't
        // actually better than raw uncorrected gaze on these held-out points, discard it and
        // fall back to PICO's raw output instead of assuming the fit is good.
        bool correctionHelps = averageResidualDegrees < averageUncorrectedResidualDegrees;
        if (!correctionHelps)
        {
            Debug.Log($"[CalibrationManager] Correction REJECTED - corrected residual ({averageResidualDegrees:F1}°) not better than raw uncorrected gaze ({averageUncorrectedResidualDegrees:F1}°). Falling back to PICO's raw gaze output.");
            CalibrationCorrectionLocal = Quaternion.identity;
            averageResidualDegrees = averageUncorrectedResidualDegrees;
            worstResidualDegrees = validationWorstUncorrectedResidualDegrees;
            worstResidualPointIndex = validationWorstUncorrectedResidualPointIndex;
        }

        UpdateValidationQualityDots(correctionHelps ? perPointValidationResiduals : perPointUncorrectedValidationResiduals);

        // Gates on both the mean AND the worst point - a good average can hide one badly
        // tracked region of the field, so every point has to independently qualify as Good.
        bool worstPointAcceptable = worstResidualDegrees <= GoodBiasCeilingDegrees;
        bool qualityAcceptable = averageResidualDegrees <= GoodBiasCeilingDegrees && worstPointAcceptable;

        // On-screen messaging only ever needs to say pass or fail (complete vs retrying) - the
        // graded quality tiers below exist for the pass/fail math and the dots' colors, not for
        // display text, so the log lines report the raw numbers rather than a label/percent.
        if (qualityAcceptable)
        {
            Debug.Log($"[CalibrationManager] Validation complete: {validationPointsMeasured}/{validationPointLocalOffsets.Length} points measured. Residual error={averageResidualDegrees:F1}° vs uncorrected {averageUncorrectedResidualDegrees:F1}° - worst point={worstResidualPointIndex} ({worstResidualDegrees:F1}°) - training-set bias was {averageTrainingBiasDegrees:F1}° for comparison. Precision (avg sample spread)={averagePrecisionDegrees:F1}°. CalibrationCorrectionLocal={CalibrationCorrectionLocal.eulerAngles}");
            // Once calibration is complete, direct the user to eyetracking scene.
            ShowResult("Calibration complete", GetBiasQualityColor(averageResidualDegrees));
            consecutiveFullRetries = 0;
            Invoke(nameof(LoadMainScene), resultDisplayDuration);
        }
        else
        {
            consecutiveFullRetries++;
            Debug.Log($"[CalibrationManager] Validation complete but quality below the {GoodBiasCeilingDegrees}° Good threshold - retrying (consecutive full retries={consecutiveFullRetries}). Residual error={averageResidualDegrees:F1}° vs uncorrected {averageUncorrectedResidualDegrees:F1}°, worst point={worstResidualPointIndex} ({worstResidualDegrees:F1}°{(worstPointAcceptable ? "" : " - FAILED worst-point gate")}), training-set bias was {averageTrainingBiasDegrees:F1}° for comparison. Precision (avg sample spread)={averagePrecisionDegrees:F1}°.");

            string message = consecutiveFullRetries >= fullSequenceFitGuidanceThreshold
                ? "Retrying... check headset fit"
                : "Retrying...";
            ShowResult(message, GetBiasQualityColor(averageResidualDegrees));
            Invoke(nameof(RetryCalibration), resultDisplayDuration);
        }
    }

    // Not yet independently validated for this device - see README. PoorBiasCeilingDegrees is
    // deliberately not the same value as maxPlausiblePointCorrectionDegrees above: that
    // threshold asks "is this point's data usable at all," this one asks "how good is an
    // already-valid result."
    private const float PoorBiasCeilingDegrees = 5f;
    private const float GoodBiasCeilingDegrees = 3f;

    private static Color GetBiasQualityColor(float averageBiasDegrees)
    {
        if (averageBiasDegrees <= GoodBiasCeilingDegrees) return new Color(0f, 1f, 0.56078434f);
        if (averageBiasDegrees <= PoorBiasCeilingDegrees) return Color.yellow;
        return new Color(1f, 0.5f, 0f);
    }

    //Logged only not shown on screen, a linear 0deg=100%/5deg=0% mapping is disconnected
    // from the actual pass/fail semantics (e.g. exactly 3deg, the pass threshold, displays as
    // 40%, reading like a poor result despite being a genuine pass).
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

    private void LoadMainScene()
    {
        SceneManager.LoadScene(mainSceneName);
    }

    // Only reached from the quality-gate failure in HandleValidationComplete now - a single
    // glitched point retries itself instead (see AdvanceToNextPoint).
    private void RetryCalibration()
    {
        currentPointIndex = 0;
        pointsCollected = 0;
        currentPointRetryCount = 0;
        acceptedPointCorrections.Clear();
        perPointCorrectionAngles.Clear();
        perPointValidationResiduals.Clear();
        perPointUncorrectedValidationResiduals.Clear();
        biasAngleSum = 0f;
        validationResidualSum = 0f;
        validationPrecisionSum = 0f;
        validationUncorrectedResidualSum = 0f;
        validationWorstResidualDegrees = 0f;
        validationWorstResidualPointIndex = -1;
        validationWorstUncorrectedResidualDegrees = 0f;
        validationWorstUncorrectedResidualPointIndex = -1;
        validationPointsMeasured = 0;
        pointTimer = 0f;
        sampleSum = Vector3.zero;
        sampleCount = 0;
        currentPointRawSamplesLocal.Clear();
        stableFrameCount = 0;
        CalibrationCorrectionLocal = Quaternion.identity;
        IsCalibrated = false;
        phase = Phase.Calibrating;

        ShowResult(string.Empty, Color.white);
        RefreshPointDots();
        if (markerFlashCoroutine != null)
        {
            StopCoroutine(markerFlashCoroutine);
            markerFlashCoroutine = null;
        }
        if (markerMaterialInstance != null)
        {
            markerMaterialInstance.color = markerBaseColor;
        }
        if (markerProgressRing != null)
        {
            markerProgressRing.fillAmount = 0f;
        }
        calibrationMarker.gameObject.SetActive(true);
        calibrationMarker.localScale = markerBaseScale;
        calibrationMarker.position = transform.TransformPoint(calibrationPointLocalOffsets[0]);
        UpdateProgressText();
    }
}
