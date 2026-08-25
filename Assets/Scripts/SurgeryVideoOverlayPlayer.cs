using System;
using UnityEngine;
using Unity.XR.PXR;

// Media play implementation and documentation
// Plays the surgery video via PICO's compositor-layer External Surface path instead of
// through Unity's normal render pipeline. This exists because both Unity's built-in
// VideoPlayer (RenderTexture target) and AVPro Video (material/shader sampling) failed to
// ever display a frame on this headset under Vulkan and GLES3, despite both reporting
// successful decode - the compositor layer bypasses Unity's texture/shader pipeline
// entirely, handing the raw Android Surface straight to PICO's system compositor.
//
// Requires Assets/Plugins/Android/playvideo.jar (PICO's own ExoPlayer-backed plugin,
// class com.pico.exoplayerdemo.PlayVideo) and its ExoPlayer .aar dependencies.

[RequireComponent(typeof(PXR_OverLay))]
public class SurgeryVideoOverlayPlayer : MonoBehaviour
{
    [SerializeField] private string videoFileName = "LAR_Surgery_3D_Robot_SEALG_v01.mp4";

    private PXR_OverLay overlay;
    private bool playbackStarted;

    private void Awake()
    {
        overlay = GetComponent<PXR_OverLay>();
        overlay.overlayShape = PXR_OverLay.OverlayShape.Quad;
        overlay.isExternalAndroidSurface = true;
        overlay.externalAndroidSurface3DType = PXR_OverLay.Surface3DType.LeftRight;
        overlay.externalAndroidSurfaceObjectCreated += OnSurfaceCreated;
    }

    private void Start()
    {
        overlay.CreateExternalSurface(overlay);
    }

    private void OnSurfaceCreated()
    {
        if (playbackStarted)
        {
            return;
        }

        if (overlay.externalAndroidSurfaceObject == IntPtr.Zero)
        {
            Debug.LogError("[SurgeryVideoOverlayPlayer] Surface creation callback fired but externalAndroidSurfaceObject is still null.");
            return;
        }

        playbackStarted = true;

#if UNITY_ANDROID && !UNITY_EDITOR
        string videoPath = System.IO.Path.Combine(Application.persistentDataPath, videoFileName);
        Debug.Log($"[SurgeryVideoOverlayPlayer] Starting playback: path={videoPath} surface={overlay.externalAndroidSurfaceObject}");

        try
        {
            using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                IntPtr playVideoClass = AndroidJNI.FindClass("com/pico/exoplayerdemo/PlayVideo");
                IntPtr methodId = AndroidJNI.GetStaticMethodID(
                    playVideoClass,
                    "playVideo",
                    "(Landroid/content/Context;Ljava/lang/String;Landroid/view/Surface;)V");

                jvalue[] args = new jvalue[3];
                args[0].l = activity.GetRawObject();
                args[1].l = AndroidJNI.NewStringUTF(videoPath);
                args[2].l = overlay.externalAndroidSurfaceObject;

                AndroidJNI.CallStaticVoidMethod(playVideoClass, methodId, args);
            }

            // log to debug
            Debug.Log("[SurgeryVideoOverlayPlayer] playVideo JNI call completed without exception.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[SurgeryVideoOverlayPlayer] playVideo JNI call failed: {e}");
        }
#endif
    }

    private void OnDestroy()
    {
        if (overlay != null)
        {
            overlay.externalAndroidSurfaceObjectCreated -= OnSurfaceCreated;
        }
    }
}
