using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IzzysFurniture;

internal sealed class MyCatalogueStore
{
    private readonly string path;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly List<MyCatalogue> catalogues = new();

    public MyCatalogueStore()
    {
        path = Path.Combine(Service.PluginInterface.GetPluginConfigDirectory(), "my-catalogues.json");
        Load();
    }

    public IReadOnlyList<MyCatalogue> Catalogues => catalogues;

    public MyCatalogue EnsureDefault()
    {
        if (catalogues.Count > 0)
            return catalogues[0];

        var catalogue = new MyCatalogue(Guid.NewGuid(), "My Catalogue");
        catalogues.Add(catalogue);
        Save();
        return catalogue;
    }

    public MyCatalogue AddCatalogue(string name)
    {
        name = CleanName(name);
        var catalogue = new MyCatalogue(Guid.NewGuid(), UniqueName(name, catalogues.Select(item => item.Name)));
        catalogues.Add(catalogue);
        Save();
        return catalogue;
    }

    public void RemoveCatalogue(Guid id)
    {
        var catalogue = catalogues.FirstOrDefault(item => item.Id == id);
        if (catalogue == null)
            return;

        catalogues.Remove(catalogue);
        Save();
    }

    public MyCatalogueItem AddMapAsset(Guid catalogueId, SpawnedFurniture spawned, string displayName)
    {
        var catalogue = catalogues.Single(item => item.Id == catalogueId);
        var mapAsset = spawned.MapAssetItem ?? throw new InvalidOperationException("not a map asset");
        var item = new MyCatalogueItem(
            Guid.NewGuid(),
            CleanName(displayName),
            spawned.ModelPath,
            mapAsset.SourcePath,
            mapAsset.Category,
            mapAsset.Kind)
        {
            ItemKind = MyCatalogueItem.MapAssetItemKind,
        };

        catalogue.Items.Add(item);
        Save();
        return item;
    }

    public MyCatalogueItem AddFxEmitter(Guid catalogueId, SpawnedFurniture spawned, string displayName)
    {
        var catalogue = catalogues.Single(item => item.Id == catalogueId);
        var fxItem = spawned.FxItem ?? throw new InvalidOperationException("not an fx emitter");
        var item = new MyCatalogueItem(
            Guid.NewGuid(),
            CleanName(displayName),
            spawned.ModelPath,
            "",
            fxItem.Category)
        {
            ItemKind = MyCatalogueItem.FxEmitterItemKind,
        };

        catalogue.Items.Add(item);
        Save();
        return item;
    }

    public void RemoveItem(Guid catalogueId, Guid itemId)
    {
        var catalogue = catalogues.FirstOrDefault(item => item.Id == catalogueId);
        if (catalogue == null)
            return;

        catalogue.Items.RemoveAll(item => item.Id == itemId);
        Save();
    }

    public MapAssetCatalogItem ToMapAsset(MyCatalogueItem item)
        => new(
            item.Name,
            item.ModelPath,
            item.SourcePath,
            item.Category,
            Kind: item.Kind);

    public FxCatalogItem ToFxEmitter(MyCatalogueItem item)
        => new(
            item.Name,
            item.ModelPath,
            item.Category);

    private void Load()
    {
        try
        {
            if (!File.Exists(path))
                return;

            var data = JsonSerializer.Deserialize<MyCatalogueData>(File.ReadAllText(path), jsonOptions);
            if (data == null || data.Version != 1)
                return;

            if (data.Catalogues.Any(catalogue =>
                    catalogue.Id == Guid.Empty ||
                    string.IsNullOrWhiteSpace(catalogue.Name) ||
                    catalogue.Items.Any(item =>
                        item.Id == Guid.Empty ||
                        string.IsNullOrWhiteSpace(item.Name) ||
                        string.IsNullOrWhiteSpace(item.ModelPath))))
            {
                Service.Log.Error("my-catalogues.json failed validation, ignoring it");
                return;
            }

            foreach (var catalogue in data.Catalogues)
                catalogue.Name = catalogue.Name.Trim();

            catalogues.AddRange(data.Catalogues);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            Service.Log.Error(error, "could not read my catalogues");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new MyCatalogueData { Version = 1, Catalogues = catalogues }, jsonOptions));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Service.Log.Error(error, "could not save my catalogues");
        }
    }

    private static string CleanName(string name)
    {
        name = name.Trim();
        return name.Length <= 96 ? name : name[..96];
    }

    private static string UniqueName(string baseName, IEnumerable<string> existing)
    {
        var names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
            return baseName;

        for (var index = 2; ; index++)
        {
            var candidate = $"{baseName} {index}";
            if (!names.Contains(candidate))
                return candidate;
        }
    }

    private sealed class MyCatalogueData
    {
        public int Version { get; set; }
        [JsonRequired]
        public List<MyCatalogue> Catalogues { get; set; } = [];
    }
}

internal sealed class MyCatalogue(Guid id, string name)
{
    [JsonRequired]
    public Guid Id { get; set; } = id;
    [JsonRequired]
    public string Name { get; set; } = name;
    [JsonRequired]
    public List<MyCatalogueItem> Items { get; set; } = [];
}

internal sealed class MyCatalogueItem(Guid id, string name, string modelPath, string sourcePath, string category, MapAssetKind kind = MapAssetKind.Model)
{
    public const string MapAssetItemKind = "MapAsset";
    public const string FxEmitterItemKind = "FxEmitter";

    [JsonRequired]
    public Guid Id { get; set; } = id;
    [JsonRequired]
    public string Name { get; set; } = name;
    [JsonRequired]
    public string ItemKind { get; set; } = MapAssetItemKind;
    [JsonRequired]
    public string ModelPath { get; set; } = modelPath;
    [JsonRequired]
    public string SourcePath { get; set; } = sourcePath;
    [JsonRequired]
    public string Category { get; set; } = category;
    [JsonRequired]
    public MapAssetKind Kind { get; set; } = kind;
    public bool IsFxEmitter => ItemKind.Equals(FxEmitterItemKind, StringComparison.OrdinalIgnoreCase) ||
        ModelPath.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase);
}
