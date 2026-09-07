using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

using CPlusProfile = (System.Guid UniqueId, string Name, string VirtualPath, System.Collections.Generic.List<(string Name, ushort WorldId, byte CharacterType, ushort CharacterSubType)> Characters, int Priority, bool IsEnabled);

namespace IzzysFurniture;

internal sealed record NpcAppearancePreset(Guid Id, string Name);

internal sealed class NpcAppearanceInterop : IDisposable
{
    private readonly GetCollections getCollections = new(Service.PluginInterface);
    private readonly SetCollectionForObject setCollectionForObject = new(Service.PluginInterface);
    private readonly GetCollectionForObject getCollectionForObject = new(Service.PluginInterface);
    private readonly SetCutsceneParentIndex setCutsceneParentIndex = new(Service.PluginInterface);
    private readonly Dictionary<ushort, (Guid? Original, Guid Applied)> collectionAssignments = [];
    private readonly Penumbra.Api.Helpers.EventSubscriber<nint, int> gameObjectRedrawn;
    private readonly GetDesignList getDesignList = new(Service.PluginInterface);
    private readonly ApplyDesign applyDesign = new(Service.PluginInterface);
    private readonly RevertState revertState = new(Service.PluginInterface);
    private readonly OpenEquipmentBarIndex openEquipmentBar = new(Service.PluginInterface);
    private readonly GetStateBase64 getStateBase64 = new(Service.PluginInterface);
    private readonly ApplyState applyState = new(Service.PluginInterface);
    private readonly ICallGateSubscriber<IList<CPlusProfile>> getCPlusProfiles =
        Service.PluginInterface.GetIpcSubscriber<IList<CPlusProfile>>("CustomizePlus.Profile.GetList");
    private readonly ICallGateSubscriber<Guid, (int ErrorCode, string? Json)> getCPlusProfile =
        Service.PluginInterface.GetIpcSubscriber<Guid, (int ErrorCode, string? Json)>("CustomizePlus.Profile.GetByUniqueId");
    private readonly ICallGateSubscriber<ushort, string, (int ErrorCode, Guid? TemporaryId)> setCPlusProfile =
        Service.PluginInterface.GetIpcSubscriber<ushort, string, (int ErrorCode, Guid? TemporaryId)>("CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
    private readonly ICallGateSubscriber<ushort, int> deleteCPlusProfile =
        Service.PluginInterface.GetIpcSubscriber<ushort, int>("CustomizePlus.Profile.DeleteTemporaryProfileOnCharacter");

    public IReadOnlyList<NpcAppearancePreset> PenumbraCollections { get; private set; } = [];
    public IReadOnlyList<NpcAppearancePreset> GlamourerDesigns { get; private set; } = [];
    public IReadOnlyList<NpcAppearancePreset> CustomizePlusProfiles { get; private set; } = [];
    public bool PenumbraAvailable => Service.PluginInterface.InstalledPlugins.Any(plugin => plugin.InternalName == "Penumbra" && plugin.IsLoaded);

    public event Action<nint, int>? PenumbraObjectRedrawn;

    public NpcAppearanceInterop()
    {
        this.gameObjectRedrawn = GameObjectRedrawn.Subscriber(Service.PluginInterface, this.OnPenumbraObjectRedrawn);
        this.gameObjectRedrawn.Enable();
    }

    public void RefreshPresets()
    {
        this.PenumbraCollections = this.TryLoadPenumbraCollections();
        this.GlamourerDesigns = this.TryLoadGlamourerDesigns();
        this.CustomizePlusProfiles = this.TryLoadCustomizePlusProfiles();
    }

    public bool TryGetCustomizePlusProfile(Guid id, out string json)
    {
        json = string.Empty;
        if (id == Guid.Empty)
            return true;

        if (!TryIpc("601", () => this.getCPlusProfile.InvokeFunc(id), out var result) ||
            result.ErrorCode != 0 ||
            string.IsNullOrWhiteSpace(result.Json))
            return false;

        json = result.Json;
        return true;
    }

    public bool SetPenumbraCollection(ushort objectIndex, Guid collectionId)
    {
        if (!this.DetachActor(objectIndex))
            return false;

        // guid.empty is penumbra's empty collection; null is only used during cleanup
        if (!TryIpc("602", () => this.setCollectionForObject.Invoke(objectIndex, collectionId, true, true), out var result))
            return false;

        if (result.Item1 is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged)
        {
            var original = this.collectionAssignments.TryGetValue(objectIndex, out var assignment)
                ? assignment.Original
                : result.Item2?.Id;
            this.collectionAssignments[objectIndex] = (original, collectionId);
            return true;
        }

        Service.Log.Debug("603");
        return false;
    }

    public bool ApplyGlamourerDesign(ushort objectIndex, Guid designId)
    {
        if (!this.DetachActor(objectIndex))
            return false;

        GlamourerApiEc result;
        if (designId == Guid.Empty)
        {
            if (!TryIpc("604", () => this.revertState.Invoke(objectIndex, 0, ApplyFlagEx.RevertDefault), out result))
                return false;

            return result is GlamourerApiEc.Success or GlamourerApiEc.NothingDone;
        }

        if (!TryIpc("605", () => this.applyDesign.Invoke(designId, objectIndex, 0, ApplyFlagEx.DesignDefault), out result))
            return false;

        if (result == GlamourerApiEc.Success)
            return true;

        Service.Log.Debug("606");
        return false;
    }

    public bool OpenGlamourerEditor(ushort objectIndex) =>
        this.DetachActor(objectIndex) && TryIpc("608", () =>
        {
            this.openEquipmentBar.Invoke(true, objectIndex);
        });

    public bool TryGetGlamourerState(ushort objectIndex, out string stateBase64)
    {
        stateBase64 = string.Empty;
        if (!this.DetachActor(objectIndex) ||
            !TryIpc("609", () => this.getStateBase64.Invoke(objectIndex, 0), out var result) ||
            result.Item1 != GlamourerApiEc.Success ||
            string.IsNullOrWhiteSpace(result.Item2))
            return false;

        stateBase64 = result.Item2;
        return true;
    }

    public bool ApplyGlamourerState(ushort objectIndex, string stateBase64)
    {
        if (string.IsNullOrWhiteSpace(stateBase64) || !this.DetachActor(objectIndex))
            return false;

        if (!TryIpc("610", () => this.applyState.Invoke(stateBase64, objectIndex, 0, ApplyFlagEx.StateDefault), out var result))
            return false;

        return result is GlamourerApiEc.Success or GlamourerApiEc.NothingDone;
    }

    public bool ApplyCustomizePlusProfile(ushort objectIndex, string profileJson)
    {
        if (!this.DetachActor(objectIndex))
            return false;

        if (!TryIpc("611", () =>
        {
            this.deleteCPlusProfile.InvokeFunc(objectIndex);
            if (string.IsNullOrWhiteSpace(profileJson))
                return true;

            var result = this.setCPlusProfile.InvokeFunc(objectIndex, profileJson);
            return result.ErrorCode == 0;
        }, out var applied))
            return false;

        return applied;
    }

    public void ClearActor(ushort objectIndex)
    {
        if (!this.DetachActor(objectIndex))
        {
            this.ForgetActor(objectIndex);
            return;
        }

        TryIpc("604", () =>
        {
            this.revertState.Invoke(objectIndex, 0, ApplyFlagEx.RevertDefault);
        });

        TryIpc("612", () =>
        {
            this.deleteCPlusProfile.InvokeFunc(objectIndex);
        });

        if (this.collectionAssignments.Remove(objectIndex, out var assignment))
        {
            TryIpc("613", () =>
            {
                var current = this.getCollectionForObject.Invoke(objectIndex);
                if (current.Item1 && current.Item2 && current.Item3.Id == assignment.Applied)
                    this.setCollectionForObject.Invoke(objectIndex, assignment.Original, true, true);
            });
        }
    }

    public void ForgetActor(ushort objectIndex)
        => this.collectionAssignments.Remove(objectIndex);

    private bool DetachActor(ushort objectIndex)
        => objectIndex != 0 && (!this.PenumbraAvailable ||
           (TryIpc("617", () => this.setCutsceneParentIndex.Invoke(objectIndex, -1), out var result) &&
            result == PenumbraApiEc.Success));

    public void Dispose()
    {
        this.gameObjectRedrawn.Disable();
        this.gameObjectRedrawn.Dispose();
    }

    private void OnPenumbraObjectRedrawn(nint address, int objectIndex)
        => this.PenumbraObjectRedrawn?.Invoke(address, objectIndex);

    private IReadOnlyList<NpcAppearancePreset> TryLoadPenumbraCollections()
    {
        if (!TryIpc("614", this.getCollections.Invoke, out var collections))
            return [];

        return collections
            .Select(pair => new NpcAppearancePreset(pair.Key, pair.Value))
            .OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<NpcAppearancePreset> TryLoadGlamourerDesigns()
    {
        if (!TryIpc("615", this.getDesignList.Invoke, out var designs))
            return [];

        return designs
            .Select(pair => new NpcAppearancePreset(pair.Key, pair.Value))
            .OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<NpcAppearancePreset> TryLoadCustomizePlusProfiles()
    {
        if (!TryIpc("616", this.getCPlusProfiles.InvokeFunc, out var profiles))
            return [];

        return profiles
            .Select(profile => new NpcAppearancePreset(profile.UniqueId, profile.Name))
            .OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryIpc(string message, Action action)
    {
        try
        {
            action();
            return true;
        }
        // ipcerror means the target plugin is absent or rejected the call
        catch (IpcError)
        {
            Service.Log.Debug(message);
            return false;
        }
    }

    private static bool TryIpc<T>(string message, Func<T> call, out T result)
    {
        try
        {
            result = call();
            return true;
        }
        catch (IpcError)
        {
            Service.Log.Debug(message);
            result = default!;
            return false;
        }
    }
}
