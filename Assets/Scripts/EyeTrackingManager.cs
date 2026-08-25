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

    // CalibrationManager lives in a separate scene file (Calibration.unity) - Inspector
    // references can't cross scene files, so CalibrationCorrectionLocal is read directly as a
    // static property each frame in Update() instead.

    private Vector3 combineEyeGazeVector;
    private Vector3 combineEyeGazeOriginOffset;
    private Vector3 combineEyeGazeOrigin;
    private Matrix4x4 headPoseMatrix;
    private Matrix4x4 originPoseMatrix;

    private Vector3 combineEyeGazeOriginInWorldSpace;

    private uint leftEyeStatus;
    private uint rightEyeStatus;

    private Vector2 primary2DAxis;

    private RaycastHit hitinfo;

    private Transform selectedObj;

    private bool wasPressed;

    // The actual raycast hit point (what the user is looking AT), as opposed to
    // combineEyeGazeOrigin which is just the ray's start point near the eyes.
    private bool hasGazeTarget;
    private Vector3 gazeTargetPoint;

    // Only ever gets entries for objects with a MeshGazeHeatmap component (see
    // GazeTargetControl). Key = object name, value = total seconds gazed at - an accumulating
    // total, always displayed, so there's no "looking at the report changes the data" problem.
    private Dictionary<string, float> dwellTimes = new Dictionary<string, float>();

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
        // Failed pxr read writes zero-valued data into its out parameter - TryReadRawGaze
        // checks all the underlying calls' return values, so a failure skips this frame entirely and everything keeps last frame's values instead of jumping to zero.
       
        if (!GazeReading.TryReadRawGaze(out headPoseMatrix, out combineEyeGazeVector, out combineEyeGazeOrigin))
        {
            return;
        }

        combineEyeGazeOrigin += combineEyeGazeOriginOffset;
        combineEyeGazeOriginInWorldSpace = originPoseMatrix.MultiplyPoint(headPoseMatrix.MultiplyPoint(combineEyeGazeOrigin));

        // Per-eye diagnostic - checks one eye is tracking worse than the other, vs. the
        // combined gaze just reflecting normal ocular dominance.
        PXR_EyeTracking.GetLeftEyePoseStatus(out leftEyeStatus);
        PXR_EyeTracking.GetRightEyePoseStatus(out rightEyeStatus);
        PXR_EyeTracking.GetLeftEyeGazeOpenness(out float leftOpenness);
        PXR_EyeTracking.GetRightEyeGazeOpenness(out float rightOpenness);
        Debug.Log($"[EyeTrackingManager] L status={leftEyeStatus} openness={leftOpenness:F2} | R status={rightEyeStatus} openness={rightOpenness:F2}");
        
        Vector3 correctedGazeVectorLocal = CalibrationManager.CalibrationCorrectionLocal * combineEyeGazeVector;
        Vector3 correctedGazeVectorWorld = originPoseMatrix.MultiplyVector(headPoseMatrix.MultiplyVector(correctedGazeVectorLocal));

        SpotLight.transform.position = combineEyeGazeOriginInWorldSpace;
        SpotLight.transform.rotation = Quaternion.LookRotation(correctedGazeVectorWorld, Vector3.up);
        
        GazeTargetControl(combineEyeGazeOriginInWorldSpace, correctedGazeVectorWorld);

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
            hasGazeTarget = true;
            gazeTargetPoint = hitinfo.point;

            if (hitinfo.collider.TryGetComponent(out MeshGazeHeatmap heatmap))
            {
                // Physics.SphereCast doesn't populate RaycastHit.textureCoord - only
                // Physics.Raycast does - so a second raycast along the same ray gets the UV
                // data, with a same-collider check before trusting it.
                if (Physics.Raycast(origin, vector, out RaycastHit uvHit, 100f) && uvHit.collider == hitinfo.collider)
                {
                    heatmap.StampAt(uvHit);
                }

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
