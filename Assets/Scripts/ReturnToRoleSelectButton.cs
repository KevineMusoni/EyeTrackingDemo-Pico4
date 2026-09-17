using UnityEngine;
using UnityEngine.SceneManagement;

// Wired to a UI Button's OnClick() in EyeTrackingDemo.unity - lets either role return to the
// role-select screen without needing a full app relaunch. Shown for both specialist and trainee
// sessions - previously specialist-only, but a trainee mid-session needs the same way back out.

public class ReturnToRoleSelectButton : MonoBehaviour
{
    [SerializeField] private string roleSelectSceneName = "RoleSelect";

    // Wired to the Button's OnClick() in the Inspector.
    public void ReturnToRoleSelect()
    {
        SceneManager.LoadScene(roleSelectSceneName);
    }
}
