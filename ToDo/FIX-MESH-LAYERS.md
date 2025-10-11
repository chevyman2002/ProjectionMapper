```markdown
# Fixing Mesh Layers — Plan

Goal:
- Ensure output mesh layers render the provided (cropped) video frame as a quadrilateral (warped) rather than only as an axis-aligned rectangle.

Summary of changes:
1. Extend renderer API to accept an optional destination quad (4 points) so layer frames can be submitted with arbitrary quad mapping.
2. Update RendererManager and all call sites to pass the optional quad (when available).
3. Implement quad-warp rendering in the SoftwareRenderer:
   - Store optional dest-quad per layer.
   - If a dest-quad is provided, compute a homography that maps source normalized coordinates to the quad.
   - Rasterize the warped image into an intermediate buffer via inverse mapping + bilinear sampling.
   - Composite that warped bitmap into the final frame.
4. Keep D3D11Renderer and other stubs compatible by adding the new parameter (no-op).
5. Update OutputMeshEditorControl to pass the output mesh points transformed into renderer coordinates when submitting frames.
6. Keep existing rectangle-based submissions working (destQuad == null).
7. Add safe error logging in all try/catch blocks (Debug.WriteLine) as required.
8. Ensure code compiles; no runtime exceptions on nulls.

Notes about implementation choices:
- SoftwareRenderer uses an inverse-homography-based software warp. This is slower than GPU but correct and works regardless of D3D availability.
- The warp implementation converts Bitmaps to Bgra32 for direct pixel access and performs bilinear sampling for decent quality.
- All new code contains comments, checks, and logged catch blocks per project guidelines.
- DestQuad ordering: TopLeft, TopRight, BottomLeft, BottomRight (consistent with LayerModel and UI code).

Files changed (this plan references them):
- Rendering/IRenderer.cs
- Rendering/RendererManager.cs
- Rendering/SoftwareRenderer.cs
- Rendering/D3D11Renderer.cs
- Services/VideoService.cs
- Views/OutputMeshEditorControl.xaml.cs

If a response is cut off, ask me to continue and I will send the next chunk.
```