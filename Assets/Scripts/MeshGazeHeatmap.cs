using UnityEngine;

// Starter/prototype gaze heatmap for a single mesh. Accumulates "heat" into a runtime
// texture wherever the gaze ray hits this object's UV space, then displays it on a
// separate unlit-transparent overlay renderer sitting just above the original surface.
//
// Wired in from EyeTrackingManager.GazeTargetControl()
//
// NOTE: uses Texture2D.GetPixel/SetPixel per brush stamp, which is fine for a first pass
// at a small brush radius but is not fast enough for production (should move to
// SetPixels32/a compute shader if this becomes a real feature).
[RequireComponent(typeof(MeshCollider))]
public class MeshGazeHeatmap : MonoBehaviour
{
    [Header("Heatmap Texture")]
    // Resolution of the heat texture in pixels (square). Higher = finer detail, more memory,
    // slower per-stamp loop below since the brush covers more pixels at the same radius ratio.
    [SerializeField] private int textureSize = 512;

    // ADDED: real-world brush size in meters (radius, not diameter), instead of a fixed pixel
    // count. This is what makes the dot look the SAME PHYSICAL SIZE on differently-sized
    // objects, instead of the old fixed-pixel-radius approach where the same number produced
    // a bigger-looking dot on a bigger object (since UV space is always 0-1 regardless of the
    // object's actual size - see ComputeBrushRadiusPixels() below for how this is converted).
    [SerializeField] private float brushRadiusWorldMeters = 0.1f;

    // Safety clamps on the CALCULATED pixel radius (see ComputeBrushRadiusPixels), so a
    // degenerate/tiny UV triangle at the hit point can't produce a wildly huge or zero brush.
    [SerializeField] private float minBrushRadiusPixels = 2f;
    [SerializeField] private float maxBrushRadiusPixels = 64f;

    // How fast heat (0-1 alpha) builds up per second of continuous gaze on the same spot.
    // At 1f, one full second of steady looking maxes out that spot's alpha to 1 (fully "hot").
    [SerializeField] private float heatPerSecond = 1f;

    [Header("Overlay Display")]
    // The separate renderer (duplicate mesh, no collider, transparent shader) that
    // shows the heatmap texture on screen - see setup steps given alongside this script.
    [SerializeField] private Renderer overlayRenderer;

    // The runtime-generated texture we paint into. Starts fully transparent (no heat anywhere).
    private Texture2D heatTexture;

    // ADDED: cached reference to this object's collision mesh, used to look up the hit
    // triangle's actual vertex positions/UVs for the texel-density calculation below.
    // Cached once in Start() rather than re-fetched every stamp (GetComponent is not free).
    private Mesh colliderMesh;

    private void Start()
    {
        // ADDED (debug): confirms Start() actually ran and whether overlayRenderer was
        // assigned - if this line never shows up in logcat, the object/script is inactive
        // or Start() isn't being called; if it shows "overlayRenderer=NULL", the Inspector
        // field assignment didn't survive into the build.
        Debug.Log($"[MeshGazeHeatmap] Start() on '{gameObject.name}' - overlayRenderer={(overlayRenderer != null ? overlayRenderer.name : "NULL")}");

        // ADDED: grab the mesh off our own Mesh Collider (required by [RequireComponent] above)
        // so ComputeBrushRadiusPixels() can read its vertices/UVs per-triangle later.
        colliderMesh = GetComponent<MeshCollider>().sharedMesh;

        // Create a blank RGBA texture at runtime - this is the "canvas" we stamp heat onto.
        // RGBA32 = 8 bits per channel including alpha, which we use to store heat intensity.
        heatTexture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);

        // Fill every pixel with fully transparent black (alpha = 0 means "no heat yet").
        Color[] clearPixels = new Color[textureSize * textureSize];
        for (int i = 0; i < clearPixels.Length; i++)
        {
            clearPixels[i] = new Color(0f, 0f, 0f, 0f);
        }
        heatTexture.SetPixels(clearPixels);
        heatTexture.Apply(); // Apply() uploads the CPU-side pixel data to the GPU texture.

        // Hand the texture to the overlay material so it actually renders on screen.
        // Without this, we'd be painting into a texture nothing ever displays.
        if (overlayRenderer != null)
        {
            overlayRenderer.material.mainTexture = heatTexture;
            // ADDED (debug): confirms which shader the runtime material instance actually has -
            // if this doesn't say "Unlit/Transparent", the .material instance isn't using the
            // shader we set in the Editor (e.g. shader got stripped from the Android build).
            Debug.Log($"[MeshGazeHeatmap] Assigned heatTexture to '{overlayRenderer.name}', shader='{overlayRenderer.material.shader.name}'");
        }
        else
        {
            Debug.LogWarning($"[MeshGazeHeatmap] '{gameObject.name}' has no overlayRenderer assigned - heatmap will never be visible.");
        }
    }

    // MODIFIED: now takes the full RaycastHit (was just Vector2 uv) - needed so we can read
    // hit.triangleIndex for the texel-density calculation, in addition to hit.textureCoord.
    // Called once per frame this object is the current gaze target (see EyeTrackingManager,
    // GazeTargetControl() calls this with the raycast hit against this object).
    public void StampAt(RaycastHit hit)
    {
        if (heatTexture == null) return; // Start() hasn't run yet, or setup failed - bail safely.

        Vector2 uv = hit.textureCoord;

        // UV coordinates are 0-1 regardless of actual texture size, so convert to pixel space.
        // e.g. uv.x = 0.5 on a 512px texture -> centerX = 256 (the middle column).
        int centerX = Mathf.RoundToInt(uv.x * textureSize);
        int centerY = Mathf.RoundToInt(uv.y * textureSize);

        // ADDED: instead of a fixed brushRadiusPixels, calculate the pixel radius that
        // corresponds to brushRadiusWorldMeters AT THIS EXACT SPOT on the mesh - this is what
        // makes the dot's real-world size consistent across differently-sized/shaped objects.
        int radius = Mathf.RoundToInt(ComputeBrushRadiusPixels(hit.triangleIndex));

        // Time.deltaTime scales this so heat accumulates at a consistent real-world rate
        // regardless of frame rate (60fps vs 90fps headsets shouldn't heat up at different speeds).
        float addedThisFrame = heatPerSecond * Time.deltaTime;

        // Loop over a square bounding box around the stamp center, then skip anything outside
        // the actual circular brush radius (the "if (dist > radius) continue" below) - this is
        // the standard way to rasterize a circle without a dedicated circle-drawing algorithm.
        for (int y = -radius; y <= radius; y++)
        {
            int py = centerY + y;
            if (py < 0 || py >= textureSize) continue; // skip pixels off the top/bottom edge

            for (int x = -radius; x <= radius; x++)
            {
                int px = centerX + x;
                if (px < 0 || px >= textureSize) continue; // skip pixels off the left/right edge

                // Distance from this pixel to the stamp's center point.
                float dist = Mathf.Sqrt(x * x + y * y);
                if (dist > radius) continue; // outside the circular brush - skip it

                // falloff = 1 at the very center of the brush, fading to 0 at the edge -
                // this is what gives the stamp a soft edge instead of a hard-edged disc.
                float falloff = 1f - (dist / radius);

                // Read this pixel's current heat (stored in the alpha channel), add this
                // frame's contribution (scaled by falloff and time), and clamp so it never
                // exceeds fully "hot" (alpha = 1).
                Color existing = heatTexture.GetPixel(px, py);
                float newAlpha = Mathf.Clamp01(existing.a + falloff * addedThisFrame);

                // Recompute the color for the new intensity level and write both color + alpha.
                Color heatColor = HeatGradient(newAlpha);
                heatTexture.SetPixel(px, py, new Color(heatColor.r, heatColor.g, heatColor.b, newAlpha));
            }
        }

        // Apply() re-uploads the whole texture to the GPU after all the SetPixel calls above -
        // only needs to happen once per StampAt() call, not once per pixel.
        heatTexture.Apply();
    }

    // ADDED: works out how many texture pixels correspond to brushRadiusWorldMeters of ACTUAL
    // real-world surface, at the specific triangle the gaze just hit. This is what makes the
    // heatmap dot look the same physical size everywhere, instead of the old approach where a
    // fixed pixel radius looked bigger on bigger objects and smaller on smaller ones.
    //
    // How it works: takes the 3 corners of the hit triangle, measures the triangle's area both
    // in real-world space (world units) and in UV space (0-1 texture space), and compares them.
    // A bigger world-area-per-UV-area ratio means "this part of the object is stretched over a
    // small bit of texture" - i.e. each texture pixel here covers MORE real-world distance, so
    // we need FEWER pixels to represent the same real-world brush radius (and vice versa).
    private float ComputeBrushRadiusPixels(int triangleIndex)
    {
        // Fallback if we don't have valid mesh data to measure from - just use a mid-range
        // pixel radius rather than crashing or producing a zero/invalid brush.
        if (colliderMesh == null || triangleIndex < 0)
        {
            return Mathf.Clamp(textureSize * 0.02f, minBrushRadiusPixels, maxBrushRadiusPixels);
        }

        // Every triangle is stored as 3 indices into the vertex/uv arrays - look up which
        // 3 vertices make up the specific triangle the raycast hit.
        int i0 = colliderMesh.triangles[triangleIndex * 3];
        int i1 = colliderMesh.triangles[triangleIndex * 3 + 1];
        int i2 = colliderMesh.triangles[triangleIndex * 3 + 2];

        // Vertex positions are stored in LOCAL space on the mesh - convert to world space via
        // this object's Transform, since "real-world meters" needs to mean actual world scale,
        // not local mesh units (which could be scaled up/down by the Transform's own Scale).
        Vector3 worldP0 = transform.TransformPoint(colliderMesh.vertices[i0]);
        Vector3 worldP1 = transform.TransformPoint(colliderMesh.vertices[i1]);
        Vector3 worldP2 = transform.TransformPoint(colliderMesh.vertices[i2]);

        // UV coordinates for those same 3 vertices - already in the 0-1 UV space we need.
        Vector2 uv0 = colliderMesh.uv[i0];
        Vector2 uv1 = colliderMesh.uv[i1];
        Vector2 uv2 = colliderMesh.uv[i2];

        // Triangle area in world space: half the magnitude of the cross product of two edges.
        float worldArea = Vector3.Cross(worldP1 - worldP0, worldP2 - worldP0).magnitude * 0.5f;

        // Triangle area in UV space: same idea, but in 2D (UV has no Z), using the 2D
        // cross-product formula (a.x*b.y - a.y*b.x) instead of Vector3.Cross.
        Vector2 uvEdge1 = uv1 - uv0;
        Vector2 uvEdge2 = uv2 - uv0;
        float uvArea = Mathf.Abs(uvEdge1.x * uvEdge2.y - uvEdge1.y * uvEdge2.x) * 0.5f;

        // Guard against a degenerate/near-zero UV triangle (can happen at UV seams or on
        // overlapping UV islands) - dividing by ~0 would give an absurd or infinite result.
        if (uvArea < 0.0000001f)
        {
            return Mathf.Clamp(textureSize * 0.02f, minBrushRadiusPixels, maxBrushRadiusPixels);
        }

        // Area scales with the SQUARE of linear size, so sqrt(worldArea / uvArea) converts an
        // area ratio back into a LINEAR ratio: "how many world meters does 1 UV unit represent
        // right here". This is the local texel density we actually need.
        float worldMetersPerUvUnit = Mathf.Sqrt(worldArea / uvArea);

        // Convert our desired real-world brush radius into UV-space units using that ratio,
        // then into pixels using the texture's resolution.
        float uvRadius = brushRadiusWorldMeters / worldMetersPerUvUnit;
        float pixelRadius = uvRadius * textureSize;

        // Clamp to sane bounds - protects against extreme values from unusual mesh geometry,
        // and keeps the stamping loop above (which is O(radius^2)) from ever going too slow.
        return Mathf.Clamp(pixelRadius, minBrushRadiusPixels, maxBrushRadiusPixels);
    }

    // Maps a 0-1 intensity value to a color, cold-to-hot: Blue -> Green -> Yellow -> Red.
    // This is the classic "heatmap" color ramp - t=0 is barely-looked-at, t=1 is stared-at-a-lot.
    private static Color HeatGradient(float t)
    {
        // Split the 0-1 range into three even bands and Lerp (linearly blend) between the two
        // colors at each band's boundary, based on how far through that band t currently is.
        if (t < 0.33f) return Color.Lerp(Color.blue, Color.green, t / 0.33f);
        if (t < 0.66f) return Color.Lerp(Color.green, Color.yellow, (t - 0.33f) / 0.33f);
        return Color.Lerp(Color.yellow, Color.red, (t - 0.66f) / 0.34f);
    }
}
