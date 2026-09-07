using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.STD;
using NativeLight = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Light;
using SceneQuaternion = FFXIVClientStructs.FFXIV.Common.Math.Quaternion;
using SceneVector2 = FFXIVClientStructs.FFXIV.Common.Math.Vector2;
using SceneVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;

namespace IzzysFurniture;

internal unsafe sealed class SceneLighting
{
    private const string PoolName = "IzzysFurniture";
    private readonly Dictionary<Guid, OwnedLight> owned = [];
    private readonly Dictionary<Guid, PlacedLightEdit> placed = [];
    private readonly HashSet<Guid> active = [];
    private readonly List<Guid> stale = [];

    public IReadOnlyList<PlacedLight> FindPlacedLights()
    {
        var world = LayoutWorld.Instance();
        if (world is null)
            return [];

        var lights = new List<PlacedLight>();
        this.AddPlacedLights(lights, world->ActiveLayout, SceneLightSource.ActiveLayout);
        if (world->GlobalLayout != world->ActiveLayout)
            this.AddPlacedLights(lights, world->GlobalLayout, SceneLightSource.GlobalLayout);
        lights.Sort((left, right) =>
        {
            var type = left.State.Settings.Type.CompareTo(right.State.Settings.Type);
            return type != 0 ? type : left.Reference.InstanceId.CompareTo(right.Reference.InstanceId);
        });
        return lights;
    }

    public void Apply(IReadOnlyList<SpawnedFurniture> scene)
    {
        this.active.Clear();
        foreach (var item in scene)
        {
            if (item.Light is not { } light)
                continue;

            this.active.Add(item.Id);
            if (light.Placed is null)
                this.ApplyOwned(item, light.Settings);
            else
                this.ApplyPlaced(item, light.Settings, light.Placed.Value);
        }

        this.stale.Clear();
        foreach (var id in this.owned.Keys)
            if (!this.active.Contains(id))
                this.stale.Add(id);
        foreach (var id in this.stale)
            this.DestroyOwned(id);

        this.stale.Clear();
        foreach (var id in this.placed.Keys)
            if (!this.active.Contains(id))
                this.stale.Add(id);
        foreach (var id in this.stale)
            this.RestorePlaced(id);
    }

    public void Clear()
    {
        this.stale.Clear();
        this.stale.AddRange(this.owned.Keys);
        foreach (var id in this.stale)
            this.DestroyOwned(id);

        this.stale.Clear();
        this.stale.AddRange(this.placed.Keys);
        foreach (var id in this.stale)
            this.RestorePlaced(id);
    }

    private void ApplyOwned(SpawnedFurniture item, SceneLightSettings settings)
    {
        var shape = ToNativeType(settings.Type);
        if (this.owned.TryGetValue(item.Id, out var old) && old.Shape != shape)
            this.DestroyOwned(item.Id);

        if (!this.owned.TryGetValue(item.Id, out var entry))
        {
            var light = NativeLight.Create(shape, PoolName);
            if (light is null)
                return;
            if (light->RenderLight is null)
            {
                light->Dtor(1);
                return;
            }

            entry = new OwnedLight((nint)light, shape);
            this.owned.Add(item.Id, entry);
        }

        var pointer = (NativeLight*)entry.Pointer;
        var state = Snapshot(item, settings);
        if (entry.Applied == state)
            return;

        ApplyState(pointer, state);
        entry.Applied = state;
    }

    private void ApplyPlaced(SpawnedFurniture item, SceneLightSettings settings, PlacedLightReference reference)
    {
        var light = this.ResolvePlaced(reference);
        if (light is null)
            return;

        if (!this.placed.TryGetValue(item.Id, out var edit) || edit.Pointer != (nint)light)
        {
            if (!TrySnapshot(light, out var original))
                return;

            edit = new PlacedLightEdit(
                (nint)light,
                original,
                reference);
            this.placed[item.Id] = edit;
        }

        var state = Snapshot(item, settings);
        if (edit.Applied == state)
            return;

        ApplyState(light, state);
        edit.Applied = state;
    }

    private void AddPlacedLights(List<PlacedLight> destination, LayoutManager* layout, SceneLightSource source)
    {
        if (layout is null || layout->InitState != 7)
            return;

        if (!layout->InstancesByType.TryGetValue(InstanceType.Light, out Pointer<StdMap<ulong, Pointer<ILayoutInstance>>> mapPointer, false) || mapPointer.IsNull)
            return;

        // layout ids are the stable identity used when the scene is loaded again
        foreach (var pair in *mapPointer.Value)
        {
            var instance = (LightLayoutInstance*)pair.Item2.Value;
            var light = instance is null ? null : instance->GraphicsObject;
            if (light is null || !TrySnapshot(light, out var state))
                continue;

            var type = state.Settings.Type;
            var reference = new PlacedLightReference(
                source,
                layout->TerritoryTypeId,
                instance->ILayoutInstance.Id.InstanceKey,
                instance->ILayoutInstance.SubId);
            destination.Add(new PlacedLight(
                $"Placed {LightName(type)} {instance->ILayoutInstance.Id.InstanceKey}",
                state,
                reference));
        }
    }

    private NativeLight* ResolvePlaced(PlacedLightReference reference)
    {
        var world = LayoutWorld.Instance();
        if (world is null)
            return null;

        var layout = reference.Source == SceneLightSource.GlobalLayout ? world->GlobalLayout : world->ActiveLayout;
        if (layout is null || layout->InitState != 7 || layout->TerritoryTypeId != reference.TerritoryId)
            return null;

        if (!layout->InstancesByType.TryGetValue(InstanceType.Light, out Pointer<StdMap<ulong, Pointer<ILayoutInstance>>> mapPointer, false) || mapPointer.IsNull)
            return null;

        var key = ((ulong)reference.InstanceId << 32) | reference.SubId;
        if (!mapPointer.Value->TryGetValue(key, out Pointer<ILayoutInstance> instancePointer, false) || instancePointer.IsNull)
            return null;

        var instance = (LightLayoutInstance*)instancePointer.Value;
        return instance->GraphicsObject;
    }

    private void RestorePlaced(Guid id)
    {
        if (!this.placed.Remove(id, out var edit))
            return;

        var current = this.ResolvePlaced(edit.Reference);
        // a reloaded layout may reuse the id with a new allocation
        if (current is not null && (nint)current == edit.Pointer)
            ApplyState(current, edit.Original);
    }

    private void DestroyOwned(Guid id)
    {
        if (!this.owned.Remove(id, out var entry))
            return;

        var light = (NativeLight*)entry.Pointer;
        light->IsVisible = false;
        // these allocations come from light.create and use the matching scene-object lifecycle
        light->CleanupRender();
        light->Dtor(1);
    }

    private static void ApplyState(NativeLight* light, LightSnapshot state)
    {
        var render = light->RenderLight;
        if (render is null)
            return;

        light->Position = state.Position;
        light->Rotation = SceneQuaternion.CreateFromEuler(new SceneVector3(
            state.RotationDegrees.X,
            state.RotationDegrees.Y,
            state.RotationDegrees.Z));
        light->Scale = state.Scale;
        light->IsVisible = state.Enabled;

        var settings = state.Settings;
        render->LightShape = ToNativeType(settings.Type);
        // native light colors use the same squared hdr scale as the game's light editor
        render->Color = new SceneVector3(
            settings.Color.X * settings.Color.X * 6.0f,
            settings.Color.Y * settings.Color.Y * 6.0f,
            settings.Color.Z * settings.Color.Z * 6.0f);
        render->Intensity = settings.Intensity;
        render->Range = settings.Range;
        render->FalloffType = (LightFalloffType)settings.Falloff;
        render->FalloffFactor = settings.FalloffFactor;
        render->SpotLightAngleDegrees = settings.SpotAngle;
        render->AngularFalloffDegrees = settings.AngularFalloff;
        render->FlatLightSkewAngleDegrees = new SceneVector2(settings.AreaSkew.X, settings.AreaSkew.Y);
        render->LightFlags = BuildFlags(settings);

        light->NotifyTransformChanged();
        light->UpdateTransforms(false);
        light->UpdateCulling();
        light->UpdateMaterials();
        light->UpdateRender();
    }

    private static LightFlags BuildFlags(SceneLightSettings settings)
    {
        var flags = (LightFlags)0;
        if (settings.SpecularHighlights)
            flags |= LightFlags.SpecularHighlights;
        if (settings.DynamicShadows)
            flags |= LightFlags.DynamicShadows;
        if (settings.CharacterShadows)
            flags |= LightFlags.CharacterShadows;
        if (settings.ObjectShadows)
            flags |= LightFlags.ObjectShadows;
        return flags;
    }

    private static LightShape ToNativeType(SceneLightType type)
        => type switch
        {
            SceneLightType.Point => LightShape.PointLight,
            SceneLightType.Spot => LightShape.SpotLight,
            SceneLightType.Area => LightShape.FlatLight,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

    private static bool TryFromNativeType(LightShape shape, out SceneLightType type)
    {
        type = shape switch
        {
            LightShape.PointLight => SceneLightType.Point,
            LightShape.SpotLight => SceneLightType.Spot,
            LightShape.FlatLight => SceneLightType.Area,
            _ => default,
        };
        return shape is LightShape.PointLight or LightShape.SpotLight or LightShape.FlatLight;
    }

    internal static string LightName(SceneLightType type)
        => type switch
        {
            SceneLightType.Point => "Point Light",
            SceneLightType.Spot => "Spotlight",
            SceneLightType.Area => "Area Light",
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

    private sealed class OwnedLight(nint pointer, LightShape shape)
    {
        public nint Pointer { get; } = pointer;
        public LightShape Shape { get; } = shape;
        public LightSnapshot? Applied { get; set; }
    }

    private sealed class PlacedLightEdit(
        nint pointer,
        LightSnapshot original,
        PlacedLightReference reference)
    {
        public nint Pointer { get; } = pointer;
        public LightSnapshot Original { get; } = original;
        public LightSnapshot? Applied { get; set; }
        public PlacedLightReference Reference { get; } = reference;
    }

    private static LightSnapshot Snapshot(SpawnedFurniture item, SceneLightSettings settings)
        => new(item.Position, item.RotationDegrees, item.Scale3, item.Enabled, settings);

    private static bool TrySnapshot(NativeLight* light, out LightSnapshot snapshot)
    {
        var render = light->RenderLight;
        if (render is null || !TryFromNativeType(render->LightShape, out var type))
        {
            snapshot = default;
            return false;
        }

        var color = render->Color;
        var settings = new SceneLightSettings(
            type,
            new Vector3(
                MathF.Sqrt(MathF.Max(0.0f, color.X) / 6.0f),
                MathF.Sqrt(MathF.Max(0.0f, color.Y) / 6.0f),
                MathF.Sqrt(MathF.Max(0.0f, color.Z) / 6.0f)),
            render->Intensity,
            render->Range,
            (SceneLightFalloff)render->FalloffType,
            render->FalloffFactor,
            render->SpotLightAngleDegrees,
            render->AngularFalloffDegrees,
            new Vector2(render->FlatLightSkewAngleDegrees.X, render->FlatLightSkewAngleDegrees.Y),
            render->LightFlags.HasFlag(LightFlags.SpecularHighlights),
            render->LightFlags.HasFlag(LightFlags.DynamicShadows),
            render->LightFlags.HasFlag(LightFlags.CharacterShadows),
            render->LightFlags.HasFlag(LightFlags.ObjectShadows));

        var euler = light->Rotation.EulerAngles;
        snapshot = new LightSnapshot(
            light->Position,
            new Vector3(euler.X, euler.Y, euler.Z),
            light->Scale,
            light->IsVisible,
            settings);
        return true;
    }
}
