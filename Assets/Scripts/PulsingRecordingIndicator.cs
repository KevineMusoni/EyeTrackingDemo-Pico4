using UnityEngine;
using UnityEngine.UI;

// Fades a recording dot in and out to show capture is ongoing - same idle-pulse shape
// CalibrationManager.UpdateMarkerIdlePulse already uses for the calibration marker
// (Mathf.Sin(Time.time * speed)), applied to alpha instead of scale since a recording
// indicator conventionally fades rather than grows/shrinks.
public class PulsingRecordingIndicator : MonoBehaviour
{
    [SerializeField] private Image dotImage;
    [SerializeField] private float pulseSpeed = 3f;

    private void Update()
    {
        if (dotImage == null) return;

        Color c = dotImage.color;
        c.a = 0.5f + 0.5f * Mathf.Sin(Time.time * pulseSpeed);
        dotImage.color = c;
    }
}
