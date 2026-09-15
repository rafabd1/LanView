using System.Resources;

namespace LanView.Windows;

public static class AppIcon
{
    public const string ResourceName = "LanView.Windows.Assets.LanView.ico";

    // Each caller owns its copy; it remains usable after the resource stream closes.
    public static Icon Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new MissingManifestResourceException("The LanView icon resource is missing.");
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }
}
