using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.XR.PXR;
using UnityEngine.UI;

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


    [Header("Progress Rings")]
    // The inner circle Image on each checker (LeftCheckerInner / RightCheckerInner), switched to
    // Image Type = Filled in the scene (Part B). Colour and fillAmount are driven every frame below.
    [SerializeField] private Image leftProgressRing;
    [SerializeField] private Image rightProgressRing;
    [SerializeField] private Color adjustingColor = new Color(0.94f, 0.62f, 0.15f); //amber
    [SerializeField] private Color centeredColor = new Color(0f, 1f, 0.56078434f); // theme green #00FF8F

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

    // The subtitle line beneath resultText - cleared (not swapped to a message) once passed, so
    // the "move your head" instruction doesn't linger under the pass message.
    [SerializeField] private TMP_Text subtitleText;

    [Header("Loading")]
    // Bottom-anchored row of three dots, hidden until the pass moment. Each one's brightness
    // follows a sine wave, phase-shifted from its neighbors, so the bright point appears to
    // travel left-to-right in a continuous pulse - the classic "..." loading indicator, not a
    // spinner and not animated text.
    [SerializeField] private Image[] loadingDots;
    [SerializeField] private Color dotDimColor = new Color(0.35f, 0.35f, 0.35f);
    [SerializeField] private Color dotBrightColor = Color.white;
    [SerializeField] private float dotPulseSpeed = 1f;   // cycles per second
    [SerializeField] private float dotPhaseOffset = 0.3f; // fraction of a cycle between dots

    // Static caption shown alongside the dots (not animated itself) - the dots alone read as
    // "something's happening", the label says what.
    [SerializeField] private TMP_Text loadingLabel;
    [SerializeField] private string loadingLabelText = "Loading calibration";
    [SerializeField] private float loadingDuration = 4f;

    private int stableFrameCount;
    private bool hasPassed;

    // Cached once in Start() rather than a separate [SerializeField] per eye - both indicators
    // already have leftEyeIndicator/rightEyeIndicator RectTransform references wired, and their
    // Image lives on that same GameObject, so this avoids a redundant Inspector slot.
    private Image leftIndicatorImage;
    private Image rightIndicatorImage;

    private void Start()
    {
        // SDK API that recenters tracking (position + rotation) right as this guide begins
        // current position/orientation the user has just settled into becomes the new zero reference
        // before calibration starts, instead of carrying over drift from earlier in the session.
        // callable recenter (Packages/PICO Unity IntegrationSDK-214-20230302/Runtime/
        // Scripts/PXR_Plugin.cs), independent of the system-level Home-button gesture.
        PXR_Plugin.Sensor.UPxr_ResetSensor(ResetSensorOption.ResetAll);

        leftIndicatorImage = leftEyeIndicator != null ? leftEyeIndicator.GetComponent<Image>() : null;
        rightIndicatorImage = rightEyeIndicator != null ? rightEyeIndicator.GetComponent<Image>() : null;
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

        float leftDistance = GetDistanceToChecker(leftDotPos, leftBase, leftChecker);
        float rightDistance = GetDistanceToChecker(rightDotPos, rightBase, rightChecker);

        bool leftCentered = leftValid && leftDistance <= pixelTolerance;
        bool rightCentered = rightValid && rightDistance <= pixelTolerance;

        UpdateProgressRing(leftProgressRing, leftIndicatorImage, leftValid, leftCentered, leftDistance);
        UpdateProgressRing(rightProgressRing, rightIndicatorImage, rightValid, rightCentered, rightDistance);

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

    private float GetDistanceToChecker(Vector2 dotPosition, Vector2 basePosition, RectTransform checker)
    {
        if (checker == null)
        {
            return float.MaxValue;
        }

        Vector2 checkerPosition = basePosition + checker.anchoredPosition;
        return Vector2.Distance(dotPosition, checkerPosition);
    }

    // Two phases: while off-target, the ring fills as a continuous "getting warmer" gauge instead
    // of staying empty until the exact instant of a pass. Once on-target, it switches to showing
    // the shared hold countdown (both eyes must stay centered together for requiredStableFrames),
    // so a viewer can see "you're doing it, just hold" rather than nothing happening for ~0.8s.
    // The indicator dot is recolored to the same state color as its ring (rather than a fixed
    // color) so the dot and the ring always read as one signal, not two.
    private void UpdateProgressRing(Image ring, Image indicatorImage, bool valid, bool centered, float distance)
    {
        if (ring == null) return;

        if (!valid)
        {
            ring.fillAmount = 0f;
            return;
        }

        Color color;
        float fillAmount;

        if (centered)
        {
            float holdProgress = Mathf.Clamp01((float)stableFrameCount / requiredStableFrames);
            fillAmount = holdProgress;
            color = Color.Lerp(adjustingColor, centeredColor, holdProgress);
        }
        else
        {
            float closeness = 1f - Mathf.Clamp01(distance / (pixelTolerance * 2f));
            fillAmount = closeness;
            color = adjustingColor;
        }

        ring.fillAmount = fillAmount;
        ring.color = color;

        if (indicatorImage != null)
        {
            indicatorImage.color = color;
        }
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

        if (subtitleText != null)
        {
            subtitleText.text = "";
        }

        yield return StartCoroutine(AnimateLoadingDots());

        LoadCalibrationScene();
    }

    // Cycles each dot's colour along a sine wave for loadingDuration seconds, phase-shifted per
    // dot so the bright point sweeps across the row, then hides them again. If unwired, just
    // waits the same duration so the pass timing is unaffected.
    private IEnumerator AnimateLoadingDots()
    {
        if (loadingDots == null || loadingDots.Length == 0)
        {
            yield return new WaitForSeconds(loadingDuration);
            yield break;
        }

        foreach (Image dot in loadingDots)
        {
            if (dot != null) dot.gameObject.SetActive(true);
        }

        if (loadingLabel != null)
        {
            loadingLabel.text = loadingLabelText;
            loadingLabel.gameObject.SetActive(true);
        }

        float elapsed = 0f;
        while (elapsed < loadingDuration)
        {
            for (int i = 0; i < loadingDots.Length; i++)
            {
                if (loadingDots[i] == null) continue;

                float phase = elapsed * dotPulseSpeed - i * dotPhaseOffset;
                float brightness = 0.5f + 0.5f * Mathf.Sin(phase * Mathf.PI * 2f);
                loadingDots[i].color = Color.Lerp(dotDimColor, dotBrightColor, brightness);
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        foreach (Image dot in loadingDots)
        {
            if (dot != null) dot.gameObject.SetActive(false);
        }

        if (loadingLabel != null)
        {
            loadingLabel.gameObject.SetActive(false);
        }
    }

    private void LoadCalibrationScene()
    {
        SceneManager.LoadScene(calibrationSceneName);
    }
}
