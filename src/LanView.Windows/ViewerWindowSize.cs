using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanView.Windows;

// Store only normal window dimensions. Pairing and upstream preferences stay separate.
public sealed record ViewerWindowSize(int Width, int Height, uint Dpi)
{
    [JsonIgnore]
    public bool IsValid => Width is >= 200 and <= 32768 && Height is >= 150 and <= 32768
        && Dpi is >= 48 and <= 768;

    public Rectangle FitToWorkingArea(Rectangle currentBounds, Rectangle workingArea, uint currentDpi)
    {
        if (!IsValid || currentDpi is < 48 or > 768 || workingArea.Width <= 0 || workingArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workingArea));
        var width = Math.Min(workingArea.Width, (int)Math.Round(Width * (double)currentDpi / Dpi));
        var height = Math.Min(workingArea.Height, (int)Math.Round(Height * (double)currentDpi / Dpi));
        var x = Math.Clamp(currentBounds.X, workingArea.Left, workingArea.Right - width);
        var y = Math.Clamp(currentBounds.Y, workingArea.Top, workingArea.Bottom - height);
        return new(x, y, width, height);
    }
}

public static class ViewerWindowSizeStore
{
    public static string FilePath { get; } = Path.Combine(
        Path.GetDirectoryName(ProfileStore.FilePath)!, "viewer-window.json");

    public static ViewerWindowSize? Load(string? filePath = null)
    {
        try
        {
            using var stream = File.OpenRead(filePath ?? FilePath);
            if (stream.Length > 4096) return null;
            var size = JsonSerializer.Deserialize<ViewerWindowSize>(stream);
            return size is { IsValid: true } ? size : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or invalid preference must never prevent a video session.
            return null;
        }
    }

    public static void Save(ViewerWindowSize size, string? filePath = null)
    {
        if (!size.IsValid) throw new ArgumentException("Invalid viewer window dimensions.", nameof(size));
        var path = filePath ?? FilePath;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"viewer-window-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(size));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
