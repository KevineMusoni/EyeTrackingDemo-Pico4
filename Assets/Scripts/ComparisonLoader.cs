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

        if (loadedSpecialist || loadedTrainee)
        {
            heatmap.CombineComparisonBuffers();
        }

        // Only meaningful with both sides present - "diverged from the trainee" has no meaning
        // if there's no trainee data (or no specialist data) to diverge from.
        if (loadedSpecialist && loadedTrainee)
        {
            MarkPeakDivergence(specialistSamples, traineeSamples);
        }

        // Consumed - shown once, on the next launch after it was saved, so a later fresh session
        // doesn't inherit stale data. This is the ONLY place that deletes an auto-resolved
        // trainee file - GazeReviewLoader reads the same file but leaves it in place, relying on
        // its shorter initialLoadDelaySeconds to guarantee it always runs first.
        if (loadedTrainee)
        {
            File.Delete(traineePath);
            Debug.Log($"[ComparisonLoader] Deleted consumed trainee recording '{traineePath}'.");
        }
    }

    // Buckets both sessions' samples by whole second, finds the second where the specialist's
    // sample count minus the trainee's sample count is largest (specialist was actively looking
    // a lot in that second, trainee comparatively wasn't), then marks the specialist's average
    // gaze location during that second in red on the final combined texture.
    private void MarkPeakDivergence(List<GazeSample> specialistSamples, List<GazeSample> traineeSamples)
    {
        if (specialistSamples.Count == 0)
        {
            return;
        }

        Dictionary<int, int> traineeCountBySecond = traineeSamples
            .GroupBy(s => Mathf.FloorToInt(s.time))
            .ToDictionary(g => g.Key, g => g.Count());

        List<IGrouping<int, GazeSample>> specialistBySecond = specialistSamples
            .GroupBy(s => Mathf.FloorToInt(s.time))
            .ToList();

        int bestSecond = -1;
        int bestDivergence = int.MinValue;
        foreach (IGrouping<int, GazeSample> group in specialistBySecond)
        {
            int traineeCount = traineeCountBySecond.TryGetValue(group.Key, out int c) ? c : 0;
            int divergence = group.Count() - traineeCount;
            if (divergence > bestDivergence)
            {
                bestDivergence = divergence;
                bestSecond = group.Key;
            }
        }

        if (bestSecond < 0)
        {
            return;
        }

        List<GazeSample> peakSamples = specialistSamples.Where(s => Mathf.FloorToInt(s.time) == bestSecond).ToList();
        float avgU = peakSamples.Average(s => s.u);
        float avgV = peakSamples.Average(s => s.v);
        int traineeCountAtPeak = traineeCountBySecond.TryGetValue(bestSecond, out int tc) ? tc : 0;

        heatmap.PaintAtColor(new Vector2(avgU, avgV), Mathf.RoundToInt(peakMarkerRadiusPixels), 1f, peakDivergenceColor, heatmap.CombinedTexture);

        Debug.Log($"[ComparisonLoader] Peak divergence at second {bestSecond} (specialist {peakSamples.Count} vs trainee {traineeCountAtPeak} samples) - marked red at ({avgU:F2}, {avgV:F2}).");
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
        foreach (GazeSample sample in samples)
        {
            heatmap.PaintAtColorBatched(new Vector2(sample.u, sample.v), Mathf.RoundToInt(sample.radius), amountPerSample, color, target);
        }
    }

    private static string GetRecordingsDir()
    {
        return Path.Combine(Application.persistentDataPath, "GazeRecordings");
    }
}
