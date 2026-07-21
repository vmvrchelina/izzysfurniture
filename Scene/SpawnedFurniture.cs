using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace IzzysFurniture;

internal sealed class SpawnedFurniture
{
    public const int MaxDyeChannels = 4;
    public const int MaxForcedDyeMaterials = 32;

    private readonly byte[] stainIds = new byte[MaxDyeChannels];
    private readonly byte[] forcedMaterialStainIds = new byte[MaxForcedDyeMaterials];
    private readonly bool[] dyeColorEnabled = new bool[MaxDyeChannels];
    private readonly Vector4[] dyeColors = Enumerable.Repeat(Vector4.One, MaxDyeChannels).ToArray();
    private readonly bool[] forcedMaterialDyeColorEnabled = new bool[MaxForcedDyeMaterials];
    private readonly Vector4[] forcedMaterialDyeColors = Enumerable.Repeat(Vector4.One, MaxForcedDyeMaterials).ToArray();

    public SpawnedFurniture(FurnitureCatalogItem catalogItem, Vector3 position)
    {
        this.Id = Guid.NewGuid();
        this.CatalogItem = catalogItem;
        this.Name = catalogItem.Name;
        this.ModelPath = catalogItem.ModelPath;
        this.Position = position;
    }

    public SpawnedFurniture(MapAssetCatalogItem mapAssetItem, Vector3 position)
    {
        this.Id = Guid.NewGuid();
        this.MapAssetItem = mapAssetItem;
        this.Name = mapAssetItem.Name;
        this.ModelPath = mapAssetItem.ModelPath;
        this.Position = position;
    }

    public SpawnedFurniture(FxCatalogItem fxItem, Vector3 position)
    {
        this.Id = Guid.NewGuid();
        this.FxItem = fxItem;
        this.Name = fxItem.Name;
        this.ModelPath = fxItem.Path;
        this.Position = position;
    }

    public SpawnedFurniture(NpcCatalogItem npcItem, Vector3 position)
    {
        this.Id = Guid.NewGuid();
        this.NpcItem = npcItem;
        this.Name = npcItem.Name;
        this.NpcName = npcItem.Name;
        this.ModelPath = npcItem.StableKey;
        this.Position = position;
    }

    private SpawnedFurniture(SpawnedFurniture source, Vector3 position)
    {
        this.Id = Guid.NewGuid();
        this.CatalogItem = source.CatalogItem;
        this.MapAssetItem = source.MapAssetItem;
        this.FxItem = source.FxItem;
        this.NpcItem = source.NpcItem;
        this.Name = source.Name;
        this.ModelPath = source.ModelPath;
        this.FolderId = source.FolderId;
        this.Position = position;
        this.RotationDegrees = source.RotationDegrees;
        this.Scale3 = source.Scale3;
        this.Enabled = source.Enabled;
        this.ForcedDyesEnabled = source.ForcedDyesEnabled;
        this.FxColor = source.FxColor;
        this.CopyNpcSettingsFrom(source);

        Array.Copy(source.stainIds, this.stainIds, this.stainIds.Length);
        Array.Copy(source.forcedMaterialStainIds, this.forcedMaterialStainIds, this.forcedMaterialStainIds.Length);
        Array.Copy(source.dyeColorEnabled, this.dyeColorEnabled, this.dyeColorEnabled.Length);
        Array.Copy(source.dyeColors, this.dyeColors, this.dyeColors.Length);
        Array.Copy(source.forcedMaterialDyeColorEnabled, this.forcedMaterialDyeColorEnabled, this.forcedMaterialDyeColorEnabled.Length);
        Array.Copy(source.forcedMaterialDyeColors, this.forcedMaterialDyeColors, this.forcedMaterialDyeColors.Length);
    }

    public Guid Id { get; }
    public FurnitureCatalogItem? CatalogItem { get; }
    public MapAssetCatalogItem? MapAssetItem { get; }
    public FxCatalogItem? FxItem { get; }
    public NpcCatalogItem? NpcItem { get; }
    public string Name { get; }
    public string ModelPath { get; set; } = string.Empty;
    public Guid? FolderId { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 RotationDegrees { get; set; }
    public Vector3 Scale3 { get; set; } = Vector3.One;
    public float Scale
    {
        get => (MathF.Abs(this.Scale3.X) + MathF.Abs(this.Scale3.Y) + MathF.Abs(this.Scale3.Z)) / 3.0f;
        set => this.Scale3 = new Vector3(ClampScale(value));
    }
    public byte StainId
    {
        get => this.stainIds[0];
        set => this.stainIds[0] = value;
    }

    public IReadOnlyList<byte> StainIds => this.stainIds;
    public IReadOnlyList<bool> DyeColorEnabled => this.dyeColorEnabled;
    public IReadOnlyList<Vector4> DyeColors => this.dyeColors;

    public bool Enabled { get; set; } = true;
    public bool ForcedDyesEnabled { get; set; }
    public Vector4 FxColor { get; set; } = Vector4.One;

    public int DyeChannelCount => Math.Clamp(this.CatalogItem?.DyeChannelCount ?? 0, 0, MaxDyeChannels);

    public bool SupportsFurnitureDye => this.DyeChannelCount > 0;

    public bool IsFxEmitter => this.FxItem is not null || this.ModelPath.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase);
    public bool IsNpc => this.NpcItem is not null;

    public string NpcName { get; set; } = string.Empty;
    public string NpcTitle { get; set; } = string.Empty;
    public Vector4 NpcNameColor { get; set; } = Vector4.One;
    public Vector4 NpcTitleColor { get; set; } = new(0.53f, 0.82f, 1.0f, 1.0f);
    public Vector4 NpcNameplateOutlineColor { get; set; } = new(0.05f, 0.2f, 0.65f, 1.0f);
    public float NpcNameSize { get; set; } = 20.0f;
    public float NpcTitleSize { get; set; } = 16.0f;
    public float NpcNameplateOutlineThickness { get; set; } = 1.5f;
    public NpcAnimationCatalogItem? NpcAnimation { get; set; }
    public bool NpcLoopAnimation { get; set; } = true;

    public bool NpcPatrolEnabled { get; set; }
    public float NpcPatrolSpeed { get; set; } = 1.5f;
    public bool NpcPatrolLoop { get; set; } = true;
    public bool NpcPatrolSnapToTerrain { get; set; } = true;
    public List<NpcPatrolPoint> NpcPatrolPoints { get; } = [];

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

    public IReadOnlyList<byte> ForcedMaterialStainIds => this.forcedMaterialStainIds;
    public IReadOnlyList<bool> ForcedMaterialDyeColorEnabled => this.forcedMaterialDyeColorEnabled;
    public IReadOnlyList<Vector4> ForcedMaterialDyeColors => this.forcedMaterialDyeColors;

    public float YawRadians => this.RotationDegrees.Y * MathF.PI / 180.0f;

    public SpawnedFurniture DuplicateAt(Vector3 position)
        => new(this, position);

    public void AddScale(Vector3 delta)
        => this.Scale3 = ClampScale(this.Scale3 + delta);

    public void SetScale(Vector3 scale)
        => this.Scale3 = ClampScale(scale);

    public byte GetStainId(int channel)
        => this.stainIds[channel];

    public void SetStainId(int channel, byte stainId)
        => this.stainIds[channel] = stainId;

    public void SetStainIds(IEnumerable<byte> stains)
        => CopyInto(this.stainIds, stains, clear: true);

    public bool IsDyeColorEnabled(int channel)
        => this.dyeColorEnabled[channel];

    public void SetDyeColorEnabled(int channel, bool enabled)
        => this.dyeColorEnabled[channel] = enabled;

    public void SetDyeColorEnabled(IEnumerable<bool> enabled)
        => CopyInto(this.dyeColorEnabled, enabled, clear: true);

    public Vector4 GetDyeColor(int channel)
        => this.dyeColors[channel];

    public void SetDyeColor(int channel, Vector4 color)
        => this.dyeColors[channel] = ColorMath.Clamp01(color);

    public void SetDyeColors(IEnumerable<Vector4> colors)
        => CopyInto(this.dyeColors, colors.Select(ColorMath.Clamp01), clear: false);

    public byte GetForcedMaterialStainId(int materialIndex)
        => this.forcedMaterialStainIds[materialIndex];

    public void SetForcedMaterialStainId(int materialIndex, byte stainId)
        => this.forcedMaterialStainIds[materialIndex] = stainId;

    public void SetForcedMaterialStainIds(IEnumerable<byte> stains)
        => CopyInto(this.forcedMaterialStainIds, stains, clear: true);

    public bool IsForcedMaterialDyeColorEnabled(int materialIndex)
        => this.forcedMaterialDyeColorEnabled[materialIndex];

    public void SetForcedMaterialDyeColorEnabled(int materialIndex, bool enabled)
        => this.forcedMaterialDyeColorEnabled[materialIndex] = enabled;

    public void SetForcedMaterialDyeColorEnabled(IEnumerable<bool> enabled)
        => CopyInto(this.forcedMaterialDyeColorEnabled, enabled, clear: true);

    public Vector4 GetForcedMaterialDyeColor(int materialIndex)
        => this.forcedMaterialDyeColors[materialIndex];

    public void SetForcedMaterialDyeColor(int materialIndex, Vector4 color)
        => this.forcedMaterialDyeColors[materialIndex] = ColorMath.Clamp01(color);

    public void SetForcedMaterialDyeColors(IEnumerable<Vector4> colors)
        => CopyInto(this.forcedMaterialDyeColors, colors.Select(ColorMath.Clamp01), clear: false);

    // zero is da natural state
    public ulong PrimaryDyeStateSignature()
        => this.IsDyeColorEnabled(0)
            ? 0x100000000UL | PackColor(this.GetDyeColor(0))
            : this.GetStainId(0);

    public ulong ForcedDyeStateSignature(int materialCount)
    {
        // hash is local change detection and is never serialized or relayed
        var count = Math.Clamp(materialCount, 0, MaxForcedDyeMaterials);
        var signature = 14695981039346656037UL;
        signature = (signature ^ (uint)count) * 1099511628211UL;
        for (var index = 0; index < count; index++)
        {
            var value = this.IsForcedMaterialDyeColorEnabled(index)
                ? 0x100000000UL | PackColor(this.GetForcedMaterialDyeColor(index))
                : this.GetForcedMaterialStainId(index);
            signature = (signature ^ value) * 1099511628211UL;
        }

        return signature;
    }

    private void CopyNpcSettingsFrom(SpawnedFurniture source)
    {
        this.NpcName = source.NpcName;
        this.NpcTitle = source.NpcTitle;
        this.NpcNameColor = source.NpcNameColor;
        this.NpcTitleColor = source.NpcTitleColor;
        this.NpcNameplateOutlineColor = source.NpcNameplateOutlineColor;
        this.NpcNameSize = source.NpcNameSize;
        this.NpcTitleSize = source.NpcTitleSize;
        this.NpcNameplateOutlineThickness = source.NpcNameplateOutlineThickness;
        this.NpcAnimation = source.NpcAnimation;
        this.NpcLoopAnimation = source.NpcLoopAnimation;

        this.NpcPatrolEnabled = source.NpcPatrolEnabled;
        this.NpcPatrolSpeed = source.NpcPatrolSpeed;
        this.NpcPatrolLoop = source.NpcPatrolLoop;
        this.NpcPatrolSnapToTerrain = source.NpcPatrolSnapToTerrain;
        this.NpcPatrolPoints.Clear();
        this.NpcPatrolPoints.AddRange(source.NpcPatrolPoints.Select(point => new NpcPatrolPoint(point.Position)));

        this.NpcSpeechEnabled = source.NpcSpeechEnabled;
        this.NpcSpeechText = source.NpcSpeechText;
        this.NpcSpeechIntervalSeconds = source.NpcSpeechIntervalSeconds;
        this.NpcSpeechTriggerDistance = source.NpcSpeechTriggerDistance;
        this.NpcSpeechDurationSeconds = source.NpcSpeechDurationSeconds;
        this.NpcSpeechRequirePlayerNear = source.NpcSpeechRequirePlayerNear;

        this.NpcPenumbraCollectionId = source.NpcPenumbraCollectionId;
        this.NpcPenumbraCollectionName = source.NpcPenumbraCollectionName;
        this.NpcGlamourerDesignId = source.NpcGlamourerDesignId;
        this.NpcGlamourerDesignName = source.NpcGlamourerDesignName;
        this.NpcGlamourerStateBase64 = source.NpcGlamourerStateBase64;
        this.NpcCustomizePlusProfileId = source.NpcCustomizePlusProfileId;
        this.NpcCustomizePlusProfileName = source.NpcCustomizePlusProfileName;
        this.NpcCustomizePlusProfileJson = source.NpcCustomizePlusProfileJson;
    }

    private static float ClampScale(float scale)
        => Math.Clamp(scale, 0.05f, 10.0f);

    private static void CopyInto<T>(T[] destination, IEnumerable<T> source, bool clear)
    {
        if (clear)
            Array.Clear(destination);

        var index = 0;
        foreach (var value in source)
            destination[index++] = value;
    }

    private static Vector3 ClampScale(Vector3 scale)
        => new(ClampScale(scale.X), ClampScale(scale.Y), ClampScale(scale.Z));

    private static uint PackColor(Vector4 color)
    {
        color = ColorMath.Clamp01(color);
        return ((uint)(color.X * 255.0f) << 24) |
            ((uint)(color.Y * 255.0f) << 16) |
            ((uint)(color.Z * 255.0f) << 8) |
            (uint)(color.W * 255.0f);
    }
}
