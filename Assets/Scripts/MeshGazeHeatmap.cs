using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Prototype gaze heatmap for a single mesh. Accumulates "heat" into a runtime texture wherever
// the gaze ray hits this object's UV space, then displays it on a separate unlit-transparent
// overlay renderer sitting just above the original surface. Wired in from
// EyeTrackingManager.GazeTargetControl().
//
// Uses Texture2D.GetPixel/SetPixel per brush stamp - fine for a first pass at a small brush
// radius, not fast enough for production (would need SetPixels32/a compute shader for that).
[RequireComponent(typeof(MeshCollider))]
public class MeshGazeHeatmap : MonoBehaviour
{
    [Header("Heatmap Texture")]
    [SerializeField] private int textureSize = 512;

    // Real-world radius in meters, not a fixed pixel count - this is what makes the dot the
    // same PHYSICAL size on differently-sized objects, since UV space is always 0-1 regardless
    // of an object's actual size (see ComputeBrushRadiusPixels for the conversion).
    [SerializeField] private float brushRadiusWorldMeters = 0.1f;

    // Clamps on the CALCULATED pixel radius, so a degenerate/tiny UV triangle at the hit point
    // can't produce a wildly huge or zero brush.
    [SerializeField] private float minBrushRadiusPixels = 2f;
    [SerializeField] private float maxBrushRadiusPixels = 64f;

    [SerializeField] private float heatPerSecond = 1f;

    [Header("Overlay Display")]
    [SerializeField] private Renderer overlayRenderer;

    // Only set on objects whose overlay quad needs to composite over a PXR_OverLay
    // External Surface video (e.g. SurgeryVideoScreen_HeatOverlay). Left unassigned on plain test objects like MadFlower, which render normally with no compositor layer involved.
    [SerializeField] private SurgeryHeatmapOverlayLayer compositorLayer;

    // Only true on the comparison screen - allocates a second buffer (comparisonTexture,
    // alongside the normal heatTexture) so ComparisonLoader can paint the specialist reference
    // into one and the trainee's session into the other (see PaintAtColor), then combines both
    // into what's actually displayed via CombineComparisonBuffers(). Off everywhere else, since
    // a single-source object (MadFlower, SurgeryVideoScreen, GazeReviewScreen) has no second
    // source to combine.
    [SerializeField] private bool useComparisonBuffer = false;
    private Texture2D comparisonTexture;
    private Texture2D combinedTexture;

    public Texture2D HeatTexture => heatTexture;
    public Texture2D ComparisonTexture => comparisonTexture;

    // The texture actually displayed when useComparisonBuffer is true (see CombineComparisonBuffers).
    // Exposed so ComparisonLoader can paint a peak-divergence marker directly onto the final
    // combined image, after the normal specialist/trainee combine has already happened.

    // adding a timestamps to recorded samples
    public Texture2D CombinedTexture => combinedTexture;

    [Header("Recording")]
    // Off by default - only the objects wanted session data saved for (e.g.
    // SurgeryVideoScreen) should have this enabled in the Inspector.
    [SerializeField] private bool recordSamples = false;
    [SerializeField] private string recordingId = "session";

    // Testing on this headset normally ends via "adb shell am force-stop", not a graceful
    // in-app quit - OnApplicationQuit() never fires on a force-stop (it's a hard kill, Android
    // gives the app no chance to run shutdown code), so relying on it would silently lose every
    // session. Periodic autosave to a fixed per-session filename means at most a few seconds of
    // the latest data is ever at risk, regardless of how the app gets closed.
    [SerializeField] private float autosaveIntervalSeconds = 5f;
    private string recordingFilePath;

    // 0 = never stop (default - test objects like MadFlower have no video, so there's no
    // "finished" moment to stop at). Set to the watched segment's length on SurgeryVideoScreen -
    // once the video's actually over, further gaze hitting that now-static screen isn't
    // "watching the surgery," it's just noise, and shouldn't keep growing the heatmap or
    // recording.
    [SerializeField] private float autoStopAfterSeconds = 0f;
    private float startTime;
    private bool hasStopped;

    // u/v/radius are the same values StampAt() already computes, kept around so a session can be
    // saved and later replayed. time is seconds since this object's Start() - added so a saved
    // recording can be grouped by "what second of the video did this happen in," not just "where
    // on screen" (see ComparisonLoader's peak-divergence marker, the reason this was added).
    [Serializable]
    private class GazeSample
    {
        public float u;
        public float v;
        public float radius;
        public float time;
    }

    [Serializable]
    private class GazeRecording
    {
        public List<GazeSample> samples = new List<GazeSample>();
    }

    private GazeRecording recording = new GazeRecording();

    private Texture2D heatTexture;
    private Mesh colliderMesh;

    private void Start()
    {
        Debug.Log($"[MeshGazeHeatmap] Start() on '{gameObject.name}' - overlayRenderer={(overlayRenderer != null ? overlayRenderer.name : "NULL")}");

        startTime = Time.time; // secs elapsed since the object's start
        colliderMesh = GetComponent<MeshCollider>().sharedMesh;

        heatTexture = CreateBlankTexture();

        // Report screen only: a second source buffer (ComparisonLoader paints the trainee's
        // session into this one, heatTexture holds the specialist reference), plus a third
        // texture that's the actual displayed result of combining both - see
        // CombineComparisonBuffers().
        if (useComparisonBuffer)
        {
            comparisonTexture = CreateBlankTexture();
            combinedTexture = CreateBlankTexture();
        }

        Texture2D displayTexture = useComparisonBuffer ? combinedTexture : heatTexture;

        if (overlayRenderer != null)
        {
            overlayRenderer.material.mainTexture = displayTexture;
            Debug.Log($"[MeshGazeHeatmap] Assigned {(useComparisonBuffer ? "combinedTexture" : "heatTexture")} to '{overlayRenderer.name}', shader='{overlayRenderer.material.shader.name}'");
        }
        else
        {
            Debug.LogWarning($"[MeshGazeHeatmap] '{gameObject.name}' has no overlayRenderer assigned - heatmap will never be visible.");
        }

        if (compositorLayer != null)
        {
            compositorLayer.SetHeatTexture(heatTexture);
            Debug.Log($"[MeshGazeHeatmap] Fed heatTexture into compositor layer on '{compositorLayer.name}'.");
        }

        if (recordSamples)
        {
            string dir = Path.Combine(Application.persistentDataPath, "GazeRecordings");
            Directory.CreateDirectory(dir);

            // Specialist recordings always overwrite the same fixed file - there's only ever
            // one current reference to compare trainees against, and GazeReviewLoader must load
            // it by that exact explicit name so it's never swept up and deleted by the
            // "most recent file" trainee-loading path. Trainee sessions keep the existing
            // timestamped-and-auto-consumed behavior, unchanged.
            recordingFilePath = SessionRoleManager.IsSpecialist
                ? Path.Combine(dir, "specialist_reference.json")
                : Path.Combine(dir, $"{recordingId}_{DateTime.Now:yyyyMMdd_HHmmss}.json");

            InvokeRepeating(nameof(SaveRecording), autosaveIntervalSeconds, autosaveIntervalSeconds);
        }
    }

    // This object is the current gaze target. Called once per frame
    public void StampAt(RaycastHit hit)
    {
        if (hasStopped)
        {
            return;
        }

        if (autoStopAfterSeconds > 0f && Time.time - startTime >= autoStopAfterSeconds)
        {
            hasStopped = true;
            if (recordSamples)
            {
                CancelInvoke(nameof(SaveRecording));
                SaveRecording();
                Debug.Log($"[MeshGazeHeatmap] '{gameObject.name}' stopped after {autoStopAfterSeconds}s - final save done, no further stamping.");
            }
            return;
        }

        Vector2 uv = hit.textureCoord;
        int radius = Mathf.RoundToInt(ComputeBrushRadiusPixels(hit.triangleIndex));

        if (recordSamples)
        {
            recording.samples.Add(new GazeSample { u = uv.x, v = uv.y, radius = radius, time = Time.time - startTime });
        }

        PaintAt(uv, radius, heatPerSecond * Time.deltaTime);
    }

    // The actual brush painting, split out from StampAt() so a saved recording can be replayed
    // (GazeReviewLoader calls this directly with stored uv/radius values) without needing a live
    // RaycastHit - both live gaze and review playback end up painting through the same code.
    //
    // "amount" is an explicit parameter, not computed from Time.deltaTime in here, because
    // review replay stamps every saved sample in a tight loop - using live Time.deltaTime for
    // each of those would size every stamp by whatever the REPLAY's current frame time happens
    // to be, not the real elapsed time each sample originally represented. Live gaze (StampAt)
    // still passes heatPerSecond * Time.deltaTime, unchanged from before; replay passes a fixed
    // per-sample amount instead (see GazeReviewLoader).
    public void PaintAt(Vector2 uv, int radius, float amount)
    {
        PaintPixels(heatTexture, uv, radius, amount, null);
    }

    // Same brush/falloff math as PaintAt(), but for the specialist-vs-trainee comparison screen:
    // paints into a caller-supplied texture using a fixed color instead of the blue-red
    // HeatGradient, so two independent sources (specialist, trainee) can be painted in
    // distinguishable colors and combined into one displayed image (see ComparisonLoader).

    // PaintAtColor (used only by ComparisonLoader) paints an arbitrary target texture in a fixed color instead this is how the specialist's data ends up orange and the trainee's ends up cyan on the same brush math, just different destination texture and color.
    
    public void PaintAtColor(Vector2 uv, int radius, float amount, Color fixedColor, Texture2D target)
    {
        PaintPixels(target, uv, radius, amount, fixedColor);
    }

    // fixedColor null = use HeatGradient(newAlpha) (the normal single-source heatmap look);
    // fixedColor set = use that color directly, intensity still driving alpha (PaintAtColor).
    private void PaintPixels(Texture2D target, Vector2 uv, int radius, float amount, Color? fixedColor)
    {
        if (target == null) return;

        int centerX = Mathf.RoundToInt(uv.x * textureSize);
        int centerY = Mathf.RoundToInt(uv.y * textureSize);

        for (int y = -radius; y <= radius; y++)
        {
            int py = centerY + y;
            if (py < 0 || py >= textureSize) continue;

            for (int x = -radius; x <= radius; x++)
            {
                int px = centerX + x;
                if (px < 0 || px >= textureSize) continue;

                float dist = Mathf.Sqrt(x * x + y * y);
                if (dist > radius) continue;

                // Soft edge: falloff is 1 at the brush center, fading to 0 at its radius.
                float falloff = 1f - (dist / radius);

                Color existing = target.GetPixel(px, py);
                float newAlpha = Mathf.Clamp01(existing.a + falloff * amount);

                Color paintColor = fixedColor ?? HeatGradient(newAlpha);
                target.SetPixel(px, py, new Color(paintColor.r, paintColor.g, paintColor.b, newAlpha));
            }
        }

        target.Apply();
    }

    private Texture2D CreateBlankTexture()
    {
        Texture2D texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
        Color[] clearPixels = new Color[textureSize * textureSize];
        for (int i = 0; i < clearPixels.Length; i++)
        {
            clearPixels[i] = new Color(0f, 0f, 0f, 0f);
        }
        texture.SetPixels(clearPixels);
        texture.Apply();
        return texture;
    }

    // Merges heatTexture (specialist) and comparisonTexture (trainee) into combinedTexture - one colour
    // the texture actually assigned to overlayRenderer when useComparisonBuffer is true. Called
    // once by ComparisonLoader after both sources have finished painting, not per-sample -
    // a full-texture GetPixels/SetPixels pass on every stamp would be far too expensive for a
    // session with thousands of samples. Per pixel, whichever source has the higher alpha
    // (more looked-at) wins outright, rather than blending colors together, so overlapping
    // areas stay readable as "mostly specialist" or "mostly trainee" instead of turning into an
    // undifferentiated third color.
    public void CombineComparisonBuffers()
    {
        if (!useComparisonBuffer || heatTexture == null || comparisonTexture == null || combinedTexture == null)
        {
            return;
        }

        Color[] specialistPixels = heatTexture.GetPixels();
        Color[] trainingPixels = comparisonTexture.GetPixels();
        Color[] combined = new Color[specialistPixels.Length];

        for (int i = 0; i < combined.Length; i++)
        {
            combined[i] = trainingPixels[i].a > specialistPixels[i].a ? trainingPixels[i] : specialistPixels[i];
        }

        combinedTexture.SetPixels(combined);
        combinedTexture.Apply();
    }

    private void OnApplicationQuit()
    {
        if (recordSamples)
        {
            SaveRecording();
        }
    }

    // Overwrites the same file (recordingFilePath, fixed for this whole session, set in Start())
    // every time this runs - called periodically via InvokeRepeating, not just at session end
    public void SaveRecording()
    {
        if (recording.samples.Count == 0 || recordingFilePath == null)
        {
            return;
        }

        File.WriteAllText(recordingFilePath, JsonUtility.ToJson(recording));
        Debug.Log($"[MeshGazeHeatmap] Autosaved {recording.samples.Count} samples to '{recordingFilePath}'.");
    }

    // Works out how many texture pixels correspond to brushRadiusWorldMeters of ACTUAL
    // real-world surface at the specific triangle the gaze hit, so the dot looks the same
    // physical size everywhere instead of bigger on bigger objects.
    //
    // Measures the hit triangle's area in both world space and UV space and compares them - a
    // bigger world-area-per-UV-area ratio means this part of the object is stretched over less
    // texture, so each texture pixel covers more real-world distance, needing fewer pixels for
    // the same real-world brush radius.
    private float ComputeBrushRadiusPixels(int triangleIndex)
    {
        if (colliderMesh == null || triangleIndex < 0)
        {
            return Mathf.Clamp(textureSize * 0.02f, minBrushRadiusPixels, maxBrushRadiusPixels);
        }

        int i0 = colliderMesh.triangles[triangleIndex * 3];
        int i1 = colliderMesh.triangles[triangleIndex * 3 + 1];
        int i2 = colliderMesh.triangles[triangleIndex * 3 + 2];

        // World space, not local mesh units, since "real-world meters" needs to account for
        // this object's Transform scale.
        Vector3 worldP0 = transform.TransformPoint(colliderMesh.vertices[i0]);
        Vector3 worldP1 = transform.TransformPoint(colliderMesh.vertices[i1]);
        Vector3 worldP2 = transform.TransformPoint(colliderMesh.vertices[i2]);

        Vector2 uv0 = colliderMesh.uv[i0];
        Vector2 uv1 = colliderMesh.uv[i1];
        Vector2 uv2 = colliderMesh.uv[i2];

        float worldArea = Vector3.Cross(worldP1 - worldP0, worldP2 - worldP0).magnitude * 0.5f;

        Vector2 uvEdge1 = uv1 - uv0;
        Vector2 uvEdge2 = uv2 - uv0;
        float uvArea = Mathf.Abs(uvEdge1.x * uvEdge2.y - uvEdge1.y * uvEdge2.x) * 0.5f;

        // Degenerate/near-zero UV triangle (UV seams, overlapping islands) would divide by ~0.
        if (uvArea < 0.0000001f)
        {
            return Mathf.Clamp(textureSize * 0.02f, minBrushRadiusPixels, maxBrushRadiusPixels);
        }

        // Area scales with the SQUARE of linear size, so sqrt(worldArea/uvArea) converts the
        // area ratio back into a linear one: world meters per UV unit, right at this triangle.
        float worldMetersPerUvUnit = Mathf.Sqrt(worldArea / uvArea);

        float uvRadius = brushRadiusWorldMeters / worldMetersPerUvUnit;
        float pixelRadius = uvRadius * textureSize;

        return Mathf.Clamp(pixelRadius, minBrushRadiusPixels, maxBrushRadiusPixels);
    }

    // Blue -> Green -> Yellow -> Red: barely-looked-at to stared-at-a-lot.
    private static Color HeatGradient(float t)
    {
        if (t < 0.33f) return Color.Lerp(Color.blue, Color.green, t / 0.33f);
        if (t < 0.66f) return Color.Lerp(Color.green, Color.yellow, (t - 0.33f) / 0.33f);
        return Color.Lerp(Color.yellow, Color.red, (t - 0.66f) / 0.34f);
    }
}
