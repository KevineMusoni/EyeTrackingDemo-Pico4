using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// Loads two saved gaze recordings - the constant specialist_reference.json and the trainee's own
// latest session - and paints them into a MeshGazeHeatmap's two comparison buffers (see
// MeshGazeHeatmap.PaintAtColor/CombineComparisonBuffers) in distinct fixed colors, so both
// show overlaid on one image instead of two separate heatmaps. Also finds the single second of
// the video where the specialist's attention diverged most from the trainee's (specialist
// looking somewhere the trainee mostly didn't) and marks that spot in red on the combined image -
// "when" comes from each sample's recorded time, "where" comes from the specialist's gaze
// location during that second.
public class ComparisonLoader : MonoBehaviour
{
    [SerializeField] private MeshGazeHeatmap heatmap;

    // Fully saturated and picked to stay readable against the room's blue/purple walls (cyan,
    // tried first, sat too close to the wall color to read as its own heat color).
    [SerializeField] private Color specialistColor = new Color(0f, 0.6f, 0f); // dark green
    [SerializeField] private Color traineeColor = new Color(0.85f, 0.65f, 0f); // gold/dark yellow

    [Header("Peak Divergence Marker")]
    // Deliberately distinct from both heat colors above, so it reads as "flagged moment" rather
    // than "more heat here."
    [SerializeField] private Color peakDivergenceColor = Color.red;

    // Fixed size in texture pixels, not tied to brushRadiusWorldMeters like the live gaze brush -
    // this is a UI annotation ("look here"), not a measurement of real-world gaze precision.
    // Smaller than a first pass at 24px - a precise point is easier to read as "the specific
    // spot that diverged" than a large circle, which starts looking like just more heat.
    [SerializeField] private float peakMarkerRadiusPixels = 10f;

    // How much heat replayed sample adds - see GazeReviewLoader.amountPerSample for why
    // this is a fixed per-sample value rather than live Time.deltaTime.
    [SerializeField] private float amountPerSample = 0.014f;

    // Fixed size in texture pixels, not tied to whichever screen originally recorded the data -
    // same reasoning as peakMarkerRadiusPixels below. Recorded sample.radius reflects the source
    // screen's OWN brushRadiusWorldMeters (e.g. ReticleDemoVideoScreen's small live-reticle
    // brush), which stays whatever it needs to be for that screen's own live look and isn't
    // meant to double as "how blended this comparison heatmap should look." Using a fixed replay
    // radius here instead means ReportScreen always presents as a proper blended heatmap -
    // overlapping stamps building up alpha (duration) into a smooth gradient - regardless of
    // which screen's recording it's replaying.
    [SerializeField] private float replayRadiusPixels = 20f;

    // Same reasoning as GazeReviewLoader.initialLoadDelaySeconds - the trainee's own session
    // needs time to actually exist on disk before this can load it. Deliberately 1s LATER than
    // GazeReviewLoader's default (22f): both loaders auto-resolve the same trainee file, but only
    // this one deletes it (see LoadMostRecentTrainee below), so it must always run second - if
    // this fired first or at the same time, GazeReviewLoader could find the file already gone.
    [SerializeField] private float initialLoadDelaySeconds = 23f;

    // Same reasoning as GazeReviewLoader.videoPlayer - when assigned, the delay above counts
    // from the video's actual PlaybackStarted event instead of this object's own Start(), since
    // those aren't the same moment. Left unassigned falls back to the original Start()-based
    // timing.
    [SerializeField] private SurgeryVideoOverlayPlayer videoPlayer;

    // The legend swatches/labels sit next to ReportScreen as separate GameObjects, not children
    // of it - so hiding ReportScreen for a specialist (see Start() below) doesn't hide these too.
    // Drag ReportScreen_LegendSwatch_Specialist, _Trainee, and both legend label objects in here
    // so they get hidden alongside the screen itself.
    [SerializeField] private GameObject[] legendObjectsToHideForSpecialist;

    // Fired once, right after LoadAndDisplay() finishes - the signal that ReportScreen actually
    // has data on it now. ViewVisualisationButton waits for this instead of showing immediately,
    // so the trainee can't reach a visualization screen before there's anything real to visualize.
    public event Action LoadCompleted;
    private bool loadCompleted;

    // Same reasoning as SurgeryVideoOverlayPlayer.SubscribeOrFireImmediately - fires the callback
    // right away if loading already finished by the time a listener asks, instead of subscribing
    // to an event that already happened and waiting forever.
    public void SubscribeOrFireImmediately(Action callback)
    {
        if (loadCompleted)
        {
            callback();
        }
        else
        {
            LoadCompleted += callback;
        }
    }

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

    private void Start()
    {
        // A specialist session has no trainee data yet to compare against, so this screen is
        // hidden entirely for them (not just left blank) - specialist view should show only the
        // main surgery video screen, the one thing actually relevant to recording the reference.
        if (SessionRoleManager.IsSpecialist)
        {
            if (heatmap != null && heatmap.OverlayGameObject != null)
            {
                heatmap.OverlayGameObject.SetActive(false);
            }

            if (legendObjectsToHideForSpecialist != null)
            {
                foreach (GameObject legendObject in legendObjectsToHideForSpecialist)
                {
                    if (legendObject != null)
                    {
                        legendObject.SetActive(false);
                    }
                }
            }

            gameObject.SetActive(false);
            return;
        }

        if (videoPlayer != null)
        {
            videoPlayer.SubscribeOrFireImmediately(OnVideoPlaybackStarted);
        }
        else
        {
            Invoke(nameof(LoadAndDisplay), initialLoadDelaySeconds);
        }
    }

    private void OnVideoPlaybackStarted()
    {
        Invoke(nameof(LoadAndDisplay), initialLoadDelaySeconds);
    }

    private void OnDestroy()
    {
        if (videoPlayer != null)
        {
            videoPlayer.PlaybackStarted -= OnVideoPlaybackStarted;
        }
    }

    private void LoadAndDisplay()
    {
        if (heatmap == null)
        {
            Debug.LogError("[ComparisonLoader] No MeshGazeHeatmap assigned - nothing to paint into.");
            return;
        }

        List<GazeSample> specialistSamples = ReadSamples(Path.Combine(GetRecordingsDir(), "specialist_reference.json"));
        string traineePath = ResolveMostRecentTraineePath();
        List<GazeSample> traineeSamples = traineePath != null ? ReadSamples(traineePath) : null;

        bool loadedSpecialist = specialistSamples != null;
        bool loadedTrainee = traineeSamples != null;

        
        if (loadedSpecialist)
        {
            PaintSamples(specialistSamples, specialistColor, heatmap.HeatTexture);
        }
        if (loadedTrainee)
        {
            PaintSamples(traineeSamples, traineeColor, heatmap.ComparisonTexture);
        }

        // One combined line with both counts side by side - easier to eyeball in logcat than
        // hunting for two separate "loaded ..." lines further apart in the stream.
        Debug.Log($"[ComparisonLoader] Specialist: {(loadedSpecialist ? specialistSamples.Count.ToString() : "none")} samples | Trainee: {(loadedTrainee ? traineeSamples.Count.ToString() : "none")} samples");

        // Merges heatTexture/comparisonTexture into combinedTexture - the texture actually
        // displayed on ReportScreen. Without this, nothing painted above ever becomes visible.
        if (loadedSpecialist || loadedTrainee)
        {
            heatmap.CombineComparisonBuffers();
        }

        if (loadedSpecialist && loadedTrainee)
        {
            MarkPeakDivergence();
        }

        // No longer deleted here. This used to consume the trainee's file so a later fresh
        // session wouldn't inherit stale data - but ResolveMostRecentTraineePath() already only
        // ever picks the newest-timestamped file, so a future session naturally ignores old ones
        // without needing them deleted. Deleting here was actively breaking
        // DivergenceReplayScreenOverlay (Visualisation.unity): it reads this same file, but only
        // after the trainee clicks the View Visualization button - which only appears after
        // LoadCompleted below, by which point the file would already be gone. Leaving old
        // recordings on disk is just housekeeping, not a correctness issue - revisit with a
        // proper cleanup pass later if GazeRecordings/ ever needs pruning.

        loadCompleted = true;
        LoadCompleted?.Invoke();
    }

    // Finds the pixel where the specialist's accumulated gaze (dwell time, via alpha buildup -
    // see MeshGazeHeatmap.PaintPixels) is highest while the trainee's is lowest at that SAME
    // location - a genuine "the specialist focused here, the trainee largely didn't" spot, not
    // just a second where one side had more tracked samples than the other.
    //
    // Reads GetPixels() once each (a bulk operation, not per-pixel GetPixel calls - same
    // performance reasoning as the SetPixels/Apply pattern used everywhere else in this project)
    // since heatTexture/comparisonTexture are already fully painted by PaintSamples() above by
    // the time this runs.
    private void MarkPeakDivergence()
    {
        if (heatmap.HeatTexture == null || heatmap.ComparisonTexture == null)
        {
            return;
        }

        // PaintSamples() painted via PaintAtColorBatched (applyImmediately: false) - that only
        // writes MeshGazeHeatmap's private in-memory backing arrays, never uploads to the GPU
        // texture. Without flushing first, GetPixels() below would read stale/blank data instead
        // of what was actually painted.
        heatmap.FlushToGpu(heatmap.HeatTexture);
        heatmap.FlushToGpu(heatmap.ComparisonTexture);

        Color[] specialistPixels = heatmap.HeatTexture.GetPixels();
        Color[] traineePixels = heatmap.ComparisonTexture.GetPixels();
        int width = heatmap.HeatTexture.width;
        int height = heatmap.HeatTexture.height;

        int bestIndex = -1;
        float bestGap = 0f; // only interested in a real specialist-more-than-trainee gap, not a tie or reversal

        for (int i = 0; i < specialistPixels.Length; i++)
        {
            float gap = specialistPixels[i].a - traineePixels[i].a;
            if (gap > bestGap)
            {
                bestGap = gap;
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            Debug.Log("[ComparisonLoader] No location where specialist dwelled more than trainee - skipping peak marker.");
            return;
        }

        int x = bestIndex % width;
        int y = bestIndex / width;
        float u = (x + 0.5f) / width;
        float v = (y + 0.5f) / height;

        heatmap.PaintAtColor(new Vector2(u, v), Mathf.RoundToInt(peakMarkerRadiusPixels), 1f, peakDivergenceColor, heatmap.CombinedTexture);

        Debug.Log($"[ComparisonLoader] Peak coverage gap at ({u:F2}, {v:F2}) - specialist alpha {specialistPixels[bestIndex].a:F2} vs trainee alpha {traineePixels[bestIndex].a:F2}.");
    }

    private string ResolveMostRecentTraineePath()
    {
        string dir = GetRecordingsDir();
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.GetFiles(dir, "*.json")
            .Where(f => !Path.GetFileName(f).Equals("specialist_reference.json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private List<GazeSample> ReadSamples(string path)
    {
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[ComparisonLoader] No file found at '{path}'.");
            return null;
        }

        SavedRecording recording = JsonUtility.FromJson<SavedRecording>(File.ReadAllText(path));
        return recording.samples;
    }

    // Batched, not PaintAtColor - a full texture upload after every sample, potentially
    // thousands in one method call, was the real cost behind the loading freeze (see
    // GazeReviewLoader for the same fix). No FlushToGpu call needed here afterward: target is
    // always heatTexture or comparisonTexture, neither of which is ever directly displayed on
    // the comparison screen (only combinedTexture is) - CombineComparisonBuffers() reads these
    // backing arrays directly and does the one real upload itself.
    private void PaintSamples(List<GazeSample> samples, Color color, Texture2D target)
    {
        int radius = Mathf.RoundToInt(replayRadiusPixels);
        foreach (GazeSample sample in samples)
        {
            heatmap.PaintAtColorBatched(new Vector2(sample.u, sample.v), radius, amountPerSample, color, target);
        }
    }

    private static string GetRecordingsDir()
    {
        return Path.Combine(Application.persistentDataPath, "GazeRecordings");
    }
}
