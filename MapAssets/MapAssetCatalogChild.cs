using System.Numerics;

namespace IzzysFurniture;

internal sealed record MapAssetCatalogChild(
    string Name,
    string ModelPath,
    Vector3 Offset,
    Vector3 RotationDegrees,
    Vector3 Scale3);
