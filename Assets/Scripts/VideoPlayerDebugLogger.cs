using UnityEngine;
using UnityEngine.Video;

// Attach alongside a VideoPlayer to surface prepare/play/error events in logcat -
// VideoPlayer fails silently by default, so without this a failed Prepare()/Play()
// produces no log output at all.
[RequireComponent(typeof(VideoPlayer))]
public class VideoPlayerDebugLogger : MonoBehaviour
{
    private VideoPlayer videoPlayer;

    private void Awake()
    {
        videoPlayer = GetComponent<VideoPlayer>();
        videoPlayer.errorReceived += OnErrorReceived;
        videoPlayer.prepareCompleted += OnPrepareCompleted;
        videoPlayer.started += OnStarted;
        videoPlayer.seekCompleted += OnSeekCompleted;
    }

    private void Start()
    {
        Debug.Log($"[VideoPlayerDebugLogger] isPrepared={videoPlayer.isPrepared} url={videoPlayer.url} clip={videoPlayer.clip} playOnAwake={videoPlayer.playOnAwake} enabled={videoPlayer.enabled} gameObjectActive={gameObject.activeInHierarchy}");
    }

    private void OnErrorReceived(VideoPlayer source, string message)
    {
        Debug.LogError($"[VideoPlayerDebugLogger] ERROR: {message}");
    }

    private void OnPrepareCompleted(VideoPlayer source)
    {
        Debug.Log($"[VideoPlayerDebugLogger] prepareCompleted isPrepared={source.isPrepared} isPlaying={source.isPlaying} frame={source.frame} frameCount={source.frameCount} width={source.width} height={source.height}");
    }

    private void OnStarted(VideoPlayer source)
    {
        Debug.Log($"[VideoPlayerDebugLogger] started");
    }

    private void OnSeekCompleted(VideoPlayer source)
    {
        Debug.Log($"[VideoPlayerDebugLogger] seekCompleted frame={source.frame}");
    }

    private void OnDestroy()
    {
        if (videoPlayer == null) return;
        videoPlayer.errorReceived -= OnErrorReceived;
        videoPlayer.prepareCompleted -= OnPrepareCompleted;
        videoPlayer.started -= OnStarted;
        videoPlayer.seekCompleted -= OnSeekCompleted;
    }
}
