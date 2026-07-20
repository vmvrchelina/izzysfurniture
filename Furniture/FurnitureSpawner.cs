using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Common.Math;
using Lumina.Data.Files;

namespace IzzysFurniture;

internal unsafe sealed class FurnitureSpawner
{
    private const string PoolName = "IzzysFurniture";
    private const int MaxMaterialSlots = SpawnedFurniture.MaxForcedDyeMaterials;
    // crc identifying the diffuse color shader parameter
    private const uint DiffuseColorConstantId = 0x2C2A34DD;
    private readonly Dictionary<Guid, SpawnedBgObject> bgObjects = [];
    private readonly Dictionary<Guid, SpawnedVfxObject> vfxObjects = [];
    private string? lastWarning;
    private string? warning;

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
            .Where(item => item.Enabled && !item.IsNpc && !string.IsNullOrWhiteSpace(item.ModelPath))
            .ToArray();
        var enabledModels = enabledFurniture
            .Where(item => !item.IsFxEmitter)
            .ToArray();
        // material handles can be shared by objects using the same model path
        // forced dyes are disabled for those duplicates
        var pathCounts = enabledModels
            .GroupBy(item => item.ModelPath.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var activeIds = enabledFurniture
            .Select(item => item.Id)
            .ToHashSet();
        // Service.Log.Debug($"apply {enabledFurniture.Length} objects, {this.bgObjects.Count} live");

        // yea
        foreach (var id in this.bgObjects.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            this.DestroyBgObject(id);
        foreach (var id in this.vfxObjects.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            this.DestroyVfxObject(id);
        foreach (var item in furniture)
        {
            if (item.IsNpc || !item.Enabled || string.IsNullOrWhiteSpace(item.ModelPath))
                continue;

            if (item.IsFxEmitter)
            {
                this.ApplyVfxObject(item);
                continue;
            }

            var path = item.ModelPath.Trim();
            var hasDuplicatePath = pathCounts.TryGetValue(path, out var count) && count > 1;
            this.ApplyDirectBgObject(item, hasDuplicatePath);
        }

        if (this.warning != null && this.warning != this.lastWarning)
            Service.Log.Warning(this.warning);
        this.lastWarning = this.warning;
    }

    public int GetMaterialSlotCount(Guid furnitureId)
    {
        if (!this.bgObjects.TryGetValue(furnitureId, out var entry) || entry.Pointer == 0)
            return 0;

        return entry.MaterialSlotCount;
    }

    public MaterialSlotInfo GetMaterialSlotInfo(Guid furnitureId, int materialIndex)
    {
        if (materialIndex < 0 || materialIndex >= MaxMaterialSlots)
            return new MaterialSlotInfo(materialIndex, false, false, null);

        if (!this.bgObjects.TryGetValue(furnitureId, out var entry) || entry.Pointer == 0)
            return new MaterialSlotInfo(materialIndex, false, false, null);

        if (materialIndex >= entry.MaterialSlotCount)
            return new MaterialSlotInfo(materialIndex, false, false, null);

        var bgObject = (BgObject*)entry.Pointer;
        if (!TryGetMaterialHandle(bgObject, materialIndex, out var material))
            return new MaterialSlotInfo(materialIndex, false, false, null);

        if (TryGetDiffuseColorConstant(material, out var diffuseConstant))
            return new MaterialSlotInfo(materialIndex, true, true, ReadDiffuseColor(diffuseConstant));

        return TryGetMaterialColorTable(material, out var table, out var width, out var height)
            ? new MaterialSlotInfo(materialIndex, true, true, ReadDiffuseColor(table))
            : new MaterialSlotInfo(materialIndex, true, false, null);
    }

    public void Clear()
    {
        foreach (var id in this.bgObjects.Keys.ToArray())
            this.DestroyBgObject(id);
        foreach (var id in this.vfxObjects.Keys.ToArray())
            this.DestroyVfxObject(id);

        this.lastWarning = null;
    }

    public void InvalidateForcedDyes(Guid furnitureId)
    {
        if (this.bgObjects.TryGetValue(furnitureId, out var entry))
        {
            entry.ForcedDyeState = 0;
            entry.Activated = false;
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

    private void ApplyDirectBgObject(SpawnedFurniture furniture, bool hasDuplicatePath)
    {
        if (this.vfxObjects.ContainsKey(furniture.Id))
            this.DestroyVfxObject(furniture.Id);

        var path = furniture.ModelPath.Trim();
        var useForcedDyes = furniture.ForcedDyesEnabled && !hasDuplicatePath && furniture.MapAssetItem is null;

        if (this.bgObjects.TryGetValue(furniture.Id, out var existing) &&
            !existing.ModelPath.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            this.DestroyBgObject(furniture.Id);
        }

        if (hasDuplicatePath && furniture.ForcedDyesEnabled)
            this.SetApplyWarning("forced dyes are off while another object uses the same model");

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
                DyeStateIsApplied(furniture, stableBgObject, useForcedDyes, stableEntry.MaterialSlotCount))
            {
                stableBgObject->IsVisible = true;
                return;
            }
        }

        if (!this.bgObjects.TryGetValue(furniture.Id, out var entry) || entry.Pointer == 0)
        {
            var pointer = this.CreateBgObject(furniture, path);
            if (pointer == 0)
                return;

            entry = new SpawnedBgObject(pointer, path);
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
        if (entry.ForcedDyeState == state && ForcedDyesAreApplied(furniture, bgObject, materialCount))
            return;

        entry.ForcedDyeState = state;

        var changed = 0;
        for (var slot = 0; slot < materialCount; slot++)
        {
            if (!TryGetMaterialHandle(bgObject, slot, out var material))
                continue;

            if (!furniture.IsForcedMaterialDyeColorEnabled(slot) && furniture.GetForcedMaterialStainId(slot) == 0)
            {
                if (entry.RestoreOriginalDiffuseConstant(slot, material))
                    changed++;
                if (entry.RestoreOriginalColorTable(slot, material))
                    changed++;

                continue;
            }

            var color = furniture.IsForcedMaterialDyeColorEnabled(slot)
                ? furniture.GetForcedMaterialDyeColor(slot)
                : ToVector4(GetHousingStainColor(furniture.GetForcedMaterialStainId(slot)));

            if (color is null)
                continue;

            var applied = false;
            applied |= ApplyMaterialDiffuseConstant(entry, slot, material, color.Value);
            applied |= ApplyMaterialColorTable(entry, slot, material, color.Value);
            if (applied)
                changed++;
        }

        if (changed > 0)
            ((DrawObject*)bgObject)->UpdateMaterials();
        entry.PrimaryDyeState = 0;
    }

    private static bool DyeStateIsApplied(SpawnedFurniture furniture, BgObject* bgObject, bool useForcedDyes, int materialCount)
        => useForcedDyes
            ? ForcedDyesAreApplied(furniture, bgObject, materialCount)
            : PrimaryDyeIsApplied(furniture, bgObject);

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

    private static bool ForcedDyesAreApplied(SpawnedFurniture furniture, BgObject* bgObject, int materialCount)
    {
        for (var slot = 0; slot < materialCount; slot++)
        {
            if (!furniture.IsForcedMaterialDyeColorEnabled(slot) && furniture.GetForcedMaterialStainId(slot) == 0)
                continue;

            if (!TryGetMaterialHandle(bgObject, slot, out var material))
                return false;

            var color = furniture.IsForcedMaterialDyeColorEnabled(slot)
                ? furniture.GetForcedMaterialDyeColor(slot)
                : ToVector4(GetHousingStainColor(furniture.GetForcedMaterialStainId(slot)));
            if (color is null)
                continue;

            if (TryGetDiffuseColorConstant(material, out var diffuse) && !RgbMatches(diffuse, color.Value))
                return false;

            if (TryGetMaterialColorTable(material, out var table, out var width, out var height) &&
                !ColorTableMatches(table, width, height, color.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RgbMatches(Span<float> values, System.Numerics.Vector4 color)
        => MathF.Abs(values[0] - Math.Clamp(color.X, 0.0f, 1.0f)) < 0.0001f &&
            MathF.Abs(values[1] - Math.Clamp(color.Y, 0.0f, 1.0f)) < 0.0001f &&
            MathF.Abs(values[2] - Math.Clamp(color.Z, 0.0f, 1.0f)) < 0.0001f;

    private static bool ColorTableMatches(Span<Half> table, int width, int height, System.Numerics.Vector4 color)
    {
        var red = (Half)Math.Clamp(color.X, 0.0f, 1.0f);
        var green = (Half)Math.Clamp(color.Y, 0.0f, 1.0f);
        var blue = (Half)Math.Clamp(color.Z, 0.0f, 1.0f);

        for (var row = 0; row < height; row++)
        {
            var offset = row * width * 4;
            if (table[offset] != red || table[offset + 1] != green || table[offset + 2] != blue)
                return false;
        }

        return true;
    }

    private void SetForcedDyeMode(SpawnedBgObject entry, bool enabled)
    {
        entry.ForcedDyesEnabled = enabled;
        entry.ForcedDyeState = 0;

        if (enabled || entry.Pointer == 0)
            return;

        var bgObject = (BgObject*)entry.Pointer;
        if (entry.RestoreAllOriginalMaterialColors(bgObject))
            ((DrawObject*)bgObject)->UpdateMaterials();
    }

    private static bool ApplyMaterialDiffuseConstant(SpawnedBgObject entry, int slot, MaterialResourceHandle* material, System.Numerics.Vector4 color)
    {
        // capture before writing because disabling forced dyes must restore this buffer
        if (!entry.CaptureOriginalDiffuseConstant(slot, material))
            return false;

        if (!TryGetDiffuseColorConstant(material, out var values))
            return false;

        values[0] = Math.Clamp(color.X, 0.0f, 1.0f);
        values[1] = Math.Clamp(color.Y, 0.0f, 1.0f);
        values[2] = Math.Clamp(color.Z, 0.0f, 1.0f);
        return true;
    }

    private static bool ApplyMaterialColorTable(SpawnedBgObject entry, int slot, MaterialResourceHandle* material, System.Numerics.Vector4 color)
    {
        if (!entry.CaptureOriginalColorTable(slot, material))
            return false;

        if (!TryGetMaterialColorTable(material, out var table, out var width, out var height))
            return false;

        var red = (Half)Math.Clamp(color.X, 0.0f, 1.0f);
        var green = (Half)Math.Clamp(color.Y, 0.0f, 1.0f);
        var blue = (Half)Math.Clamp(color.Z, 0.0f, 1.0f);

        for (var row = 0; row < height; row++)
        {
            var offset = row * width * 4;
            if (offset + 2 >= table.Length)
                break;

            table[offset] = red;
            table[offset + 1] = green;
            table[offset + 2] = blue;
        }

        // prepare the edited table for the next material update
        material->PrepareColorTable(0, 0);
        return true;
    }

    private static bool TryGetMaterialColorTable(MaterialResourceHandle* material, out Span<Half> table, out int width, out int height)
    {
        table = default;
        width = 0;
        height = 0;

        if (material is null ||
            material->LoadState != 7 ||
            material->AdditionalData is null ||
            material->DataSet is null ||
            material->AdditionalDataSize < sizeof(uint))
        {
            return false;
        }

        var dataFlags = *(uint*)material->AdditionalData;
        if ((dataFlags & 0x4) == 0)
            return false;

        // these flag nibbles encode the color table dimensions as powers of two
        var widthLog = (byte)((dataFlags >> 4) & 0xF);
        var heightLog = (byte)((dataFlags >> 8) & 0xF);
        width = (dataFlags & 0xFF0) == 0 ? 4 : 1 << widthLog;
        height = (dataFlags & 0xFF0) == 0 ? 16 : 1 << heightLog;

        var length = height * width * 4;
        var byteLength = length * sizeof(Half);
        if (material->DataSetSize < byteLength)
            return false;

        table = new Span<Half>((Half*)material->DataSet, length);
        return true;
    }

    private static bool TryGetDiffuseColorConstant(MaterialResourceHandle* material, out Span<float> values)
    {
        values = default;

        if (material is null ||
            material->LoadState != 7 ||
            material->Material is null ||
            material->Material->MaterialParameterCBuffer is null ||
            material->ShaderPackageResourceHandle is null)
        {
            return false;
        }

        var shaderPackage = material->ShaderPackageResourceHandle->ShaderPackage;
        if (shaderPackage is null ||
            shaderPackage->MaterialElements is null ||
            shaderPackage->MaterialElementCount == 0)
        {
            return false;
        }

        var cbuffer = material->Material->MaterialParameterCBuffer;
        if (cbuffer->ByteSize <= 0)
            return false;

        for (var i = 0; i < shaderPackage->MaterialElementCount; i++)
        {
            var element = shaderPackage->MaterialElements[i];
            if (element.CRC != DiffuseColorConstantId || element.Size < sizeof(float) * 3)
                continue;

            if (element.Offset >= cbuffer->ByteSize || element.Offset + element.Size > cbuffer->ByteSize)
                return false;

            // the shader element offset addresses the material parameter buffer
            var source = cbuffer->LoadSourcePointer(element.Offset, element.Size);
            if (source is null)
                return false;

            values = new Span<float>(source, element.Size / sizeof(float));
            return values.Length >= 3;
        }

        return false;
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

    private static System.Numerics.Vector4 ReadDiffuseColor(Span<Half> table)
        => new(
            Math.Clamp((float)table[0], 0.0f, 1.0f),
            Math.Clamp((float)table[1], 0.0f, 1.0f),
            Math.Clamp((float)table[2], 0.0f, 1.0f),
            1.0f);

    private static System.Numerics.Vector4 ReadDiffuseColor(Span<float> values)
        => new(
            Math.Clamp(values[0], 0.0f, 1.0f),
            Math.Clamp(values[1], 0.0f, 1.0f),
            Math.Clamp(values[2], 0.0f, 1.0f),
            1.0f);

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
        if (!this.bgObjects.Remove(id, out var entry))
            return;

        if (entry.Pointer == 0)
            return;

        var bgObject = (BgObject*)entry.Pointer;
        if (entry.RestoreAllOriginalMaterialColors(bgObject))
            ((DrawObject*)bgObject)->UpdateMaterials();

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

    private sealed class SpawnedBgObject(nint pointer, string modelPath)
    {
        private readonly Dictionary<int, ColorTableBackup> originalColorTables = [];
        private readonly Dictionary<int, DiffuseConstantBackup> originalDiffuseConstants = [];

        public nint Pointer { get; } = pointer;
        public string ModelPath { get; } = modelPath;
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

        public bool CaptureOriginalColorTable(int slot, MaterialResourceHandle* material)
        {
            if (!TryGetMaterialColorTable(material, out var table, out _, out _))
                return false;

            var materialPointer = (nint)material;
            // a reloaded material needs a fresh backup even when the slot is unchanged
            if (this.originalColorTables.TryGetValue(slot, out var backup) && backup.MaterialPointer == materialPointer)
                return true;

            this.originalColorTables[slot] = new ColorTableBackup(materialPointer, table.ToArray());
            return true;
        }

        public bool CaptureOriginalDiffuseConstant(int slot, MaterialResourceHandle* material)
        {
            if (!TryGetDiffuseColorConstant(material, out var values))
                return false;

            var materialPointer = (nint)material;
            if (this.originalDiffuseConstants.TryGetValue(slot, out var backup) && backup.MaterialPointer == materialPointer)
                return true;

            this.originalDiffuseConstants[slot] = new DiffuseConstantBackup(materialPointer, values.ToArray());
            return true;
        }

        public bool RestoreOriginalDiffuseConstant(int slot, MaterialResourceHandle* material)
        {
            if (!this.originalDiffuseConstants.TryGetValue(slot, out var backup) || backup.MaterialPointer != (nint)material)
                return false;

            return RestoreDiffuseConstant(material, backup);
        }

        public bool RestoreOriginalColorTable(int slot, MaterialResourceHandle* material)
        {
            if (!this.originalColorTables.TryGetValue(slot, out var backup) || backup.MaterialPointer != (nint)material)
                return false;

            return RestoreColorTable(material, backup);
        }

        public bool RestoreAllOriginalMaterialColors(BgObject* bgObject)
        {
            var model = bgObject->ModelResourceHandle;
            if (model is null || model->LoadState != 7 || model->MaterialResourceHandles is null)
                return false;

            var restored = false;
            foreach (var (slot, backup) in this.originalDiffuseConstants)
            {
                if (slot < 0 || slot >= MaxMaterialSlots)
                    continue;

                if (!TryGetMaterialHandle(bgObject, slot, out var material) || (nint)material != backup.MaterialPointer)
                    continue;

                restored |= RestoreDiffuseConstant(material, backup);
            }

            foreach (var (slot, backup) in this.originalColorTables)
            {
                if (slot < 0 || slot >= MaxMaterialSlots)
                    continue;

                if (!TryGetMaterialHandle(bgObject, slot, out var material) || (nint)material != backup.MaterialPointer)
                    continue;

                restored |= RestoreColorTable(material, backup);
            }

            return restored;
        }

        private static bool RestoreDiffuseConstant(MaterialResourceHandle* material, DiffuseConstantBackup backup)
        {
            if (material is null || backup.MaterialPointer == 0)
                return false;

            if (!TryGetDiffuseColorConstant(material, out var values) || values.Length < backup.Values.Length)
                return false;

            backup.Values.CopyTo(values);
            return true;
        }

        private static bool RestoreColorTable(MaterialResourceHandle* material, ColorTableBackup backup)
        {
            if (material is null || backup.MaterialPointer == 0)
                return false;

            if (!TryGetMaterialColorTable(material, out var table, out _, out _) || table.Length != backup.Values.Length)
                return false;

            backup.Values.CopyTo(table);
            material->PrepareColorTable(0, 0);
            return true;
        }
    }

    private sealed record ColorTableBackup(nint MaterialPointer, Half[] Values);
    private sealed record DiffuseConstantBackup(nint MaterialPointer, float[] Values);
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
