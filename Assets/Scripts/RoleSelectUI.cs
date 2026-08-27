using UnityEngine;
using UnityEngine.SceneManagement;

// Two buttons - Specialist / Trainee - wired to their OnClick() events in the Inspector, calling
// SelectSpecialist()/SelectTrainee() below. Sets SessionRoleManager's static role then loads the main demo scene, same handoff pattern CalibrationManager uses (SceneManager.LoadScene after
// setting a static field the next scene reads).
public class RoleSelectUI : MonoBehaviour
{
    [SerializeField] private string mainSceneName = "EyeTrackingDemo";

    public void SelectSpecialist()
    {
        SessionRoleManager.SetRole(true);
        SceneManager.LoadScene(mainSceneName);
    }

    public void SelectTrainee()
    {
        SessionRoleManager.SetRole(false); 
        SceneManager.LoadScene(mainSceneName);
    }
}
