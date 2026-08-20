using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Unity.XR.PXR;

// Live headset-fit guide, shown before Calibration.unity loads. Reads Pico's per-eye position-
// guide signal (normalized 0-1, ideally centered at 0.5/0.5) and moves UI indicators to match,
// helping the user to visually adjust fit before calibration starts.
//
// UNVERIFIED HARDWARE SUPPORT: PXR_EyeTracking.GetLeftEyePositionGuide/GetRightEyePositionGuide
// are documented "Available for Neo3 Pro Eye only" - this headset (Pico 4 Enterprise) isn't in
// that list. The per-frame log line below is the actual evidence for whether it works here:
// consistently false, or true with static/nonsense values, means this isn't usable on this
// hardware and this scene should be skipped/removed rather than shipped half-working.

public class PositionGuideManager : MonoBehaviour
{
    [Header("Scene Flow")]
    [SerializeField] private string calibrationSceneName = "Calibration";

    [Header("UI")]
    [SerializeField] private RectTransform leftEyeIndicator;
    [SerializeField] private RectTransform rightEyeIndicator;
    [SerializeField] private Button continueButton;

    // Maps the 0-1 normalized position's offset from center into the frame graphic's actual
    // pixel size - tune to match whatever frame texture ends up used.
    [SerializeField] private float movementMultiplier = 200f;

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

        UpdateIndicator(leftEyeIndicator, leftValid, leftPosition);
        UpdateIndicator(rightEyeIndicator, rightValid, rightPosition);
    }

    private void UpdateIndicator(RectTransform indicator, bool valid, Vector3 position)
    {
        if (indicator == null)
        {
            return;
        }

        indicator.gameObject.SetActive(valid);
        if (!valid)
        {
            return;
        }

        Vector2 offsetFromCenter = new Vector2(position.x - 0.5f, position.y - 0.5f);
        indicator.anchoredPosition = offsetFromCenter * movementMultiplier;
    }

    private void LoadCalibrationScene()
    {
        SceneManager.LoadScene(calibrationSceneName);
    }
}
