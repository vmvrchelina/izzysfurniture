using System.Collections.Generic;
using System.Numerics;

namespace IzzysFurniture;

internal sealed record MapAssetCatalogItem(
    string Name,
    string ModelPath,
    string SourcePath,
    string Category,
    Vector3? Position = null,
    Vector3? RotationDegrees = null,
    Vector3? Scale3 = null,
    uint OriginalInstanceId = 0,
    MapAssetKind Kind = MapAssetKind.Model,
    IReadOnlyList<MapAssetCatalogChild>? Children = null)
{
    public bool IsSharedGroup => this.Kind == MapAssetKind.SharedGroup;

    public IReadOnlyList<MapAssetCatalogChild> ChildItems => this.Children ?? [];
}
