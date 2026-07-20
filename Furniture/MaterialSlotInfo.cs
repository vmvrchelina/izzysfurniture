using System.Numerics;

namespace IzzysFurniture;

internal readonly record struct MaterialSlotInfo(
    int Slot,
    bool IsLoaded,
    bool HasDiffuseControl,
    Vector4? DiffuseColor);
