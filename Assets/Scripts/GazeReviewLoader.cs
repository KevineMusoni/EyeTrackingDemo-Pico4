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
        // A specialist session is purely "record the reference" - there's nothing meaningful to
        // review about their own single session, so this screen stays inactive for them.
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
            Debug.LogError("[GazeReviewLoader] No MeshGazeHeatmap assigned - nothing to paint into.");
            return;
        }

        string path = ResolveRecordingPath();
        if (path == null)
        {
            Debug.LogWarning("[GazeReviewLoader] No recording file found.");
            return;
        }

        SavedRecording recording = JsonUtility.FromJson<SavedRecording>(File.ReadAllText(path));

        foreach (GazeSample sample in recording.samples)
        {
            heatmap.PaintAt(new Vector2(sample.u, sample.v), Mathf.RoundToInt(sample.radius), amountPerSample);
        }

        Debug.Log($"[GazeReviewLoader] Replayed {recording.samples.Count} samples from '{path}'.");

        // Does not delete the auto-resolved file here anymore. ComparisonScreen's
        // ComparisonLoader also auto-resolves and reads this exact same trainee file, on a
        // slightly longer delay so it always runs after this one - if this loader deleted it
        // immediately, ComparisonLoader would find nothing and come up blank. Cleanup is
        // ComparisonLoader's job now, since it's guaranteed to be the last reader.
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

        // Excludes the constant specialist reference - without this, a trainee session that
        // never actually produces a file (e.g. the trainee never once looked at the video, so
        // nothing ever got saved) would make specialist_reference.json the most-recently-written
        // file in the folder, and this would wrongly treat it as an ordinary trainee session.
        return Directory.GetFiles(dir, "*.json")
            .Where(f => !Path.GetFileName(f).Equals("specialist_reference.json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
