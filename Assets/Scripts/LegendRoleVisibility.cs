using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One shared legend slot instead of two side-by-side ones - the text and dot colour
// interchange based on role, rather than toggling which of two separate objects is active.
public class LegendRoleVisibility : MonoBehaviour
{
    [SerializeField] private TMP_Text legendText;
    [SerializeField] private Image legendDot;

    private static readonly Color SpecialistColor = new Color(0f, 0.6f, 0f);
    private static readonly Color TraineeColor = new Color(0.85f, 0.65f, 0f);

    private void Start()
    {
        bool isSpecialist = SessionRoleManager.IsSpecialist;

        Color roleColor = isSpecialist ? SpecialistColor : TraineeColor;

        if (legendText != null)
        {
            legendText.text = isSpecialist ? "Specialist" : "Trainee";
            legendText.color = roleColor;
        }
        if (legendDot != null) legendDot.color = roleColor;
    }
}
