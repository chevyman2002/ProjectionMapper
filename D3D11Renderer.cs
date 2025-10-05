using System;
using System.Windows.Media.Imaging;

// Other necessary using directives...

public class D3D11Renderer : IRenderer
{
    public event Action<BitmapSource?>? FrameReady;

    public async Task RenderFrameAsync()
    {
        // Your rendering logic here...

        // Raise the event with null by default
        FrameReady?.Invoke(null);
    }
}