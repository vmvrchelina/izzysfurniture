using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Utility;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;
using LuminaTransform = Lumina.Data.Parsing.Common.Transformation;
using LuminaVector3 = Lumina.Data.Parsing.Common.Vector3;

namespace IzzysFurniture;

internal sealed class MapAssetCatalog
{
    private readonly Lazy<IReadOnlyList<MapZoneCatalogItem>> zones = new(BuildZones);
    private readonly Dictionary<uint, IReadOnlyList<MapAssetCatalogItem>> itemsByTerritory = [];
    private readonly Dictionary<uint, IReadOnlyList<MapAssetCatalogItem>> placedItemsByTerritory = [];

    private uint cachedTerritory;
    private IReadOnlyList<MapAssetCatalogItem> cachedItems = [];
    private IReadOnlyList<MapAssetCatalogItem> cachedPlacedItems = [];

    public IReadOnlyList<MapZoneCatalogItem> Zones => this.zones.Value;

    public IReadOnlyList<MapAssetCatalogItem> Items
    {
        get
        {
            var territory = Service.ClientState.TerritoryType;
            if (territory != this.cachedTerritory)
            {
                this.cachedTerritory = territory;
                this.cachedItems = this.BuildCatalog(territory, out this.cachedPlacedItems);
            }

            return this.cachedItems;
        }
    }

    public IReadOnlyList<MapAssetCatalogItem> ItemsForTerritory(uint territoryId)
    {
        if (territoryId == 0)
            return this.Items;

        if (!this.itemsByTerritory.TryGetValue(territoryId, out var items))
        {
            items = this.BuildCatalog(territoryId, out var placedItems);
            this.placedItemsByTerritory[territoryId] = placedItems;

            this.itemsByTerritory[territoryId] = items;
        }
        return items;
    }

    public IReadOnlyList<MapAssetCatalogItem> PlacedItemsForTerritory(uint territoryId)
    {
        if (territoryId == 0)
        {
            _ = this.Items;
            return this.cachedPlacedItems;
        }

        if (!this.placedItemsByTerritory.TryGetValue(territoryId, out var placedItems))
            _ = this.ItemsForTerritory(territoryId);

        return this.placedItemsByTerritory.TryGetValue(territoryId, out placedItems)
            ? placedItems
            : [];
    }

    private static IReadOnlyList<MapZoneCatalogItem> BuildZones()
    {
        var sheet = Service.DataManager.GetExcelSheet<TerritoryType>();
        var zones = new List<MapZoneCatalogItem>();
        foreach (var row in sheet)
        {
            var bg = row.Bg.ExtractText().Trim();
            if (row.RowId == 0 || string.IsNullOrWhiteSpace(bg))
                continue;

            var name = row.PlaceName.RowId != 0
                ? row.PlaceName.Value.Name.ExtractText()
                : row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            zones.Add(new MapZoneCatalogItem(row.RowId, $"{name} ({row.RowId})", bg));
        }

        return zones
            .GroupBy(zone => zone.TerritoryId)
            .Select(group => group.First())
            .OrderBy(zone => zone.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<MapAssetCatalogItem> BuildCatalog(uint territoryId, out IReadOnlyList<MapAssetCatalogItem> placedItems)
    {
        if (territoryId == 0)
        {
            placedItems = [];
            return [];
        }

        var zone = this.Zones.FirstOrDefault(item => item.TerritoryId == territoryId);
        if (zone is null)
        {
            placedItems = [];
            return [];
        }

        var bg = zone.BgPath;

        var lgbPaths = TerritoryLgbCandidates(bg)
            .Where(Service.DataManager.FileExists)
            .ToArray();
        if (lgbPaths.Length == 0)
        {
            placedItems = [];
            return [];
        }

        // TODO: first open of a big zone stalls for a moment, could cache this to disk
        var results = new List<MapAssetCatalogItem>(1024);
        var activeSharedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lgbPath in lgbPaths)
        {
            var lgb = this.GetLayerFile(lgbPath);
            if (lgb is null)
                continue;

            this.AddFromLayers(results, lgb.Layers, lgbPath, activeSharedGroups, null, true, false);
        }

        placedItems = results
            .Where(item => Service.DataManager.FileExists(item.ModelPath))
            .Select(item => item with { Category = Categorize(item.Name, item.ModelPath, item.SourcePath) })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ModelPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var items = placedItems
            .Where(item => item.Kind != MapAssetKind.SharedGroupChild || IsZoneSetSharedGroup(item.SourcePath, item.Name))
            .GroupBy(item => item.ModelPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ModelPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return items;
    }

    private static IEnumerable<string> TerritoryLgbCandidates(string bg)
    {
        var path = NormalizeGamePath(bg);
        if (string.IsNullOrWhiteSpace(path))
            yield break;

        if (path.EndsWith(".lgb", StringComparison.OrdinalIgnoreCase))
        {
            yield return EnsureBgPrefix(path);
            yield break;
        }

        var withPrefix = EnsureBgPrefix(path);
        yield return $"{withPrefix}.lgb";

        var folder = withPrefix.Contains('/')
            ? withPrefix[..withPrefix.LastIndexOf('/')]
            : withPrefix;

        foreach (var fileName in new[] { "bg", "planevent", "planmap" })
            yield return $"{folder}/{fileName}.lgb";
    }

    private void AddFromLayers(
        List<MapAssetCatalogItem> results,
        IEnumerable<LayerCommon.Layer>? layers,
        string sourcePath,
        HashSet<string> activeSharedGroups,
        LuminaTransform? parentTransform,
        bool allowLiveOriginals,
        bool isSharedGroupChild)
    {
        if (layers is null)
            return;

        foreach (var layer in layers)
        {
            if (layer.InstanceObjects is null)
                continue;

            foreach (var instance in layer.InstanceObjects)
            {
                switch (instance.Object)
                {
                    case LayerCommon.BGInstanceObject bg:
                        this.AddModel(results, instance, bg.AssetPath, sourcePath, parentTransform, allowLiveOriginals, isSharedGroupChild);
                        break;

                    case LayerCommon.SharedGroupInstanceObject sharedGroup:
                        var sharedGroupTransform = Combine(parentTransform, instance.Transform);
                        var sharedGroupPath = NormalizeGamePath(sharedGroup.AssetPath);
                        var children = this.AddSharedGroup(results, sharedGroup.AssetPath, sourcePath, activeSharedGroups, sharedGroupTransform);
                        if (children.Count == 0)
                            break;

                        if (IsZoneSetSharedGroup(sharedGroupPath, instance.Name))
                            break;

                        results.Add(new MapAssetCatalogItem(
                            DisplayName(instance.Name, sharedGroupPath),
                            sharedGroupPath,
                            sourcePath,
                            Category: "Shared Groups",
                            Position: ToNumerics(sharedGroupTransform.Translation),
                            RotationDegrees: RadiansToDegrees(ToNumerics(sharedGroupTransform.Rotation)),
                            Scale3: ToNumerics(sharedGroupTransform.Scale),
                            Kind: MapAssetKind.SharedGroup,
                            Children: children));
                        break;
                }
            }
        }
    }

    private IReadOnlyList<MapAssetCatalogChild> AddSharedGroup(
        List<MapAssetCatalogItem> results,
        string assetPath,
        string sourcePath,
        HashSet<string> activeSharedGroups,
        LuminaTransform? parentTransform)
    {
        var path = NormalizeGamePath(assetPath);
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".sgb", StringComparison.OrdinalIgnoreCase))
            return [];

        // so they dont walk forever
        if (!activeSharedGroups.Add(path) || !Service.DataManager.FileExists(path))
            return [];

        var sgb = this.GetSharedGroupFile(path);
        if (sgb?.LayerGroups is null)
        {
            activeSharedGroups.Remove(path);
            return [];
        }

        var startIndex = results.Count;
        foreach (var layerGroup in sgb.LayerGroups)
            this.AddFromLayers(results, layerGroup.Layers, path, activeSharedGroups, parentTransform, false, true);
        activeSharedGroups.Remove(path);

        var parentMatrix = parentTransform is null ? Matrix4x4.Identity : TransformMatrix(parentTransform.Value);
        Matrix4x4.Invert(parentMatrix, out var inverseParent);
        // convert collected world transforms back into coordinates local to the group
        return results
            .Skip(startIndex)
            .Where(item => item.Kind == MapAssetKind.SharedGroupChild && item.Position is not null && item.RotationDegrees is not null && item.Scale3 is not null)
            .Select(item =>
            {
                var world = Matrix4x4.CreateScale(item.Scale3!.Value) *
                    Matrix4x4.CreateFromQuaternion(QuaternionFromRadians(item.RotationDegrees!.Value * (MathF.PI / 180.0f))) *
                    Matrix4x4.CreateTranslation(item.Position!.Value);
                Matrix4x4.Decompose(world * inverseParent, out var scale, out var rotation, out var offset);
                return new MapAssetCatalogChild(item.Name, item.ModelPath, offset, RadiansToDegrees(EulerFromQuaternion(rotation)), scale);
            })
            .ToArray();
    }

    private void AddModel(
        List<MapAssetCatalogItem> results,
        LayerCommon.InstanceObject instance,
        string assetPath,
        string sourcePath,
        LuminaTransform? parentTransform,
        bool allowLiveOriginal,
        bool isSharedGroupChild)
    {
        var path = NormalizeGamePath(assetPath);
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return;

        var transform = Combine(parentTransform, instance.Transform);
        var liveOriginal = allowLiveOriginal && instance.InstanceId != 0;
        results.Add(new MapAssetCatalogItem(
            DisplayName(instance.Name, path),
            path,
            sourcePath,
            string.Empty,
            Position: ToNumerics(transform.Translation),
            RotationDegrees: RadiansToDegrees(ToNumerics(transform.Rotation)),
            Scale3: ToNumerics(transform.Scale),
            OriginalInstanceId: liveOriginal ? instance.InstanceId : 0,
            Kind: isSharedGroupChild ? MapAssetKind.SharedGroupChild : MapAssetKind.Model));
    }

    private static LuminaTransform Combine(LuminaTransform? parent, LuminaTransform child)
    {
        if (parent is null)
            return child;

        var parentValue = parent.Value;
        var childMatrix = TransformMatrix(child);
        var parentMatrix = TransformMatrix(parentValue);
        Matrix4x4.Decompose(childMatrix * parentMatrix, out var scale, out var rotation, out var translation);

        return new LuminaTransform
        {
            Translation = ToLumina(translation),
            Rotation = ToLumina(EulerFromQuaternion(rotation)),
            Scale = ToLumina(scale),
        };
    }

    private static Vector3 ToNumerics(LuminaVector3 vector)
        => new(vector.X, vector.Y, vector.Z);

    private static LuminaVector3 ToLumina(Vector3 vector)
        => new()
        {
            X = vector.X,
            Y = vector.Y,
            Z = vector.Z,
        };

    private static Vector3 RadiansToDegrees(Vector3 radians)
        => radians * (180.0f / MathF.PI);

    private static Quaternion QuaternionFromRadians(Vector3 radians)
        => Quaternion.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z);

    private static Matrix4x4 TransformMatrix(LuminaTransform transform)
        => Matrix4x4.CreateScale(ToNumerics(transform.Scale)) *
            Matrix4x4.CreateFromQuaternion(QuaternionFromRadians(ToNumerics(transform.Rotation))) *
            Matrix4x4.CreateTranslation(ToNumerics(transform.Translation));

    private static Vector3 EulerFromQuaternion(Quaternion value)
    {
        var x = MathF.Atan2(2.0f * ((value.W * value.X) + (value.Y * value.Z)),
            1.0f - (2.0f * ((value.X * value.X) + (value.Y * value.Y))));
        var sinY = Math.Clamp(2.0f * ((value.W * value.Y) - (value.Z * value.X)), -1.0f, 1.0f);
        var y = MathF.Asin(sinY);
        var z = MathF.Atan2(2.0f * ((value.W * value.Z) + (value.X * value.Y)),
            1.0f - (2.0f * ((value.Y * value.Y) + (value.Z * value.Z))));
        return new Vector3(x, y, z);
    }

    private LgbFile? GetLayerFile(string path)
        => Service.DataManager.GetFile<LgbFile>(path);

    private SgbFile? GetSharedGroupFile(string path)
        => Service.DataManager.GetFile<SgbFile>(path);

    private static string Categorize(string name, string modelPath, string sourcePath)
    {
        var text = TokenText(name, modelPath, sourcePath);

        if (TextSearch.ContainsAnyToken(text, "hou", "house", "door", "dor", "wall", "wal", "gate", "roof", "rof", "stair", "step", "pillar", "window"))
            return "Structures";

        if (TextSearch.ContainsAnyToken(text, "rock", "cave", "cav", "soil", "sand"))
            return "Rocks / Terrain";

        if (TextSearch.ContainsAnyToken(text, "tree", "tre", "wood", "bush", "grass", "plant", "leaf", "moss", "root"))
            return "Foliage";

        if (TextSearch.ContainsAnyToken(text, "water", "pond", "fall", "fnt"))
            return "Water";

        if (TextSearch.ContainsAnyToken(text, "lamp", "lmp", "light", "torch", "fire"))
            return "Lighting";

        if (TextSearch.ContainsAnyToken(text, "sign", "cart", "table", "chair", "book", "flag"))
            return "Props";

        if (TextSearch.ContainsAnyToken(text, "ship", "boat", "gear", "pipe", "rail", "lift", "elev"))
            return "Machinery / Vehicles";

        if (TextSearch.ContainsAnyToken(text, "vfx", "lightshaft"))
            return "Effects";

        return "Other";
    }

    private static bool IsZoneSetSharedGroup(string path, string name)
    {
        var text = TokenText(path, name);
        return text.Contains("zsg", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("zone set", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("zone group", StringComparison.OrdinalIgnoreCase);
    }

    private static string TokenText(params string[] values)
        => string.Join(' ', values)
            .Replace('\\', '/')
            .Replace('/', ' ')
            .Replace('_', ' ')
            .Replace('-', ' ')
            .ToLowerInvariant();

    private static string NormalizeGamePath(string path)
        => path.Replace('\\', '/').Trim();

    private static string EnsureBgPrefix(string path)
        => path.StartsWith("bg/", StringComparison.OrdinalIgnoreCase) ? path : $"bg/{path}";

    private static string DisplayName(string instanceName, string path)
    {
        if (!string.IsNullOrWhiteSpace(instanceName))
            return instanceName.Trim();

        var fileName = path.Split('/', '\\').LastOrDefault() ?? path;
        return Path.GetFileNameWithoutExtension(fileName);
    }
}
