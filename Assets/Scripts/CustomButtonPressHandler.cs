using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;

// Unity's stock Button click logic requires the press AND release to land on the same target,
// with the ray staying within a drag-distance tolerance the whole time in between. This rig's
// physical trigger is analog, not a clean binary switch - already proven via on-device logs for
// CustomSliderDragHandler (see SETUP_AND_DEBUG_NOTES.md), where holding it "still" still let it
// briefly dip below the press threshold. The same small ray jitter that causes that is enough to
// exceed Unity's drag threshold mid-press and silently cancel a click, producing exactly "works,
// but not on the first try" rather than "doesn't work at all".
//
// Same fix pattern as CustomSliderDragHandler: bypass Unity's click/drag-threshold logic
// entirely, reading ActionBasedController.uiPressAction directly (not
// XRRayInteractor.isSelectActive/ActionBasedController.selectAction - see
// CustomSliderDragHandler's header comment for why those don't reflect this rig's trigger at
// all). A press only needs to START while hovering this button; release then fires the click
// regardless of exactly where the ray has drifted to by that point, tolerating the jitter
// Unity's stricter same-target check doesn't.
//
// Invokes the existing Button's own onClick (unchanged/still wired in the Inspector) rather than
// calling a specific target method directly, so this stays reusable on any UI Button. Unity's own
// click detection is left running alongside this rather than disabled - if the ray happens to
// stay steady enough for Unity's own path to also fire, onClick gets invoked twice in a row,
// which is harmless for anything idempotent (e.g. a scene load already in progress) but would
// need attention if reused on a button whose click has a real side effect that isn't safe to
// double-fire.
[RequireComponent(typeof(Button))]
public class CustomButtonPressHandler : MonoBehaviour
{
    // Both hand controllers' ray interactors - whichever one is pressed while hovering this
    // button drives it.
    [SerializeField] private XRRayInteractor[] rayInteractors;

    private Button button;
    private RectTransform buttonRect;
    private ActionBasedController[] controllers;
    private bool[] wasPressed;
    private bool[] pressStartedOnButton;

    private void Awake()
    {
        button = GetComponent<Button>();
        buttonRect = (RectTransform)transform;

        // Auto-discover if nothing was wired by hand - FindObjectsOfType is one-time Awake() cost, not a per-frame one, so this doesn't add ongoing overhead. Every button in the scene ends up finding the same two ray interactors this way, the same as if they'd all been manually wired to them individually.
        // FindObjectsOfType, not the newer FindObjectsByType - this project targets Unity
        // 2021.3.13f1, which predates FindObjectsByType/FindObjectsSortMode.
        if (rayInteractors == null || rayInteractors.Length == 0){
            rayInteractors = FindObjectsOfType<XRRayInteractor>();
        }

        int count = rayInteractors?.Length ?? 0;
        controllers = new ActionBasedController[count];
        wasPressed = new bool[count];
        pressStartedOnButton = new bool[count];

        for (int i = 0; i < count; i++)
        {
            if (rayInteractors[i] != null)
            {
                controllers[i] = rayInteractors[i].GetComponentInParent<ActionBasedController>();
            }
        }
    }

    private void Update()
    {
        if (button == null || rayInteractors == null || !button.interactable) return;

        for (int i = 0; i < rayInteractors.Length; i++)
        {
            XRRayInteractor interactor = rayInteractors[i];
            if (interactor == null) continue;

            // Falls back to isSelectActive only if this interactor has no resolvable
            // ActionBasedController at all - see CustomSliderDragHandler for the same pattern.
            bool pressed = controllers[i] != null && controllers[i].uiPressAction.action != null
                ? controllers[i].uiPressAction.action.IsPressed()
                : interactor.isSelectActive;

            bool risingEdge = pressed && !wasPressed[i];
            bool fallingEdge = !pressed && wasPressed[i];

            if (risingEdge)
            {
                // Remember whether THIS press began on the button - release only fires the click
                // if it did, so pressing elsewhere and drifting onto the button doesn't count.
                pressStartedOnButton[i] = TryGetButtonPlaneHit(interactor, out bool inRect) && inRect;
            }
            else if (fallingEdge)
            {
                if (pressStartedOnButton[i])
                {
                    button.onClick.Invoke();
                }
                pressStartedOnButton[i] = false;
            }

            wasPressed[i] = pressed;
        }
    }

    // Same ray-plane intersection as CustomSliderDragHandler.TryGetSliderPlaneHit, against this
    // button's own RectTransform instead of a slider's.
    private bool TryGetButtonPlaneHit(XRRayInteractor interactor, out bool hitInRect)
    {
        hitInRect = false;

        Transform origin = interactor.rayOriginTransform != null ? interactor.rayOriginTransform : interactor.transform;
        Vector3 rayOrigin = origin.position;
        Vector3 rayDirection = origin.forward;

        Vector3 planeNormal = buttonRect.forward;
        Vector3 planePoint = buttonRect.position;

        float denom = Vector3.Dot(rayDirection, planeNormal);
        if (Mathf.Abs(denom) < 0.0001f) return false; // ray is parallel to the button's plane

        float t = Vector3.Dot(planePoint - rayOrigin, planeNormal) / denom;
        if (t < 0f) return false; // plane is behind the controller

        Vector3 worldPoint = rayOrigin + rayDirection * t;
        Vector3 local = buttonRect.InverseTransformPoint(worldPoint);
        hitInRect = buttonRect.rect.Contains(new Vector2(local.x, local.y));
        return true;
    }
}
