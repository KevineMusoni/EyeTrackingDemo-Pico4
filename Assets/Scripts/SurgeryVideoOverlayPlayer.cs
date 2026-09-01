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
    [SerializeField] private bool autoPlayOnStart = true;
    // Fired once, right when the Android Surface is ready and playback is actually issued -
    // the closest thing to a real "video started" signal this plugin exposes (there's no
    // position/duration query on the native ExoPlayer side, so this is it). Anything that needs
    // "N seconds since the video started" (MeshGazeHeatmap's autoStopAfterSeconds,
    // GazeReviewLoader/ComparisonLoader's initialLoadDelaySeconds) should measure from this
    // event, not from its own Start() - those can fire a beat or more before the surface is
    // actually ready, which is what caused the "review/report load, but video wasn't done"
    // mismatch this was added to fix. Fired unconditionally (not inside the UNITY_ANDROID
    // block below) so Editor Play Mode testing of the downstream timing still works even though
    // the JNI playVideo call itself is Android-only.
    public event Action PlaybackStarted;

    private PXR_OverLay overlay;
    private bool playbackStarted;

    // Subscribe through this instead of `PlaybackStarted +=` directly. On-device logging showed
    // Start() order between different scripts - even components on the SAME GameObject - is not
    // just "unspecified" but can genuinely span many frames apart in this scene (real Start() work
    // like texture allocation, plus staggered SessionRoleManager-driven activation), so no fixed
    // number of deferred frames on the firing side can reliably guarantee every subscriber has
    // subscribed first. This sidesteps the ordering question entirely: if playback has already
    // started by the time a listener calls this, it fires the callback immediately instead of
    // silently losing an event nobody was listening for yet; otherwise it just subscribes normally
    // and waits for the real event like before.
    public void SubscribeOrFireImmediately(Action callback)
    {
        if (playbackStarted)
        {
            callback();
        }
        else
        {
            PlaybackStarted += callback;
        }
    }

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
        // No artificial delay needed here - see SubscribeOrFireImmediately above. Whether this
        // resolves before or after any given listener's own Start() has already run is now a
        // non-issue on both sides, so this can fire as early as the SDK allows instead of
        // deliberately waiting.

        if (autoPlayOnStart){
        overlay.CreateExternalSurface(overlay);
        }

    }

    public void BeginPlayback(){
        overlay.CreateExternalSurface(overlay);
    }

    private void OnSurfaceCreated()
    {
        Debug.Log($"[SurgeryVideoOverlayPlayer] '{gameObject.name}' OnSurfaceCreated fired at Time.time={Time.time:F2} - playbackStarted={playbackStarted}, surfaceObject={overlay.externalAndroidSurfaceObject}, subscriberCount={PlaybackStarted?.GetInvocationList().Length ?? 0}");

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
        PlaybackStarted?.Invoke();

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
