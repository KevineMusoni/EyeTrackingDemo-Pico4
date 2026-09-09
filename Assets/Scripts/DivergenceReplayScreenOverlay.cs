using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

// Replays the specialist's and trainee's already-recorded gaze as two live-moving dots over the
// surgery video playing again on DivergenceReplayScreen (a third, independent playback - separate
// from the trainee's original live session on SurgeryVideoScreen). Unlike ComparisonLoader/
// GazeReviewLoader, which paint a completed session into a static texture, this animates frame by
// frame in sync with THIS screen's own video, so a viewer can see not just where each person
// looked overall but how their attention moved together (or apart) moment to moment. Also flags
// the one second where their gaze positions were furthest apart with a highlight ring.
public class DivergenceReplayScreenOverlay : MonoBehaviour
{
    // The compositor layer on this same object - fed the dots texture every frame once replaying.
    [SerializeField] private SurgeryHeatmapOverlayLayer overlayLayer;

    [SerializeField] private int textureSize = 512;

    // Matches ComparisonLoader's specialistColor/traineeColor exactly, so this screen reads as
    // the same visual language as ReportScreen's legend.
    [SerializeField] private Color specialistDotColor = new Color(0f, 0.6f, 0f);   // dark green
    [SerializeField] private Color traineeDotColor = new Color(0.85f, 0.65f, 0f);  // gold

    [SerializeField] private int dotRadiusPixels = 12;

    [SerializeField] private float tailDurationSeconds = 1f; // how far back in time the tail reaches
    [SerializeField] private int tailPointCount = 8;  //dots per tail - denser = smoother
    [SerializeField] private int tailMinRadiusPixels = 4; //size of the oldest (tail-end) dot

    // The one second where their gaze positions differed most gets a ring around both dots
    // instead of/alongside the plain fill - distinct from the dot colors so it reads as "notable
    // moment" rather than a third data series. Red matches ComparisonLoader's own
    // peakDivergenceColor (its dot marker on ReportScreen's combined heatmap) and the slider's
    // PeakDivergenceMarker segment - same "flagged moment" color across all three screens.
    [SerializeField] private Color peakHighlightColor = Color.red;
    [SerializeField] private int peakRingRadiusPixels = 20;

    // The trimmed clip's length - also how far Update() counts before stopping.
    [SerializeField] private int videoLengthSeconds = 20;

    // DivergenceReplayScreen's own SurgeryVideoOverlayPlayer - the dot timer waits for its
    // PlaybackStarted event instead of starting from this script's own Start(), 

    [SerializeField] private SurgeryVideoOverlayPlayer videoPlayer;
    [SerializeField] private Slider replayProgressSlider;
    [SerializeField] private TMP_Text replayTimeText;



    [Serializable]
    private class GazeSample
    {
        public float u;
        public float v;
        public float radius;
        public float time;
    }

    [Serializable]
    private class SavedRecording
    {
        public List<GazeSample> samples = new List<GazeSample>();
    }

    private List<GazeSample> specialistSamples;
    private List<GazeSample> traineeSamples;
    private float replayStartTime;
    private bool isReplaying;
    private Texture2D dotsTexture;
    private Color[] dotsPixels;

    [Serializable]
    private class VideoPhase
    {
        public string name;
        public float startSecond;
        public float endSecond;

    // Filled in once by FindPeakDivergenceInRange() in Start() - not serialized, computed fresh every load from the actual recorded samples, same as the old single peakDivergenceSecond was.
    [NonSerialized] public int peakDivergenceSecond = -1;

    [NonSerialized] public Vector2 peakSpecialistUV;
    [NonSerialized] public Vector2 peakTraineeUV;

    }

    // Editable in the Inspector rather than hardcorded, same reasoning as CalibrationManager.calibrationPointLocalOffsets, can be retimed here without touching code if the video ever changes.
    [SerializeField]
    private VideoPhase[] videoPhases = new VideoPhase[]
    {
        new VideoPhase { name = "Intro", startSecond = 0f, endSecond = 06f},
        new VideoPhase {name = "Applying Seal", startSecond = 6f, endSecond = 14f},
        new VideoPhase {name = "Outro", startSecond = 14f, endSecond = 20f},
    };

    // one marker per phase, smae anchorig/ sizing approach as the old sigle peakDivergence Marker
    // used - assign 3 slider-marker RectTransforms here, in the same order as videoPhases above.
    [SerializeField] private RectTransform[] phaseDivergenceMarkers;

    // No more "wait for the main video, stay hidden until ready" dance - that only existed to
    // survive coexisting mid-session with the live surgery video in EyeTrackingDemo. This screen
    // now lives in its own scene, loaded only after the trainee's session (and its recording -
    // see MeshGazeHeatmap.StampAt()'s autoStopAfterSeconds handling) has already fully finished,
    // so it can just load and play immediately like any normal screen.
    private void Start()
    {
        Debug.Log($"[DivergenceReplayScreenOverlay] Start() at Time.time={Time.time:F2}");

        specialistSamples = ReadSamples(Path.Combine(GetRecordingsDir(), "specialist_reference.json"));
        string traineePath = ResolveMostRecentTraineePath();
        traineeSamples = traineePath != null ? ReadSamples(traineePath) : null;

        specialistSamples?.Sort((a,b) => a.time.CompareTo(b.time));
        traineeSamples?.Sort((a,b) => a.time.CompareTo(b.time));

        // Requires both sides loaded, this must run after both lists above are assigned -
        // compares actual gaze POSITION per second, matching what the two dots on screen show.
        // One call per phase, so every phase gets its own flagged moment instead of one spike
        // anywhere in the video dominating all the others.
        foreach (VideoPhase phase in videoPhases)
        {
            FindPeakDivergenceInRange(phase);
        }

        if (replayProgressSlider != null)
        {
            replayProgressSlider.minValue = 0f;
            replayProgressSlider.maxValue = videoLengthSeconds;
        }

        if (replayProgressSlider != null && phaseDivergenceMarkers != null)
        {
            // Assumes each marker's RectTransform is anchored to the left edge of the same bar
            // the slider fill, with its pivot at (0,0.5) - same assumption the old single marker made.
            float barWidth = replayProgressSlider.GetComponent<RectTransform>().rect.width;
            float secondWidth = barWidth / videoLengthSeconds;

            for (int i = 0; i < videoPhases.Length && i < phaseDivergenceMarkers.Length; i++)
            {
                VideoPhase phase = videoPhases[i];
                RectTransform marker = phaseDivergenceMarkers[i];
                if (marker == null || phase.peakDivergenceSecond < 0) continue;

                float startX = phase.peakDivergenceSecond * secondWidth;
                marker.anchoredPosition = new Vector2(startX, marker.anchoredPosition.y);
                marker.sizeDelta = new Vector2(secondWidth, marker.sizeDelta.y);
            }
        }

        Debug.Log($"[DivergenceReplayScreenOverlay] Loaded - specialist={specialistSamples?.Count ?? 0} samples, trainee={traineeSamples?.Count ?? 0} samples, phases=[{string.Join(", ", videoPhases.Select(p => $"{p.name}:{p.peakDivergenceSecond}"))}].");

        dotsTexture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
        dotsPixels = new Color[textureSize * textureSize];

        if (videoPlayer != null)
        {
            // plays immediately if the video already started  
            
            videoPlayer.SubscribeOrFireImmediately(OnVideoPlaybackStarted);
        }
        else
        {
            BeginReplayTiming();
        }
    }

    private void OnVideoPlaybackStarted()
    {
        BeginReplayTiming();
    }

    private void BeginReplayTiming()
    {
        replayStartTime = Time.time;
        isReplaying = true;
        Debug.Log($"[DivergenceReplayScreenOverlay] Replay timer begins at Time.time={Time.time:F2}");
    }

    // Called when the user drags and releases the replay slider - jumps both the actual video (via videoPlayer.SeekTo) the script to the new position, so the video and the dots agree with each other immediately afterward instead of the dots silently continuing from the old position

    public void SeekToSecond(float seconds){
        // Temporary - confirms this method actually runs and reaches videoPlayer.SeekTo(). Remove
        // once the chain is confirmed working.
        Debug.Log($"[DivergenceReplayScreenOverlay] SeekToSecond({seconds:F2}) called, videoPlayer={(videoPlayer != null ? "wired" : "NULL")}.");

        if(videoPlayer != null){
            videoPlayer.SeekTo(seconds);
        }

        // Update() computes elapsed as (Time.time - replayStartTime) * PlaybackSpeed. Solving that equation backwards for replayStartTime, given we want elapsed to equal 'seconds' starting right now, gives this line the same formula BeginReplayTiming() effectively uses (where the target was 0 instead of arbitrary seconds value).
        float speed = videoPlayer != null ? videoPlayer.PlaybackSpeed : 1f;
        replayStartTime = Time.time - (seconds / speed);

        // In case the replay had already finished (isReplaying was false) before you dragged back into range - without this, Update() would still just do nothing even after a valid seek.
        isReplaying = true;
    }

    private void OnDestroy()
    {
        if (videoPlayer != null)
        {
            videoPlayer.PlaybackStarted -= OnVideoPlaybackStarted;
        }
    }

    private void Update()
    {
        if (!isReplaying) return;


        float elapsed = (Time.time - replayStartTime) * videoPlayer.PlaybackSpeed;
        if(replayProgressSlider!= null){
            // CustomSliderDragHandler.LateUpdate() re-applies the drag position after this, every
            // frame, while a hand is actively dragging - so this can write unconditionally here
            // without needing to coordinate with it directly.
            replayProgressSlider.value = elapsed;
        }

        if(replayTimeText != null){
            replayTimeText.text = $"{FormatTime(elapsed)} / {FormatTime(videoLengthSeconds)}";
        }


        if (elapsed > videoLengthSeconds)
        {
            isReplaying = false; // stops updating - texture just holds its last frame
            return;

        }

        ClearPixels();

        // A null sample means that person wasn't looking at the video around this exact moment -
        // no dot drawn that frame rather than showing a stale/wrong position.
        DrawTail(specialistSamples, elapsed, specialistDotColor);
        DrawTail(traineeSamples, elapsed, traineeDotColor);

        // apply the ring to whichever phase's flagged second is currently playing - Mathf.FloorToInt
        // matches the same whole-second bucketing FindPeakDivergenceInRange used to find it.
        foreach (VideoPhase phase in videoPhases)
        {
            if (phase.peakDivergenceSecond >= 0 && Mathf.FloorToInt(elapsed) == phase.peakDivergenceSecond)
            {
                DrawRing(phase.peakSpecialistUV.x, phase.peakSpecialistUV.y, peakHighlightColor);
                DrawRing(phase.peakTraineeUV.x, phase.peakTraineeUV.y, peakHighlightColor);
                break; // only one phase's flagged second can be playing at a time
            }
        }

        dotsTexture.SetPixels(dotsPixels);
        dotsTexture.Apply();
        if (overlayLayer != null)
        {
            overlayLayer.SetHeatTexture(dotsTexture);
        }
    }

    // time in secs to show on slider

    private static string FormatTime(float seconds){
        int totalSeconds = Mathf.FloorToInt (seconds);
        int minutes = totalSeconds / 60;
        int secs = totalSeconds %60;
      
        return $"{minutes}:{secs:D2}";
    }

    private void ClearPixels()
    {
        for (int i = 0; i < dotsPixels.Length; i++)
        {
            dotsPixels[i] = Color.clear;
        }
    }

    // Nearest-sample lookup rather than an exact time match - real samples never land exactly on
    // a frame boundary. Re-sorts the whole list every call, which is wasteful at ~72 samples/sec
    // called every Update() - fine for a 20s replay, but the first place to optimize (e.g. track
    // a per-person index and only scan forward from it) if this causes frame drops on-device.
    private GazeSample FindSampleNearTime(List<GazeSample> samples, float time)
    {
        if (samples == null || samples.Count == 0) return null;

        GazeSample nearest = samples.OrderBy(s => Mathf.Abs(s.time - time)).First();
        const float tolerance = 0.15f; // outside this window, treat it as "no data" rather than stale
        return Mathf.Abs(nearest.time - time) <= tolerance ? nearest : null;
    }



    private GazeSample FindInterpolatedSample(List<GazeSample> samples,float time )
    {
        if (samples == null || samples.Count == 0) return null;

        GazeSample before = null;
        GazeSample after = null;

        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].time <= time) before = samples[i];
            if (samples[i].time > time) {after = samples[i]; break;}
        }

        const float tolerance = 0.15f;

        if (before == null && after == null) return null;
        if (before == null) return Mathf.Abs(after.time - time) <= tolerance ? after: null;
        if (after == null) return Mathf.Abs(before.time - time) <= tolerance ? before : null;

        float span = after.time - before.time;
        if (span <= 0f) return before;

        if (span > tolerance * 2f){
            if (time - before.time <= tolerance) return before;
            if (after.time - time <= tolerance) return after;
            return null;
        }

        float lerp = (time - before.time) / span;
        return new GazeSample{
            u = Mathf.Lerp(before.u, after.u, lerp),
            v = Mathf.Lerp(before.v, after.v, lerp),
            radius = Mathf.Lerp(before.radius, after.radius, lerp),
            time = time
        };


    }

    // Calculate the difference per second where the specialist's and trainee's gaze positions were furthest
    // apart (both must have a sample near that second - can't compare a gap against a value).
    // private int FindPeakDivergenceSecond()
    // {
    //     // they both start at -1 because nothing is found yet, distance is always >= 0
    //     int bestSecond = -1; // method value to return global maximum - highest peak
    //     float bestDistance = -1f; // method to calculate difference between the trainee and specialist dots

    //     for (int second = 0; second < videoLengthSeconds; second++)
    //     {
    //         GazeSample specialistAt = FindSampleNearTime(specialistSamples, second);
    //         GazeSample traineeAt = FindSampleNearTime(traineeSamples, second);
    //         if (specialistAt == null || traineeAt == null) continue;

    //         float distance = Vector2.Distance(new Vector2(specialistAt.u, specialistAt.v), new Vector2(traineeAt.u, traineeAt.v));
    //         if (distance > bestDistance)
    //         {
    //             bestDistance = distance;
    //             bestSecond = second;

    //             // cache the exact positions that produced this distance - Update() draws the ring here for the rest of the method's lifetime, not at whatever gaze position is live when the flagged second is actually playing back. 
    //             peakSpecialistUV = new Vector2(specialistAt.u, specialistAt.v);
    //             peakTraineeUV = new Vector2(traineeAt.u, traineeAt.v);

    //         }
    //     }

    //     return bestSecond;
    // }

    // Same distance-based comparison as before, restricted to one phase's own time range - called
// once per phase instead of once for the whole video, so every phase gets its own flagged
// moment instead of whichever single spike is biggest across all 20 seconds dominating the rest.
private void FindPeakDivergenceInRange(VideoPhase phase)
{
    int bestSecond = -1;
    float bestDistance = -1f;

    int startWhole = Mathf.FloorToInt(phase.startSecond);
    int endWhole = Mathf.CeilToInt(phase.endSecond);

    for (int second = startWhole; second < endWhole; second++)
    {
        GazeSample specialistAt = FindSampleNearTime(specialistSamples, second);
        GazeSample traineeAt = FindSampleNearTime(traineeSamples, second);
        if (specialistAt == null || traineeAt == null) continue;

        float distance = Vector2.Distance(new Vector2(specialistAt.u, specialistAt.v), new Vector2(traineeAt.u, traineeAt.v));
        if (distance > bestDistance)
        {
            bestDistance = distance;
            bestSecond = second;
            phase.peakSpecialistUV = new Vector2(specialistAt.u, specialistAt.v);
            phase.peakTraineeUV = new Vector2(traineeAt.u, traineeAt.v);
        }
    }

    phase.peakDivergenceSecond = bestSecond;
}

    private void DrawDot(float u, float v, Color color, int radius)
    {
        int x = Mathf.RoundToInt(Mathf.Clamp01(u) * (textureSize - 1));
        int y = Mathf.RoundToInt(Mathf.Clamp01(v) * (textureSize - 1));

        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > radius * radius) continue;
                SetPixelSafe(x + dx, y + dy, color);
            }
        }
    }

    // stamps DrawDot repeatedly  along the segment from uv1 to uv2, close enough together that the stamps overlap and read as one continous stroke rather than separate dots . Both color and radius are interpolated along the segment , so a tail built from several of these reads as one smoothly tapering thread rather thana chain of same-size beads.
    private void DrawLine(Vector2 uv1, Vector2 uv2, Color color1, Color color2, int radius1, int radius2){
        Vector2 p1 = uv1 * (textureSize -1);
        Vector2 p2 = uv2 * (textureSize -1);
        float distancePixels = Vector2.Distance(p1, p2);

        int stepRadius = Mathf.Max(1, Mathf.Min(radius1, radius2));
        int steps = Mathf.Max(1, Mathf.CeilToInt(distancePixels / stepRadius));

        for (int s = 0; s <= steps; s++){
            float lerp = (float)s / steps;
            Vector2 uv = Vector2.Lerp(uv1, uv2, lerp);
            Color color = Color.Lerp(color1, color2, lerp);
            int radius = Mathf.RoundToInt(Mathf.Lerp(radius1, radius2, lerp));
            DrawDot(uv.x, uv.y, color, radius);
        }
    }

    // Draws a fading tapering trail of past positions leading up to the current one, instead of a single static dot - reads back into the already-loaded sample list at several earlier timestamps rather than tracking any new runtime state, so it stays correct even after seeking.
    private void DrawTail(List<GazeSample> samples, float elapsed, Color baseColor){
        Vector2? prevUV = null;
        int prevRadius = tailMinRadiusPixels;
        Color prevColor = baseColor;

        for (int i = 0; i < tailPointCount; i++){
            float age = tailPointCount > 1 ? (float)i / (tailPointCount - 1): 1f; // 0 = oldest, 1 = current
            float t = elapsed - (1f - age) * tailDurationSeconds;
            if(t < 0f) continue;

            GazeSample sample = FindInterpolatedSample(samples,t);
            if(sample == null) { prevUV = null; continue;}

            int radius = Mathf.RoundToInt(Mathf.Lerp(tailMinRadiusPixels, dotRadiusPixels, age));
            Color faded = new Color(baseColor.r, baseColor.g, baseColor.b, Mathf.Lerp(0.15f, baseColor.a, age));
            Vector2 uv = new Vector2(sample.u, sample.v);

            if(prevUV.HasValue)
                DrawLine(prevUV.Value, uv, prevColor, faded, prevRadius, radius);
            else
                DrawDot(uv.x, uv.y, faded, radius);
            
            prevUV = uv;
            prevRadius = radius;
            prevColor = faded;

        }
    }

    // Same center math as DrawDot, but only keeps a band between innerRadius and
    // peakRingRadiusPixels - an unfilled ring instead of a filled circle, so it reads as a
    // highlight around the dot rather than a third, larger dot.
    private void DrawRing(float u, float v, Color color)
    {
        int x = Mathf.RoundToInt(Mathf.Clamp01(u) * (textureSize - 1));
        int y = Mathf.RoundToInt(Mathf.Clamp01(v) * (textureSize - 1));
        int outerRadius = peakRingRadiusPixels;
        int innerRadius = peakRingRadiusPixels - 3;

        for (int dy = -outerRadius; dy <= outerRadius; dy++)
        {
            for (int dx = -outerRadius; dx <= outerRadius; dx++)
            {
                int distSq = dx * dx + dy * dy;
                if (distSq > outerRadius * outerRadius || distSq < innerRadius * innerRadius) continue;
                SetPixelSafe(x + dx, y + dy, color);
            }
        }
    }

    private void SetPixelSafe(int x, int y, Color color)
    {
        if (x < 0 || x >= textureSize || y < 0 || y >= textureSize) return;
        dotsPixels[y * textureSize + x] = color;
    }

    // ComparisonLoader/GazeReviewLoader, same resolution logic - most recently written file in
    // the recordings folder, excluding the fixed specialist reference.
    private string ResolveMostRecentTraineePath()
    {
        string dir = GetRecordingsDir();
        if (!Directory.Exists(dir)) return null;

        return Directory.GetFiles(dir, "*.json")
            .Where(f => !Path.GetFileName(f).Equals("specialist_reference.json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private List<GazeSample> ReadSamples(string path)
    {
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[DivergenceReplayScreenOverlay] No file found at '{path}'.");
            return null;
        }

        SavedRecording recording = JsonUtility.FromJson<SavedRecording>(File.ReadAllText(path));
        return recording.samples;
    }

    private static string GetRecordingsDir()
    {
        return Path.Combine(Application.persistentDataPath, "GazeRecordings");
    }
}
