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

    private Texture2D heatTexture;
    private Mesh colliderMesh;

    private void Start()
    {
        Debug.Log($"[MeshGazeHeatmap] Start() on '{gameObject.name}' - overlayRenderer={(overlayRenderer != null ? overlayRenderer.name : "NULL")}");

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
    }

    // Called once per frame this object is the current gaze target.
    public void StampAt(RaycastHit hit)
    {
        if (heatTexture == null) return;

        Vector2 uv = hit.textureCoord;
        int centerX = Mathf.RoundToInt(uv.x * textureSize);
        int centerY = Mathf.RoundToInt(uv.y * textureSize);

        int radius = Mathf.RoundToInt(ComputeBrushRadiusPixels(hit.triangleIndex));
        float addedThisFrame = heatPerSecond * Time.deltaTime;

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
                float newAlpha = Mathf.Clamp01(existing.a + falloff * addedThisFrame);

                Color heatColor = HeatGradient(newAlpha);
                heatTexture.SetPixel(px, py, new Color(heatColor.r, heatColor.g, heatColor.b, newAlpha));
            }
        }

        heatTexture.Apply();
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
