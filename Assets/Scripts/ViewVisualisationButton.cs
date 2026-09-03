using UnityEngine;
using UnityEngine.SceneManagement;

// "View Visualization" - loads Visualisation.unity as a genuinely standalone scene (Single mode,
// replacing EyeTrackingDemo entirely - not tied to it in any way). Visualisation.unity has its own
// XR rig and reads the specialist/trainee recordings fresh from disk (see
// DivergenceReplayScreenOverlay.cs), so nothing here needs to reach back into EyeTrackingDemo at
// all - a deliberate simplification after the earlier additive-loading approach (keeping
// EyeTrackingDemo alive in the background) turned out to cause more problems than it solved: a
// double-tap could stack two loaded copies of Visualisation on top of each other, and the two
// scenes' XR rigs/cameras risked conflicting while both stayed active.
public class ViewVisualisationButton : MonoBehaviour
{
    [SerializeField] private string visualizationSceneName = "Visualisation";

    // Waited on before revealing this button - see ComparisonLoader.LoadCompleted. Without this,
    // the button showed immediately at scene start, before the heatmap process was even done or
    // ReportScreen had anything real to show - a trainee could click through to a visualization
    // screen with nothing to visualize yet.

    [SerializeField] private ComparisonLoader comparisonLoader;

    private void Start()
    {
        // Specialist has nothing to visualize, ever - hidden permanently, same reasoning as
        // ComparisonLoader/GazeReviewLoader.
        if (SessionRoleManager.IsSpecialist)
        {
            gameObject.SetActive(false);
            return;
        }

        // Hidden until ComparisonLoader confirms there's actually data on ReportScreen - see
        // ComparisonLoader.SubscribeOrFireImmediately for why this is safe regardless of which
        // script's Start() happens to run first.
        gameObject.SetActive(false);
        if (comparisonLoader != null)
        {
            comparisonLoader.SubscribeOrFireImmediately(OnComparisonDataReady);
        }
        else
        {
            // No reference assigned - fall back to always-visible rather than a button that can
            // never appear.
            gameObject.SetActive(true);
        }
    }

    private void OnComparisonDataReady()
    {
        gameObject.SetActive(true);
    }

    public void GoToVisualization()
    {
        SceneManager.LoadScene(visualizationSceneName);
    }
}
