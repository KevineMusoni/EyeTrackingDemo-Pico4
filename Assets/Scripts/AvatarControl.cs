using System.Collections;
using System.Collections.Generic;
using Unity.XR.PXR;
using UnityEngine;

public class AvatarControl : MonoBehaviour
{
    public SkinnedMeshRenderer Skin;
    public SkinnedMeshRenderer LeftEyeExample;
    public SkinnedMeshRenderer RightEyeExample;

    private int leftEyeBlinkIndex;
    private float leftEyeOpenness;


    private int rightEyeBlinkIndex;
    private float rightEyeOpenness;

    void Start()
    {
        leftEyeBlinkIndex = Skin.sharedMesh.GetBlendShapeIndex("eyeBlinkLeft");
        rightEyeBlinkIndex = Skin.sharedMesh.GetBlendShapeIndex("eyeBlinkRight");
    }

    void Update()
    {
        PXR_EyeTracking.GetLeftEyeGazeOpenness(out leftEyeOpenness);
        Skin.SetBlendShapeWeight(leftEyeBlinkIndex, (1f-leftEyeOpenness) * 100);
        PXR_EyeTracking.GetRightEyeGazeOpenness(out rightEyeOpenness);
        Skin.SetBlendShapeWeight(rightEyeBlinkIndex, (1f-rightEyeOpenness) * 100);
    }
}
