using UnityEngine;
using UnityEngine.SceneManagement;

// View Visualisation loads Visualisation.unity ADDITIVELY (EyetrackingDemo stays loaded underneath, untouched) rather than replacing the current scene. This means going "back" later never has to reconstruct amything from scratch - see ReturnFromVisualisation() below, called by a script living in Visualisation.unity itself.

public class ViewVisualisationButton : MonoBehaviour
{
    [SerializeField] private string visualizationSceneName = "Visualisation";

    // SurgeryVideoScreen, its heat overlay, GazeReviewScreen, ReportScreen, etc. - hidden while
    // Visualisation is loaded on top (both scenes' objects otherwise coexist in the same 3D
    // space simultaneously), shown again once back. Same pattern as
    // ComparisonLoader.legendObjectsToHideForSpecialist.
    [SerializeField] private GameObject[] objectsToHideWhileVisualizing;

    // Sett once in Start() and never cleared - this object is never destroyed under the additive
    // approach, so the static reference stays valid for the whole app session, letting
    // ReturnFromVisualization() below reach back into this scene from Visualisation.unity, which
    // can't hold a direct Inspector reference to an object in a different scene.
    private static ViewVisualisationButton activeInstance;

    // Guards against loading Visualisation.unity more than once - on-device logging caught a
    // double-tap on this button stacking two full additive copies of the scene simultaneously
    // (two independent DivergenceReplayScreen video layers at the exact same position), which
    // showed up as flickering - the compositor has no defined way to resolve two overlays fighting
    // for the same spot. Reset back to false in ReturnFromVisualization() once actually unloaded,
    // so a later visit can load it again normally.
    private bool isVisualizationLoaded;

    private void Start()
    {
        activeInstance = this;
        // Only meaningful for a trainee who's finished their session - specialist has nothing to visualise, and there's nothing to show before the video's actually done.
        gameObject.SetActive(!SessionRoleManager.IsSpecialist);
    }

    public void GoToVisualization()
    {
        if (isVisualizationLoaded)
        {
            return;
        }
        isVisualizationLoaded = true;

        SetHiddenObjectsActive(false);
        SceneManager.LoadScene(visualizationSceneName, LoadSceneMode.Additive);
    }

    // Called from Visualisation.unity's back button - see VisualisationBackBtn.cs.
    public static void ReturnFromVisualization()
    {
        if (activeInstance != null)
        {
            activeInstance.SetHiddenObjectsActive(true);
            activeInstance.isVisualizationLoaded = false;
        }
        SceneManager.UnloadSceneAsync("Visualisation");
    }

    private void SetHiddenObjectsActive(bool active)
    {
        foreach (GameObject obj in objectsToHideWhileVisualizing)
        {
            if (obj != null)
            {
                obj.SetActive(active);
            }
        }
    }
}
