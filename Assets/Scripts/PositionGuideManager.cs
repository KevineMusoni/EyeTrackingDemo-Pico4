using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.XR.PXR;

// Live headset-fit guide, shown before Calibration.unity loads. Reads Pico's per-eye position-
// guide signal (normalized 0-1, ideally centered at 0.5/0.5) and moves UI indicators to match,
// helping the user to visually adjust fit before calibration starts.
//
// PXR_EyeTracking.GetLeftEyePositionGuide/GetRightEyePositionGuide is officially supported on
// this hardware - this project's bundled SDK package (2.1.4, March 2023) has a stale doc
// comment saying "Neo3 Pro Eye only," but a newer SDK build's doc comment confirms PICO 4 Pro
// and PICO 4 Enterprise are supported too. Verified working once on-device (real, distinct,
// stable per-eye values); since then, consistently stuck at (0,0,0) despite valid=true - traced
// to gd32ipdservice (the native eye/IPD driver) failing UART communication with the physical
// sensor, confirmed via adb logcat outside this app entirely. That's a hardware/driver fault,
// not a bug in this script or an unsupported-device situation - see README for the full
// diagnosis and troubleshooting steps tried.
//
// Pass/fail is judged by the backend, not the user: each eye's live indicator is compared against
// its own fixed checker marker, in on-screen pixels, and the scene auto-advances once both eyes
// stay close enough for a moment.
public class PositionGuideManager : MonoBehaviour
{
    [Header("Scene Flow")]
    [SerializeField] private string calibrationSceneName = "Calibration";

    [Header("UI")]
    [SerializeField] private RectTransform leftEyeIndicator;
    [SerializeField] private RectTransform rightEyeIndicator;
    [SerializeField] private RectTransform leftCenterTarget;
    [SerializeField] private RectTransform rightCenterTarget;
    [SerializeField] private RectTransform leftChecker;
    [SerializeField] private RectTransform rightChecker;

    // Maps the 0-1 normalized position's offset from center into the frame graphic's actual
    // pixel size - tune to match whatever frame texture ends up used.
    [SerializeField] private float movementMultiplier = 200f;

    [Header("Pass Condition")]
    // How close, in on-screen pixels, the indicator must be to its checker to count as "centered."
    [SerializeField] private float pixelTolerance = 30f;

    // Both eyes must stay within tolerance for this many consecutive frames before passing - a
    // tracking dropout (e.g. a blink) holds this steady instead of resetting it, but time spent
    // visibly off-target decays it, so the count reflects sustained fit rather than a lucky frame.
    [SerializeField] private int requiredStableFrames = 60;

    [Header("Pass Animation")]
    // How long the dots take to visibly glide onto their checkers once a pass is detected.
    [SerializeField] private float snapDuration = 0.4f;

    // Reused for both the guide instructions and the pass message - swapped to passMessage once
    // the dots lock onto their checkers.
    [SerializeField] private TMP_Text resultText;
    [SerializeField] private string passMessage = "Headset positioned correctly";

    // How long the pass message stays on screen before the scene actually loads.
    [SerializeField] private float messageDuration = 4f;

    private int stableFrameCount;
    private bool hasPassed;

    private void Start()
    {
        // SDK API that recenters tracking (position + rotation) right as this guide begins 
        // current position/orientation the user has just settled into becomes the new zero reference
        // before calibration starts, instead of carrying over drift from earlier in the session.
        // callable recenter (Packages/PICO Unity IntegrationSDK-214-20230302/Runtime/
        // Scripts/PXR_Plugin.cs), independent of the system-level Home-button gesture.
        PXR_Plugin.Sensor.UPxr_ResetSensor(ResetSensorOption.ResetAll);
    }

    private void Update()
    {
        if (hasPassed)
        {
            return;
        }

        bool leftValid = PXR_EyeTracking.GetLeftEyePositionGuide(out Vector3 leftPosition);
        bool rightValid = PXR_EyeTracking.GetRightEyePositionGuide(out Vector3 rightPosition);

        Debug.Log($"[PositionGuideManager] left valid={leftValid} pos={leftPosition} | right valid={rightValid} pos={rightPosition}");

        Vector2 leftBase = leftCenterTarget != null ? leftCenterTarget.anchoredPosition : Vector2.zero;
        Vector2 rightBase = rightCenterTarget != null ? rightCenterTarget.anchoredPosition : Vector2.zero;

        Vector2 leftDotPos = UpdateIndicator(leftEyeIndicator, leftValid, leftPosition, leftBase);
        Vector2 rightDotPos = UpdateIndicator(rightEyeIndicator, rightValid, rightPosition, rightBase);

        bool leftCentered = leftValid && IsOnChecker(leftDotPos, leftBase, leftChecker);
        bool rightCentered = rightValid && IsOnChecker(rightDotPos, rightBase, rightChecker);

        if (leftCentered && rightCentered)
        {
            stableFrameCount++;
            if (stableFrameCount >= requiredStableFrames)
            {
                hasPassed = true;
                Vector2 leftCheckerPos = leftBase + (leftChecker != null ? leftChecker.anchoredPosition : Vector2.zero);
                Vector2 rightCheckerPos = rightBase + (rightChecker != null ? rightChecker.anchoredPosition : Vector2.zero);
                StartCoroutine(PlayPassAnimationThenLoad(leftCheckerPos, rightCheckerPos));
            }
        }
        else if (!leftValid || !rightValid)
        {
            // Tracking dropout (e.g. a blink) - hold progress
        }
        else
        {
            // Both eyes tracked but off-target
            stableFrameCount = Mathf.Max(0, stableFrameCount - 1);
        }
    }

    private bool IsOnChecker(Vector2 dotPosition, Vector2 basePosition, RectTransform checker)
    {
        if (checker == null)
        {
            return false;
        }

        Vector2 checkerPosition = basePosition + checker.anchoredPosition;
        return Vector2.Distance(dotPosition, checkerPosition) <= pixelTolerance;
    }

    private Vector2 UpdateIndicator(RectTransform indicator, bool valid, Vector3 position, Vector2 basePosition)
    {
        if (indicator == null)
        {
            return basePosition;
        }

        indicator.gameObject.SetActive(valid);
        if (!valid)
        {
            return indicator.anchoredPosition;
        }

        // basePosition is that eye's own target's position, so a centered reading (0.5,
        // 0.5) renders the dot exactly on its target - not always at canvas center regardless of where the target actually sits.

        Vector2 offsetFromCenter = new Vector2(position.x - 0.5f, position.y - 0.5f);
        indicator.anchoredPosition = basePosition + offsetFromCenter * movementMultiplier;
        return indicator.anchoredPosition;
    }


    private IEnumerator PlayPassAnimationThenLoad(Vector2 leftTarget, Vector2 rightTarget)
    {
        Vector2 leftStart = leftEyeIndicator != null ? leftEyeIndicator.anchoredPosition : leftTarget;
        Vector2 rightStart = rightEyeIndicator != null ? rightEyeIndicator.anchoredPosition : rightTarget;

        float elapsed = 0f;
        while (elapsed < snapDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / snapDuration);

            if (leftEyeIndicator != null)
            {
                leftEyeIndicator.anchoredPosition = Vector2.Lerp(leftStart, leftTarget, t);
            }

            if (rightEyeIndicator != null)
            {
                rightEyeIndicator.anchoredPosition = Vector2.Lerp(rightStart, rightTarget, t);
            }

            yield return null;
        }

        if (leftEyeIndicator != null)
        {
            leftEyeIndicator.anchoredPosition = leftTarget;
        }

        if (rightEyeIndicator != null)
        {
            rightEyeIndicator.anchoredPosition = rightTarget;
        }

        if (resultText != null)
        {
            resultText.text = passMessage;
        }

        yield return new WaitForSeconds(messageDuration);

        LoadCalibrationScene();
    }

    private void LoadCalibrationScene()
    {
        SceneManager.LoadScene(calibrationSceneName);
    }
}
