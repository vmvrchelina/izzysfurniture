using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using EquipmentSlot = FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer.EquipmentSlot;
using WeaponSlot = FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer.WeaponSlot;

namespace IzzysFurniture;

internal unsafe sealed class NpcSpawner : IDisposable
{
    private readonly NpcAppearanceInterop appearanceInterop;
    private readonly Dictionary<Guid, SpawnedNpc> npcs = [];

    public NpcSpawner(NpcAppearanceInterop appearanceInterop)
    {
        this.appearanceInterop = appearanceInterop;
        this.appearanceInterop.PenumbraObjectRedrawn += this.OnPenumbraObjectRedrawn;
    }

    public void ReapplyAppearance(Guid id)
    {
        if (!this.npcs.TryGetValue(id, out var entry))
            return;

        entry.RecreateRequested = true;
    }

    public bool TryGetObjectIndex(Guid id, out ushort objectIndex)
    {
        if (this.npcs.TryGetValue(id, out var entry) && this.GetCharacter(entry.ClientObjectIndex) is not null)
        {
            objectIndex = entry.ObjectIndex;
            return true;
        }

        objectIndex = 0;
        return false;
    }

    public bool CaptureGlamourerAppearance(Guid id, SpawnedFurniture item)
    {
        if (!this.npcs.TryGetValue(id, out var entry) ||
            !this.appearanceInterop.TryGetGlamourerState(entry.ObjectIndex, out var state))
            return false;

        item.NpcGlamourerStateBase64 = state;
        entry.LastGlamourerStateBase64 = state;
        entry.GlamourerStateAttempted = true;
        return true;
    }

    public void Apply(IReadOnlyList<SpawnedFurniture> sceneObjects)
    {
        var npcItems = sceneObjects
            .Where(item => item.IsNpc && item.NpcItem is not null)
            .ToArray();
        var activeIds = npcItems.Select(item => item.Id).ToHashSet();

        foreach (var id in this.npcs.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            this.DestroyNpc(id);

        foreach (var item in npcItems)
        {
            if (item.Enabled)
                this.ApplyNpc(item);
            else
                this.HideNpc(item.Id);
        }
    }

    public void Clear()
    {
        foreach (var id in this.npcs.Keys.ToArray())
            this.DestroyNpc(id);
    }

    public void Dispose()
    {
        this.appearanceInterop.PenumbraObjectRedrawn -= this.OnPenumbraObjectRedrawn;
        this.Clear();
    }

    private void ApplyNpc(SpawnedFurniture item)
    {
        if (item.NpcItem is null)
            return;

        if (!this.npcs.TryGetValue(item.Id, out var entry))
        {
            if (!this.CreateNpc(item, out entry))
                return;

            this.npcs[item.Id] = entry;
        }

        if (entry.RecreateRequested)
        {
            this.DestroyNpc(item.Id);
            return;
        }

        var character = this.GetCharacter(entry.ClientObjectIndex);
        if (character is null)
            return;

        this.ApplyAppearanceIntegrations(character, item, entry);
        this.SetNpcVisible(character, entry, true);
        this.ApplyPatrol(item, entry);
        this.ApplyTransform(character, item, entry);
        this.ApplyAnimation(character, item, entry);
        this.ApplySpeech(character, item, entry);
    }

    private bool CreateNpc(SpawnedFurniture item, out SpawnedNpc entry)
    {
        entry = null!;

        var localPlayer = Service.ObjectTable.LocalPlayer;
        if (localPlayer is null)
            return false;

        var com = ClientObjectManager.Instance();
        if (com is null)
            return false;

        // client object manager
        var idCheck = com->CreateBattleCharacter();
        if (idCheck == 0xffffffff)
            return false;

        var clientObjectIndex = (ushort)idCheck;
        var gameObject = com->GetObjectByIndex(clientObjectIndex);
        if (gameObject is null)
            return false;

        var character = (NativeCharacter*)gameObject;
        var objectIndex = gameObject->ObjectIndex;
        var actorName = CreateActorName(objectIndex);
        SetActorName((NativeGameObject*)gameObject, actorName);
        var sourceCharacter = (NativeCharacter*)localPlayer.Address;
        // this initializes native character containers before the npc data overwrites them
        character->CharacterSetup.CopyFromCharacter(sourceCharacter, CharacterSetupContainer.CopyFlags.WeaponHiding);
        if (!this.ApplyAppearance(character, item.NpcItem!))
        {
            com->DeleteObjectByIndex(clientObjectIndex, 0);
            return false;
        }
        entry = new SpawnedNpc(clientObjectIndex, objectIndex);
        this.ApplyTransform(character, item, entry, forceModified: true);
        this.SetNpcVisible(character, entry, true);
        return true;
    }

    private bool ApplyAppearance(NativeCharacter* character, NpcCatalogItem item)
    {
        character->DisableDraw();
        switch (item.SourceKind)
        {
            case NpcSourceKind.BattleNpc:
                character->BaseId = item.RowId;
                character->CharacterSetup.SetupBNpc(item.RowId, 0);
                break;
            case NpcSourceKind.EventNpc:
                if (!this.ApplyEventNpcAppearance(character, item.RowId))
                    return false;
                break;
            default:
                character->ModelContainer.ModelCharaId = (int)item.ModelCharaId;
                character->CharacterSetup.CopyFromCharacter(character, CharacterSetupContainer.CopyFlags.None);
                break;
        }

        return true;
    }

    private bool ApplyEventNpcAppearance(NativeCharacter* character, uint rowId)
    {
        var npc = Service.DataManager.GetExcelSheet<ENpcBase>().GetRow(rowId);
        character->BaseId = rowId;
        character->ModelContainer.ModelCharaId = (int)npc.ModelChara.RowId;

        var customize = character->DrawData.CustomizeData;
        var data = customize.Data;
        // customize byte layout
        data[0] = (byte)npc.Race.RowId;
        data[1] = npc.Gender;
        data[2] = npc.BodyType;
        data[3] = npc.Height;
        data[4] = (byte)npc.Tribe.RowId;
        data[5] = npc.Face;
        data[6] = npc.HairStyle;
        data[7] = npc.HairHighlight;
        data[8] = npc.SkinColor;
        data[9] = npc.EyeHeterochromia;
        data[10] = npc.HairColor;
        data[11] = npc.HairHighlightColor;
        data[12] = npc.FacialFeature;
        data[13] = npc.FacialFeatureColor;
        data[14] = npc.Eyebrows;
        data[15] = npc.EyeColor;
        data[16] = npc.EyeShape;
        data[17] = npc.Nose;
        data[18] = npc.Jaw;
        data[19] = npc.Mouth;
        data[20] = npc.LipColor;
        data[21] = npc.BustOrTone1;
        data[22] = npc.ExtraFeature1;
        data[23] = npc.ExtraFeature2OrBust;
        data[24] = npc.FacePaint;
        data[25] = npc.FacePaintColor;
        character->DrawData.CustomizeData = customize;

        // enpc equipment fields
        NpcEquip? equip = npc.NpcEquip.RowId == 0 ? null : npc.NpcEquip.Value;
        uint EquipModel(Func<NpcEquip, uint> get) => equip is { } value ? get(value) : 0;
        uint EquipStain(Func<NpcEquip, Lumina.Excel.RowRef<Stain>> get) => equip is { } value ? get(value).RowId : 0;
        ulong EquipWeapon(Func<NpcEquip, ulong> get) => equip is { } value ? get(value) : 0;

        this.SetEquipment(character, EquipmentSlot.Head, npc.ModelHead, npc.DyeHead.RowId, npc.Dye2Head.RowId, EquipModel(row => row.ModelHead), EquipStain(row => row.DyeHead), EquipStain(row => row.Dye2Head));
        this.SetEquipment(character, EquipmentSlot.Body, npc.ModelBody, npc.DyeBody.RowId, npc.Dye2Body.RowId, EquipModel(row => row.ModelBody), EquipStain(row => row.DyeBody), EquipStain(row => row.Dye2Body));
        this.SetEquipment(character, EquipmentSlot.Hands, npc.ModelHands, npc.DyeHands.RowId, npc.Dye2Hands.RowId, EquipModel(row => row.ModelHands), EquipStain(row => row.DyeHands), EquipStain(row => row.Dye2Hands));
        this.SetEquipment(character, EquipmentSlot.Legs, npc.ModelLegs, npc.DyeLegs.RowId, npc.Dye2Legs.RowId, EquipModel(row => row.ModelLegs), EquipStain(row => row.DyeLegs), EquipStain(row => row.Dye2Legs));
        this.SetEquipment(character, EquipmentSlot.Feet, npc.ModelFeet, npc.DyeFeet.RowId, npc.Dye2Feet.RowId, EquipModel(row => row.ModelFeet), EquipStain(row => row.DyeFeet), EquipStain(row => row.Dye2Feet));
        this.SetEquipment(character, EquipmentSlot.Ears, npc.ModelEars, npc.DyeEars.RowId, npc.Dye2Ears.RowId, EquipModel(row => row.ModelEars), EquipStain(row => row.DyeEars), EquipStain(row => row.Dye2Ears));
        this.SetEquipment(character, EquipmentSlot.Neck, npc.ModelNeck, npc.DyeNeck.RowId, npc.Dye2Neck.RowId, EquipModel(row => row.ModelNeck), EquipStain(row => row.DyeNeck), EquipStain(row => row.Dye2Neck));
        this.SetEquipment(character, EquipmentSlot.Wrists, npc.ModelWrists, npc.DyeWrists.RowId, npc.Dye2Wrists.RowId, EquipModel(row => row.ModelWrists), EquipStain(row => row.DyeWrists), EquipStain(row => row.Dye2Wrists));
        this.SetEquipment(character, EquipmentSlot.RFinger, npc.ModelRightRing, npc.DyeRightRing.RowId, npc.Dye2RightRing.RowId, EquipModel(row => row.ModelRightRing), EquipStain(row => row.DyeRightRing), EquipStain(row => row.Dye2RightRing));
        this.SetEquipment(character, EquipmentSlot.LFinger, npc.ModelLeftRing, npc.DyeLeftRing.RowId, npc.Dye2LeftRing.RowId, EquipModel(row => row.ModelLeftRing), EquipStain(row => row.DyeLeftRing), EquipStain(row => row.Dye2LeftRing));

        var mainHand = npc.ModelMainHand != 0 ? npc.ModelMainHand : EquipWeapon(row => row.ModelMainHand);
        var offHand = npc.ModelOffHand != 0 ? npc.ModelOffHand : EquipWeapon(row => row.ModelOffHand);
        this.SetWeapon(character, WeaponSlot.MainHand, mainHand, npc.DyeMainHand.RowId, npc.Dye2MainHand.RowId, EquipStain(row => row.DyeMainHand), EquipStain(row => row.Dye2MainHand));
        this.SetWeapon(character, WeaponSlot.OffHand, offHand, npc.DyeOffHand.RowId, npc.Dye2OffHand.RowId, EquipStain(row => row.DyeOffHand), EquipStain(row => row.Dye2OffHand));

        character->DrawData.SetVisor(npc.Visor || (equip is { } visorEquip && visorEquip.Visor));
        character->CharacterSetup.CopyFromCharacter(character, CharacterSetupContainer.CopyFlags.None);
        return true;
    }

    private void SetEquipment(
        NativeCharacter* character,
        EquipmentSlot slot,
        uint directModel,
        uint directStain0,
        uint directStain1,
        uint baseModel,
        uint baseStain0,
        uint baseStain1)
    {
        var model = new EquipmentModelId
        {
            Value = directModel != 0 ? directModel : baseModel,
            Stain0 = (byte)(directStain0 != 0 ? directStain0 : baseStain0),
            Stain1 = (byte)(directStain1 != 0 ? directStain1 : baseStain1),
        };
        character->DrawData.Equipment(slot) = model;
    }

    private void SetWeapon(
        NativeCharacter* character,
        WeaponSlot slot,
        ulong modelValue,
        uint directStain0,
        uint directStain1,
        uint baseStain0,
        uint baseStain1)
    {
        var model = new WeaponModelId { Value = modelValue };
        character->DrawData.LoadWeapon(
            slot,
            model,
            (byte)(directStain0 != 0 ? directStain0 : baseStain0),
            (byte)(directStain1 != 0 ? directStain1 : baseStain1),
            0,
            0,
            false);
    }

    private static void SetActorName(NativeGameObject* gameObject, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        var length = Math.Min(bytes.Length, 63);
        for (var index = 0; index < length; index++)
            gameObject->Name[index] = bytes[index];

        gameObject->Name[length] = 0;
    }

    private static string CreateActorName(ushort objectIndex)
    {
        // unique name
        Span<char> suffix = stackalloc char[4];
        var value = objectIndex;
        for (var index = suffix.Length - 1; index >= 0; index--)
        {
            suffix[index] = (char)('a' + value % 26);
            value /= 26;
        }

        return $"Izzy Actor{new string(suffix)}";
    }

    private void ApplyAppearanceIntegrations(NativeCharacter* character, SpawnedFurniture item, SpawnedNpc entry)
    {
        if (character->DrawObject is null)
            return;

        // penumbra runs first
        if (!entry.PenumbraAttempted || entry.LastPenumbraCollectionId != item.NpcPenumbraCollectionId)
        {
            this.ApplyPenumbraCollection(item, entry);
            if (entry.AwaitingPenumbraRedraw)
                return;
        }

        if (entry.AwaitingPenumbraRedraw)
            return;

        // no beastman customize
        if (item.NpcItem?.DisplayKind != NpcDisplayKind.Human)
            return;

        var hasPortableState = !string.IsNullOrWhiteSpace(item.NpcGlamourerStateBase64);
        if (hasPortableState)
        {
            if (!entry.GlamourerStateAttempted ||
                !string.Equals(entry.LastGlamourerStateBase64, item.NpcGlamourerStateBase64, StringComparison.Ordinal))
            {
                entry.GlamourerStateAttempted = true;
                entry.LastGlamourerStateBase64 = item.NpcGlamourerStateBase64;
                this.appearanceInterop.ApplyGlamourerState(
                    entry.ObjectIndex,
                    item.NpcGlamourerStateBase64);
                entry.GlamourerDesignAttempted = true;
                entry.LastGlamourerDesignId = item.NpcGlamourerDesignId;
                entry.CustomizePlusAttempted = false;
            }
        }
        else
        {
            entry.GlamourerStateAttempted = false;
            entry.LastGlamourerStateBase64 = string.Empty;

            if (!entry.GlamourerDesignAttempted || entry.LastGlamourerDesignId != item.NpcGlamourerDesignId)
            {
                entry.GlamourerDesignAttempted = true;
                entry.LastGlamourerDesignId = item.NpcGlamourerDesignId;
                var applied = this.appearanceInterop.ApplyGlamourerDesign(
                    entry.ObjectIndex,
                    item.NpcGlamourerDesignId);
                if (applied &&
                    item.NpcGlamourerDesignId != Guid.Empty &&
                    this.appearanceInterop.TryGetGlamourerState(entry.ObjectIndex, out var appliedState))
                {
                    item.NpcGlamourerStateBase64 = appliedState;
                    entry.LastGlamourerStateBase64 = appliedState;
                    entry.GlamourerStateAttempted = true;
                }

                entry.CustomizePlusAttempted = false;
            }
        }

        if (!entry.CustomizePlusAttempted ||
            entry.LastCustomizePlusProfileId != item.NpcCustomizePlusProfileId ||
            !string.Equals(entry.LastCustomizePlusProfileJson, item.NpcCustomizePlusProfileJson, StringComparison.Ordinal))
        {
            entry.CustomizePlusAttempted = true;
            entry.LastCustomizePlusProfileId = item.NpcCustomizePlusProfileId;
            entry.LastCustomizePlusProfileJson = item.NpcCustomizePlusProfileJson;
            this.appearanceInterop.ApplyCustomizePlusProfile(
                entry.ObjectIndex,
                item.NpcCustomizePlusProfileJson);
        }
    }

    private void ApplyPenumbraCollection(SpawnedFurniture item, SpawnedNpc entry)
    {
        Guid? collectionId = item.NpcPenumbraCollectionId == Guid.Empty ? null : item.NpcPenumbraCollectionId;
        var needsAssignment = collectionId is not null || entry.LastPenumbraCollectionId != Guid.Empty;
        entry.PenumbraAttempted = true;
        entry.LastPenumbraCollectionId = item.NpcPenumbraCollectionId;
        var applied = !needsAssignment ||
            this.appearanceInterop.SetPenumbraCollection(entry.ObjectIndex, collectionId);
        entry.GlamourerStateAttempted = false;
        entry.GlamourerDesignAttempted = false;
        entry.CustomizePlusAttempted = false;

        if (!needsAssignment || !applied)
            return;

        entry.AwaitingPenumbraRedraw = true;
        if (!this.appearanceInterop.RedrawPenumbraObject(entry.ObjectIndex))
            entry.AwaitingPenumbraRedraw = false;
    }

    private void OnPenumbraObjectRedrawn(int objectIndex)
    {
        foreach (var entry in this.npcs.Values)
        {
            if (entry.ObjectIndex != objectIndex)
                continue;

            entry.AwaitingPenumbraRedraw = false;
            // force one transform write
            entry.LastAppliedPosition = new Vector3(float.NaN, float.NaN, float.NaN);
            return;
        }
    }

    private NativeCharacter* GetCharacter(ushort objectIndex)
    {
        var com = ClientObjectManager.Instance();
        if (com is null)
            return null;

        var gameObject = com->GetObjectByIndex(objectIndex);
        return gameObject is null ? null : (NativeCharacter*)gameObject;
    }

    private void HideNpc(Guid id)
    {
        if (!this.npcs.TryGetValue(id, out var entry))
            return;

        var character = this.GetCharacter(entry.ClientObjectIndex);
        if (character is null)
            return;

        this.SetNpcVisible(character, entry, false);
    }

    private void SetNpcVisible(NativeCharacter* character, SpawnedNpc entry, bool visible)
    {
        if (visible)
        {
            character->Alpha = 1.0f;
            if (!entry.Visible || character->RenderFlags != VisibilityFlags.None || character->DrawObject is null)
                character->EnableDraw();

            entry.Visible = true;
            return;
        }

        if (entry.Visible)
            character->YellBalloon.CloseBalloon();

        character->Alpha = 0.0f;
        entry.Visible = false;
    }

    private void ApplyTransform(NativeCharacter* character, SpawnedFurniture item, SpawnedNpc entry, bool forceModified = false)
    {
        var position = item.Position;
        var rotation = item.YawRadians;
        var scale = item.Scale;
        var positionChanged = forceModified || !IsSamePosition(entry.LastAppliedPosition, position);
        var rotationChanged = forceModified || !IsSameRotation(entry.LastAppliedYawRadians, rotation);
        var scaleChanged = forceModified || !IsSameScale(entry.LastAppliedScale, scale);

        if (!positionChanged && !rotationChanged && !scaleChanged)
            return;

        character->SetPosition(position.X, position.Y, position.Z);
        character->SetRotation(rotation);
        character->Scale = scale;
        character->ModelScale = scale;

        if (forceModified || !entry.MovedThisTick)
        {
            // keep this origin there
            character->DefaultPosition = position;
            character->DefaultRotation = rotation;

            if (positionChanged)
                character->PositionModified();
            if (rotationChanged)
                character->RotationModified();
        }

        if (scaleChanged && character->DrawObject is not null)
        {
            character->DrawObject->Object.Scale = new FFXIVClientStructs.FFXIV.Common.Math.Vector3(scale, scale, scale);
        }

        entry.LastAppliedPosition = position;
        entry.LastAppliedYawRadians = rotation;
        entry.LastAppliedScale = scale;
    }

    private static bool IsSamePosition(Vector3 a, Vector3 b)
        => !float.IsNaN(a.X) && Vector3.DistanceSquared(a, b) < 0.0001f;

    private static bool IsSameRotation(float a, float b)
        => !float.IsNaN(a) && MathF.Abs(a - b) < 0.0001f;

    private static bool IsSameScale(float a, float b)
        => !float.IsNaN(a) && MathF.Abs(a - b) < 0.0001f;

    private void ApplyPatrol(SpawnedFurniture item, SpawnedNpc entry)
    {
        if (!item.NpcPatrolEnabled || item.NpcPatrolPoints.Count < 2)
        {
            entry.LastPatrolTimestamp = Stopwatch.GetTimestamp();
            entry.MovedThisTick = false;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        // cap a delayed frame so a hitch cannot skip an entire patrol segment
        var elapsed = entry.LastPatrolTimestamp == 0
            ? 0.0f
            : Math.Clamp((now - entry.LastPatrolTimestamp) / (float)Stopwatch.Frequency, 0.0f, 0.25f);
        entry.LastPatrolTimestamp = now;

        var targetIndex = Math.Clamp(entry.PatrolTargetIndex, 0, item.NpcPatrolPoints.Count - 1);

        var target = GroundedPatrolTarget(item, targetIndex);
        var toTarget = target - item.Position;
        var distance = toTarget.Length();
        if (distance < 0.05f)
        {
            targetIndex++;
            if (targetIndex >= item.NpcPatrolPoints.Count)
            {
                if (item.NpcPatrolLoop)
                    targetIndex = 0;
                else
                {
                    item.NpcPatrolEnabled = false;
                    entry.MovedThisTick = false;
                    return;
                }
            }

            entry.PatrolTargetIndex = targetIndex;
            target = GroundedPatrolTarget(item, targetIndex);
            toTarget = target - item.Position;
            distance = toTarget.Length();
        }

        if (distance <= 0.001f)
        {
            entry.MovedThisTick = false;
            return;
        }

        var direction = Vector3.Normalize(toTarget);
        var step = Math.Min(distance, item.NpcPatrolSpeed * elapsed);
        if (step > 0.0f)
        {
            var nextPosition = item.Position + direction * step;
            if (item.NpcPatrolSnapToTerrain && TryFindGround(nextPosition, out var groundedPosition))
                nextPosition.Y = groundedPosition.Y;

            item.Position = nextPosition;
        }

        item.RotationDegrees = new Vector3(item.RotationDegrees.X, MathF.Atan2(direction.X, direction.Z) * 180.0f / MathF.PI, item.RotationDegrees.Z);
        entry.MovedThisTick = true;
    }

    private static bool TryFindGround(Vector3 position, out Vector3 groundedPosition)
    {
        // cast from above the waypoint to tolerate points placed below uneven terrain
        // thanks noyel
        var origin = new Vector3(position.X, position.Y + 80.0f, position.Z);
        var direction = new Vector3(0.0f, -1.0f, 0.0f);
        if (BGCollisionModule.RaycastMaterialFilter(origin, direction, out var hit, 240.0f))
        {
            groundedPosition = hit.Point;
            return true;
        }

        groundedPosition = position;
        return false;
    }

    private static Vector3 GroundedPatrolTarget(SpawnedFurniture item, int targetIndex)
    {
        var target = item.NpcPatrolPoints[targetIndex].Position;
        if (item.NpcPatrolSnapToTerrain && TryFindGround(target, out var groundedTarget))
            target.Y = groundedTarget.Y;

        return target;
    }

    private void ApplyAnimation(NativeCharacter* character, SpawnedFurniture item, SpawnedNpc entry)
    {
        // locomotion temporarily owns the timeline while patrol movement is active
        if (entry.MovedThisTick)
        {
            var locomotionTimeline = LocomotionTimelineFor(item);
            if (entry.LastTimelineId != locomotionTimeline || !entry.WasMovingLastTick)
            {
                ResetAnimationToIdle(character);
                entry.LastTimelineId = locomotionTimeline;
                entry.LastLoopSetting = true;
                this.ApplyLocomotionTimeline(character, locomotionTimeline, entry);
            }

            entry.WasMovingLastTick = true;
            return;
        }

        entry.WasMovingLastTick = false;

        var animation = item.NpcAnimation;
        if (animation is not null && item.NpcItem is { } npcItem && !animation.Supports(npcItem.DisplayKind))
            animation = null;
        if (animation is null || animation.TimelineId == 0)
        {
            if (entry.LastTimelineId != 3 || !entry.LastLoopSetting)
            {
                ResetAnimationToIdle(character);
                entry.LastTimelineId = 3;
                entry.LastLoopSetting = true;
            }

            return;
        }

        var loop = item.NpcLoopAnimation && animation.IsLoop;
        if (entry.LastTimelineId != animation.TimelineId || entry.LastLoopSetting != loop)
            StartAnimation(character, animation, loop, entry);
    }

    private static void StartAnimation(NativeCharacter* character, NpcAnimationCatalogItem animation, bool loop, SpawnedNpc entry)
    {
        character->Timeline.OverallSpeed = 1.0f;
        if (loop)
        {
            character->SetMode(CharacterModes.AnimLock, 0);
            character->Timeline.BaseOverride = animation.TimelineId;
            character->Timeline.TimelineSequencer.SetSlotTimeline(0, animation.TimelineId);
            character->Timeline.TimelineSequencer.SetSlotSpeed(0, 1.0f);
        }
        else
        {
            character->SetMode(CharacterModes.Normal, 0);
            character->Timeline.BaseOverride = 3;
        }

        character->Timeline.TimelineSequencer.PlayTimeline(animation.TimelineId);
        entry.LastTimelineId = animation.TimelineId;
        entry.LastLoopSetting = loop;
    }

    private static void ResetAnimationToIdle(NativeCharacter* character)
    {
        character->SetMode(CharacterModes.Normal, 0);
        character->Timeline.OverallSpeed = 1.0f;
        character->Timeline.BaseOverride = 3;
        character->Timeline.TimelineSequencer.SetSlotTimeline(0, 3);
        character->Timeline.TimelineSequencer.SetSlotSpeed(0, 1.0f);
        character->Timeline.TimelineSequencer.PlayTimeline(3);
    }

    private static ushort LocomotionTimelineFor(SpawnedFurniture item)
    {
        if (item.NpcAnimation is { Category: "Locomotion" } animation)
            return animation.TimelineId;

        return item.NpcPatrolSpeed >= 3.5f ? (ushort)22 : (ushort)13;
    }

    private void ApplyLocomotionTimeline(NativeCharacter* character, ushort timelineId, SpawnedNpc entry)
    {
        character->SetMode(CharacterModes.Normal, 0);
        character->Timeline.OverallSpeed = 1.0f;
        character->Timeline.BaseOverride = timelineId;
        character->Timeline.TimelineSequencer.SetSlotTimeline(0, timelineId);
        character->Timeline.TimelineSequencer.SetSlotSpeed(0, 1.0f);
    }

    private void ApplySpeech(NativeCharacter* character, SpawnedFurniture item, SpawnedNpc entry)
    {
        if (!item.NpcSpeechEnabled || string.IsNullOrWhiteSpace(item.NpcSpeechText))
            return;

        if (item.NpcSpeechRequirePlayerNear && Service.ObjectTable.LocalPlayer is { } localPlayer)
        {
            var distance = Vector3.Distance(localPlayer.Position, item.Position);
            if (distance > item.NpcSpeechTriggerDistance)
                return;
        }

        var interval = Math.Clamp(item.NpcSpeechIntervalSeconds, 0.25f, 3600.0f);
        if (Environment.TickCount64 - entry.LastSpeechMilliseconds < interval * 1000.0f)
            return;

        entry.LastSpeechMilliseconds = Environment.TickCount64;
        this.OpenBalloon(character, item.NpcSpeechText, Math.Clamp(item.NpcSpeechDurationSeconds, 0.1f, 120.0f));
    }

    private void OpenBalloon(NativeCharacter* character, string text, float duration)
    {
        var bytes = Encoding.UTF8.GetBytes(text.Trim() + '\0');
        fixed (byte* textPtr = bytes)
        {
            character->YellBalloon.OpenBalloon(new CStringPointer(textPtr), duration, false, 0.0f, false, true, true, 25);
        }
    }

    private void DestroyNpc(Guid id)
    {
        if (!this.npcs.Remove(id, out var entry))
            return;

        // release temporary appearance assignments
        this.appearanceInterop.ClearActor(entry.ObjectIndex);

        var character = this.GetCharacter(entry.ClientObjectIndex);
        if (character is not null)
        {
            character->YellBalloon.CloseBalloon();
            this.SetNpcVisible(character, entry, false);
        }

        var com = ClientObjectManager.Instance();
        if (com is not null)
            com->DeleteObjectByIndex(entry.ClientObjectIndex, 0);
    }

    private sealed class SpawnedNpc(ushort clientObjectIndex, ushort objectIndex)
    {
        public ushort ClientObjectIndex { get; } = clientObjectIndex;
        public ushort ObjectIndex { get; } = objectIndex;
        public ushort LastTimelineId { get; set; }
        public bool LastLoopSetting { get; set; }
        public long LastSpeechMilliseconds { get; set; }
        public long LastPatrolTimestamp { get; set; }
        public int PatrolTargetIndex { get; set; } = 1;
        public bool MovedThisTick { get; set; }
        public bool WasMovingLastTick { get; set; }
        public bool Visible { get; set; }
        public Vector3 LastAppliedPosition { get; set; } = new(float.NaN, float.NaN, float.NaN);
        public float LastAppliedYawRadians { get; set; } = float.NaN;
        public float LastAppliedScale { get; set; } = float.NaN;
        public bool PenumbraAttempted { get; set; }
        public bool AwaitingPenumbraRedraw { get; set; }
        public Guid LastPenumbraCollectionId { get; set; }
        public bool GlamourerDesignAttempted { get; set; }
        public Guid LastGlamourerDesignId { get; set; }
        public bool CustomizePlusAttempted { get; set; }
        public Guid LastCustomizePlusProfileId { get; set; }
        public string LastCustomizePlusProfileJson { get; set; } = string.Empty;
        public bool RecreateRequested { get; set; }
        public bool GlamourerStateAttempted { get; set; }
        public string LastGlamourerStateBase64 { get; set; } = string.Empty;
    }
}
