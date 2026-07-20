using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IzzysFurniture;

internal sealed class FxCatalog
{
    private static readonly FxCatalogItem[] SeedItems =
    [
        new("Small Fire", "bgcommon/vfx/eff/w_fire101_4y.avfx", "Fire"),
        new("Fire Glow", "bgcommon/vfx/eff/w_fire208n1o.avfx", "Fire"),
        new("Forest Fire 2", "bg/ffxiv/fst_f1/common/vfx/eff/f1b4fire02_y.avfx", "Fire"),
        new("Forest Fire 3", "bg/ffxiv/fst_f1/common/vfx/eff/f1b4fire03_y.avfx", "Fire"),
        new("Forest Fire 4", "bg/ffxiv/fst_f1/common/vfx/eff/f1b4fire04_y.avfx", "Fire"),
        new("Forest Fire 5", "bg/ffxiv/fst_f1/common/vfx/eff/f1b4fire05_y.avfx", "Fire"),
        new("Smoke", "bgcommon/vfx/eff/w_smoke_003o.avfx", "Smoke"),
        new("Smoke Column", "bgcommon/vfx/eff/w_smok004_2y.avfx", "Smoke"),
        new("Forest Cloud", "bg/ffxiv/fst_f1/common/vfx/eff/f1b4cloud1_y.avfx", "Smoke"),
        new("Mist", "bg/ffxiv/fst_f1/common/vfx/eff/f1b4kiri3_y.avfx", "Smoke"),
        new("Water Ripple 1", "bgcommon/vfx/eff/w_watr001a4y.avfx", "Water"),
        new("Water Ripple 2", "bgcommon/vfx/eff/w_watr001a5y.avfx", "Water"),
        new("Water Flow 1", "bgcommon/vfx/eff/w_watr002_1y.avfx", "Water"),
        new("Water Flow 2", "bgcommon/vfx/eff/w_watr002_2y.avfx", "Water"),
        new("Water Fall", "bgcommon/vfx/eff/w_watr003_9h1.avfx", "Water"),
        new("Water Splash", "bgcommon/vfx/eff/w_watr004_2y.avfx", "Water"),
        new("Housing Water", "bg/ffxiv/hou_xx/common/vfx/eff/h1m221wter1_b1.avfx", "Housing"),
        new("Trap Effect", "bg/ffxiv/fst_f1/common/vfx/eff/b0941trp1a_o.avfx", "Misc"),
    ];

    public IReadOnlyList<FxCatalogItem> Items { get; } = BuildItems();

    private static IReadOnlyList<FxCatalogItem> BuildItems()
    {
        var paths = LoadCachedPaths()
            .Concat(SeedItems.Select(item => item.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim().Replace('\\', '/'))
            .Where(path => path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Service.DataManager.FileExists)
            .ToArray();

        var items = paths
            .Select(path => new FxCatalogItem(DisplayName(path), path, Categorize(path)))
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return items;
    }

    private static IEnumerable<string> LoadCachedPaths()
    {
        var path = Path.Combine(Service.PluginInterface.AssemblyLocation.DirectoryName!, "FxPaths.txt");

        if (!File.Exists(path))
            return [];

        try
        {
            return File.ReadLines(path).ToArray();
        }
        catch (Exception error)
        {
            Service.Log.Warning($"could not read FxPaths.txt: {error.Message}");
            return [];
        }
    }

    private static string DisplayName(string path)
        => Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));

    private static string Categorize(string path)
    {
        var text = path.ToLowerInvariant();
        if (TextSearch.ContainsAny(text, "fir", "bari"))
            return "Fire / Energy";
        if (TextSearch.ContainsAny(text, "smok", "fog", "mist", "kiri", "cloud", "clud", "yuge"))
            return "Smoke / Fog";
        if (TextSearch.ContainsAny(text, "watr", "taki", "fall", "bubl", "awa"))
            return "Water";
        if (TextSearch.ContainsAny(text, "sand", "dust", "soil", "snow", "ice", "wind", "wnd"))
            return "Weather / Terrain";
        if (TextSearch.ContainsAny(text, "kira", "glow", "beam", "aura"))
            return "Light / Sparkle";
        if (TextSearch.ContainsAny(text, "hou"))
            return "Housing";
        if (TextSearch.ContainsAny(text, "pvp"))
            return "PvP";

        return "Misc";
    }
}
