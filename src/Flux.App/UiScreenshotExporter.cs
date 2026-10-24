using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Flux.App;

/// <summary>Exports this app's own WPF visual for documentation during explicitly selected UI tests.</summary>
internal static class UiScreenshotExporter
{
    public static async Task CaptureAsync(Window window, string pngPath)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!Path.IsPathFullyQualified(pngPath) || !Path.GetExtension(pngPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an absolute PNG file path.", nameof(pngPath));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (window.Content is not FrameworkElement content)
            throw new InvalidOperationException("The Flux window has no exportable WPF content.");

        // Explicit layout works even when the process's window is hidden or has no compositor frame.
        // Only this owned content tree is measured, arranged, and rendered; no OS capture is involved.
        var logicalWidth = double.IsFinite(window.Width) && window.Width > 0 ? window.Width : 1280;
        var logicalHeight = double.IsFinite(window.Height) && window.Height > 0 ? window.Height : 840;
        if (logicalWidth > 8192 || logicalHeight > 8192) throw new InvalidOperationException("The window is too large for a UI test image.");
        var layout = new Size(logicalWidth, logicalHeight);
        if (content is System.Windows.Controls.Panel panel && panel.Background == null) panel.Background = window.Background;
        if (content is System.Windows.Controls.Border border && border.Background == null) border.Background = window.Background;
        content.ApplyTemplate();
        content.Measure(layout);
        content.Arrange(new Rect(new Point(0, 0), layout));
        content.UpdateLayout();

        // Use a predictable 96 DPI image of the actual WPF content, independent of desktop scaling.
        // This never captures the screen, another process, OS chrome, or another application's pixels.
        var width = (int)Math.Ceiling(logicalWidth);
        var height = (int)Math.Ceiling(logicalHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetDirectoryName(pngPath)!;
        Directory.CreateDirectory(directory);
        using var output = new FileStream(pngPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }
}
