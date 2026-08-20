using UnityEngine;
using Unity.XR.PXR;

// Single trusted gaze-reading function shared by CalibrationManager and EyeTrackingManager, so
// both check PXR's return values the same way instead of each silently trusting them. Plain
// static utility (not a MonoBehaviour) so it can be called from either scene's scripts without
// creating a dependency between them

public static class GazeReading
{
    // A failed/uninitialized PXR read typically comes back as exactly (0,0,0). Checking this
    // directly (not just the bool returns) catches that case even if a return value were ever
    // wrong, and rejects a genuinely degenerate direction either way.
    private const float MinGazeVectorSqrMagnitude = 0.000001f;

    // Visibility into how often this rejects a read on real hardware - otherwise fails silently.
    private static int totalAttempts = 0;
    private static int totalFailures = 0;

    public static bool TryReadRawGaze(out Matrix4x4 headPose, out Vector3 gazeLocal, out Vector3 originLocal)
    {
        totalAttempts++;

        bool headValid = PXR_EyeTracking.GetHeadPosMatrix(out headPose);
        bool vectorValid = PXR_EyeTracking.GetCombineEyeGazeVector(out gazeLocal);
        bool originValid = PXR_EyeTracking.GetCombineEyeGazePoint(out originLocal);
        bool statusValid = PXR_EyeTracking.GetCombinedEyePoseStatus(out uint combinedStatus);

        if (!headValid || !vectorValid || !originValid || !statusValid ||
            combinedStatus == 0 || gazeLocal.sqrMagnitude < MinGazeVectorSqrMagnitude)
        {
            totalFailures++;
            Debug.Log($"[GazeReading] Rejected invalid read (head={headValid} vector={vectorValid} origin={originValid} status={statusValid}/{combinedStatus}) - {totalFailures}/{totalAttempts} rejected this session.");
            return false;
        }

        return true;
    }
}
