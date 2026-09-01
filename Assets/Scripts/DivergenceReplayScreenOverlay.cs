using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// Replays the specialist's and trainee's already-recorded gaze as two live-moving dots over the
// surgery video playing again on DivergenceReplayScreen (a third, independent playback - separate
// from the trainee's original live session on SurgeryVideoScreen). Unlike ComparisonLoader/
// GazeReviewLoader, which paint a completed session into a static texture, this animates frame by
// frame in sync with THIS screen's own video, so a viewer can see not just where each person
// looked overall but how their attention moved together (or apart) moment to moment. Also flags
// the one second where their gaze positions were furthest apart with a highlight ring.
public class DivergenceReplayScreenOverlay : MonoBehaviour
{
    // This object's own Mesh Renderer - toggled to hide/reveal the visual. Deliberately NOT
    // gameObject.SetActive() on this object: Start() below schedules Invoke(BeginReplay) on
    // itself, and Unity suspends Invoke/Update on an inactive GameObject - deactivating this
    // object would mean that Invoke never fires, and nothing would ever reactivate it again.
    // Toggling just the renderer hides the same visual without stopping this script from running.
    [SerializeField] private Renderer selfRenderer;

    // DivergenceReplayScreen (the video mesh itself) - a different GameObject, so SetActive is
    // safe here; nothing on THIS script depends on IT staying active.
    [SerializeField] private GameObject videoScreenObject;

    // DivergenceReplayScreen's own SurgeryVideoOverlayPlayer (autoPlayOnStart = false on that
    // component) - triggered via BeginPlayback() once the replay is actually ready to start.
    [SerializeField] private SurgeryVideoOverlayPlayer videoPlayer;

    // SurgeryVideoScreen's player - this screen's replay is timed off the ORIGINAL session
    // starting, same pattern as ComparisonLoader/GazeReviewLoader, so the trainee's recording has
    // actually finished saving by the time this tries to read it.
    [SerializeField] private SurgeryVideoOverlayPlayer mainVideoPlayer;

    // The compositor layer on this same object - fed the dots texture every frame once replaying.
    [SerializeField] private SurgeryHeatmapOverlayLayer overlayLayer;

    [SerializeField] private int textureSize = 512;

    // Matches ComparisonLoader's specialistColor/traineeColor exactly, so this screen reads as
    // the same visual language as ReportScreen's legend.
    [SerializeField] private Color specialistDotColor = new Color(0f, 0.6f, 0f);   // dark green
    [SerializeField] private Color traineeDotColor = new Color(0.85f, 0.65f, 0f);  // gold

    [SerializeField] private int dotRadiusPixels = 12;

    // The one second where their gaze positions differed most gets a ring around both dots
    // instead of/alongside the plain fill - distinct from the dot colors so it reads as "notable
    // moment" rather than a third data series.
    [SerializeField] private Color peakHighlightColor = Color.white;
    [SerializeField] private int peakRingRadiusPixels = 20;

    // The trimmed clip's length - also how far Update() counts before stopping.
    [SerializeField] private int videoLengthSeconds = 20;

    // Same tier as GazeReviewLoader's delay, not ComparisonLoader's - this only reads files, and
    // must finish before ComparisonLoader's own longer delay deletes the trainee recording.
    [SerializeField] private float initialLoadDelaySeconds = 22f;

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

    // Computed once in BeginReplay(), read every frame in Update() - -1 means "never computed"
    // (e.g. one side had no data at all), in which case no ring is ever drawn.
    private int peakDivergenceSecond = -1;

    private void Start()
    {
        // Hidden until the replay actually begins - a blank video sitting visible in the room
        // for the first ~22s would look broken, not intentional. See selfRenderer's comment
        // above for why this hides the renderer, not the whole GameObject.
        if (selfRenderer != null)
        {
            selfRenderer.enabled = false;
        }
        if (videoScreenObject != null)
        {
            videoScreenObject.SetActive(false);
        }

        // Specialist has no trainee data to replay against yet - stays hidden permanently, same
        // reasoning as ComparisonLoader/GazeReviewLoader.
        if (SessionRoleManager.IsSpecialist)
        {
            return;
        }

        if (mainVideoPlayer != null)
        {
            // Fires immediately if the main video already started by the time we get here, or
            // subscribes and waits otherwise - see SurgeryVideoOverlayPlayer.SubscribeOrFireImmediately.
            mainVideoPlayer.SubscribeOrFireImmediately(OnMainVideoStarted);
        }
        else
        {
            // No main-video reference - fall back to the less precise Start()-based timing.
            Invoke(nameof(BeginReplay), initialLoadDelaySeconds);
        }
    }

    private void OnMainVideoStarted()
    {
        Invoke(nameof(BeginReplay), initialLoadDelaySeconds);
    }

    private void OnDestroy()
    {
        if (mainVideoPlayer != null)
        {
            mainVideoPlayer.PlaybackStarted -= OnMainVideoStarted;
        }
    }

    private void BeginReplay()
    {
        specialistSamples = ReadSamples(Path.Combine(GetRecordingsDir(), "specialist_reference.json"));
        string traineePath = ResolveMostRecentTraineePath();
        traineeSamples = traineePath != null ? ReadSamples(traineePath) : null;

        // Requires both sides loaded and sampled per-second, so this must run after both lists
        // above are assigned - a genuinely different metric from ComparisonLoader's peak (which
        // compares sample COUNT per second); this compares actual gaze POSITION, matching what
        // the two dots on screen are already showing.
        peakDivergenceSecond = FindPeakDivergenceSecond();

        if (selfRenderer != null)
        {
            selfRenderer.enabled = true;
        }
        if (videoScreenObject != null)
        {
            videoScreenObject.SetActive(true);
        }

        if (videoPlayer != null)
        {
            videoPlayer.BeginPlayback();
        }

        dotsTexture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
        dotsPixels = new Color[textureSize * textureSize];

        // Measured from THIS screen's own video starting (via BeginPlayback above), not the
        // original session - that's what keeps the dots in sync with what's actually playing here.
        replayStartTime = Time.time;
        isReplaying = true;
    }

    private void Update()
    {
        if (!isReplaying) return;

        float elapsed = Time.time - replayStartTime;
        if (elapsed > videoLengthSeconds)
        {
            isReplaying = false; // stops updating - texture just holds its last frame
            return;
        }

        ClearPixels();

        // A null sample means that person wasn't looking at the video around this exact moment -
        // no dot drawn that frame rather than showing a stale/wrong position.
        GazeSample specialistNow = FindSampleNearTime(specialistSamples, elapsed);
        if (specialistNow != null)
        {
            DrawDot(specialistNow.u, specialistNow.v, specialistDotColor);
        }

        GazeSample traineeNow = FindSampleNearTime(traineeSamples, elapsed);
        if (traineeNow != null)
        {
            DrawDot(traineeNow.u, traineeNow.v, traineeDotColor);
        }

        // Ring only appears during the one second flagged as the peak - Mathf.FloorToInt matches
        // the same whole-second bucketing FindPeakDivergenceSecond used to find it.
        if (peakDivergenceSecond >= 0 && Mathf.FloorToInt(elapsed) == peakDivergenceSecond)
        {
            if (specialistNow != null) DrawRing(specialistNow.u, specialistNow.v, peakHighlightColor);
            if (traineeNow != null) DrawRing(traineeNow.u, traineeNow.v, peakHighlightColor);
        }

        dotsTexture.SetPixels(dotsPixels);
        dotsTexture.Apply();
        if (overlayLayer != null)
        {
            overlayLayer.SetHeatTexture(dotsTexture);
        }
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

    // Finds the whole second where the specialist's and trainee's gaze positions were furthest
    // apart (both must have a sample near that second - can't compare a gap against a value).
    private int FindPeakDivergenceSecond()
    {
        int bestSecond = -1;
        float bestDistance = -1f;

        for (int second = 0; second < videoLengthSeconds; second++)
        {
            GazeSample specialistAt = FindSampleNearTime(specialistSamples, second);
            GazeSample traineeAt = FindSampleNearTime(traineeSamples, second);
            if (specialistAt == null || traineeAt == null) continue;

            float distance = Vector2.Distance(new Vector2(specialistAt.u, specialistAt.v), new Vector2(traineeAt.u, traineeAt.v));
            if (distance > bestDistance)
            {
                bestDistance = distance;
                bestSecond = second;
            }
        }

        return bestSecond;
    }

    private void DrawDot(float u, float v, Color color)
    {
        int x = Mathf.RoundToInt(Mathf.Clamp01(u) * (textureSize - 1));
        int y = Mathf.RoundToInt(Mathf.Clamp01(v) * (textureSize - 1));

        for (int dy = -dotRadiusPixels; dy <= dotRadiusPixels; dy++)
        {
            for (int dx = -dotRadiusPixels; dx <= dotRadiusPixels; dx++)
            {
                if (dx * dx + dy * dy > dotRadiusPixels * dotRadiusPixels) continue;
                SetPixelSafe(x + dx, y + dy, color);
            }
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

    // Same resolution logic as ComparisonLoader/GazeReviewLoader - most recently written file in
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
