using UnityEngine;

// Same gating as ViewVisualisationButton.cs - hidden entirely for a specialist (nothing to
// report on their own single session), and hidden for a trainee until ComparisonLoader confirms
// the comparison data is actually ready, rather than showing a button with nothing behind it yet.
public class ViewReportButton : MonoBehaviour
{
    [SerializeField] private ComparisonLoader comparisonLoader;

    private void Start()
    {
        if (SessionRoleManager.IsSpecialist)
        {
            gameObject.SetActive(false);
            return;
        }

        gameObject.SetActive(false);
        if (comparisonLoader != null)
        {
            comparisonLoader.SubscribeOrFireImmediately(OnComparisonDataReady);
        }
        else
        {
            gameObject.SetActive(true);
        }
    }

    private void OnComparisonDataReady()
    {
        gameObject.SetActive(true);
    }
}
