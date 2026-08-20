using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;
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
    [Header("Multi-Scene Setup")]
    [SerializeField] private string mainSceneName = "EyeTrackingDemo";

    [Header("References")]
    [SerializeField] private Transform calibrationMarker;
    // This scene's own copy of the XR Origin - no EyeTrackingManager instance exists yet to
    // borrow one from during calibration.
    [SerializeField] private Transform xrOrigin;
    [SerializeField] private TMP_Text statusText;

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
    [SerializeField] private float dwellDurationPerPoint = 2f;
    // Sampling only starts after this long, letting the initial saccade to the new point settle.
    [SerializeField] private float settleTimeBeforeSampling = 1f;
    [SerializeField] private float resultDisplayDuration = 2f;

    [Header("Adaptive Sampling")]
    // A point finishes early once its samples converge, instead of always waiting out
    // dwellDurationPerPoint. Same angular-deviation-from-mean math as the precision metric
    // below, just computed live every frame. Starting values, not yet tuned against real data.
    [SerializeField] private float convergencePrecisionDegrees = 0.5f;
    [SerializeField] private int minSamplesBeforeConvergenceCheck = 5;
    [SerializeField] private int minStableFramesToConverge = 10;

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
        }
    }

    private Vector3[] CurrentPointSet => phase == Phase.Calibrating ? calibrationPointLocalOffsets : validationPointLocalOffsets;

    private void Update()
    {
        if (phase == Phase.Finished || calibrationMarker == null || xrOrigin == null)
        {
            return;
        }

        pointTimer += Time.deltaTime;

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

        string qualityLabel = GetBiasQualityLabel(averageResidualDegrees);
        int qualityPercent = GetBiasQualityPercent(averageResidualDegrees);

        // Gates on both the mean AND the worst point - a good average can hide one badly
        // tracked region of the field, so every point has to independently qualify as Good.
        bool worstPointAcceptable = worstResidualDegrees <= GoodBiasCeilingDegrees;
        bool qualityAcceptable = averageResidualDegrees <= GoodBiasCeilingDegrees && worstPointAcceptable;

        if (qualityAcceptable)
        {
            Debug.Log($"[CalibrationManager] Validation complete: {validationPointsMeasured}/{validationPointLocalOffsets.Length} points measured. Residual error={averageResidualDegrees:F1}° ({qualityLabel}, {qualityPercent}%) vs uncorrected {averageUncorrectedResidualDegrees:F1}° - worst point={worstResidualPointIndex} ({worstResidualDegrees:F1}°) - training-set bias was {averageTrainingBiasDegrees:F1}° for comparison. Precision (avg sample spread)={averagePrecisionDegrees:F1}°. CalibrationCorrectionLocal={CalibrationCorrectionLocal.eulerAngles}");
            // Label/percent stay in the log only - see GetBiasQualityPercent.
            ShowResult("Tracking ready", GetBiasQualityColor(averageResidualDegrees));
            consecutiveFullRetries = 0;
            Invoke(nameof(LoadMainScene), resultDisplayDuration);
        }
        else
        {
            consecutiveFullRetries++;
            Debug.Log($"[CalibrationManager] Validation complete but quality below the {GoodBiasCeilingDegrees}° Good threshold - retrying (consecutive full retries={consecutiveFullRetries}). Residual error={averageResidualDegrees:F1}° ({qualityLabel}, {qualityPercent}%) vs uncorrected {averageUncorrectedResidualDegrees:F1}°, worst point={worstResidualPointIndex} ({worstResidualDegrees:F1}°{(worstPointAcceptable ? "" : " - FAILED worst-point gate")}), training-set bias was {averageTrainingBiasDegrees:F1}° for comparison. Precision (avg sample spread)={averagePrecisionDegrees:F1}°.");

            string message = "Calibration Quality Too Low - Retrying...";
            if (consecutiveFullRetries >= fullSequenceFitGuidanceThreshold)
            {
                message += " Try adjusting your headset fit.";
            }
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
        return new Color(1f, 0.5f, 0f);
    }

    // Logged only, not shown on-screen - a linear 0deg=100%/5deg=0% mapping is disconnected
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
        calibrationMarker.gameObject.SetActive(true);
        calibrationMarker.position = transform.TransformPoint(calibrationPointLocalOffsets[0]);
    }
}
