using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// Loads a saved gaze recording (written by MeshGazeHeatmap.SaveRecording()) and replays every
// sample straight into a MeshGazeHeatmap's PaintAt(), reconstructing the session's heatmap
// without needing a live RaycastHit or the video that produced the original data.
public class GazeReviewLoader : MonoBehaviour
{
    [SerializeField] private MeshGazeHeatmap heatmap;

    // Leave blank to load the most recently written recording in GazeRecordings/ - set a
    // specific filename to review a particular session instead.
    [SerializeField] private string recordingFileName;

    // How much heat each replayed sample adds - independent of live heatPerSecond/Time.deltaTime
    // (see MeshGazeHeatmap.PaintAt), since every sample here gets stamped in a tight loop rather
    // than one per frame ~1/72s worth is a stand-in for "one original frame."
    [SerializeField] private float amountPerSample = 0.014f;

    // Loading happens once, after this delay - not immediately in Start(). Start() runs at
    // frame 0, before anyone has watched anything yet this session, so an immediate load can
    // only ever show a PREVIOUS session's data (or nothing). Waiting roughly as long as the
    // watched segment takes (plus a beat past the last autosave interval) means this session's
    // own recording actually exists on disk by the time the load happens.
    [SerializeField] private float initialLoadDelaySeconds = 22f;

    [Serializable]
    private class SavedRecording
    {
        public List<Vector3> samples = new List<Vector3>();
    }

    private void Start()
    {
        Invoke(nameof(LoadAndDisplay), initialLoadDelaySeconds);
    }

    private void LoadAndDisplay()
    {
        if (heatmap == null)
        {
            Debug.LogError("[GazeReviewLoader] No MeshGazeHeatmap assigned - nothing to paint into.");
            return;
        }

        // Only auto-resolved ("most recent file") loads get consumed - an explicitly named
        // recordingFileName is treated as a reusable reference (e.g. an expert recording meant
        // to be shown across many trainee sessions) and is never deleted.
        bool wasAutoResolved = string.IsNullOrEmpty(recordingFileName);
        string path = ResolveRecordingPath();
        if (path == null)
        {
            Debug.LogWarning("[GazeReviewLoader] No recording file found.");
            return;
        }

        SavedRecording recording = JsonUtility.FromJson<SavedRecording>(File.ReadAllText(path));

        foreach (Vector3 sample in recording.samples)
        {
            heatmap.PaintAt(new Vector2(sample.x, sample.y), Mathf.RoundToInt(sample.z), amountPerSample);
        }

        Debug.Log($"[GazeReviewLoader] Replayed {recording.samples.Count} samples from '{path}'.");

        if (wasAutoResolved)
        {
            File.Delete(path);
            Debug.Log($"[GazeReviewLoader] Deleted consumed recording '{path}'.");
        }
    }

    private string ResolveRecordingPath()
    {
        string dir = Path.Combine(Application.persistentDataPath, "GazeRecordings");

        if (!string.IsNullOrEmpty(recordingFileName))
        {
            string explicitPath = Path.Combine(dir, recordingFileName);
            return File.Exists(explicitPath) ? explicitPath : null;
        }

        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.GetFiles(dir, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
