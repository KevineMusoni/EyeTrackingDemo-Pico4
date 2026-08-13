using UnityEngine;
using Unity.XR.PXR;
using UnityEngine.XR;
using TMPro;
using System.Collections.Generic;

public class EyeTrackingManager : MonoBehaviour
{
    public Transform Origin;
    public GameObject EyeCoordinates;
    public GameObject Models;
    public Transform Greenpoint;
    public GameObject SpotLight;
    public TMP_Text GazeOffsetText;

    // MODIFIED: no longer an Inspector-wired reference - CalibrationManager now lives in a
    // separate scene file (Calibration.unity), and Inspector references can't cross scene
    // files. Read CalibrationManager.CalibrationCorrection directly (a static property) each
    // frame in Update() instead - see that class for why it's static.

    private Vector3 combineEyeGazeVector;
    private Vector3 combineEyeGazeOriginOffset;
    private Vector3 combineEyeGazeOrigin;
    private Matrix4x4 headPoseMatrix;
    private Matrix4x4 originPoseMatrix;

    private Vector3 combineEyeGazeVectorInWorldSpace;
    private Vector3 combineEyeGazeOriginInWorldSpace;

    private uint leftEyeStatus;
    private uint rightEyeStatus;

    private Vector2 primary2DAxis;

    private RaycastHit hitinfo;

    private Transform selectedObj;

    private bool wasPressed;

    // ADDED: tracks the actual raycast hit point (what the user is looking AT),
    // as opposed to combineEyeGazeOrigin which is just the ray's start point near the eyes.
    private bool hasGazeTarget;
    private Vector3 gazeTargetPoint;

    // ADDED: dwell-time report data. Only ever gets entries for objects that have a
    // MeshGazeHeatmap component (see GazeTargetControl) - i.e. just the two test objects,
    // not every object in the scene. Key = object name, value = total seconds gazed at.
    // Always displayed- no button/toggle, replaces the old live Vector/Point/
    // Target readout entirely, so there's no more "looking at the report changes the data"
    // problem: this isn't a single live value, it's an accumulating total per object.
    private Dictionary<string, float> dwellTimes = new Dictionary<string, float>();

    // ADDED: read-only access to this frame's RAW (uncorrected) world-space gaze data, for
    // CalibrationManager to sample from during its calibration routine. "Raw" specifically
    // means before CalibrationCorrection is applied below - calibration needs to measure the
    // actual tracking bias, which would be hidden if it read already-corrected data.
    public Vector3 RawGazeOriginWorld => combineEyeGazeOriginInWorldSpace;
    public Vector3 RawGazeVectorWorld => combineEyeGazeVectorInWorldSpace;

    void Start()
    {
        combineEyeGazeOriginOffset = Vector3.zero;
        combineEyeGazeVector = Vector3.zero;
        combineEyeGazeOrigin = Vector3.zero;
        originPoseMatrix = Origin.localToWorldMatrix;
    }

    void Update()
    {
        //Offest Adjustment
        if (InputDevices.GetDeviceAtXRNode(XRNode.RightHand).TryGetFeatureValue(CommonUsages.primary2DAxis, out primary2DAxis))
        {

            combineEyeGazeOriginOffset.x += primary2DAxis.x*0.001f;
            combineEyeGazeOriginOffset.y += primary2DAxis.y*0.001f;

        }
        // pxr eyetracking, static class exposing data to code
        // eye gaze vector - ie. eye compass
        // gaze point - pysical location of the eyes
        PXR_EyeTracking.GetHeadPosMatrix(out headPoseMatrix);
        PXR_EyeTracking.GetCombineEyeGazeVector(out combineEyeGazeVector);
        PXR_EyeTracking.GetCombineEyeGazePoint(out combineEyeGazeOrigin);
        //Translate Eye Gaze point and vector to world space
        combineEyeGazeOrigin += combineEyeGazeOriginOffset;
        combineEyeGazeOriginInWorldSpace = originPoseMatrix.MultiplyPoint(headPoseMatrix.MultiplyPoint(combineEyeGazeOrigin));
        combineEyeGazeVectorInWorldSpace = originPoseMatrix.MultiplyVector(headPoseMatrix.MultiplyVector(combineEyeGazeVector));

        // ADDED: per-eye diagnostic logging - populates the previously-unused leftEyeStatus/
        // rightEyeStatus fields, to check whether one eye is tracking worse than the other
        // (vs. the combined gaze just reflecting normal ocular dominance).
        PXR_EyeTracking.GetLeftEyePoseStatus(out leftEyeStatus);
        PXR_EyeTracking.GetRightEyePoseStatus(out rightEyeStatus);
        PXR_EyeTracking.GetLeftEyeGazeOpenness(out float leftOpenness);
        PXR_EyeTracking.GetRightEyeGazeOpenness(out float rightOpenness);
        Debug.Log($"[EyeTrackingManager] L status={leftEyeStatus} openness={leftOpenness:F2} | R status={rightEyeStatus} openness={rightOpenness:F2}");

        // MODIFIED: reads the static CalibrationManager.CalibrationCorrection directly (was
        // an Inspector-wired instance reference) - see field comment above for why. Only the
        // direction is corrected, not the origin point - matches the angular-bias error
        // model the calibration routine measures. Safe unconditionally: this static property
        // defaults to Quaternion.identity (a no-op rotation) until/unless a Calibration scene
        // actually runs and completes - if this main scene is opened directly without ever
        // going through Calibration.unity (e.g. quick testing in the Editor), it's just a
        // harmless no-op, same as raw uncorrected data.
        Vector3 correctedGazeVectorWorld = CalibrationManager.CalibrationCorrection * combineEyeGazeVectorInWorldSpace;

        SpotLight.transform.position = combineEyeGazeOriginInWorldSpace;
        SpotLight.transform.rotation = Quaternion.LookRotation(correctedGazeVectorWorld, Vector3.up);

        // MOVED: GazeTargetControl now runs before the text update below, so gazeTargetPoint
        // (set inside it) is fresh for this frame's display instead of one frame stale.
        // MODIFIED: passes correctedGazeVectorWorld (was combineEyeGazeVectorInWorldSpace) so
        // all downstream hit-detection/heatmap stamping/dwell-tracking uses the calibrated
        // direction, not the raw one.
        GazeTargetControl(combineEyeGazeOriginInWorldSpace, correctedGazeVectorWorld);

        // MODIFIED: replaced the live Vector/Point/Target readout with the always-on dwell
        // report - cleaner, and avoids the problem where reading a live "Target" value on
        // the panel while looking at the panel changes what it's currently reporting.
        string report = "Gaze Report (seconds looked at each object):\n\n";
        foreach (KeyValuePair<string, float> entry in dwellTimes)
        {
            report += $"{entry.Key}: {entry.Value:F1}s\n\n";
        }
        GazeOffsetText.text = report;
        Debug.Log($"[EyeTrackingManager] {report.Replace("\n", " ")}");
    }


    void GazeTargetControl(Vector3 origin,Vector3 vector)
    {
        Ray ray = new Ray(origin,vector);
        if (Physics.SphereCast(origin,0.0005f,vector,out hitinfo))
        {
            // ADDED: expose the hit point for the text display regardless of collider tag,
            // "Target" shows what is actually under the gaze even for non-"Target"-tagged objects.
            hasGazeTarget = true;
            gazeTargetPoint = hitinfo.point;

            // gaze heatmap hook - only runsif what is hit has a MeshGazeHeatmap component attached (TryGetComponent returns false otherwise,
            // so this is a no-op for every other object in the scene, e.g. the cube/character).
            if (hitinfo.collider.TryGetComponent(out MeshGazeHeatmap heatmap))
            {
                // Physics.SphereCast (used above, "hitinfo") does not populate
                // RaycastHit.textureCoord - only Physics.Raycast does. So instead of reusing
                // "hitinfo", we fire a second, plain Raycast along the exact same ray just to
                // get a RaycastHit with valid UV data. 100f = max raycast distance in meters,
                // generous since the sphere-cast above already found something within range.
                if (Physics.Raycast(origin, vector, out RaycastHit uvHit, 100f) && uvHit.collider == hitinfo.collider)
                {
                    // (uvHit.collider == hitinfo.collider) confirms the
                    // second raycast hit the SAME object as the sphere-cast did, not something
                    // else in between - only then is uvHit.textureCoord meaningful to use.
                    // MODIFIED: pass the whole RaycastHit (was just uvHit.textureCoord) - the
                    // heatmap now also needs uvHit.triangleIndex to auto-scale the brush size
                    // to this object's actual physical size (see MeshGazeHeatmap for why).
                    heatmap.StampAt(uvHit);
                }

                // ADDED: dwell-time report accumulation. Only runs inside this same
                // "does the hit object have MeshGazeHeatmap" check, so it automatically only
                // tracks the two designated test objects, not anything else in the scene.
                // Uses TryGetValue (not GetValueOrDefault) for compatibility with older
                // .NET API Compatibility Level settings this project might be using.
                string objName = hitinfo.collider.gameObject.name;
                dwellTimes.TryGetValue(objName, out float existingDwell);
                dwellTimes[objName] = existingDwell + Time.deltaTime;
            }

            if (hitinfo.collider.transform.tag.Equals("Target"))
            {
                Greenpoint.gameObject.SetActive(true);
                Greenpoint.position= hitinfo.point;
            }

            if (selectedObj != null && selectedObj != hitinfo.transform)
            {
                if(selectedObj.GetComponent<ETObject>()!=null)
                    selectedObj.GetComponent<ETObject>().UnFocused();
                selectedObj = null;
            }
            else if (selectedObj == null)
            {
                selectedObj = hitinfo.transform;
                if (selectedObj.GetComponent<ETObject>() != null)
                    selectedObj.GetComponent<ETObject>().IsFocused();
            }

        }
        else
        {
            // ADDED: no collider hit this frame, so there's no valid target point to show.
            hasGazeTarget = false;

            if (selectedObj != null)
            {
               if (selectedObj.GetComponent<ETObject>() != null)
                    selectedObj.GetComponent<ETObject>().UnFocused();
                selectedObj = null;
            }
            Greenpoint.gameObject.SetActive(false);
        }
    }
}
