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
    [Header("Flat / 2D mode")]
    [Tooltip("Show the video mono, no stereo depth. Source is side-by-side stereo, so this also " +
         "crops to the left-eye half and feeds it to both eyes.")]
    [SerializeField] private bool flatMono = false;
    [SerializeField] private string videoFileName = "LAR_Surgery_3D_Robot_SEALG_v01.mp4";
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

    [SerializeField] private float playbackSpeed = 1f;

    // Per-instance so a screen (e.g. DivergenceReplayScreen) can be switched to Cylinder without
    // affecting any other screen using this same script (SurgeryVideoScreen,
    // ReticleDemoVideoScreen) - those keep defaulting to Quad, matching what was previously
    // hardcoded here, unless explicitly changed in their own Inspector.
    [SerializeField] private PXR_OverLay.OverlayShape overlayShape = PXR_OverLay.OverlayShape.Quad;

    // PXR_OverLay.radius exists on the component regardless of shape, but this SDK's custom
    // Inspector (PXR_OverLayEditor.cs) only ever draws a "Radius" field for Equirect - a gap in
    // the bundled Editor script, not a sign Cylinder doesn't use it. Exposed here directly so it
    // shows up in the normal Inspector without needing Debug mode. Only meaningful when
    // overlayShape is Cylinder; left at 0 (PXR_OverLay's own default) has no effect on Quad.
    [SerializeField] private float cylinderRadius = 0f;

    public float PlaybackSpeed => playbackSpeed;

    public event Action PlaybackStarted;

    private PXR_OverLay overlay;
    private bool playbackStarted;

    // Cached once, in OnSurfaceCreated() - needs to be a field, not a local variable, since SeekTo() is called much later (whenever the replay slider is dragged), long after OnSurfaceCreated()'s own local variables would have gone out of scope.
    private IntPtr playVideoClass;

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
#if UNITY_ANDROID && !UNITY_EDITOR
        // Raw AndroidJNI calls (used throughout this file for the playVideo/seekTo reach-around)
        // don't propagate Java-side exceptions into C#'s try/catch - a thread-mismatch, a bad
        // argument, anything thrown on the Java side can be silently swallowed by the JNI layer,
        // invisible here even with a catch block wrapped around the call. Setting this makes the
        // JNI layer log those exceptions to logcat instead of eating them - only way to find out
        // whether a JNI call that "did nothing visible" actually failed on the Java side.
        AndroidJNIHelper.debug = true;
#endif
        overlay = GetComponent<PXR_OverLay>();

        if (flatMono)
        {
            // Single = no L/R split, the compositor samples one image for both eyes. Then crop that
            // sample to the left half of the (still side-by-side) surface so both eyes see one
            // undistorted eye's view = flat, no depth.
            overlay.externalAndroidSurface3DType = PXR_OverLay.Surface3DType.Single;
            overlay.useImageRect  = true;
            overlay.srcRectLeft   = new Rect(0f, 0f, 0.5f, 1f);
            overlay.srcRectRight  = new Rect(0f, 0f, 0.5f, 1f);
        }
        else
        {
            overlay.externalAndroidSurface3DType = PXR_OverLay.Surface3DType.LeftRight;
        }
        overlay.overlayShape = overlayShape;
        overlay.radius = cylinderRadius;
        overlay.isExternalAndroidSurface = true;
        overlay.externalAndroidSurfaceObjectCreated += OnSurfaceCreated;
    }

    private void Start()
    {
        // No artificial delay needed here - see SubscribeOrFireImmediately above. Whether this
        // resolves before or after any given listener's own Start() has already run is now a
        // non-issue on both sides, so this can fire as early as the SDK allows instead of
        // deliberately waiting.
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
                IntPtr localPlayVideoClass = AndroidJNI.FindClass("com/pico/exoplayerdemo/PlayVideo");
                playVideoClass = AndroidJNI.NewGlobalRef(localPlayVideoClass);
                AndroidJNI.DeleteLocalRef(localPlayVideoClass);

                IntPtr methodId = AndroidJNI.GetStaticMethodID(
                    playVideoClass,
                    "playVideo",
                    "(Landroid/content/Context;Ljava/lang/String;Landroid/view/Surface;)V");

                jvalue[] args = new jvalue[3];
                args[0].l = activity.GetRawObject();
                args[1].l = AndroidJNI.NewStringUTF(videoPath);
                args[2].l = overlay.externalAndroidSurfaceObject;

                AndroidJNI.CallStaticVoidMethod(playVideoClass, methodId, args); // this starts the video via JNI
                if (!Mathf.Approximately(playbackSpeed, 1f))
                {
                    ApplyPlaybackSpeed(playVideoClass, playbackSpeed);
                }
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

#if UNITY_ANDROID && !UNITY_EDITOR
// Adjusted playback speed via unity
    private void ApplyPlaybackSpeed(IntPtr playVideoClass, float speed){
        try{
            IntPtr exoPlayerFieldId = AndroidJNI.GetStaticFieldID(
                playVideoClass, "exoPlayer", "Lcom/google/android/exoplayer2/SimpleExoPlayer;");
            IntPtr exoPlayerInstance = AndroidJNI.GetStaticObjectField(playVideoClass, exoPlayerFieldId);

            if (exoPlayerInstance == IntPtr.Zero){
                Debug.LogWarning("[SurgeryVideoOverlayPlayer] exoPlayer field was null - can't apply playback speed.");
                return;
            }

            IntPtr paramsClass = AndroidJNI.FindClass("com/google/android/exoplayer2/PlaybackParameters");
            IntPtr ctor = AndroidJNI.GetMethodID(paramsClass, "<init>", "(F)V");
            jvalue[] ctorArgs = new jvalue[1];
            ctorArgs[0].f = speed;
            IntPtr playbackParams = AndroidJNI.NewObject(paramsClass, ctor, ctorArgs);

            IntPtr exoPlayerClass = AndroidJNI.FindClass("com/google/android/exoplayer2/SimpleExoPlayer");
            IntPtr setParamsMethod = AndroidJNI.GetMethodID(
                exoPlayerClass, "setPlaybackParameters", 
                "(Lcom/google/android/exoplayer2/PlaybackParameters;)V");
                jvalue[] callArgs = new jvalue[1];
                callArgs[0].l = playbackParams;
                AndroidJNI.CallVoidMethod(exoPlayerInstance, setParamsMethod, callArgs);

                Debug.Log($"[SurgeryVideoOverlayPlayer] Applied playback speed {speed}.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[SurgeryVideoOverlayPlayer] ApplyPlaybackSpeed failed: {e}");
        }
    }
#endif


// called when the replay slider is dragged and released - jumps the video to a new position. Same JNI reach-around technique as ApplyPlaybackSpeed: this plugin exposes no seek method of its own, so this reaches directly into the exoplayer instance it already created, calling ExoPlayer's own real seekTo() method instead.

    public void SeekTo(float seconds)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!playbackStarted){
            // nothing to seek yet no exoPlayer
            // instance to reach into (same guard reasoning as ApplybackSpeed).
            return;
        }

        try{
            IntPtr exoPlayerFieldId = AndroidJNI.GetStaticFieldID(
                playVideoClass, "exoPlayer", "Lcom/google/android/exoplayer2/SimpleExoPlayer;");
            IntPtr exoPlayerInstance = AndroidJNI.GetStaticObjectField(playVideoClass, exoPlayerFieldId);

            if (exoPlayerInstance == IntPtr.Zero)
            {
                Debug.LogWarning("[SurgeryVideoOverlayPlayer] exoPlayer field was null - can't seek.");
                return;
            }

            IntPtr exoPlayerClass = AndroidJNI.FindClass("com/google/android/exoplayer2/SimpleExoPlayer");
            // "(IJ)V" - confirmed against the actual compiled exoplayer-core-2.11.5.aar, not
            // guessed: takes an I (window index) AND a J (Java long position in ms), not just a
            // long alone. Calling CallVoidMethod with a method ID from a wrong/nonexistent
            // signature is what caused a native crash the first time around - GetMethodID can
            // return an invalid handle without throwing a catchable exception, so the null-check
            // below matters, not just the try/catch.
            IntPtr seekMethod = AndroidJNI.GetMethodID(exoPlayerClass, "seekTo", "(IJ)V");

            if (seekMethod == IntPtr.Zero)
            {
                Debug.LogError("[SurgeryVideoOverlayPlayer] seekTo method not found - wrong signature.");
                return;
            }

            // setPlayWhenReady(boolean) - "(Z)V". Pausing immediately before the seek and
            // resuming immediately after forces the decoder to drop whatever frame it was
            // mid-presenting and decode/present a genuinely fresh one at the new position,
            // rather than seeking while the decode pipeline is actively mid-stream - a known
            // fix for "seekTo() completes with no exception, but the Surface keeps showing
            // stale content" on some hardware decoders. Reverted once already without ever
            // actually being tested on-device - trying it for real this time.
            IntPtr setPlayWhenReadyMethod = AndroidJNI.GetMethodID(exoPlayerClass, "setPlayWhenReady", "(Z)V");
            bool canTogglePlayback = setPlayWhenReadyMethod != IntPtr.Zero;

            if (canTogglePlayback)
            {
                jvalue[] pauseArgs = new jvalue[1];
                pauseArgs[0].z = false;
                AndroidJNI.CallVoidMethod(exoPlayerInstance, setPlayWhenReadyMethod, pauseArgs);
            }

            long positionMs = (long)(seconds * 1000f);
            jvalue[] args = new jvalue[2];
            args[0].i = 0;          // window index - always 0, this project only ever plays a single media item, no playlist
            args[1].j = positionMs; // .j is JNI's field for a Java long, same idea as .f for float earlier
            AndroidJNI.CallVoidMethod(exoPlayerInstance, seekMethod, args);

            if (canTogglePlayback)
            {
                jvalue[] resumeArgs = new jvalue[1];
                resumeArgs[0].z = true;
                AndroidJNI.CallVoidMethod(exoPlayerInstance, setPlayWhenReadyMethod, resumeArgs);
            }

            Debug.Log($"[SurgeryVideoOverlayPlayer] Seeked to {seconds}s ({positionMs}ms), togglePlayback={canTogglePlayback}.");
        }
        catch (Exception e){
            Debug.LogError($"[SurgeryVideoOverlayPlayer] SeekTo failed: {e}");
        }
#endif
    }




    private void OnDestroy()
    {
        if (overlay != null)
        {
            overlay.externalAndroidSurfaceObjectCreated -= OnSurfaceCreated;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // Global references don't get released automatically like local ones do - has to be
        // explicit, or it leaks for the lifetime of the native process, not just this object.
        if (playVideoClass != IntPtr.Zero)
        {
            AndroidJNI.DeleteGlobalRef(playVideoClass);
        }
#endif
    }
}
