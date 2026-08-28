using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using Unity.XR.PXR;

// this will give the reticle its own independent PXR_OverLay compositor layer, stacked above the ReticleDemoVideoScreen's own video layer via layerDepth - basically the same reasoning as SurgeryHeatmapOverlayLayer: the video plays via External Surface, which bypasses Unity's renderer entirely. So a normal MeshRenderer object can never appear in front of it regardless of 3D positrion. A second compositor layer, ordered by layerDepth, is the most efficient way.
[RequireComponent(typeof(PXR_OverLay))]

public class GazeReticleOverLayer : MonoBehaviour
{
    [SerializeField] private int layerDepth = -1; //Must be higher than the ReticledemoVideoScreen's own PXR_Overlay Depth (Currently 1)

    [SerializeField] private int textureSize = 64;
    [SerializeField] private Color dotColor = Color.cyan;

    private PXR_OverLay overlay;

    private void Awake(){
        overlay = GetComponent<PXR_OverLay>();
        overlay.overlayShape = PXR_OverLay.OverlayShape.Quad;
        overlay.textureType = PXR_OverLay.TextureType.DynamicTexture;
        overlay.isExternalAndroidSurface = false;
        overlay.layerDepth = layerDepth;
    }

    // SetTexture() deliberately NOT called from Awake() - SurgeryHeatmapOverlayLayer (the proven
    // working version of this same pattern) never calls it there either, only from a separate
    // method invoked later, from another object's Start(). Activating GazeReticle at scene load
    // with SetTexture() still inside Awake() crashed/hung the whole scene - likely the PXR
    // runtime isn't fully ready for a texture handoff that early. Start() runs only after every
    // object's Awake() in the scene has already finished, giving it more time.
    private void Start()
    {
        overlay.SetTexture(CreateDotTexture(), true);
    }

    // Drawn once at start up, not per-frame - the reticle doesn't need repainting, just
    // repositioning (see EyeTrackingManager.GazeTargetControl, which already moves this
    // object's Transform to the current gaze point - the compositor layer follows automatically).

    private Texture2D CreateDotTexture()
    {
        Texture2D texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[textureSize * textureSize];
        Vector2 center = new Vector2(textureSize / 2f, textureSize / 2f);

        float radius = textureSize * 0.35f;

        for (int y = 0; y < textureSize; y++)
        {
            for (int x = 0; x < textureSize; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), center);
                pixels[y * textureSize + x] = dist <= radius ? dotColor : new Color(0f, 0f, 0f, 0f);
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();
        return texture;
    }
}

