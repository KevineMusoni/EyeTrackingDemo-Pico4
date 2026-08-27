using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// Loads two saved gaze recordings - the constant specialist_reference.json and the trainee's own
// latest session - and paints them into a MeshGazeHeatmap's two comparison buffers (see
// MeshGazeHeatmap.PaintAtColor/CombineComparisonBuffers) in distinct fixed colors, so both
// show overlaid on one image instead of two separate heatmaps.
public class ComparisonLoader : MonoBehaviour
{
    [SerializeField] private MeshGazeHeatmap heatmap;

    [SerializeField] private Color specialistColor = new Color(1f, 0.65f, 0f); // orange
    [SerializeField] private Color traineeColor = new Color(0f, 1f, 1f); // cyan

    // How much heat each replayed sample adds - see GazeReviewLoader.amountPerSample for why
    // this is a fixed per-sample value rather than live Time.deltaTime.
    [SerializeField] private float amountPerSample = 0.014f;

    // Same reasoning as GazeReviewLoader.initialLoadDelaySeconds - the trainee's own session
    // needs time to actually exist on disk before this can load it. Deliberately 1s LATER than
    // GazeReviewLoader's default (22f): both loaders auto-resolve the same trainee file, but only
    // this one deletes it (see LoadMostRecentTrainee below), so it must always run second - if
    // this fired first or at the same time, GazeReviewLoader could find the file already gone.
    [SerializeField] private float initialLoadDelaySeconds = 23f;

    [Serializable]
    private class SavedRecording
    {
        public List<Vector3> samples = new List<Vector3>();
    }

    private void Start()
    {
        // Same reasoning as GazeReviewLoader - a specialist session has no trainee data yet to
        // compare against, so this screen stays inactive for them.
        if (SessionRoleManager.IsSpecialist)
        {
            return;
        }

        Invoke(nameof(LoadAndDisplay), initialLoadDelaySeconds);
    }

    private void LoadAndDisplay()
    {
        if (heatmap == null)
        {
            Debug.LogError("[ComparisonLoader] No MeshGazeHeatmap assigned - nothing to paint into.");
            return;
        }

        bool loadedSpecialist = LoadInto(GetRecordingsDir(), "specialist_reference.json", specialistColor, heatmap.HeatTexture, deleteAfterLoad: false);
        bool loadedTrainee = LoadMostRecentTrainee();

        if (loadedSpecialist || loadedTrainee)
        {
            heatmap.CombineComparisonBuffers();
        }
    }

    private bool LoadMostRecentTrainee()
    {
        string dir = GetRecordingsDir();
        if (!Directory.Exists(dir))
        {
            return false;
        }

        string path = Directory.GetFiles(dir, "*.json")
            .Where(f => !Path.GetFileName(f).Equals("specialist_reference.json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (path == null)
        {
            Debug.LogWarning("[ComparisonLoader] No trainee recording found.");
            return false;
        }

        PaintRecording(path, traineeColor, heatmap.ComparisonTexture);
        Debug.Log($"[ComparisonLoader] Loaded trainee session from '{path}'.");

        // Consumed - shown once, on the next launch after it was saved, so a later fresh
        // session doesn't inherit stale data. This is now the ONLY place that deletes an
        // auto-resolved trainee file - GazeReviewLoader reads the same file but leaves it in
        // place, relying on initialLoadDelaySeconds above to guarantee it always runs first.
        File.Delete(path);
        Debug.Log($"[ComparisonLoader] Deleted consumed trainee recording '{path}'.");
        return true;
    }

    private bool LoadInto(string dir, string fileName, Color color, Texture2D target, bool deleteAfterLoad)
    {
        string path = Path.Combine(dir, fileName);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[ComparisonLoader] No file found at '{path}'.");
            return false;
        }

        PaintRecording(path, color, target);
        Debug.Log($"[ComparisonLoader] Loaded '{path}'.");

        if (deleteAfterLoad)
        {
            File.Delete(path);
        }
        return true;
    }

    private void PaintRecording(string path, Color color, Texture2D target)
    {
        SavedRecording recording = JsonUtility.FromJson<SavedRecording>(File.ReadAllText(path));
        foreach (Vector3 sample in recording.samples)
        {
            heatmap.PaintAtColor(new Vector2(sample.x, sample.y), Mathf.RoundToInt(sample.z), amountPerSample, color, target);
        }
    }

    private static string GetRecordingsDir()
    {
        return Path.Combine(Application.persistentDataPath, "GazeRecordings");
    }
}
