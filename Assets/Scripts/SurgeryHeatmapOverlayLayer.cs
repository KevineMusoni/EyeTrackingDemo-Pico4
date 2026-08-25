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

    private PXR_OverLay overlay;

    private void Awake()
    {
        overlay = GetComponent<PXR_OverLay>();
        overlay.overlayShape = PXR_OverLay.OverlayShape.Quad;
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
