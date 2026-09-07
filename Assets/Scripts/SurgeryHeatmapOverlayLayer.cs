using UnityEngine;
using Unity.XR.PXR;

// Gives the gaze heatmap its own independent PXR_OverLay compositor layer, stacked above the
// surgery video's own compositor layer (see SurgeryVideoOverlayPlayer.cs) via layerDepth.
//
// Needed because the video plays via PXR_OverLay External Surface, which bypasses Unity's
// renderer entirely - so a normal MeshRenderer-based heatmap quad can only composite either
// fully behind the eye buffer (Underlay, needs alpha hole-punching Unity isn't set up for) or
// gets buried under the video's default Overlay layer. Two independent compositor layers,
// ordered by layerDepth, sidesteps both problems.
[RequireComponent(typeof(PXR_OverLay))]
public class SurgeryHeatmapOverlayLayer : MonoBehaviour
{
    [SerializeField] private int layerDepth = 1;

    // Per-instance, must match whatever shape the video's own SurgeryVideoOverlayPlayer.overlayShape
    // is set to on the same screen (e.g. both Cylinder with the same Radius on DivergenceReplayScreen)
    // - a mismatch here shows up as gaze dots drifting off the video, worse toward the edges. Other
    // screens' heat overlays keep defaulting to Quad, matching what was previously hardcoded here.
    [SerializeField] private PXR_OverLay.OverlayShape overlayShape = PXR_OverLay.OverlayShape.Quad;

    // PXR_OverLay.radius exists regardless of shape, but this SDK's custom Inspector
    // (PXR_OverLayEditor.cs) only ever draws "Radius" for Equirect - a gap in the bundled Editor
    // script, not a sign Cylinder doesn't use it. Exposed here so it's editable in the normal
    // Inspector. Must match SurgeryVideoOverlayPlayer.cylinderRadius on the same screen, or the
    // dots won't sit on the curve correctly.
    [SerializeField] private float cylinderRadius = 0f;

    private PXR_OverLay overlay;

    private void Awake()
    {
        overlay = GetComponent<PXR_OverLay>();
        overlay.overlayShape = overlayShape;
        overlay.radius = cylinderRadius;
        overlay.textureType = PXR_OverLay.TextureType.DynamicTexture;
        // isExternalAndroidSurface (not textureType) is what InitializeBuffer() actually
        // branches on - must be explicitly false here, since a stray Inspector value (e.g.
        // left over from copying the video layer's PXR_OverLay) silently routes this layer
        // down the external-surface path instead, where CopyRT() never runs and our fed
        // heatmap texture never reaches the compositor.
        overlay.isExternalAndroidSurface = false;
        overlay.layerDepth = layerDepth;
    }

    public void SetHeatTexture(Texture2D texture)
    {
        overlay.SetTexture(texture, true);
    }
}
