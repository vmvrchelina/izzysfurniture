using System.Numerics;

namespace IzzysFurniture;

internal sealed class PlacedLight
{
    public PlacedLight(string name, LightSnapshot state, PlacedLightReference reference)
    {
        this.Name = name;
        this.State = state;
        this.Reference = reference;
    }

    public string Name { get; }
    public LightSnapshot State { get; }
    public PlacedLightReference Reference { get; }
}

internal readonly record struct LightSnapshot(
    Vector3 Position,
    Vector3 RotationDegrees,
    Vector3 Scale,
    bool Enabled,
    SceneLightSettings Settings);
