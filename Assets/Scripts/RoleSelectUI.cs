using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Two buttons - Specialist / Trainee - wired to their OnClick() events in the Inspector, calling
// SelectSpecialist()/SelectTrainee() below. Sets SessionRoleManager's static role then loads the main demo scene, same handoff pattern CalibrationManager uses (SceneManager.LoadScene after
// setting a static field the next scene reads).
public class RoleSelectUI : MonoBehaviour
{
    [SerializeField] private string mainSceneName = "EyeTrackingDemo";

    // The coloured frame behind each button (RoleSelect.unity - SpecialistBorder / TraineeBorder).
    // On click the picked one is popped brighter and held for selectionFlashSeconds so the choice
    // registers visually before the scene swaps out. Optional - leave unwired (or set the time to
    // 0) and the load just happens immediately, as it did before.
    [SerializeField] private Image specialistBorder;
    [SerializeField] private Image traineeBorder;
    [SerializeField] private float selectionFlashSeconds = 0.18f;

    // Both CustomButtonPressHandler and a stray real Button.onClick can fire for one press through
    // this rig - without this, a double-fire would start two loads / two coroutines.
    private bool choosing;

    public void SelectSpecialist()
    {
        SessionRoleManager.SetRole(true);
        BeginLoad(specialistBorder);
    }

    public void SelectTrainee()
    {
        SessionRoleManager.SetRole(false);
        BeginLoad(traineeBorder);
    }

    private void BeginLoad(Image pickedBorder)
    {
        if (choosing) return;
        choosing = true;

        if (pickedBorder != null && selectionFlashSeconds > 0f)
        {
            StartCoroutine(FlashThenLoad(pickedBorder));
        }
        else
        {
            SceneManager.LoadScene(mainSceneName);
        }
    }

    private IEnumerator FlashThenLoad(Image pickedBorder)
    {
        // Push the frame toward white so it's clearly brighter than either its resting tint or the
        // other (untouched) border. The Button's own Selected transition starts dimming it after
        // ~0.1s, but the scene load lands before that fade completes.
        pickedBorder.color = Color.Lerp(pickedBorder.color, Color.white, 0.4f);
        yield return new WaitForSeconds(selectionFlashSeconds);
        SceneManager.LoadScene(mainSceneName);
    }
}
