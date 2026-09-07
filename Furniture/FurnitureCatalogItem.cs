using System;

namespace IzzysFurniture;

internal enum FurnitureSourceKind : byte
{
    Indoor = 76,
    Outdoor = 77,
}

internal sealed record FurnitureCatalogItem(
    uint ItemId,
    string Name,
    uint IconId,
    ushort ModelKey,
    byte HousingItemCategory,
    string CategoryName,
    byte DyeCount,
    FurnitureSourceKind SourceKind)
{
    public int DyeChannelCount => Math.Clamp(this.DyeCount, (byte)0, (byte)4);

    public string CategoryKey => $"{this.SourceKind}:{this.HousingItemCategory}";

    public string CategoryLabel => string.IsNullOrWhiteSpace(this.CategoryName)
        ? $"{this.SourceKind} Category {this.HousingItemCategory}"
        : this.CategoryName;

    public string ModelPath => this.SourceKind switch
    {
        FurnitureSourceKind.Indoor => $"bgcommon/hou/indoor/general/{this.ModelKey:D4}/bgparts/fun_b0_m{this.ModelKey:D4}.mdl",
        FurnitureSourceKind.Outdoor => $"bgcommon/hou/outdoor/general/{this.ModelKey:D4}/bgparts/gar_b0_m{this.ModelKey:D4}.mdl",
        _ => string.Empty,
    };
}
