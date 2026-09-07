using System;
using System.Numerics;

namespace IzzysFurniture;

internal enum SceneLightType
{
    Point,
    Spot,
    Area,
}

internal enum SceneLightFalloff
{
    Linear,
    Quadratic,
    Cubic,
}

internal enum SceneLightSource
{
    ActiveLayout,
    GlobalLayout,
}

internal readonly record struct PlacedLightReference(
    SceneLightSource Source,
    uint TerritoryId,
    uint InstanceId,
    uint SubId);

internal readonly record struct SceneLightSettings(
    SceneLightType Type,
    Vector3 Color,
    float Intensity,
    float Range,
    SceneLightFalloff Falloff,
    float FalloffFactor,
    float SpotAngle,
    float AngularFalloff,
    Vector2 AreaSkew,
    bool SpecularHighlights,
    bool DynamicShadows,
    bool CharacterShadows,
    bool ObjectShadows)
{
    public static SceneLightSettings Create(SceneLightType type)
    {
        var range = type switch
        {
            SceneLightType.Point => 8.0f,
            SceneLightType.Spot => 15.0f,
            SceneLightType.Area => 10.0f,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        return new SceneLightSettings(
            type,
            Vector3.One,
            1.0f,
            range,
            SceneLightFalloff.Quadratic,
            1.0f,
            45.0f,
            0.5f,
            Vector2.Zero,
            true,
            false,
            false,
            false);
    }
}

internal sealed class SceneLight
{
    public SceneLight(SceneLightSettings settings, PlacedLightReference? placed = null)
    {
        this.Settings = settings;
        this.Placed = placed;
    }

    public SceneLightSettings Settings { get; set; }
    public PlacedLightReference? Placed { get; }

    public SceneLight CopyAsCustom()
        => new(this.Settings);
}
