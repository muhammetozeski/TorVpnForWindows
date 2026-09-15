using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Ui;

/// <summary>The icon Windows shows for an executable, cached per path.</summary>
public static class ProgramIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when the file is gone or carries no icon. Call on the UI thread.</summary>
    public static ImageSource? For(string path)
    {
        if (Cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        ImageSource? image = null;

        try
        {
            if (File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);

                if (icon is not null)
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    image = source;
                }
            }
        }
        catch (Exception ex)
        {
            Log.App($"Could not read the icon of {path}: {ex.GetType().Name}");
        }

        Cache[path] = image;
        return image;
    }
}
