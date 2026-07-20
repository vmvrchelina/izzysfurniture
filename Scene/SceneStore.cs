using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IzzysFurniture;

internal sealed class SceneStore
{
    private readonly FurnitureCatalog catalog;
    private readonly NpcCatalog npcCatalog = new();
    private readonly NpcAnimationCatalog npcAnimationCatalog = new();
    private readonly string sceneDirectory;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SceneStore(FurnitureCatalog catalog)
    {
        this.catalog = catalog;
        this.sceneDirectory = Path.Combine(Service.PluginInterface.GetPluginConfigDirectory(), "scenes");
        Directory.CreateDirectory(this.sceneDirectory);
    }

    public string DefaultDirectory => this.sceneDirectory;

    public string Save(string path, IReadOnlyList<SpawnedFurniture> props, IReadOnlyList<FurnitureFolder> folders, InteriorFixtureState interiorFixtures)
    {
        path = NormalizeSavePath(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, this.Serialize(props, folders, interiorFixtures));
        return path;
    }

    public static string NormalizeSavePath(string path)
        => EnsureJsonExtension(path);

    public string Serialize(IReadOnlyList<SpawnedFurniture> props, IReadOnlyList<FurnitureFolder> folders, InteriorFixtureState interiorFixtures)
    {
        // disk saves and relay snapshots
        var scene = new SavedScene
        {
            Version = 1,
            Folders = folders.Select(SavedFolder.FromFolder).ToList(),
            Props = props.Select(SavedProp.FromSpawned).ToList(),
            InteriorFixtures = interiorFixtures.Selections
                .Select(SavedInteriorFixture.FromSelection)
                .ToList(),
        };

        return JsonSerializer.Serialize(scene, this.jsonOptions);
    }

    public LoadSceneResult Load(string path)
    {
        if (!File.Exists(path))
            return new LoadSceneResult([], [], [], 0, "scene file not found");

        return this.LoadJson(File.ReadAllText(path));
    }

    public LoadSceneResult LoadJson(string json)
    {
        // trust boundary
        var scene = JsonSerializer.Deserialize<SavedScene>(json, this.jsonOptions);
        if (scene is null || scene.Version != 1)
            return new LoadSceneResult([], [], [], 0, "unrecognized scene format");

        if (scene.Folders.Any(folder => folder.Id == Guid.Empty || string.IsNullOrWhiteSpace(folder.Name)))
            return new LoadSceneResult([], [], [], 0, "scene folder data is invalid");

        if (scene.Folders.Select(folder => folder.Id).Distinct().Count() != scene.Folders.Count)
            return new LoadSceneResult([], [], [], 0, "scene folder data is invalid");

        var folders = scene.Folders
            .Select(folder => folder.ToFolder())
            .ToList();
        var folderIds = folders.Select(folder => folder.Id).ToHashSet();
        var props = new List<SpawnedFurniture>(scene.Props.Count);
        var skipped = 0;

        foreach (var saved in scene.Props)
        {
            var prop = this.ToSpawned(saved);
            if (prop is null)
            {
                skipped++;
                continue;
            }

            if (prop.FolderId is { } folderId && !folderIds.Contains(folderId))
            {
                skipped++;
                continue;
            }

            props.Add(prop);
        }

        var fixtures = scene.InteriorFixtures
            .Select(fixture => fixture.ToSelection())
            .Where(selection => selection.FixtureId != 0)
            .ToArray();

        return new LoadSceneResult(props, folders, fixtures, skipped, "");
    }

    private SpawnedFurniture? ToSpawned(SavedProp saved)
    {
        if (saved.FolderId == Guid.Empty ||
            !saved.Position.IsFinite ||
            !saved.RotationDegrees.IsFinite ||
            !saved.Scale3.IsPositiveFinite)
            return null;

        // collection limits
        if (saved.StainIds.Count > SpawnedFurniture.MaxDyeChannels ||
            saved.DyeColors.Count > SpawnedFurniture.MaxDyeChannels ||
            saved.DyeColorEnabled.Count > SpawnedFurniture.MaxDyeChannels)
            return null;

        if (saved.ForcedMaterialStainIds.Count > SpawnedFurniture.MaxForcedDyeMaterials ||
            saved.ForcedMaterialDyeColors.Count > SpawnedFurniture.MaxForcedDyeMaterials ||
            saved.ForcedMaterialDyeColorEnabled.Count > SpawnedFurniture.MaxForcedDyeMaterials)
            return null;

        if (string.Equals(saved.Kind, SavedProp.FxEmitterKind, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(saved.ModelPath) ||
                !saved.ModelPath.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase) ||
                !Service.DataManager.FileExists(saved.ModelPath))
            {
                return null;
            }

            var prop = new SpawnedFurniture(
                new FxCatalogItem(saved.Name, saved.ModelPath, saved.Category),
                saved.Position.ToVector3());
            ApplySavedTransform(prop, saved);
            prop.FxColor = saved.FxColor.ToVector4();
            return prop;
        }

        if (string.Equals(saved.Kind, SavedProp.NpcKind, StringComparison.OrdinalIgnoreCase))
        {
            // resolve stable sheet ids again
            var npcItem = this.npcCatalog.Find(saved.NpcSourceKind, saved.NpcRowId);

            if (npcItem is null)
                return null;

            var prop = new SpawnedFurniture(npcItem, saved.Position.ToVector3());
            ApplySavedTransform(prop, saved);
            return saved.ApplyNpcSettings(prop, this.npcAnimationCatalog, npcItem.DisplayKind) ? prop : null;
        }

        if (string.Equals(saved.Kind, SavedProp.FurnitureKind, StringComparison.OrdinalIgnoreCase))
        {
            if (saved.SourceKind is not { } sourceKind || saved.ItemId == 0)
                return null;

            var catalogItem = this.catalog.Find(saved.ItemId, sourceKind);
            if (catalogItem is null)
                return null;

            var prop = new SpawnedFurniture(catalogItem, saved.Position.ToVector3());
            ApplySavedTransform(prop, saved);
            return prop;
        }

        if (string.Equals(saved.Kind, SavedProp.MapAssetKind, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(saved.ModelPath) ||
                !saved.ModelPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ||
                !Service.DataManager.FileExists(saved.ModelPath))
            {
                return null;
            }

            var prop = new SpawnedFurniture(
                new MapAssetCatalogItem(saved.Name, saved.ModelPath, saved.SourcePath, saved.Category, Kind: saved.MapAssetItemKind),
                saved.Position.ToVector3());
            ApplySavedTransform(prop, saved);
            return prop;
        }

        return null;
    }

    private static string EnsureJsonExtension(string path)
    {
        path = path.Trim();
        if (string.IsNullOrWhiteSpace(Path.GetExtension(path)))
            return $"{path}.json";

        return path;
    }

    private static void ApplySavedTransform(SpawnedFurniture prop, SavedProp saved)
    {
        prop.Position = saved.Position.ToVector3();
        prop.FolderId = saved.FolderId;
        prop.RotationDegrees = saved.RotationDegrees.ToVector3();
        prop.SetScale(saved.Scale3.ToVector3());
        if (saved.StainIds.Count > 0)
            prop.SetStainIds(saved.StainIds);
        if (saved.DyeColors.Count > 0)
            prop.SetDyeColors(saved.DyeColors.Select(color => color.ToVector4()));
        if (saved.DyeColorEnabled.Count > 0)
            prop.SetDyeColorEnabled(saved.DyeColorEnabled);
        prop.ForcedDyesEnabled = saved.ForcedDyesEnabled;
        if (saved.ForcedMaterialStainIds.Count > 0)
            prop.SetForcedMaterialStainIds(saved.ForcedMaterialStainIds);
        if (saved.ForcedMaterialDyeColors.Count > 0)
            prop.SetForcedMaterialDyeColors(saved.ForcedMaterialDyeColors.Select(color => color.ToVector4()));
        if (saved.ForcedMaterialDyeColorEnabled.Count > 0)
            prop.SetForcedMaterialDyeColorEnabled(saved.ForcedMaterialDyeColorEnabled);
        prop.Enabled = saved.Enabled;
    }

    private sealed class SavedScene
    {
        [JsonRequired]
        public int Version { get; set; }
        [JsonRequired]
        public List<SavedFolder> Folders { get; set; } = [];
        [JsonRequired]
        public List<SavedProp> Props { get; set; } = [];
        [JsonRequired]
        public List<SavedInteriorFixture> InteriorFixtures { get; set; } = [];
    }

    private sealed class SavedFolder
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsOpen { get; set; } = true;

        public static SavedFolder FromFolder(FurnitureFolder folder)
            => new()
            {
                Id = folder.Id,
                Name = folder.Name,
                IsOpen = folder.IsOpen,
            };

        public FurnitureFolder ToFolder()
            => new(this.Id, this.Name, this.IsOpen);
    }

    private sealed class SavedProp
    {
        public const string FurnitureKind = "Furniture";
        public const string MapAssetKind = "MapAsset";
        public const string FxEmitterKind = "FxEmitter";
        public const string NpcKind = "Npc";

        [JsonRequired]
        public string Kind { get; set; } = string.Empty;
        [JsonRequired]
        public string Name { get; set; } = string.Empty;
        [JsonRequired]
        public string ModelPath { get; set; } = string.Empty;
        public Guid? FolderId { get; set; }
        public bool Enabled { get; set; } = true;
        public SavedVector3 Position { get; set; }
        public SavedVector3 RotationDegrees { get; set; }
        public SavedVector3 Scale3 { get; set; }
        [JsonRequired]
        public List<byte> StainIds { get; set; } = [];
        [JsonRequired]
        public List<SavedColor> DyeColors { get; set; } = [];
        [JsonRequired]
        public List<bool> DyeColorEnabled { get; set; } = [];
        public bool ForcedDyesEnabled { get; set; }
        [JsonRequired]
        public List<byte> ForcedMaterialStainIds { get; set; } = [];
        [JsonRequired]
        public List<SavedColor> ForcedMaterialDyeColors { get; set; } = [];
        [JsonRequired]
        public List<bool> ForcedMaterialDyeColorEnabled { get; set; } = [];
        public SavedColor FxColor { get; set; } = SavedColor.FromVector4(Vector4.One);

        public NpcSourceKind NpcSourceKind { get; set; }
        public uint NpcRowId { get; set; }
        public ushort NpcTimelineId { get; set; }
        public bool NpcLoopAnimation { get; set; } = true;
        public bool NpcPatrolEnabled { get; set; }
        public float NpcPatrolSpeed { get; set; } = 1.5f;
        public bool NpcPatrolLoop { get; set; } = true;
        public bool NpcPatrolSnapToTerrain { get; set; } = true;
        [JsonRequired]
        public List<SavedVector3> NpcPatrolPoints { get; set; } = [];
        public bool NpcSpeechEnabled { get; set; }
        public string NpcSpeechText { get; set; } = string.Empty;
        public float NpcSpeechIntervalSeconds { get; set; } = 10.0f;
        public float NpcSpeechTriggerDistance { get; set; } = 5.0f;
        public float NpcSpeechDurationSeconds { get; set; } = 4.0f;
        public bool NpcSpeechRequirePlayerNear { get; set; } = true;
        public Guid NpcPenumbraCollectionId { get; set; }
        public string NpcPenumbraCollectionName { get; set; } = string.Empty;
        public Guid NpcGlamourerDesignId { get; set; }
        public string NpcGlamourerDesignName { get; set; } = string.Empty;
        public string NpcGlamourerStateBase64 { get; set; } = string.Empty;
        public Guid NpcCustomizePlusProfileId { get; set; }
        public string NpcCustomizePlusProfileName { get; set; } = string.Empty;
        public string NpcCustomizePlusProfileJson { get; set; } = string.Empty;

        public uint ItemId { get; set; }
        public FurnitureSourceKind? SourceKind { get; set; }

        public string SourcePath { get; set; } = string.Empty;
        [JsonRequired]
        public string Category { get; set; } = string.Empty;
        [JsonRequired]
        public IzzysFurniture.MapAssetKind MapAssetItemKind { get; set; }

        public bool ApplyNpcSettings(SpawnedFurniture prop, NpcAnimationCatalog animations, NpcDisplayKind displayKind)
        {
            if (this.NpcTimelineId != 0)
            {
                prop.NpcAnimation = animations.Find(this.NpcTimelineId);
                if (prop.NpcAnimation is null || !prop.NpcAnimation.Supports(displayKind))
                    return false;
            }

            prop.NpcLoopAnimation = this.NpcLoopAnimation;

            prop.NpcPatrolEnabled = this.NpcPatrolEnabled;
            prop.NpcPatrolSpeed = this.NpcPatrolSpeed;
            prop.NpcPatrolLoop = this.NpcPatrolLoop;
            prop.NpcPatrolSnapToTerrain = this.NpcPatrolSnapToTerrain;
            prop.NpcPatrolPoints.AddRange(this.NpcPatrolPoints.Select(point => new NpcPatrolPoint(point.ToVector3())));

            prop.NpcSpeechEnabled = this.NpcSpeechEnabled;
            prop.NpcSpeechText = this.NpcSpeechText;
            prop.NpcSpeechIntervalSeconds = this.NpcSpeechIntervalSeconds;
            prop.NpcSpeechTriggerDistance = this.NpcSpeechTriggerDistance;
            prop.NpcSpeechDurationSeconds = this.NpcSpeechDurationSeconds;
            prop.NpcSpeechRequirePlayerNear = this.NpcSpeechRequirePlayerNear;

            prop.NpcPenumbraCollectionId = this.NpcPenumbraCollectionId;
            prop.NpcPenumbraCollectionName = this.NpcPenumbraCollectionName;
            prop.NpcGlamourerDesignId = this.NpcGlamourerDesignId;
            prop.NpcGlamourerDesignName = this.NpcGlamourerDesignName;
            prop.NpcGlamourerStateBase64 = this.NpcGlamourerStateBase64;
            prop.NpcCustomizePlusProfileId = this.NpcCustomizePlusProfileId;
            prop.NpcCustomizePlusProfileName = this.NpcCustomizePlusProfileName;
            prop.NpcCustomizePlusProfileJson = this.NpcCustomizePlusProfileJson;
            return true;
        }

        private void CaptureNpcSettings(SpawnedFurniture prop)
        {
            if (prop.NpcAnimation is not null)
                this.NpcTimelineId = prop.NpcAnimation.TimelineId;
            this.NpcLoopAnimation = prop.NpcLoopAnimation;

            this.NpcPatrolEnabled = prop.NpcPatrolEnabled;
            this.NpcPatrolSpeed = prop.NpcPatrolSpeed;
            this.NpcPatrolLoop = prop.NpcPatrolLoop;
            this.NpcPatrolSnapToTerrain = prop.NpcPatrolSnapToTerrain;
            this.NpcPatrolPoints = prop.NpcPatrolPoints.Select(point => SavedVector3.FromVector3(point.Position)).ToList();

            this.NpcSpeechEnabled = prop.NpcSpeechEnabled;
            this.NpcSpeechText = prop.NpcSpeechText;
            this.NpcSpeechIntervalSeconds = prop.NpcSpeechIntervalSeconds;
            this.NpcSpeechTriggerDistance = prop.NpcSpeechTriggerDistance;
            this.NpcSpeechDurationSeconds = prop.NpcSpeechDurationSeconds;
            this.NpcSpeechRequirePlayerNear = prop.NpcSpeechRequirePlayerNear;

            this.NpcPenumbraCollectionId = prop.NpcPenumbraCollectionId;
            this.NpcPenumbraCollectionName = prop.NpcPenumbraCollectionName;
            this.NpcGlamourerDesignId = prop.NpcGlamourerDesignId;
            this.NpcGlamourerDesignName = prop.NpcGlamourerDesignName;
            this.NpcGlamourerStateBase64 = prop.NpcGlamourerStateBase64;
            this.NpcCustomizePlusProfileId = prop.NpcCustomizePlusProfileId;
            this.NpcCustomizePlusProfileName = prop.NpcCustomizePlusProfileName;
            this.NpcCustomizePlusProfileJson = prop.NpcCustomizePlusProfileJson;
        }

        public static SavedProp FromSpawned(SpawnedFurniture prop)
        {
            var saved = new SavedProp
            {
                Kind = prop.MapAssetItem is not null ? MapAssetKind : FurnitureKind,
                Name = prop.Name,
                ModelPath = prop.ModelPath,
                FolderId = prop.FolderId,
                Enabled = prop.Enabled,
                Position = SavedVector3.FromVector3(prop.Position),
                RotationDegrees = SavedVector3.FromVector3(prop.RotationDegrees),
                Scale3 = SavedVector3.FromVector3(prop.Scale3),
                StainIds = prop.StainIds.Take(prop.DyeChannelCount).ToList(),
                DyeColors = prop.DyeColors.Take(prop.DyeChannelCount).Select(SavedColor.FromVector4).ToList(),
                DyeColorEnabled = prop.DyeColorEnabled.Take(prop.DyeChannelCount).ToList(),
                ForcedDyesEnabled = prop.ForcedDyesEnabled,
                ForcedMaterialStainIds = prop.ForcedMaterialStainIds.Take(SpawnedFurniture.MaxForcedDyeMaterials).ToList(),
                ForcedMaterialDyeColors = prop.ForcedMaterialDyeColors.Take(SpawnedFurniture.MaxForcedDyeMaterials).Select(SavedColor.FromVector4).ToList(),
                ForcedMaterialDyeColorEnabled = prop.ForcedMaterialDyeColorEnabled.Take(SpawnedFurniture.MaxForcedDyeMaterials).ToList(),
                FxColor = SavedColor.FromVector4(prop.FxColor),
            };

            if (prop.IsFxEmitter)
                saved.Kind = FxEmitterKind;

            if (prop.IsNpc && prop.NpcItem is not null)
            {
                saved.Kind = NpcKind;
                saved.NpcSourceKind = prop.NpcItem.SourceKind;
                saved.NpcRowId = prop.NpcItem.RowId;
                saved.CaptureNpcSettings(prop);
            }

            if (prop.CatalogItem is not null)
            {
                saved.ItemId = prop.CatalogItem.ItemId;
                saved.SourceKind = prop.CatalogItem.SourceKind;
            }

            if (prop.MapAssetItem is not null)
            {
                saved.SourcePath = prop.MapAssetItem.SourcePath;
                saved.Category = prop.MapAssetItem.Category;
                saved.MapAssetItemKind = prop.MapAssetItem.Kind;
            }

            if (prop.FxItem is not null)
                saved.Category = prop.FxItem.Category;

            return saved;
        }
    }

    private readonly record struct SavedVector3(float X, float Y, float Z)
    {
        public bool IsFinite => float.IsFinite(this.X) && float.IsFinite(this.Y) && float.IsFinite(this.Z);
        public bool IsPositiveFinite => this.IsFinite && this.X > 0 && this.Y > 0 && this.Z > 0;

        public static SavedVector3 FromVector3(Vector3 value)
            => new(value.X, value.Y, value.Z);

        public Vector3 ToVector3()
            => new(this.X, this.Y, this.Z);
    }

    private readonly record struct SavedColor(float R, float G, float B, float A)
    {
        public static SavedColor FromVector4(Vector4 value)
            => new(value.X, value.Y, value.Z, value.W);

        public Vector4 ToVector4()
            => new(this.R, this.G, this.B, this.A);
    }

    private sealed class SavedInteriorFixture
    {
        public InteriorFixtureFloor Floor { get; set; }
        public InteriorFixturePart Part { get; set; }
        public uint FixtureId { get; set; }

        public static SavedInteriorFixture FromSelection(InteriorFixtureSelection selection)
            => new()
            {
                Floor = selection.Floor,
                Part = selection.Part,
                FixtureId = selection.FixtureId,
            };

        public InteriorFixtureSelection ToSelection()
            => new(this.Floor, this.Part, this.FixtureId);
    }
}

internal sealed record LoadSceneResult(
    IReadOnlyList<SpawnedFurniture> Props,
    IReadOnlyList<FurnitureFolder> Folders,
    IReadOnlyList<InteriorFixtureSelection> InteriorFixtures,
    int Skipped,
    string Error);
