using System.Collections;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Role picker. Each button's OnClick (wired in the inspector) calls SelectSpecialist() or SelectTrainee(): set the static role, flash the chosen frame, load the main scene.

// Recording state guard (Awake): the Trainee flow needs specialist_reference.json to compare against. It's missing, the trainee button is disabled and statusText explains why. If it exists, specialistDescription is reworded so the user knows recording again overwrites it.


public class RoleSelectUI : MonoBehaviour
{
    [SerializeField] private string mainSceneName = "EyeTrackingDemo";

    [Header ("Selection flash")]
    // The coloured frame behind each button (RoleSelect.unity - SpecialistBorder / TraineeBorder).
    // On click the picked one is popped brighter and held for selectionFlashSeconds so the choice
    // registers visually before the scene swaps out. Optional - leave unwired (or set the time to
    // 0) and the load just happens immediately, as it did before.
    [SerializeField] private Image specialistBorder;
    [SerializeField] private Image traineeBorder;
    [SerializeField] private float selectionFlashSeconds = 0.18f;

    [Header ("Recordig-state guard")]
    [SerializeField] private Button traineeButton;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text specialistDescription;
    [SerializeField] private CanvasGroup traineeGroup;
    [SerializeField] private float disabledTraineeAlpha = 0.4f;

    [TextArea] [SerializeField] private string noReferenceMessage = "No reference recorded yet - a specialist must record one first.";
    [TextArea] [SerializeField] private string specialistReRecordMessage = "Re-record the reference - this replaces the current benchmark";

    private bool referenceExists;
    // Both CustomButtonPressHandler and a stray real Button.onClick can fire for one press through
    // this rig - without this, a double-fire would start two loads / two coroutines.
    private bool choosing;

    private void Awake(){
        string path = Path.Combine(Application.persistentDataPath, "GazeRecordings", "specialist_reference.json");
        referenceExists = File.Exists(path);

        if (traineeButton != null) traineeButton.interactable = referenceExists;
        if (traineeGroup != null) traineeGroup.alpha = referenceExists ? 1f : disabledTraineeAlpha;
        if (statusText != null) statusText.text = referenceExists ? "" : noReferenceMessage;
        if (specialistDescription != null && referenceExists)
            specialistDescription.text = specialistReRecordMessage;
    }

    public void SelectSpecialist()
    {
        SessionRoleManager.SetRole(true);
        BeginLoad(specialistBorder);
    }

    // enabled only when specalist json file is available

    public void SelectTrainee()
    {
        if (!referenceExists) return;
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
