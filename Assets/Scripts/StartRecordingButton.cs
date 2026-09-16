// using UnityEngine;
// using UnityEngine.UI;

// The video is already loaded and visible (just paused - see SurgeryVideoOverlayPlayer's
// SetPlayWhenReady(false) right after it loads). This button's only job is to resume it -
// RequestStart() is what actually fires PlaybackStarted, which MeshGazeHeatmap already waits on
// before it starts recording, so gating this one button gates both.
// public class StartRecordingButton : MonoBehaviour
// {
//     [SerializeField] private SurgeryVideoOverlayPlayer videoPlayer;
//     [SerializeField] private GameObject overlayRoot; // this button's canvas, hidden once clicked

//     private void Start()
//     {
//         GetComponent<Button>().onClick.AddListener(OnStartClicked);
//     }

//     private void OnStartClicked()
//     {
//         if (videoPlayer != null) videoPlayer.RequestStart();
//         if (overlayRoot != null) overlayRoot.SetActive(false);
//     }
// }
