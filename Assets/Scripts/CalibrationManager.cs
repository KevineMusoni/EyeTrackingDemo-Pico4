using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;
using System.Collections.Generic;

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
// corrections are combined into one final CalibrationCorrectionLocal quaternion via a
// simultaneous least-squares fit (AverageQuaternions, Markley's method - see its comment),
// which EyeTrackingManager (in the main scene, once loaded) applies to its own gaze vector.
//
// HEAD-LOCAL, NOT WORLD SPACE: the fit and blend above happen entirely in head-local
// coordinates (the raw gaze vector's own natural space), never transformed to world space
// first. The physical bias this corrects for is a property of the eye/sensor relative to the
// HEAD, not the room, so a correction computed in world space would only stay accurate for as
// long as the head stayed in the exact orientation it was in during calibration - turning the
// head afterward would silently misalign it. Converting to world space happens only as the
// very last step, after the correction has already been applied locally.
//
// MULTI-SCENE SETUP (true sequential, not additive): this script lives in a dedicated
// "Calibration" scene with its OWN copy of the XR rig (camera + PXR_Manager, duplicated from
// the main scene) - so it can render and read real gaze data completely independently,
// before the main scene has even loaded. Calibration runs entirely in isolation; only once
// all 5 points are done does it call a normal (non-additive) SceneManager.LoadScene(), which
// automatically replaces this scene with the main one - no manual unload needed, and no
// overlap between the two scenes at any point (unlike an earlier additive-loading version).
//
// CalibrationCorrectionLocal/IsCalibrated are STATIC and never written to disk - they live purely
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
    // mechanism, but these are held out of CalibrationCorrectionLocal entirely (see
    // RecordValidationResidual). Measuring accuracy on points the correction was never fit on
    // (a train/test split) is what actually justifies a quality score - reusing the training
    // points themselves would optimistically overstate how well the correction generalizes.
    //
    // MODIFIED: 4 cardinal points (top/bottom/left/right) instead of 2 points on the same
    // diagonal - the old pair only ever tested along one line through the field, so a bias
    // specific to the other diagonal (or to the horizontal/vertical axes) could pass unnoticed.
    // Each offset's magnitude is 0.583, chosen so every point sits at the SAME angular distance
    // from center (~16.26°, purely geometric - see calibrationPointLocalOffsets' diagonal
    // corners) as the training corners already do: sqrt(0.5^2 + 0.3^2) = sqrt(0.34) = 0.583 =
    // 2 * tan(16.26 degrees). Same "how far from center" difficulty as training, just spent on
    // one axis instead of split across two, so validation exercises a comparable region of the
    // field along axes training never directly tested.
    [SerializeField]
    private Vector3[] validationPointLocalOffsets = new Vector3[]
    {
        new Vector3(0f, 0.583f, 2f),    // top
        new Vector3(0f, -0.583f, 2f),   // bottom
        new Vector3(-0.583f, 0f, 2f),   // left
        new Vector3(0.583f, 0f, 2f),    // right
    };

    [Header("Timing")]
    // MODIFIED: no longer a fixed duration every point sits for - now a TIMEOUT ceiling. Most
    // points converge (see [Header("Adaptive Sampling")] below) well before this, and advance
    // early; this only matters as a fallback if a point never stabilizes (noisy tracking).
    [SerializeField] private float dwellDurationPerPoint = 2f;
    // Only samples raw gaze data during the LAST portion of the dwell window, letting the
    // initial saccade (the fast eye movement jumping to the new point) settle first -
    // sampling too early would average in the "still moving toward the target" data.
    [SerializeField] private float settleTimeBeforeSampling = 1f;
    // MODIFIED: "Calibration Failed" no longer exists as an outcome (see HandleCalibrationComplete -
    // per-point retry means training can't reach a failed state anymore). Now just how long the
    // "Calibration Passed"/"Calibration Quality Too Low" message stays on screen before the scene
    // transitions (pass) or the whole 5+4-point sequence restarts (quality-gate fail).
    [SerializeField] private float resultDisplayDuration = 2f;

    [Header("Adaptive Sampling")]
    // ADDED: lets a point finish EARLY once its samples have visibly converged, instead of
    // always waiting out the full dwellDurationPerPoint - most points settle in well under 2s
    // (precision numbers logged elsewhere in this class are often under 1°), so a fixed window
    // wastes time on already-stable points every single calibration run. Purely a time/UX
    // improvement - doesn't change what data is considered valid, just how soon "enough" data
    // is judged to have been collected. Uses the SAME angular-deviation-from-mean math as
    // precisionDegrees elsewhere in this class, just computed live instead of only at the end.
    // Starting values, not yet tuned against real device data - see README.
    [SerializeField] private float convergencePrecisionDegrees = 0.5f;
    // Below this many samples, the running precision estimate is too noisy on its own to trust
    // (e.g. 1-2 samples are trivially "tightly clustered") - convergence isn't even checked yet.
    [SerializeField] private int minSamplesBeforeConvergenceCheck = 5;
    // Requires precision to stay under convergencePrecisionDegrees for this many CONSECUTIVE
    // frames (not just one) before accepting convergence - guards against stopping early on a
    // single lucky frame in an otherwise-still-settling or noisy sample window.
    [SerializeField] private int minStableFramesToConverge = 10;

    [Header("Validation")]
    // A point's raw-to-true correction angle above this is treated as the user not actually
    // looking at the marker (distracted, looked elsewhere, blinked through the whole window)
    // rather than a genuine tracking bias this large - see RecordCurrentPointCorrection.
    [SerializeField] private float maxPlausiblePointCorrectionDegrees = 20f;

    [Header("Fit Guidance")]
    // ADDED: NOT a retry cap - calibration still retries indefinitely by design (see class
    // comment). These are purely diagnostic nudges shown when
    // retries pile up in a pattern that's a common symptom of headset FIT rather than a one-off
    // glitch: either the SAME point failing repeatedly in a row, or the WHOLE sequence's
    // measured quality failing repeatedly in a row. Neither ever stops or limits retrying -
    // they only add a supplementary on-screen hint while retrying continues as normal.
    [SerializeField] private int perPointFitGuidanceThreshold = 3;
    [SerializeField] private int fullSequenceFitGuidanceThreshold = 3;

    // STATIC - see class comment above for why. Defaults to identity/false so that opening
    // the main scene directly (skipping calibration entirely, e.g. for a quick test in the
    // Editor) still behaves safely - EyeTrackingManager just applies a no-op correction.
    // RENAMED from CalibrationCorrection: this is now fit and must be applied in HEAD-LOCAL
    // space, not world space - see the class comment above and Update()/RecordCurrentPointCorrection
    // for why a world-space correction doesn't stay valid once the head turns.
    public static bool IsCalibrated { get; private set; } = false;
    public static Quaternion CalibrationCorrectionLocal { get; private set; } = Quaternion.identity;

    private Matrix4x4 originPoseMatrix;
    private Matrix4x4 headPoseMatrix;
    // ADDED: the world-space gaze origin from the most recent SUCCESSFUL read - needed because
    // AdvanceToNextPoint's dwell-completion check can land on a frame where TryReadRawGaze
    // failed, and the origin barely moves frame-to-frame anyway, so the last good value is a
    // safe stand-in rather than having nothing to pass to RecordCurrentPointCorrection/
    // RecordValidationResidual.
    private Vector3 lastValidGazeOriginWorld;
    // ADDED: the head pose matrix from that same successful frame - needed to transform the
    // TRUE direction (a world-space fact, since the marker has a fixed world position) backward
    // into the same head-local frame the raw gaze samples were actually collected in.
    private Matrix4x4 lastValidHeadPoseMatrix;

    private int currentPointIndex = 0;
    private float pointTimer = 0f;
    private Vector3 sampleSum = Vector3.zero;
    private int sampleCount = 0;
    private int pointsCollected = 0;
    // ADDED: consecutive frames the running sample precision has stayed under
    // convergencePrecisionDegrees - see that field's comment. Reset to 0 the moment precision
    // exceeds the threshold on any frame, so only a genuinely STABLE run of good frames counts.
    private int stableFrameCount = 0;
    // ADDED: consecutive times the CURRENT point specifically has failed and been retried (not the whole sequence) - drives perPointFitGuidanceThreshold. Reset on that point's success.
    private int currentPointRetryCount = 0;
    // ADDED: consecutive times the WHOLE sequence has been restarted via RetryCalibration due
    // to a quality-gate failure (the only remaining reason RetryCalibration gets called - see
    // its comment) - drives fullSequenceFitGuidanceThreshold. Reset on eventual overall pass.
    private int consecutiveFullRetries = 0;
    // ADDED: each accepted training point's own individual correction, collected here instead of
    // being blended in immediately - see AverageQuaternions for why fitting all 5 SIMULTANEOUSLY
    // (once, after all are collected) is more correct than the old sequential Quaternion.Slerp
    // chain (order-dependent, not a true least-squares combination of the 5 measurements).
    private List<Quaternion> acceptedPointCorrections = new List<Quaternion>();
    // ADDED: raw per-sample directions collected during the CURRENT point's settle window,
    // alongside sampleSum/sampleCount above. sampleSum's running total is enough to compute the
    // MEAN direction, but not how spread out the individual samples were around that mean - that
    // spread is what precisionDegrees (see RecordValidationResidual) measures. Cleared every
    // point in AdvanceToNextPoint, same as sampleSum.
    private List<Vector3> currentPointRawSamplesLocal = new List<Vector3>();
    // ADDED: running sum of each accepted point's pointCorrectionAngle (bias, in degrees) -
    // used to compute the average bias shown as a plain-language quality label after passing.
    // See GetBiasQualityLabel for why the degree thresholds are set where they are.
    private float biasAngleSum = 0f;
    // ADDED: sum/count of residual errors measured against the held-out validation points,
    // AFTER applying the already-finalized CalibrationCorrectionLocal - this is the honest,
    // train/test-split accuracy number, as opposed to biasAngleSum above (the training-set
    // number, which is optimistic since those same points were used to fit the correction).
    private float validationResidualSum = 0f;
    private int validationPointsMeasured = 0;
    // ADDED: sum of each validation point's PRECISION (average angular deviation of its raw
    // samples from their own mean direction) - a DIFFERENT statistic from validationResidualSum.
    // Residual/bias measures ACCURACY: how far the average sample is from the true point.
    // Precision measures CONSISTENCY: how tightly clustered the samples were around each other,
    // independent of whether that cluster is centered in the right place. A correction can be
    // precise-but-biased (tight cluster, wrong spot) or accurate-but-imprecise (centered right,
    // noisy) - conflating the two, as the old bias-only tracking did, hides that distinction.
    private float validationPrecisionSum = 0f;
    // ADDED: sum of each validation point's UNCORRECTED residual (raw gaze vs true direction,
    // CalibrationCorrectionLocal never applied) - the baseline validationResidualSum (corrected)
    // gets compared against in HandleValidationComplete. A fitted correction is only an estimate
    // built from a short, noisy sample - if that estimate happened to capture one-off sample
    // noise rather than real, persistent tracking bias, applying it can leave gaze WORSE off
    // than doing nothing. This is what lets that case be caught instead of assumed away.
    private float validationUncorrectedResidualSum = 0f;
    // ADDED: the single WORST point's residual/index, not just the average across all 4. A good
    // average can hide one badly-tracked region of the visual field (e.g. 3 points at 0.5° and
    // one at 8° still averages to a "Good" 2.4°) - gating on this too means a bad region can't
    // hide behind good ones. Tracked separately for corrected vs uncorrected, same reason
    // validationResidualSum/validationUncorrectedResidualSum are separate - whichever result
    // actually ships (see the correctionHelps fallback in HandleValidationComplete) determines
    // which "worst" value the gate should actually check.
    private float validationWorstResidualDegrees = 0f;
    private int validationWorstResidualPointIndex = -1;
    private float validationWorstUncorrectedResidualDegrees = 0f;
    private int validationWorstUncorrectedResidualPointIndex = -1;

    // ADDED: which stage of the flow Update() is currently driving. Calibrating fits
    // CalibrationCorrectionLocal from the 5 training points; Validating measures accuracy against
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

    // Which point array Update()/AdvanceToNextPoint are currently walking through.
    private Vector3[] CurrentPointSet => phase == Phase.Calibrating ? calibrationPointLocalOffsets : validationPointLocalOffsets;

    private void Update()
    {
        if (phase == Phase.Finished || calibrationMarker == null || xrOrigin == null)
        {
            return;
        }

        pointTimer += Time.deltaTime;

        // MODIFIED: was calling GetHeadPosMatrix/GetCombineEyeGazeVector/GetCombineEyeGazePoint
        // directly and ignoring their bool returns - a failed PXR read still writes zero-valued
        // data into its out parameter, which was silently getting averaged in as a real sample.
        // TryReadRawGaze checks all of them (see GazeReading.cs); a failed frame now contributes
        // nothing to sampleSum/sampleCount instead of corrupting the average.
        if (GazeReading.TryReadRawGaze(out headPoseMatrix, out Vector3 rawGazeVector, out Vector3 rawGazeOrigin))
        {
            // MODIFIED: sampleSum now accumulates rawGazeVector directly - no world-space
            // transform here anymore. The correction is fit in this same LOCAL (head-relative)
            // space instead, so it stays valid no matter which way the head later turns - see
            // class comment and RecordCurrentPointCorrection. lastValidHeadPoseMatrix is
            // remembered from this same successful frame so the true-direction-to-marker (a
            // world-space fact) can be transformed backward into this same local frame later.
            lastValidHeadPoseMatrix = headPoseMatrix;
            lastValidGazeOriginWorld = originPoseMatrix.MultiplyPoint(headPoseMatrix.MultiplyPoint(rawGazeOrigin));

            // Only start averaging raw gaze samples after the settle time has passed, so the
            // initial saccade toward the new point doesn't skew the average.
            if (pointTimer >= settleTimeBeforeSampling)
            {
                sampleSum += rawGazeVector;
                sampleCount++;
                currentPointRawSamplesLocal.Add(rawGazeVector);

                // ADDED: live convergence check - same math as precisionDegrees elsewhere in
                // this class (average angular deviation of each sample from the running mean),
                // just computed every frame instead of only once at the end. See
                // convergencePrecisionDegrees' field comment for why this lets a point finish
                // early instead of always waiting out the full dwellDurationPerPoint.
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

        // MODIFIED: a point now completes on EITHER the old fixed timeout OR early convergence
        // (stableFrameCount reaching minStableFramesToConverge) - whichever happens first. See
        // [Header("Adaptive Sampling")] for why.
        bool dwellTimedOut = pointTimer >= dwellDurationPerPoint;
        bool sampleConverged = stableFrameCount >= minStableFramesToConverge;
        if (dwellTimedOut || sampleConverged)
        {
            // ADDED: visibility into whether/how much adaptive sampling actually saves - logs
            // the real elapsed time for this point and why it ended, so time savings are
            // directly observable in adb logcat rather than assumed.
            string completionReason = sampleConverged ? "converged early" : "timed out";
            Debug.Log($"[CalibrationManager] Point {currentPointIndex} {completionReason} after {pointTimer:F2}s (vs {dwellDurationPerPoint:F2}s fixed timeout).");

            // MODIFIED: RecordCurrentPointCorrection/RecordValidationResidual now return
            // if this point was actually accepted - AdvanceToNextPoint uses that to
            // decide whether to move on or retry this SAME point, instead of always advancing.
            bool pointSucceeded = phase == Phase.Calibrating
                ? RecordCurrentPointCorrection(lastValidGazeOriginWorld, lastValidHeadPoseMatrix)
                : RecordValidationResidual(lastValidGazeOriginWorld, lastValidHeadPoseMatrix);
            AdvanceToNextPoint(pointSucceeded);
        }
    }

    // MODIFIED: now returns whether the point was accepted - see AdvanceToNextPoint, which
    // retries this SAME point on false instead of always moving to the next one.
    private bool RecordCurrentPointCorrection(Vector3 gazeOriginWorld, Matrix4x4 headPose)
    {
        if (sampleCount == 0)
        {
            // Didn't get a sample window (e.g. eye tracking dropped out) - skip this
            // point rather than corrupting the blended correction
            Debug.Log($"[CalibrationManager] Point {currentPointIndex} dropout - no valid samples collected this window.");
            return false;
        }

        // MODIFIED: averageRawDirection is now in LOCAL (head-relative) space, since sampleSum
        // was never transformed to world space (see Update()).
        Vector3 averageRawDirectionLocal = (sampleSum / sampleCount).normalized;

        // ADDED: the true direction to the marker is naturally a WORLD-space fact (the marker
        // has a fixed world position), so it's computed in world space first, then transformed
        // BACKWARD into the same local frame the raw gaze is already in, using the inverse of
        // the same matrices Update() uses to go the other way. This is what makes the resulting
        // correction "ride along" with head rotation instead of only being valid for the exact
        // head orientation present during calibration - see class comment.
        Vector3 truePointDirectionWorld = (calibrationMarker.position - gazeOriginWorld).normalized;
        Vector3 truePointDirectionLocal = headPose.inverse.MultiplyVector(originPoseMatrix.inverse.MultiplyVector(truePointDirectionWorld)).normalized;

        // The rotation that would take the raw (uncorrected) LOCAL direction and align it
        // exactly with the TRUE LOCAL direction to the known calibration point - this IS the
        // correction, now expressed relative to the head instead of the room.
        Quaternion pointCorrection = Quaternion.FromToRotation(averageRawDirectionLocal, truePointDirectionLocal);

        // ADDED: reject implausibly large per-point corrections. Quaternion.Angle gives the
        // single overall rotation magnitude (handles axis wraparound correctly, unlike reading
        // eulerAngles directly) - if this point alone would require a huge correction, the
        // averaged raw direction almost certainly isn't the user looking at the marker at all,
        // and blending it in would corrupt the final CalibrationCorrectionLocal with garbage data.
        float pointCorrectionAngle = Quaternion.Angle(Quaternion.identity, pointCorrection);
        if (pointCorrectionAngle > maxPlausiblePointCorrectionDegrees)
        {
            Debug.Log($"[CalibrationManager] Point {currentPointIndex} rejected - correction angle {pointCorrectionAngle:F1}° exceeds {maxPlausiblePointCorrectionDegrees}° threshold (likely not looking at the marker).");
            return false;
        }

        // MODIFIED: no longer blended into CalibrationCorrectionLocal immediately - just
        // collected here. All 5 accepted corrections get combined ONCE, simultaneously, in
        // HandleCalibrationComplete once every point is done - see AverageQuaternions.
        acceptedPointCorrections.Add(pointCorrection);

        // ADDED: log each point's own individual correction as it's collected, so the 5 inputs
        // going into AverageQuaternions can be checked against its final output (already logged
        // in HandleValidationComplete's "Validation complete" line, CalibrationCorrectionLocal=)
        // - a sanity check that the combined result actually sits among/close to the 5 individual
        // answers, not something wildly different that would indicate a bug in the fit itself.
        Debug.Log($"[CalibrationManager] Point {currentPointIndex} correction: {pointCorrection.eulerAngles} (angle={pointCorrectionAngle:F1}°) - accepted {acceptedPointCorrections.Count}/{calibrationPointLocalOffsets.Length} so far.");

        biasAngleSum += pointCorrectionAngle;
        pointsCollected++;
        return true;
    }

    // ADDED: measures accuracy against a held-out point - one CalibrationCorrectionLocal was
    // never fit on - instead of trusting the optimistic training-set number above. MODIFIED:
    // now returns whether the point was accepted - see AdvanceToNextPoint, which retries this
    // SAME point on false instead of always moving to the next one.
    private bool RecordValidationResidual(Vector3 gazeOriginWorld, Matrix4x4 headPose)
    {
        if (sampleCount == 0)
        {
            // Dropout - skip this validation point rather than counting it as a bad measurement.
            Debug.Log($"[CalibrationManager] Validation point {currentPointIndex} dropout - no valid samples collected this window.");
            return false;
        }

        // Same local-space treatment as RecordCurrentPointCorrection above - see its comments
        // for why the true direction gets transformed backward into local space here.
        Vector3 averageRawDirectionLocal = (sampleSum / sampleCount).normalized;
        Vector3 truePointDirectionWorld = (calibrationMarker.position - gazeOriginWorld).normalized;
        Vector3 truePointDirectionLocal = headPose.inverse.MultiplyVector(originPoseMatrix.inverse.MultiplyVector(truePointDirectionWorld)).normalized;

        // Same "were they even looking at this point" gate the training points use, checked on
        // the RAW angle - a glance-away during validation would otherwise inject a huge, bogus
        // residual into the very number this feature exists to make more honest.
        float rawAngle = Vector3.Angle(averageRawDirectionLocal, truePointDirectionLocal);
        if (rawAngle > maxPlausiblePointCorrectionDegrees)
        {
            Debug.Log($"[CalibrationManager] Validation point {currentPointIndex} rejected - raw angle {rawAngle:F1}° exceeds {maxPlausiblePointCorrectionDegrees}° threshold (likely not looking at the marker).");
            return false;
        }

        // Apply the ALREADY-FINALIZED CalibrationCorrectionLocal (fit only on the 5 training
        // points) and measure how far the corrected direction still is from this held-out
        // point's true direction - this is real calibration accuracy, not a number
        // optimistically measured on the same points used to build the correction.
        Vector3 correctedDirectionLocal = CalibrationCorrectionLocal * averageRawDirectionLocal;
        float residualAngle = Vector3.Angle(correctedDirectionLocal, truePointDirectionLocal);

        // ADDED: precision (self-consistency) for this point - average angular deviation of
        // each individual raw sample from THIS point's own mean direction (averageRawDirectionLocal),
        // not from the true point. A tight cluster gives a small value here even if the whole
        // cluster is off-target (that's what residualAngle above is for) - see the
        // validationPrecisionSum field comment for why these are tracked separately.
        float precisionDegrees = 0f;
        foreach (Vector3 sample in currentPointRawSamplesLocal)
        {
            precisionDegrees += Vector3.Angle(sample.normalized, averageRawDirectionLocal);
        }
        precisionDegrees /= currentPointRawSamplesLocal.Count;

        Debug.Log($"[CalibrationManager] Validation point {currentPointIndex} accepted - residual (accuracy)={residualAngle:F1}°, precision (sample spread)={precisionDegrees:F1}°, uncorrected (raw) residual={rawAngle:F1}°.");

        // ADDED: rawAngle above was already the uncorrected residual (raw gaze vs true
        // direction) - it was only being used for the rejection gate and then discarded. Now
        // also summed here so HandleValidationComplete can compare corrected vs uncorrected
        // performance - see validationUncorrectedResidualSum's field comment for why.
        validationResidualSum += residualAngle;
        validationPrecisionSum += precisionDegrees;
        validationUncorrectedResidualSum += rawAngle;
        // ADDED: track the worst point (by value AND index) alongside the sums above - see
        // validationWorstResidualDegrees' field comment for why the average alone isn't enough.
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

    // MODIFIED: retries only the point that just failed, not the whole sequence - see class
    // comment / [Header("Fit Guidance")]. Per-point transient state always resets (same as
    // before), but currentPointIndex only advances on SUCCESS - a failed point leaves the
    // marker right where it is and gets attempted again, with every earlier point's
    // already-accepted data left completely untouched.
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
            // ADDED: clear a fit-guidance hint that was showing for this now-resolved point.
            if (currentPointRetryCount >= perPointFitGuidanceThreshold)
            {
                ShowResult(string.Empty, Color.white);
            }
            currentPointRetryCount = 0;
        }
        else
        {
            currentPointRetryCount++;
            // ADDED: nudge, not a cap - the SAME point keeps retrying regardless; this just
            // tells the user why, since one specific point being consistently unreachable
            // while others succeed is a common symptom of headset fit, not something more
            // silent retries alone are likely to fix.
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

    // MODIFIED: no longer needs a pass/fail branch here. AdvanceToNextPoint only advances
    // currentPointIndex on a SUCCESSFUL point (a failed point retries itself instead) - so
    // reaching this method at all means every one of the 5 training points already succeeded,
    // by construction. pointsCollected == calibrationPointLocalOffsets.Length is guaranteed.
    private void HandleCalibrationComplete()
    {
        // ADDED: compute the final correction ONCE, from all 5 accepted points simultaneously
        // - see AverageQuaternions for why this replaces the old per-point Quaternion.Slerp
        // blend that used to happen inside RecordCurrentPointCorrection.
        CalibrationCorrectionLocal = AverageQuaternions(acceptedPointCorrections);

        // The correction itself is now final - only the QUALITY SCORE still needs measuring,
        // against the held-out validation points next.
        IsCalibrated = true;
        phase = Phase.Validating;
        currentPointIndex = 0;
        calibrationMarker.position = transform.TransformPoint(validationPointLocalOffsets[0]);
    }

    // ADDED: replaces the old per-point Quaternion.Slerp chain. Combines N individual rotation
    // estimates into the single rotation that best represents all of them SIMULTANEOUSLY
    // (Markley et al., "Averaging Quaternions", 2007) - unlike sequential Slerp, this is
    // order-independent and actually minimizes the total angular distance to all N inputs at
    // once, rather than approximating an average via a chain of pairwise blends where later
    // points get progressively less influence purely due to processing order.
    //
    // Method: each quaternion is treated as a 4D unit vector (x,y,z,w). The 4x4 symmetric
    // matrix M = sum(q_i * q_i^T) has the property that its DOMINANT eigenvector is the
    // closest single rotation to all N inputs in a least-squares sense. That eigenvector is
    // found here via power iteration (repeatedly multiply by M and renormalize) rather than a
    // full eigendecomposition/SVD, since only the dominant eigenvector is needed and M is a
    // small (4x4), well-conditioned matrix for this use case (5 similar rotations, not
    // adversarial data) - power iteration converges quickly and reliably for that case.
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

        // q and -q represent the SAME rotation but would partially cancel out in the sum below
        // if left in opposite hemispheres - flip each one to match the first quaternion's sign.
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

        // Power iteration - starts from the reference quaternion (already a reasonable guess,
        // close to the true answer for similar input rotations like these), and converges
        // within a handful of iterations for well-conditioned data.
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

    // ADDED: the quality score shown to the user is now based on validationResidualSum (the  train/test-split number), not biasAngleSum (the optimistic training-set number,
    // still logged below for comparison).

    // MODIFIED: no longer needs an "incomplete" check here either, same reasoning as
    // HandleCalibrationComplete - AdvanceToNextPoint only advances on a successful point, so
    // validationPointsMeasured == validationPointLocalOffsets.Length is guaranteed by the time
    // this method is reached. The ONLY way a full calibration retry (RetryCalibration) can
    // still happen from here now is the quality gate below - a real train/test-split accuracy
    // problem with the fitted correction itself, not any single point being unusable.
    private void HandleValidationComplete()
    {
        phase = Phase.Finished;
        calibrationMarker.gameObject.SetActive(false);

        float averageResidualDegrees = validationResidualSum / validationPointsMeasured;
        float averageTrainingBiasDegrees = biasAngleSum / pointsCollected;
        // ADDED: average precision (sample spread) across all 4 validation points - reported
        // alongside accuracy but NOT part of the pass/fail gate below, which still gates purely
        // on averageResidualDegrees (accuracy). See validationPrecisionSum's field comment for
        // why these stay separate numbers instead of being merged into one score.
        float averagePrecisionDegrees = validationPrecisionSum / validationPointsMeasured;
        float averageUncorrectedResidualDegrees = validationUncorrectedResidualSum / validationPointsMeasured;

        // A fitted correction is only ever an estimate from a short, noisy sample, and can occasionally be worse than doing nothing
        // (see validationUncorrectedResidualSum's field comment). If the corrected residual
        // isn't actually better than raw, uncorrected gaze on these same held-out points, discard
        // the fitted correction and fall back to PICO's raw output instead. Reassigning
        // averageResidualDegrees here means the quality label/percent/gate logic below always
        // reflects whichever result is actually about to ship, not always the fitted one.
        // ADDED: mirrors the averageResidualDegrees reassignment below - the worst-point value
        // used for the gate has to reflect the SAME result (corrected or fallback) that
        // averageResidualDegrees ends up reflecting, not always the corrected one.
        float worstResidualDegrees = validationWorstResidualDegrees;
        int worstResidualPointIndex = validationWorstResidualPointIndex;

        bool correctionHelps = averageResidualDegrees < averageUncorrectedResidualDegrees;
        if (!correctionHelps)
        {
            Debug.Log($"[CalibrationManager] Correction REJECTED - corrected residual ({averageResidualDegrees:F1}°) not better than raw uncorrected gaze ({averageUncorrectedResidualDegrees:F1}°). Falling back to PICO's raw gaze output.");
            CalibrationCorrectionLocal = Quaternion.identity;
            averageResidualDegrees = averageUncorrectedResidualDegrees;
            worstResidualDegrees = validationWorstUncorrectedResidualDegrees;
            worstResidualPointIndex = validationWorstUncorrectedResidualPointIndex;
        }

        // MOVED here from right after averageResidualDegrees was first computed - must run
        // AFTER the fallback block above so these reflect whichever residual actually ships
        // (corrected or the uncorrected fallback), not always the original corrected value.
        string qualityLabel = GetBiasQualityLabel(averageResidualDegrees);
        int qualityPercent = GetBiasQualityPercent(averageResidualDegrees);

        // ADDED: quality gate - Fair/Poor no longer silently proceeds to the main scene. For a
        // precision-sensitive use case, a correction this imprecise defeats the point of
        // calibrating at all, so it's treated the same as a data-validity failure: retry from
        // scratch. Deliberately uncapped (no max attempts) - the goal is an actually-good
        // calibration, not a best-effort one after N tries; if this proves too strict in
        // practice (e.g. stuck retrying), that's a signal to revisit the bands
        // themselves via the validation data now being collected, not to add a silent cap.
        // ADDED: gate on the WORST point too, not just the mean - strict, reuses the same
        // GoodBiasCeilingDegrees (3°) the mean is gated on, so every individual point has to
        // independently qualify as "Good," not just the average of all 4. See
        // validationWorstResidualDegrees' field comment for why the mean alone isn't enough.
        bool worstPointAcceptable = worstResidualDegrees <= GoodBiasCeilingDegrees;
        bool qualityAcceptable = averageResidualDegrees <= GoodBiasCeilingDegrees && worstPointAcceptable;

        if (qualityAcceptable)
        {
            Debug.Log($"[CalibrationManager] Validation complete: {validationPointsMeasured}/{validationPointLocalOffsets.Length} points measured. Residual error={averageResidualDegrees:F1}° ({qualityLabel}, {qualityPercent}%) vs uncorrected {averageUncorrectedResidualDegrees:F1}° - worst point={worstResidualPointIndex} ({worstResidualDegrees:F1}°) - training-set bias was {averageTrainingBiasDegrees:F1}° for comparison. Precision (avg sample spread)={averagePrecisionDegrees:F1}°. CalibrationCorrectionLocal={CalibrationCorrectionLocal.eulerAngles}");
            // MODIFIED: dropped the label+percent from the on-screen message - the percentage
            // was a linear 0deg=100%/5deg=0% mapping unrelated to the actual pass/fail
            // semantics, so a genuine PASS could show a confusingly low number (e.g. exactly
            // 3deg, the pass threshold itself, displayed as 40%). qualityLabel/qualityPercent
            // are still computed and logged above for anyone reading adb logcat - this only
            // changes what the user sees on-screen. Color still reflects pass/fail (green here).
            ShowResult("Tracking ready", GetBiasQualityColor(averageResidualDegrees));
            // Reset the full-sequence fit-guidance counter on an eventual pass.
            consecutiveFullRetries = 0;
            // MODIFIED: delayed via Invoke (was immediate) so the pass message is actually
            // visible before the scene transitions.
            Invoke(nameof(LoadMainScene), resultDisplayDuration);
        }
        else
        {
            // This is the only path that calls RetryCalibration (a full restart) - see HandleValidationComplete's class comment. Tracked here (not a
            // cap) purely to drive fullSequenceFitGuidanceThreshold below.
            consecutiveFullRetries++;
            Debug.Log($"[CalibrationManager] Validation complete but quality below the {GoodBiasCeilingDegrees}° Good threshold - retrying (consecutive full retries={consecutiveFullRetries}). Residual error={averageResidualDegrees:F1}° ({qualityLabel}, {qualityPercent}%) vs uncorrected {averageUncorrectedResidualDegrees:F1}°, worst point={worstResidualPointIndex} ({worstResidualDegrees:F1}°{(worstPointAcceptable ? "" : " - FAILED worst-point gate")}), training-set bias was {averageTrainingBiasDegrees:F1}° for comparison. Precision (avg sample spread)={averagePrecisionDegrees:F1}°.");

            // MODIFIED: same reasoning as the pass message above - dropped the label+percent,
            // same misleading-precision concern applies here too. qualityLabel/qualityPercent
            // stay in the log line above; only the on-screen text changes.
            //
            // ADDED: nudge, not a cap - retrying continues uncapped regardless; this just adds
            // a diagnostic hint once the WHOLE sequence has failed its quality gate repeatedly
            // in a row, since that pattern (not just one bad point) more often points to fit.
            string message = "Calibration Quality Too Low - Retrying...";
            if (consecutiveFullRetries >= fullSequenceFitGuidanceThreshold)
            {
                message += " Try adjusting your headset fit.";
            }
            ShowResult(message, GetBiasQualityColor(averageResidualDegrees));
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

    // MODIFIED: a single glitched point no longer reaches this method at all - it just retries
    // itself in place (see AdvanceToNextPoint). This now only runs for the one remaining full-
    // restart case: HandleValidationComplete's quality gate, where the fitted correction itself
    // (not any one point) measured too imprecise - resets all per-run state and starts the
    // whole 5+4-point sequence over from point 1.
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
