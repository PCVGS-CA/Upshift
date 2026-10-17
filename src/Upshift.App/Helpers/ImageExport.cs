using Microsoft.UI.Xaml;

namespace Upshift.App.Helpers;

/// <summary>"Save as image" for a result card: Measure results and Pre-build shaders' "Did it help?".</summary>
public static class ImageExport
{
    /// <summary>
    /// Renders the element to a PNG first (while it's on screen: the save dialog can close a flyout), then asks where to
    /// save it. Returns a status line, or null when cancelled.
    /// </summary>
    public static async Task<string?> SavePngAsync(UIElement element, string suggestedName)
    {
        byte[] pixels;
        int width, height;
        try
        {
            var target = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await target.RenderAsync(element, (int)(element.ActualSize.X * 2), (int)(element.ActualSize.Y * 2));
            pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(await target.GetPixelsAsync());
            (width, height) = (target.PixelWidth, target.PixelHeight);
            if (width == 0 || height == 0) return "The image couldn't be made: the result card wasn't on screen.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return $"The image couldn't be made: {ex.Message}"; }

        Windows.Storage.StorageFile? file = null;
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary };
            picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
            picker.SuggestedFileName = SafeName(suggestedName);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow));
            file = await picker.PickSaveFileAsync();
            if (file is null) return null;
            using (var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
            {
                stream.Size = 0;
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    (uint)width, (uint)height, 192, 192, pixels);
                await encoder.FlushAsync();
            }
            return $"Saved as {file.Name}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The save dialog makes an empty file; don't leave it behind.
            try { if (file is not null && new FileInfo(file.Path).Length == 0) File.Delete(file.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return $"The image wasn't saved: {ex.Message}";
        }
    }

    public static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}
