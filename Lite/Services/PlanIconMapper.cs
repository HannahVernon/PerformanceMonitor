using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace PerformanceMonitorLite.Services;

public static class PlanIconMapper
{
    private static readonly Dictionary<string, BitmapImage> _iconCache = new();

    public static BitmapImage? GetIcon(string iconName)
    {
        if (_iconCache.TryGetValue(iconName, out var cached))
            return cached;

        try
        {
            var uri = new Uri($"pack://application:,,,/Resources/PlanIcons/{iconName}.png", UriKind.Absolute);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 32;
            bitmap.EndInit();
            bitmap.Freeze();
            _iconCache[iconName] = bitmap;
            return bitmap;
        }
        catch
        {
            // Try fallback icon
            if (iconName != "iterator_catch_all")
                return GetIcon("iterator_catch_all");
            return null;
        }
    }
}
