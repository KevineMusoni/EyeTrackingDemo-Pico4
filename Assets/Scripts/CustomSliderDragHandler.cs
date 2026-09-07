using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;

// Replaces ReplaySliderSeekHandler. Unity's built-in Slider drag events
// (IBeginDragHandler/IDragHandler/IEndDragHandler) never fire through this project's XR
// ray-interaction setup for World Space UI - confirmed on-device: zero drag events logged
// despite the ray visibly holding on the slider and being dragged across it.
//
// Bypasses Unity's UI event system entirely - polls raw controller/ray state every frame
// instead. Root-causing this took three separate on-device bugs; see the "Custom XR slider
// drag" section in SETUP_AND_DEBUG_NOTES.md for the full story. Summary of what this class
// does differently from an obvious first attempt, and why:
//   - Finds the ray/slider hit itself via plain ray-plane geometry (TryGetSliderPlaneHit),
//     not XRRayInteractor.TryGetCurrentUIRaycastResult() - that returns XRI's own internally
//     cached UI raycast, which only ever produced a usable hit after the replay video had
//     already finished playing, never during, for reasons never fully pinned down.
//   - Re-applies the drag's value again in LateUpdate() (guaranteed by Unity to run after
//     every Update() in the scene) rather than trying to coordinate with
//     DivergenceReplayScreenOverlay's own Update() (which also writes Slider.value, from the
//     live playback clock) via a "don't write this frame" flag - Unity doesn't guarantee
//     Update() order between different scripts, so that flag lost the race unpredictably.
//   - Reads ActionBasedController.uiPressAction directly, not XRRayInteractor.isSelectActive
//     or ActionBasedController.selectAction - neither of those ever reflects this rig's
//     trigger at all. UI clicks and 3D-object selection are driven by two entirely separate
//     input actions; uiPressAction is the one XRI's own UI click path is actually backed by.


[RequireComponent(typeof(Slider))]
public class CustomSliderDragHandler : MonoBehaviour
{
    // Both hand controllers' ray interactors - whichever one is actively pressed while pointed
    // at this slider drives it. Only one is expected to be doing so at a time.
    [SerializeField] private XRRayInteractor[] rayInteractors;

    // Optional - leave unwired in the Inspector to run this slider as a standalone control with
    // no connection to the replay screen or video at all.
    [SerializeField] private DivergenceReplayScreenOverlay replayOverlay;

    // Minimum real time between actual seeks - caps how often ExoPlayer gets asked to jump while
    // the trigger is held and being dragged across the slider. The handle itself still moves
    // every frame regardless; this only throttles the expensive native seek calls.
    [SerializeField] private float minSeekInterval = 0.3f;

    private Slider slider;
    private RectTransform sliderRect;
    private bool isDragging;
    private float lastSeekTime = -999f;

    // The value Update() computed from the ray this frame, while dragging - re-applied in
    // LateUpdate() below so it always wins over DivergenceReplayScreenOverlay's own playback-
    // driven write to the same Slider.value, regardless of Update() ordering between the two.
    private float lastDragValue;

    // Resolved once in Awake() - see the class comment above for why this, not
    // XRRayInteractor.isSelectActive/ActionBasedController.selectAction, drives the drag.
    private ActionBasedController[] controllers;

    private void Awake()
    {
        slider = GetComponent<Slider>();
        sliderRect = (RectTransform)transform;

        controllers = new ActionBasedController[rayInteractors?.Length ?? 0];
        for (int i = 0; i < controllers.Length; i++)
        {
            if (rayInteractors[i] != null)
            {
                controllers[i] = rayInteractors[i].GetComponentInParent<ActionBasedController>();
            }
        }
    }

    private void Update()
    {
        if (slider == null || rayInteractors == null) return;

        for (int i = 0; i < rayInteractors.Length; i++)
        {
            XRRayInteractor interactor = rayInteractors[i];
            if (interactor == null) continue;

            // Falls back to isSelectActive only if this interactor has no resolvable
            // ActionBasedController at all.
            bool selecting = controllers[i] != null && controllers[i].uiPressAction.action != null
                ? controllers[i].uiPressAction.action.IsPressed()
                : interactor.isSelectActive;

            if (!selecting) continue;
            if (!TryGetSliderPlaneHit(interactor, out Vector3 worldPoint, out bool inRect) || !inRect) continue;

            // Found a hand that's actively pressed while pointed at this slider - drive the drag
            // from it and stop looking at the other hand this frame.
            isDragging = true;

            float value = WorldPointToSliderValue(worldPoint);
            lastDragValue = value;

            slider.SetValueWithoutNotify(value);

            if (Time.unscaledTime - lastSeekTime >= minSeekInterval)
            {
                DoSeek(value);
            }
            return;
        }

        if (isDragging)
        {
            // Trigger released (or the ray moved off the slider) - guarantee the final dragged-to
            // position actually gets applied, even if it landed inside the last throttle window,
            // then let DivergenceReplayScreenOverlay.Update() resume driving the slider from
            // playback again.
            isDragging = false;
            DoSeek(slider.value);
        }
    }

    // Runs after every Update() in the scene this frame, guaranteed by Unity's execution order -
    // unlike Update()-vs-Update() ordering between two different scripts, which is unspecified.
    // While actively dragging, forcibly re-applies the value the drag computed this frame, so it
    // always wins over DivergenceReplayScreenOverlay's own playback-driven write to the same
    // Slider.value, no matter which Update() happened to run first.
    private void LateUpdate()
    {
        if (isDragging)
        {
            slider.SetValueWithoutNotify(lastDragValue);
        }
    }

    // Intersects the controller's ray with the infinite plane the slider's own RectTransform
    // sits on, then checks whether that intersection point actually falls within the slider's
    // rectangle (not just somewhere else on the same plane).
    private bool TryGetSliderPlaneHit(XRRayInteractor interactor, out Vector3 worldPoint, out bool hitInRect)
    {
        worldPoint = default;
        hitInRect = false;

        Transform origin = interactor.rayOriginTransform != null ? interactor.rayOriginTransform : interactor.transform;
        Vector3 rayOrigin = origin.position;
        Vector3 rayDirection = origin.forward;

        Vector3 planeNormal = sliderRect.forward;
        Vector3 planePoint = sliderRect.position;

        float denom = Vector3.Dot(rayDirection, planeNormal);
        if (Mathf.Abs(denom) < 0.0001f) return false; // ray is parallel to the slider's plane

        float t = Vector3.Dot(planePoint - rayOrigin, planeNormal) / denom;
        if (t < 0f) return false; // plane is behind the controller

        worldPoint = rayOrigin + rayDirection * t;

        Vector3 local = sliderRect.InverseTransformPoint(worldPoint);
        hitInRect = sliderRect.rect.Contains(new Vector2(local.x, local.y));
        return true;
    }

    private float WorldPointToSliderValue(Vector3 worldPoint)
    {
        Vector3 local = sliderRect.InverseTransformPoint(worldPoint);
        float normalized = Mathf.InverseLerp(sliderRect.rect.xMin, sliderRect.rect.xMax, local.x);
        return Mathf.Lerp(slider.minValue, slider.maxValue, normalized);
    }

    private void DoSeek(float value)
    {
        lastSeekTime = Time.unscaledTime;

        // Temporary - confirms this method actually runs, since nothing downstream logs unless
        // this call reaches SeekToSecond() successfully. Remove once the chain is confirmed working.
        Debug.Log($"[CustomSliderDragHandler] DoSeek({value:F2}) called, replayOverlay={(replayOverlay != null ? "wired" : "NULL")}.");

        if (replayOverlay == null) return; // standalone mode - see field comment above

        replayOverlay.SeekToSecond(value);
    }
}
