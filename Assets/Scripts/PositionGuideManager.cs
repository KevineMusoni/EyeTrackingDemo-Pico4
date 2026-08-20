using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;
using Unity.XR.PXR;

// Live headset-fit guide, shown before Calibration.unity loads. Reads Pico's per-eye position-
// guide signal (normalized 0-1, ideally centered at 0.5/0.5) and moves UI indicators to match,
// helping the user to visually adjust fit before calibration starts.
//
// UNVERIFIED HARDWARE SUPPORT: PXR_EyeTracking.GetLeftEyePositionGuide/GetRightEyePositionGuide
// are documented "Available for Neo3 Pro Eye only" - this headset (Pico 4 Enterprise) isn't in - tested on pico 4 enterprise, valid: true
// that list. The per-frame log line below is the actual evidence for whether it works here:
// consistently false, or true with static/nonsense values, means this isn't usable on this
// hardware and this scene should be skipped/removed rather than shipped half-working.

public class PositionGuideManager : MonoBehaviour
{
    [Header("Scene Flow")]
    [SerializeField] private string calibrationSceneName = "Calibration";

    [Header("UI")]
    // Both eyes' positions are averaged into ONE indicator, rather than shown as two separate
    // dots - simpler to read at a glance ("is the single dot centered?"), at the cost of not
    // being able to tell which specific eye is off if only one is (averaging can partially
    // cancel out a real asymmetric fit issue).
    [SerializeField] private RectTransform combinedIndicator;
    [SerializeField] private Button continueButton;

    // Maps the 0-1 normalized position's offset from center into the frame graphic's actual
    // pixel size - tune to match whatever frame texture ends up used.
    [SerializeField] private float movementMultiplier = 200f;

    // Pico's documented axes ((0,0) = upper-right, (1,1) = lower-left) don't necessarily match
    // Unity UI's on-screen axes (+X right, +Y up) - flip whichever axis moves the wrong way in
    // practice (deliberately tilt/shift the headset one known direction and check the dot moves
    // the way you'd expect; if not, flip the matching toggle here).
    [SerializeField] private bool invertX;
    [SerializeField] private bool invertY;

    [Header("Result Feedback")]
    [SerializeField] private TMP_Text resultText;
    // How close to the documented center (0.5, 0.5) counts as "positioned correctly," in the
    // same normalized 0-1 units the API returns - both eyes converging near (0,0) offset is
    // what a correctly-worn headset looks like, since each eye is independently centered in
    // its own sensor at that point. Untuned starting guess.
    [SerializeField] private float centeredThreshold = 0.05f;

    private void Start()
    {
        if (continueButton != null)
        {
            continueButton.onClick.AddListener(LoadCalibrationScene);
        }
    }

    private void Update()
    {
        bool leftValid = PXR_EyeTracking.GetLeftEyePositionGuide(out Vector3 leftPosition);
        bool rightValid = PXR_EyeTracking.GetRightEyePositionGuide(out Vector3 rightPosition);

        Debug.Log($"[PositionGuideManager] left valid={leftValid} pos={leftPosition} | right valid={rightValid} pos={rightPosition}");

        bool bothValid = leftValid && rightValid;
        // Simple midpoint of the two raw positions - only meaningful once both eyes have a
        // valid reading this frame, same as GazeReading's "all-or-nothing" validity pattern
        // elsewhere in this project.
        Vector3 combinedPosition = (leftPosition + rightPosition) / 2f;

        // for debugging, in log. Knowing the positions of the left and right eye
        if (bothValid)
        {
            float leftDistance = DistanceFromCenter(leftPosition);
            float rightDistance = DistanceFromCenter(rightPosition);
            Debug.Log($"[PositionGuideManager] per-eye distance from center: left={leftDistance:F3} right={rightDistance:F3} asymmetry={Mathf.Abs(leftDistance - rightDistance):F3}");
        }

        float distanceFromCenter = UpdateIndicator(combinedIndicator, bothValid, combinedPosition);

        UpdateResultText(bothValid, distanceFromCenter);
    }

    // Returns how far this eye's raw position is from the documented center (0.5, 0.5), in the
    // same normalized 0-1 units the API returns - independent of invertX/invertY, which only
    // flip on-screen DIRECTION for display, not the actual distance.
    private float UpdateIndicator(RectTransform indicator, bool valid, Vector3 position)
    {
        if (indicator != null)
        {
            indicator.gameObject.SetActive(valid);
        }
        if (!valid)
        {
            return float.PositiveInfinity;
        }

        Vector2 offsetFromCenter = new Vector2(position.x - 0.5f, position.y - 0.5f);
        if (indicator != null)
        {
            Vector2 displayOffset = offsetFromCenter;
            if (invertX)
            {
                displayOffset.x *= -1f;
            }
            if (invertY)
            {
                displayOffset.y *= -1f;
            }
            indicator.anchoredPosition = displayOffset * movementMultiplier;
        }

        return offsetFromCenter.magnitude;
    }

    private void UpdateResultText(bool valid, float distanceFromCenter)
    {
        if (resultText == null)
        {
            return;
        }

        if (!valid)
        {
            resultText.text = "Waiting for eye tracking...";
            resultText.color = Color.white;
            return;
        }

        bool centered = distanceFromCenter <= centeredThreshold;
        resultText.text = centered ? "Headset positioned correctly" : "Adjust your headset until the dot is centered";
        resultText.color = centered ? Color.green : Color.yellow;
    }

    // helper

    private static float DistanceFromCenter(Vector3 position)
    {
        return new Vector2(position.x - 0.5f, position.y - 0.5f).magnitude;
    }

    private void LoadCalibrationScene()
    {
        SceneManager.LoadScene(calibrationSceneName);
    }
}
