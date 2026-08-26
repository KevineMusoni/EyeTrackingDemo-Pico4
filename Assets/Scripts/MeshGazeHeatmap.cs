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

    [Header("Recording")]
    // Off by default - only the objects we actually want session data saved for (e.g.
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

    // x=u, y=v, z=brush radius in pixels - the same values StampAt() already computes, just also
    // kept around so a session can be saved and later replayed in a review scene.
    [Serializable]
    private class GazeRecording
    {
        public List<Vector3> samples = new List<Vector3>();
    }

    private GazeRecording recording = new GazeRecording();

    private Texture2D heatTexture;
    private Mesh colliderMesh;

    private void Start()
    {
        Debug.Log($"[MeshGazeHeatmap] Start() on '{gameObject.name}' - overlayRenderer={(overlayRenderer != null ? overlayRenderer.name : "NULL")}");

        startTime = Time.time;
        colliderMesh = GetComponent<MeshCollider>().sharedMesh;

        heatTexture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);

        Color[] clearPixels = new Color[textureSize * textureSize];
        for (int i = 0; i < clearPixels.Length; i++)
        {
            clearPixels[i] = new Color(0f, 0f, 0f, 0f);
        }
        heatTexture.SetPixels(clearPixels);
        heatTexture.Apply();

        if (overlayRenderer != null)
        {
            overlayRenderer.material.mainTexture = heatTexture;
            Debug.Log($"[MeshGazeHeatmap] Assigned heatTexture to '{overlayRenderer.name}', shader='{overlayRenderer.material.shader.name}'");
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
            recordingFilePath = Path.Combine(dir, $"{recordingId}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
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
            recording.samples.Add(new Vector3(uv.x, uv.y, radius));
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
        if (heatTexture == null) return;

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

                Color existing = heatTexture.GetPixel(px, py);
                float newAlpha = Mathf.Clamp01(existing.a + falloff * amount);

                Color heatColor = HeatGradient(newAlpha);
                heatTexture.SetPixel(px, py, new Color(heatColor.r, heatColor.g, heatColor.b, newAlpha));
            }
        }

        heatTexture.Apply();
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
