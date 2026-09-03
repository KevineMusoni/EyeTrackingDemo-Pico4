using UnityEngine;
using UnityEngine.SceneManagement;

// Visualisation.unity is now a standalone scene, not paused-and-returned-to like the earlier
// additive design - going "back" from here means ending this trainee's session and returning to
// role selection for the next one, same as ReturnToRoleSelectButton does from EyeTrackingDemo.
public class VisualisationBackBtn : MonoBehaviour
{
    [SerializeField] private string roleSelectSceneName = "EyeTrackingDemo";

    public void GoBack()
    {
        Debug.Log($"[VisualisationBackBtn] GoBack() called - loading '{roleSelectSceneName}'.");
        SceneManager.LoadScene(roleSelectSceneName);
    }
}
