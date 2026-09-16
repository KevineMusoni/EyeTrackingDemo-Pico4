using UnityEngine;
using Unity.XR.PXR;

// Recenters position+rotation tracking once, automatically, right after the first scene loads in
// a build - independent of which scene ends up first and with no GameObject/scene wiring needed.
// Same PXR_Plugin call PositionGuideManager.cs already uses, just not tied to a scene that's
// actually enabled in Build Settings.
public static class AutoRecenter
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void RecenterOnLaunch()
    {
        PXR_Plugin.Sensor.UPxr_ResetSensor(ResetSensorOption.ResetAll);
    }
}
