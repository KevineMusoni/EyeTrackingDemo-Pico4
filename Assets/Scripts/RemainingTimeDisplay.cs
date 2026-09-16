using TMPro;
using UnityEngine;

// Shows how much of the live surgery video is left, reading MeshGazeHeatmap's own
// startTime/autoStopAfterSeconds (via GetRemainingSeconds()) - the same values that already
// gate StampAt()'s auto-stop - rather than tracking a second, separate timer.
public class RemainingTimeDisplay : MonoBehaviour
{
    [SerializeField] private MeshGazeHeatmap heatmap;
    [SerializeField] private TMP_Text remainingText;

    private void Update()
    {
        if (heatmap == null || remainingText == null) return;
        remainingText.text = $"{Mathf.CeilToInt(heatmap.GetRemainingSeconds())}s remaining";
    }
}
