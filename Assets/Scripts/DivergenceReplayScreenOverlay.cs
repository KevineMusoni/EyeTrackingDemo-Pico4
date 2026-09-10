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
// looked overall but how their attention moved together (or apart) moment to moment. Also flags,
// per phase, the "key area" - wherever the specialist's attention concentrated most - and how
// long each person actually spent looking near that same spot.
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

    // The key area (per phase) gets a pair of concentric rings: an outer one for the specialist
    // and an inner one for the trainee. Each ring reuses that person's own dot colour
    // (specialistDotColor / traineeDotColor) so it reads as "this is where <that person> should
    // be / is" - no separate colour to learn, the ring just echoes the dot it belongs to.
    [SerializeField] private int peakRingRadiusPixels = 20;
  
    // Each phase's slider segment is coloured as a scorecard: did the trainee match the
    // specialist's dwell at that phase's key area? Matched or beat it -> pass; looked there
    // but less -> partial; never looked near it -> missed. Semi-transparent (markerAlpha) so
    // the slider fill still reads through underneath. markerGapPixels leaves a gap between
    // segments so the three read as separate blocks, not one continuous bar (the phases tile
    // the whole 0-videoLength timeline with no gaps of their own).
    [SerializeField] private Color markerPassColor = new Color(0.2f, 0.8f, 0.3f);
    [SerializeField] private Color markerPartialColor = new Color(0.9f, 0.65f, 0.1f);
    [SerializeField] private Color markerMissedColor = new Color(0.9f, 0.15f, 0.15f);
    [SerializeField] private float markerAlpha = 0.5f;
    [SerializeField] private float markerHeightPixels = 8f;
    [SerializeField] private float markerGapPixels = 6f;

    // The trimmed clip's length - also how far Update() counts before stopping.
    [SerializeField] private int videoLengthSeconds = 20;

    // DivergenceReplayScreen's own SurgeryVideoOverlayPlayer - the dot timer waits for its
    // PlaybackStarted event instead of starting from this script's own Start(),

    [SerializeField] private SurgeryVideoOverlayPlayer videoPlayer;
    [SerializeField] private Slider replayProgressSlider;
    [SerializeField] private TMP_Text replayTimeText;
    [SerializeField] private TMP_Text phaseReadoutText;


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

        // Filled in once by FindKeyAreaAndDwellTimes() in Start() - not serialized, computed
        // fresh every load from the actual recorded samples.
        [NonSerialized] public bool hasKeyArea;
        [NonSerialized] public Vector2 keyAreaUV;
        [NonSerialized] public float specialistDwellSeconds;
        [NonSerialized] public float traineeDwellSeconds;
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

    // How close a sample needs to be to the key area to count as "looking at it," in UV space.
    // Matches ComparisonLoader.replayRadiusPixels (20px) on a 512px texture, converted to UV:
    // 20/512 ≈ 0.039 - reusing the same already-proven radius, not a new guess.
    [SerializeField] private float keyAreaRadius = 0.039f;

    // How finely to bucket the specialist's samples when searching for their key area - a 20x20
    // grid over the whole 0-1 UV space. Coarser than the pixel texture (deliberately) since this
    // only needs to find roughly where attention concentrated, not a pixel-precise location.
    [SerializeField] private int keyAreaGridResolution = 20;

    // Fixed conversion from "matched sample count" to seconds, based on this headset's typical
    // ~72Hz sampling rate - NOT derived per-person from phaseDuration/totalCount. Deriving it per
    // person let missing data (blinks, tracking gaps, rejected samples) silently inflate that
    // person's dwell score: fewer total samples meant a bigger seconds-per-sample multiplier, so
    // someone barely tracked could still show up as "dwelled the whole phase." A fixed rate means
    // less data just produces a smaller, honestly-lower number instead.
    [SerializeField] private float secondsPerSample = 1f / 72f;

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

        // Requires both sides loaded, this must run after both lists above are assigned. One call
        // per phase, so every phase gets its own key area and dwell-time comparison, instead of
        // one result for the whole video.
        foreach (VideoPhase phase in videoPhases)
        {
            FindKeyAreaAndDwellTimes(phase);
        }

        // Show phase 1's result before playback starts, rather than a blank line.
        UpdatePhaseReadout(0f);

        if (replayProgressSlider != null)
        {
            replayProgressSlider.minValue = 0f;
            replayProgressSlider.maxValue = videoLengthSeconds;
        }

        if (replayProgressSlider != null && phaseDivergenceMarkers != null)
        {
            // Layout hasn't run yet this frame - force it so the RectTransform widths below
            // are the resolved on-screen sizes, not the serialized design-time values (the
            // Fill Area stretches to its parent, so its rect.width is meaningless until a
            // layout pass fills it in).
            Canvas.ForceUpdateCanvases();

            // Map the phase timeline onto the slider's Fill Area, not the slider bounds: the
            // markers are parented there and it's inset from the slider edges, so measuring
            // the slider instead would scale every segment slightly too wide and overflow the
            // last one past the track.
            float barWidth = replayProgressSlider.GetComponent<RectTransform>().rect.width;
            RectTransform markerParent = phaseDivergenceMarkers.Length > 0 && phaseDivergenceMarkers[0] != null
                ? phaseDivergenceMarkers[0].parent as RectTransform
                : null;
            if (markerParent != null)
            {
                barWidth = markerParent.rect.width;
            }
            float pixelsPerSecond = barWidth / videoLengthSeconds;

            for (int i = 0; i < videoPhases.Length && i < phaseDivergenceMarkers.Length; i++)
            {
                VideoPhase phase = videoPhases[i];
                RectTransform marker = phaseDivergenceMarkers[i];
                if (marker == null) continue;

                // A phase with no specialist data has no key area to score against - hide
                // its segment rather than leaving it at its scene-default size and colour.
                if (!phase.hasKeyArea)
                {
                    marker.gameObject.SetActive(false);
                    continue;
                }
                marker.gameObject.SetActive(true);

                // Span the phase, inset by half the gap on each side so neighbouring
                // segments don't touch. Clamp the right edge to barWidth: the phases tile
                // the full 0-videoLength range, so rounding can otherwise push the last
                // segment a pixel or two past the end of the bar.
                float startX = phase.startSecond * pixelsPerSecond + markerGapPixels * 0.5f;
                float endX = Mathf.Min(phase.endSecond * pixelsPerSecond - markerGapPixels * 0.5f, barWidth);
                float width = Mathf.Max(0f, endX - startX);
                marker.anchoredPosition = new Vector2(startX, marker.anchoredPosition.y);
                marker.sizeDelta = new Vector2(width, markerHeightPixels);

                // The colour is the feedback. Pass needs the specialist to actually have a
                // dwell time to match (specialistDwellSeconds > 0); a degenerate phase where
                // even the specialist scored zero falls through to missed/partial.
                Image markerImage = marker.GetComponent<Image>();
                if (markerImage != null)
                {
                    Color c;
                    if (phase.specialistDwellSeconds > 0f && phase.traineeDwellSeconds >= phase.specialistDwellSeconds)
                        c = markerPassColor;
                    else if (phase.traineeDwellSeconds <= 0f)
                        c = markerMissedColor;
                    else
                        c = markerPartialColor;

                    c.a = markerAlpha;
                    markerImage.color = c;
                }
            }
        }

        Debug.Log($"[DivergenceReplayScreenOverlay] Loaded - specialist={specialistSamples?.Count ?? 0} samples, trainee={traineeSamples?.Count ?? 0} samples, phases=[{string.Join(", ", videoPhases.Select(p => $"{p.name}: spec={p.specialistDwellSeconds:F1}s trainee={p.traineeDwellSeconds:F1}s"))}].");

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

        UpdatePhaseReadout(elapsed);


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

        // Show whichever phase is currently playing's key area - for its ENTIRE duration, not one
        // instant. Outer ring (specialist colour) is always full size - the key area is defined as
        // the specialist's peak. Inner ring (trainee colour) is scaled by how much of the
        // specialist's dwell time the trainee matched, clamped to 1 so a trainee who matched or
        // exceeded the specialist doesn't draw an inner ring bigger than the outer one. Matching
        // ring colour to dot colour means the trainee can tell at a glance which ring is theirs.
        foreach (VideoPhase phase in videoPhases)
        {
            if (!phase.hasKeyArea || elapsed < phase.startSecond || elapsed > phase.endSecond) continue;

            DrawRing(phase.keyAreaUV.x, phase.keyAreaUV.y, specialistDotColor, peakRingRadiusPixels);

            if (phase.specialistDwellSeconds > 0f)
            {
                float ratio = Mathf.Clamp01(phase.traineeDwellSeconds / phase.specialistDwellSeconds);
                int innerRadius = Mathf.RoundToInt(peakRingRadiusPixels * ratio);
                if (innerRadius > 0)
                {
                    DrawRing(phase.keyAreaUV.x, phase.keyAreaUV.y, traineeDotColor, innerRadius);
                }
            }
            break;
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

    // Writes the "which phase, and how did the trainee do in it" block. Called every frame from
    // Update() and once from Start() so it's populated before playback begins.
    private void UpdatePhaseReadout(float elapsed){
        if (phaseReadoutText == null || videoPhases == null || videoPhases.Length == 0) return;

        int idx = -1;
        for (int i = 0; i < videoPhases.Length; i++)
        {
            if (elapsed >= videoPhases[i].startSecond && elapsed <= videoPhases[i].endSecond)
            {
                idx = i;
                break;
            }
        }
        if (idx < 0) idx = videoPhases.Length - 1; // past the end - hold on the last phase

        VideoPhase p = videoPhases[idx];

        string header = $"<b>{p.name}</b>   <color=#FFFFFFAA>Phase {idx + 1} of {videoPhases.Length}</color>";

        string detail;
        if (!p.hasKeyArea)
        {
            detail = "<color=#FFFFFF99>no clear key area this phase</color>";
        }
        else
        {
            detail = $"<color=#00FF8F>Expert {p.specialistDwellSeconds:0.0}s</color>" +
                    $"     <color=#D9A600>You {p.traineeDwellSeconds:0.0}s</color>" +
                    $"     {PhaseVerdict(p)}";
        }

        phaseReadoutText.text = header + "\n" + detail;
    }

    // Same classification the slider scorecard uses, as a coloured word.
    private string PhaseVerdict(VideoPhase p)
    {
        if (p.specialistDwellSeconds > 0f && p.traineeDwellSeconds >= p.specialistDwellSeconds)
            return "<color=#33CC4D>Matched</color>";
        if (p.traineeDwellSeconds <= 0f)
            return "<color=#E62626>Missed</color>";
        return "<color=#FF8C00>Partial</color>";
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

    // Finds where the specialist's attention concentrated most within this phase (their "key
    // area"), then measures how long each person actually spent looking near that same spot.
    // Two-step process:
    //   1. Bucket the specialist's samples into a coarse grid, find the densest cell - that's
    //      where they focused. Cheaper than painting into a full pixel texture (like
    //      ComparisonLoader does) since this only runs once per phase over a few hundred samples,
    //      not scanning a whole 512x512 buffer - same "accumulate then find the peak" shape,
    //      lighter implementation.
    //   2. Count each person's samples within keyAreaRadius of that point, convert to seconds.
    private void FindKeyAreaAndDwellTimes(VideoPhase phase)
    {
        var cellCounts = new Dictionary<(int, int), int>();
        var cellPositionSums = new Dictionary<(int, int), Vector2>();

        foreach (GazeSample sample in specialistSamples)
        {
            if (sample.time < phase.startSecond || sample.time > phase.endSecond) continue;

            int cellX = Mathf.Clamp(Mathf.FloorToInt(sample.u * keyAreaGridResolution), 0, keyAreaGridResolution - 1);
            int cellY = Mathf.Clamp(Mathf.FloorToInt(sample.v * keyAreaGridResolution), 0, keyAreaGridResolution - 1);
            var cell = (cellX, cellY);

            cellCounts[cell] = cellCounts.TryGetValue(cell, out int count) ? count + 1 : 1;
            cellPositionSums[cell] = cellPositionSums.TryGetValue(cell, out Vector2 sum)
                ? sum + new Vector2(sample.u, sample.v)
                : new Vector2(sample.u, sample.v);
        }

        if (cellCounts.Count == 0)
        {
            phase.hasKeyArea = false;
            return;
        }

        var winningCell = cellCounts.OrderByDescending(kv => kv.Value).First();
        phase.keyAreaUV = cellPositionSums[winningCell.Key] / winningCell.Value;
        phase.hasKeyArea = true;

        phase.specialistDwellSeconds = ComputeDwellSeconds(specialistSamples, phase);
        phase.traineeDwellSeconds = ComputeDwellSeconds(traineeSamples, phase);
    }

    // Counts how many of this person's samples (within the phase's time range) fall within
    // keyAreaRadius of phase.keyAreaUV, then converts that count to seconds using the FIXED
    // secondsPerSample rate (not derived from this person's own sample count - see that field's
    // comment for why deriving it per-person would let missing data inflate the result).
    private float ComputeDwellSeconds(List<GazeSample> samples, VideoPhase phase)
    {
        if (samples == null || samples.Count == 0) return 0f;

        int matchCount = 0;

        foreach (GazeSample sample in samples)
        {
            if (sample.time < phase.startSecond || sample.time > phase.endSecond) continue;

            if (Vector2.Distance(new Vector2(sample.u, sample.v), phase.keyAreaUV) <= keyAreaRadius)
            {
                matchCount++;
            }
        }

        return matchCount * secondsPerSample;
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

    // Same center math as DrawDot, but only keeps a band between innerRadius and outerRadius - an
    // unfilled ring instead of a filled circle, so it reads as a highlight around the dot rather
    // than a third, larger dot. outerRadius is a parameter (not always peakRingRadiusPixels) so
    // the specialist's and trainee's rings at the same key area can be drawn at different sizes -
    // the size difference is the actual feedback, so both need to be independently sized.
    private void DrawRing(float u, float v, Color color, int outerRadius)
    {
        int x = Mathf.RoundToInt(Mathf.Clamp01(u) * (textureSize - 1));
        int y = Mathf.RoundToInt(Mathf.Clamp01(v) * (textureSize - 1));
        int innerRadius = Mathf.Max(0, outerRadius - 3);

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
