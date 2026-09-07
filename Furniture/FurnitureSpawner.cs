using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Common.Math;
using Lumina.Data.Files;
using RenderMaterial = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Material;
using RenderModel = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model;

namespace IzzysFurniture;

internal unsafe sealed class FurnitureSpawner : IDisposable
{
    private const string PoolName = "IzzysFurniture";
    private const int MaxMaterialSlots = SpawnedFurniture.MaxForcedDyeMaterials;
    private readonly ConcurrentDictionary<Guid, SpawnedBgObject> bgObjects = [];
    private readonly Dictionary<Guid, SpawnedVfxObject> vfxObjects = [];
    private readonly ConcurrentDictionary<nint, SpawnedBgObject> renderModels = [];
    private readonly Hook<ModelDrawInitDelegate> modelDrawInitHook;
    private readonly Hook<OnRenderMaterialDelegate> onRenderMaterialHook;
    private SpawnedBgObject? creatingBgObject;
    private string? lastWarning;
    private string? warning;

    private delegate bool ModelDrawInitDelegate(RenderModel* model, ModelResourceHandle* modelResource,
        ModelRenderer.Callback* renderModelCallback, ModelRenderer.Callback* renderMaterialCallback);
    private delegate ushort* OnRenderMaterialDelegate(ModelRenderer* renderer, ModelRenderer.OnRenderMaterialParams2* parameters,
        RenderMaterial* material, uint materialIndex);

    public FurnitureSpawner()
    {
        this.modelDrawInitHook = Service.GameInteropProvider.HookFromAddress<ModelDrawInitDelegate>(
            (nint)RenderModel.MemberFunctionPointers.ModelDrawInit,
            this.ModelDrawInitDetour);
        this.onRenderMaterialHook = Service.GameInteropProvider.HookFromAddress<OnRenderMaterialDelegate>(
            (nint)ModelRenderer.MemberFunctionPointers.OnRenderMaterial,
            this.OnRenderMaterialDetour);
        this.modelDrawInitHook.Enable();
        this.onRenderMaterialHook.Enable();
    }

    public bool TryGetWorldSize(Guid furnitureId, out System.Numerics.Vector3 size)
    {
        size = System.Numerics.Vector3.Zero;
        if (!this.bgObjects.TryGetValue(furnitureId, out var entry) || entry.Pointer == 0)
            return false;

        var bgObject = (BgObject*)entry.Pointer;
        if (!IsModelLoaded(bgObject))
            return false;

        var bounds = new AxisAlignedBounds();
        bgObject->ComputeAxisAlignedBounds(&bounds);
        size = new System.Numerics.Vector3(
            MathF.Abs(bounds.Max.X - bounds.Min.X),
            MathF.Abs(bounds.Max.Y - bounds.Min.Y),
            MathF.Abs(bounds.Max.Z - bounds.Min.Z));

        return IsUsableDimension(size.X) || IsUsableDimension(size.Y) || IsUsableDimension(size.Z);
    }

    public void Apply(IReadOnlyList<SpawnedFurniture> furniture)
    {
        this.warning = null;
        var enabledFurniture = furniture
            .Where(item => item.Enabled && !item.IsNpc && !item.IsLight && !string.IsNullOrWhiteSpace(item.ModelPath))
            .ToArray();
        var activeIds = enabledFurniture
            .Select(item => item.Id)
            .ToHashSet();
        foreach (var id in this.bgObjects.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            this.DestroyBgObject(id);
        foreach (var id in this.vfxObjects.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            this.DestroyVfxObject(id);
        foreach (var item in furniture)
        {
            if (item.IsNpc || item.IsLight || !item.Enabled || string.IsNullOrWhiteSpace(item.ModelPath))
                continue;

            if (item.IsFxEmitter)
            {
                this.ApplyVfxObject(item);
                continue;
            }

            this.ApplyDirectBgObject(item);
        }

        if (this.warning != null && this.warning != this.lastWarning)
            Service.Log.Warning(this.warning);
        this.lastWarning = this.warning;
    }

    public IReadOnlyList<TextureSlotInfo> GetTextureSlotInfos(Guid furnitureId)
    {
        if (!this.bgObjects.TryGetValue(furnitureId, out var entry) || entry.Pointer == 0)
            return [];

        var result = new List<TextureSlotInfo>();
        var bgObject = (BgObject*)entry.Pointer;
        for (var materialIndex = 0; materialIndex < entry.MaterialSlotCount; materialIndex++)
        {
            if (!TryGetMaterialHandle(bgObject, materialIndex, out var material))
                continue;

            for (var textureIndex = 0; textureIndex < material->TextureCount; textureIndex++)
            {
                var path = material->TexturePath(textureIndex).ToString();
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                result.Add(new TextureSlotInfo(materialIndex, textureIndex,
                    string.IsNullOrWhiteSpace(name) ? $"Texture {textureIndex + 1}" : name));
            }
        }

        return result;
    }

    public void Clear()
    {
        foreach (var id in this.bgObjects.Keys.ToArray())
            this.DestroyBgObject(id);
        foreach (var id in this.vfxObjects.Keys.ToArray())
            this.DestroyVfxObject(id);

        this.lastWarning = null;
    }

    public void Dispose()
    {
        this.Clear();
        this.onRenderMaterialHook.Dispose();
        this.modelDrawInitHook.Dispose();
    }

    public void InvalidateForcedDyes(Guid furnitureId)
    {
        if (this.bgObjects.TryGetValue(furnitureId, out var entry))
        {
            entry.ForcedDyeState = 0;
            entry.Activated = false;
            entry.ResetTextureTintFailures();
        }
    }

    private void ApplyVfxObject(SpawnedFurniture furniture)
    {
        var path = furniture.ModelPath.Trim();
        if (this.bgObjects.ContainsKey(furniture.Id))
            this.DestroyBgObject(furniture.Id);

        if (!path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase))
        {
            this.SetApplyWarning("fx path must end in .avfx");
            return;
        }

        if (!Service.DataManager.FileExists(path))
        {
            this.SetApplyWarning("fx file does not exist in game data");
            return;
        }

        if (this.vfxObjects.TryGetValue(furniture.Id, out var existing) &&
            !existing.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            this.DestroyVfxObject(furniture.Id);
        }

        if (!this.vfxObjects.TryGetValue(furniture.Id, out var entry) || entry.Pointer == 0)
        {
            var pointer = this.CreateVfxObject(path);
            if (pointer == 0)
                return;

            entry = new SpawnedVfxObject(pointer, path);
            this.vfxObjects[furniture.Id] = entry;
        }

        var vfx = (VfxObject*)entry.Pointer;
        var visualState = new VfxVisualState(furniture.Position, furniture.RotationDegrees, furniture.Scale3, furniture.FxColor);
        if (entry.AppliedVisualState == visualState)
        {
            vfx->IsVisible = true;
            return;
        }

        entry.AppliedVisualState = visualState;
        vfx->Position = furniture.Position;
        vfx->Rotation = Quaternion.CreateFromEuler(new Vector3(
            furniture.RotationDegrees.X,
            furniture.RotationDegrees.Y,
            furniture.RotationDegrees.Z));
        vfx->Scale = furniture.Scale3;
        vfx->Color = ToSceneVector4(furniture.FxColor);
        vfx->Speed = 1.0f;
        vfx->IsVisible = true;
        ((DrawObject*)vfx)->NotifyTransformChanged();
        vfx->SomeFlags &= 0xF7;
        vfx->Update(0.0f);
    }

    private void ApplyDirectBgObject(SpawnedFurniture furniture)
    {
        if (this.vfxObjects.ContainsKey(furniture.Id))
            this.DestroyVfxObject(furniture.Id);

        var path = furniture.ModelPath.Trim();
        var useForcedDyes = furniture.ForcedDyesEnabled && furniture.MapAssetItem is null;

        if (this.bgObjects.TryGetValue(furniture.Id, out var existing) &&
            !existing.ModelPath.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            this.DestroyBgObject(furniture.Id);
        }

        if (this.bgObjects.TryGetValue(furniture.Id, out var existingMode) && existingMode.ForcedDyesEnabled != useForcedDyes)
            this.SetForcedDyeMode(existingMode, useForcedDyes);

        if (!useForcedDyes &&
            this.bgObjects.TryGetValue(furniture.Id, out var existingDye) &&
            furniture.PrimaryDyeStateSignature() == 0 &&
            existingDye.PrimaryDyeState != 0)
        {
            this.DestroyBgObject(furniture.Id);
        }

        BgVisualState? visualState = null;
        if (this.bgObjects.TryGetValue(furniture.Id, out var stableEntry) && stableEntry.Activated)
        {
            // most frames should leave an already loaded object completely untouched
            visualState = CreateVisualState(furniture, useForcedDyes, stableEntry.MaterialSlotCount);
            var stableBgObject = (BgObject*)stableEntry.Pointer;
            if (IsModelLoaded(stableBgObject) &&
                stableEntry.AppliedVisualState == visualState.Value &&
                DyeStateIsApplied(furniture, stableEntry, stableBgObject, useForcedDyes, stableEntry.MaterialSlotCount))
            {
                stableBgObject->IsVisible = true;
                return;
            }
        }

        if (!this.bgObjects.TryGetValue(furniture.Id, out var entry) || entry.Pointer == 0)
        {
            entry = new SpawnedBgObject(path);
            this.creatingBgObject = entry;
            var pointer = this.CreateBgObject(furniture, path);
            this.creatingBgObject = null;
            if (pointer == 0)
                return;

            entry.Pointer = pointer;
            entry.ForcedDyesEnabled = useForcedDyes;
            entry.MaterialSlotCount = GetMdlMaterialCount(path);
            this.bgObjects[furniture.Id] = entry;
        }

        var bgObject = (BgObject*)entry.Pointer;
        if (bgObject->ModelResourceHandle is null || bgObject->ModelResourceHandle->LoadState != 7)
        {
            bgObject->IsVisible = false;
            return;
        }

        entry.ModelResourcePointer = (nint)bgObject->ModelResourceHandle;
        entry.RequestMaterialLoad(bgObject);
        if (!AreMaterialsReady(bgObject, entry.MaterialSlotCount))
        {
            bgObject->IsVisible = false;
            this.SetApplyWarning("waiting for materials to load");
            return;
        }

        visualState ??= CreateVisualState(furniture, useForcedDyes, entry.MaterialSlotCount);

        bgObject->Position = furniture.Position;
        bgObject->Rotation = Quaternion.CreateFromEuler(new Vector3(
            furniture.RotationDegrees.X,
            furniture.RotationDegrees.Y,
            furniture.RotationDegrees.Z));
        bgObject->Scale = furniture.Scale3;
        bgObject->IsVisible = true;
        if (useForcedDyes)
            this.ApplyForcedDyes(furniture, entry, bgObject);
        else
            this.ApplyStain(furniture, entry, bgObject);
        bgObject->NotifyTransformChanged();
        if (!entry.Activated || entry.AppliedVisualState != visualState.Value)
        {
            entry.AppliedVisualState = visualState.Value;
            entry.Activated = true;
        }
    }

    private nint CreateVfxObject(string path)
    {
        var vfx = VfxObject.Create(path, PoolName);
        if (vfx is null)
        {
            this.SetApplyWarning("could not create the vfx object");
            return 0;
        }

        vfx->SomeFlags &= 0xF7;
        vfx->Speed = 1.0f;
        vfx->IsVisible = false;
        vfx->Update(0.0f);
        return (nint)vfx;
    }

    private void ApplyStain(SpawnedFurniture furniture, SpawnedBgObject entry, BgObject* bgObject)
    {
        if (!furniture.SupportsFurnitureDye)
            return;

        var primaryStainId = furniture.GetStainId(0);
        // housing stain exposes only the primary dye channel here
        if (furniture.DyeChannelCount > 1 &&
            (furniture.StainIds.Skip(1).Take(furniture.DyeChannelCount - 1).Any(stain => stain != 0) ||
                Enumerable.Range(1, furniture.DyeChannelCount - 1).Any(furniture.IsDyeColorEnabled)))
        {
            this.SetApplyWarning("only the first dye channel works here");
        }

        var state = furniture.PrimaryDyeStateSignature();
        if (state == 0 ||
            (entry.PrimaryDyeState == state && PrimaryDyeIsApplied(furniture, bgObject)))
            return;

        entry.PrimaryDyeState = state;

        var color = furniture.IsDyeColorEnabled(0)
            ? ToByteColor(furniture.GetDyeColor(0))
            : GetHousingStainColor(primaryStainId);

        if (color is null)
        {
            this.SetApplyWarning("no housing color for that stain");
            return;
        }

        if (!bgObject->TrySetStainColor(color.Value))
        {
            this.SetApplyWarning("this model ignores stain colors");
            return;
        }
    }

    private void ApplyForcedDyes(SpawnedFurniture furniture, SpawnedBgObject entry, BgObject* bgObject)
    {
        var materialCount = GetMdlMaterialCount(entry.ModelPath);
        entry.MaterialSlotCount = materialCount;
        if (materialCount == 0)
            return;

        if (!AreMaterialsReady(bgObject, materialCount))
        {
            this.SetApplyWarning("waiting for materials to load");
            return;
        }

        var state = furniture.ForcedDyeStateSignature(materialCount);
        if (entry.ForcedDyeState == state)
            return;

        var wanted = new HashSet<ForcedTextureKey>();
        var allReady = true;
        for (var slot = 0; slot < materialCount; slot++)
        {
            if (!TryGetMaterialHandle(bgObject, slot, out var material))
                continue;

            for (var textureIndex = 0; textureIndex < material->TextureCount; textureIndex++)
            {
                var dye = furniture.GetForcedTextureDye(slot, textureIndex);
                if (!dye.CustomColorEnabled && dye.StainId == 0)
                    continue;

                var color = dye.CustomColorEnabled
                    ? dye.Color
                    : ToVector4(GetHousingStainColor(dye.StainId));
                if (color is null)
                    continue;

                var key = new ForcedTextureKey(slot, textureIndex);
                wanted.Add(key);
                var sourceHandle = material->Textures[textureIndex].TextureResourceHandle;
                if (sourceHandle is null || sourceHandle->Texture is null)
                {
                    allReady = false;
                    continue;
                }

                var path = material->TexturePath(textureIndex).ToString();
                if (!entry.UpdateTextureTint(key, sourceHandle, color.Value,
                        () => CreateTintedTexture(path, (nint)sourceHandle, color.Value), out var failed))
                {
                    allReady = false;
                    if (failed)
                        this.SetApplyWarning("301");
                    continue;
                }
            }
        }

        entry.RemoveUnusedTextureTints(wanted);
        entry.ForcedDyeState = allReady ? state : 0;
        entry.PrimaryDyeState = 0;
    }

    private static bool DyeStateIsApplied(SpawnedFurniture furniture, SpawnedBgObject entry, BgObject* bgObject,
        bool useForcedDyes, int materialCount)
        => useForcedDyes
            ? ForcedTextureDyesAreCurrent(furniture, entry, bgObject, materialCount)
            : PrimaryDyeIsApplied(furniture, bgObject);

    private static bool ForcedTextureDyesAreCurrent(SpawnedFurniture furniture, SpawnedBgObject entry, BgObject* bgObject,
        int materialCount)
    {
        if (entry.ForcedDyeState != furniture.ForcedDyeStateSignature(materialCount))
            return false;

        for (var slot = 0; slot < materialCount; slot++)
        {
            if (!TryGetMaterialHandle(bgObject, slot, out var material))
                return false;

            for (var textureIndex = 0; textureIndex < material->TextureCount; textureIndex++)
            {
                var dye = furniture.GetForcedTextureDye(slot, textureIndex);
                if (!dye.CustomColorEnabled && dye.StainId == 0)
                    continue;

                var color = dye.CustomColorEnabled
                    ? dye.Color
                    : ToVector4(GetHousingStainColor(dye.StainId));
                var source = material->Textures[textureIndex].TextureResourceHandle;
                if (color is null || source is null || !entry.TextureTintIsCurrent(new ForcedTextureKey(slot, textureIndex), source, color.Value))
                    return false;
            }
        }

        return true;
    }

    private static bool PrimaryDyeIsApplied(SpawnedFurniture furniture, BgObject* bgObject)
    {
        if (!furniture.SupportsFurnitureDye || furniture.PrimaryDyeStateSignature() == 0)
            return true;

        var expected = furniture.IsDyeColorEnabled(0)
            ? ToByteColor(furniture.GetDyeColor(0))
            : GetHousingStainColor(furniture.GetStainId(0));
        if (expected is null || bgObject->StainBuffer is null)
            return true;

        var actual = bgObject->StainBuffer->SrgbByteColor;
        var color = expected.Value;
        return actual.R == color.R && actual.G == color.G && actual.B == color.B && actual.A == color.A;
    }

    private void SetForcedDyeMode(SpawnedBgObject entry, bool enabled)
    {
        entry.ForcedDyesEnabled = enabled;
        entry.ForcedDyeState = 0;

        if (!enabled)
            entry.ClearTextureTints();
    }

    private static Task<TintedTexture?> CreateTintedTexture(string path, nint sourceHandle,
        System.Numerics.Vector4 color)
        => TextureTintFactory.Create(path, color).ContinueWith(completed =>
        {
            var texture = completed.GetAwaiter().GetResult();
            return texture == 0 ? null : new TintedTexture(sourceHandle, texture, color);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private bool ModelDrawInitDetour(RenderModel* model, ModelResourceHandle* modelResource,
        ModelRenderer.Callback* renderModelCallback, ModelRenderer.Callback* renderMaterialCallback)
    {
        var initialized = this.modelDrawInitHook.Original(model, modelResource, renderModelCallback, renderMaterialCallback);
        if (!initialized)
            return false;

        var entry = this.creatingBgObject;
        if (entry is not null && entry.TryAssignRenderModel((nint)model))
        {
            this.renderModels[(nint)model] = entry;
            return true;
        }

        foreach (var candidate in this.bgObjects.Values)
        {
            if (candidate.ModelResourcePointer != (nint)modelResource || !candidate.TryAssignRenderModel((nint)model))
                continue;

            this.renderModels[(nint)model] = candidate;
            break;
        }

        return true;
    }

    private ushort* OnRenderMaterialDetour(ModelRenderer* renderer, ModelRenderer.OnRenderMaterialParams2* parameters,
        RenderMaterial* material, uint materialIndex)
    {
        if (parameters is null || parameters->Inner is null || parameters->Inner->Model is null)
            return this.onRenderMaterialHook.Original(renderer, parameters, material, materialIndex);

        if (!this.renderModels.TryGetValue((nint)parameters->Inner->Model, out var entry))
            return this.onRenderMaterialHook.Original(renderer, parameters, material, materialIndex);

        var model = parameters->Inner->Model;
        if (model->Materials is null)
            return this.onRenderMaterialHook.Original(renderer, parameters, material, materialIndex);

        var materialSlot = -1;
        for (var index = 0; index < model->MaterialCount; index++)
        {
            if (model->Materials[index] == material)
            {
                materialSlot = index;
                break;
            }
        }

        if (materialSlot < 0)
            return this.onRenderMaterialHook.Original(renderer, parameters, material, materialIndex);

        var tints = entry.AcquireTextureTints(materialSlot);
        if (tints.Count == 0)
            return this.onRenderMaterialHook.Original(renderer, parameters, material, materialIndex);

        var swaps = new List<TextureSwap>(tints.Count);
        // renderer materials share texture handles, so each swap exists only for this model's material callback
        foreach (var tint in tints)
        {
            var handle = (TextureResourceHandle*)tint.SourceHandle;
            var texture = (Texture*)tint.Texture;
            if (handle is null || texture is null || swaps.Any(swap => swap.Handle == tint.SourceHandle))
                continue;

            swaps.Add(new TextureSwap(tint.SourceHandle, (nint)handle->Texture));
            handle->Texture = texture;
        }

        try
        {
            return this.onRenderMaterialHook.Original(renderer, parameters, material, materialIndex);
        }
        finally
        {
            for (var index = swaps.Count - 1; index >= 0; index--)
            {
                var swap = swaps[index];
                ((TextureResourceHandle*)swap.Handle)->Texture = (Texture*)swap.OriginalTexture;
            }

            foreach (var tint in tints)
                ((Texture*)tint.Texture)->DecRef();
        }
    }

    private static bool TryGetMaterialHandle(BgObject* bgObject, int slot, out MaterialResourceHandle* material)
    {
        material = null;
        if (slot < 0 || slot >= MaxMaterialSlots)
            return false;

        var model = bgObject->ModelResourceHandle;
        if (model is null || model->LoadState != 7 || model->MaterialResourceHandles is null)
            return false;

        material = model->MaterialResourceHandles[slot];
        return material is not null && material->LoadState == 7;
    }

    private static bool AreMaterialsReady(BgObject* bgObject, int materialCount)
    {
        if (!IsModelLoaded(bgObject))
            return false;

        for (var slot = 0; slot < materialCount; slot++)
        {
            if (!TryGetMaterialHandle(bgObject, slot, out _))
                return false;
        }

        return true;
    }

    private static bool IsModelLoaded(BgObject* bgObject)
        => bgObject is not null &&
            bgObject->ModelResourceHandle is not null &&
            bgObject->ModelResourceHandle->LoadState == 7;

    private static ByteColor? GetHousingStainColor(byte stainId)
    {
        var color = SharedGroupLayoutInstance.GetObjectStainColorByIndex(stainId);
        return color is null ? null : *color;
    }

    private static ByteColor ToByteColor(System.Numerics.Vector4 color)
        => new()
        {
            R = (byte)Math.Clamp(color.X * 255.0f, 0.0f, 255.0f),
            G = (byte)Math.Clamp(color.Y * 255.0f, 0.0f, 255.0f),
            B = (byte)Math.Clamp(color.Z * 255.0f, 0.0f, 255.0f),
            A = 255,
        };

    private static Vector4 ToSceneVector4(System.Numerics.Vector4 color)
        => new(
            Math.Clamp(color.X, 0.0f, 1.0f),
            Math.Clamp(color.Y, 0.0f, 1.0f),
            Math.Clamp(color.Z, 0.0f, 1.0f),
            Math.Clamp(color.W, 0.0f, 1.0f));

    private static System.Numerics.Vector4? ToVector4(ByteColor? color)
    {
        if (color is null)
            return null;

        var value = color.Value;
        return new System.Numerics.Vector4(
            value.R / 255.0f,
            value.G / 255.0f,
            value.B / 255.0f,
            1.0f);
    }

    private nint CreateBgObject(SpawnedFurniture furniture, string path)
    {
        if (!path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            this.SetApplyWarning("model path must end in .mdl");
            return 0;
        }

        if (!Service.DataManager.FileExists(path))
        {
            this.SetApplyWarning("model file does not exist in game data");
            return 0;
        }

        var bgObject = BgObject.Create(path, PoolName);
        if (bgObject is null)
        {
            this.SetApplyWarning("could not create the bg object");
            return 0;
        }

        bgObject->IsVisible = false;
        return (nint)bgObject;
    }

    private static int GetMdlMaterialCount(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return 0;

        var file = Service.DataManager.GetFile<MdlFile>(path);
        if (file is null)
            return 0;

        return Math.Clamp((int)file.FileHeader.MaterialCount, 0, MaxMaterialSlots);
    }

    private static bool IsUsableDimension(float value)
        => value > 0.001f;

    private static BgVisualState CreateVisualState(SpawnedFurniture furniture, bool useForcedDyes, int materialSlotCount)
        => new(
            furniture.Position,
            furniture.RotationDegrees,
            furniture.Scale3,
            useForcedDyes,
            useForcedDyes ? 0 : furniture.PrimaryDyeStateSignature(),
            useForcedDyes ? furniture.ForcedDyeStateSignature(materialSlotCount) : 0);

    private void SetApplyWarning(string message)
    {
        this.warning = message;
    }

    private void DestroyBgObject(Guid id)
    {
        if (!this.bgObjects.TryRemove(id, out var entry))
            return;

        if (entry.Pointer == 0)
            return;

        var bgObject = (BgObject*)entry.Pointer;
        if (entry.RenderModel != 0)
            this.renderModels.TryRemove(entry.RenderModel, out _);
        entry.ClearTextureTints();

        bgObject->IsVisible = false;
        // cleanup render is only valid after the object has entered the scene once
        if (entry.Activated)
            bgObject->CleanupRender();
        bgObject->Dtor(1);
    }

    private void DestroyVfxObject(Guid id)
    {
        if (!this.vfxObjects.Remove(id, out var entry) || entry.Pointer == 0)
            return;

        var vfx = (VfxObject*)entry.Pointer;
        vfx->IsVisible = false;
        vfx->CleanupRender();
        vfx->Dtor(1);
    }

    private sealed class SpawnedBgObject(string modelPath)
    {
        private readonly Dictionary<ForcedTextureKey, TintedTexture> textureTints = [];
        private readonly Dictionary<ForcedTextureKey, PendingTextureTint> pendingTextureTints = [];
        private readonly Dictionary<ForcedTextureKey, FailedTextureTint> failedTextureTints = [];
        private readonly object textureLock = new();
        private nint renderModel;

        public nint Pointer { get; set; }
        public string ModelPath { get; } = modelPath;
        public nint ModelResourcePointer { get; set; }
        public nint RenderModel => Interlocked.CompareExchange(ref this.renderModel, 0, 0);
        public ulong PrimaryDyeState { get; set; }
        public ulong ForcedDyeState { get; set; }
        public int MaterialSlotCount { get; set; }
        public bool ForcedDyesEnabled { get; set; }
        public bool Activated { get; set; }
        public bool MaterialsLoadRequested { get; set; }
        public BgVisualState AppliedVisualState { get; set; }

        public void RequestMaterialLoad(BgObject* bgObject)
        {
            if (this.MaterialsLoadRequested)
                return;

            this.MaterialsLoadRequested = true;
            bgObject->ModelResourceHandle->LoadMaterials();
        }

        public bool TryAssignRenderModel(nint model)
            => Interlocked.CompareExchange(ref this.renderModel, model, 0) == 0;

        public bool UpdateTextureTint(ForcedTextureKey key, TextureResourceHandle* sourceHandle, System.Numerics.Vector4 color,
            Func<Task<TintedTexture?>> create, out bool failed)
        {
            failed = false;
            lock (this.textureLock)
            {
                var source = (nint)sourceHandle;
                if (this.textureTints.TryGetValue(key, out var tint) && tint.SourceHandle == source && tint.Color == color)
                    return true;

                if (this.textureTints.Remove(key, out tint))
                    tint.Dispose();

                if (this.failedTextureTints.TryGetValue(key, out var previousFailure))
                {
                    if (previousFailure.SourceHandle == source && previousFailure.Color == color)
                    {
                        failed = true;
                        return false;
                    }

                    this.failedTextureTints.Remove(key);
                }

                if (this.pendingTextureTints.TryGetValue(key, out var pending))
                {
                    if (pending.SourceHandle != source || pending.Color != color)
                    {
                        this.pendingTextureTints.Remove(key);
                        DisposeWhenComplete(pending.Task);
                    }
                    else if (!pending.Task.IsCompleted)
                    {
                        return false;
                    }
                    else
                    {
                        this.pendingTextureTints.Remove(key);
                        if (!pending.Task.IsCompletedSuccessfully)
                        {
                            _ = pending.Task.Exception;
                            this.failedTextureTints[key] = new FailedTextureTint(source, color);
                            failed = true;
                            return false;
                        }

                        if (pending.Task.Result is not { } completed)
                        {
                            this.failedTextureTints[key] = new FailedTextureTint(source, color);
                            failed = true;
                            return false;
                        }

                        if (this.textureTints.Remove(key, out var oldTint))
                            oldTint.Dispose();
                        this.textureTints[key] = completed;
                        return true;
                    }
                }

                this.pendingTextureTints[key] = new PendingTextureTint(source, color, create());
                return false;
            }
        }

        public bool TextureTintIsCurrent(ForcedTextureKey key, TextureResourceHandle* sourceHandle, System.Numerics.Vector4 color)
        {
            lock (this.textureLock)
                return this.textureTints.TryGetValue(key, out var tint) &&
                    tint.SourceHandle == (nint)sourceHandle && tint.Color == color;
        }

        public void RemoveUnusedTextureTints(HashSet<ForcedTextureKey> wanted)
        {
            lock (this.textureLock)
            {
                foreach (var key in this.textureTints.Keys.Where(key => !wanted.Contains(key)).ToArray())
                {
                    this.textureTints[key].Dispose();
                    this.textureTints.Remove(key);
                }

                foreach (var key in this.pendingTextureTints.Keys.Where(key => !wanted.Contains(key)).ToArray())
                {
                    DisposeWhenComplete(this.pendingTextureTints[key].Task);
                    this.pendingTextureTints.Remove(key);
                }
                foreach (var key in this.failedTextureTints.Keys.Where(key => !wanted.Contains(key)).ToArray())
                    this.failedTextureTints.Remove(key);
            }
        }

        public List<TintedTexture> AcquireTextureTints(int materialIndex)
        {
            lock (this.textureLock)
            {
                var result = this.textureTints
                    .Where(item => item.Key.MaterialIndex == materialIndex)
                    .Select(item => item.Value)
                    .ToList();
                foreach (var tint in result)
                    ((Texture*)tint.Texture)->IncRef();
                return result;
            }
        }

        public void ClearTextureTints()
        {
            lock (this.textureLock)
            {
                foreach (var tint in this.textureTints.Values)
                    tint.Dispose();
                this.textureTints.Clear();
                foreach (var pending in this.pendingTextureTints.Values)
                    DisposeWhenComplete(pending.Task);
                this.pendingTextureTints.Clear();
                this.failedTextureTints.Clear();
            }
        }

        public void ResetTextureTintFailures()
        {
            lock (this.textureLock)
                this.failedTextureTints.Clear();
        }

        private static void DisposeWhenComplete(Task<TintedTexture?> task)
        {
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsCompletedSuccessfully)
                    completed.Result?.Dispose();
                else
                    _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private sealed record TintedTexture(nint SourceHandle, nint Texture, System.Numerics.Vector4 Color) : IDisposable
    {
        public void Dispose()
            => ((Texture*)this.Texture)->DecRef();
    }

    private sealed record PendingTextureTint(nint SourceHandle, System.Numerics.Vector4 Color, Task<TintedTexture?> Task);
    private readonly record struct FailedTextureTint(nint SourceHandle, System.Numerics.Vector4 Color);

    private readonly record struct TextureSwap(nint Handle, nint OriginalTexture);
    private readonly record struct BgVisualState(
        Vector3 Position,
        Vector3 RotationDegrees,
        Vector3 Scale,
        bool ForcedDyesEnabled,
        ulong PrimaryDye,
        ulong ForcedDyes);
    private readonly record struct VfxVisualState(
        System.Numerics.Vector3 Position,
        System.Numerics.Vector3 RotationDegrees,
        System.Numerics.Vector3 Scale,
        System.Numerics.Vector4 Color);
    private sealed class SpawnedVfxObject(nint pointer, string path)
    {
        public nint Pointer { get; } = pointer;
        public string Path { get; } = path;
        public VfxVisualState? AppliedVisualState { get; set; }
    }
}
