using UnityEngine;
using UnityEngine.SceneManagement;

// Wired to a UI Button's OnClick() in EyeTrackingDemo.unity - lets a specialist return to the role-select screen without needing a full app relaunch. Only makes sense for a
// specialist session (a trainee's flow already ends at GazeReviewScreen/ReportScreen, so this hides itself entirely for trainees the same way GazeReviewLoader/ComparisonLoader hide their screens for specialists 
// same SetActive(false)-in-Start() pattern, just the opposite role. 

public class ReturnToRoleSelectButton : MonoBehaviour
{
    [SerializeField] private string roleSelectSceneName = "RoleSelect";

    private void Start()
    {
        gameObject.SetActive(SessionRoleManager.IsSpecialist);
    }

    // Wired to the Button's OnClick() in the Inspector.
    public void ReturnToRoleSelect()
    {
        SceneManager.LoadScene(roleSelectSceneName);
    }
}
