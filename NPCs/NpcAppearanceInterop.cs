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
    private readonly RedrawObject redrawObject = new(Service.PluginInterface);
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

    public event Action<int>? PenumbraObjectRedrawn;

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

        if (!TryIpc("customize+ profile lookup failed", () => this.getCPlusProfile.InvokeFunc(id), out var result) ||
            result.ErrorCode != 0 ||
            string.IsNullOrWhiteSpace(result.Json))
            return false;

        json = result.Json;
        return true;
    }

    public bool SetPenumbraCollection(ushort objectIndex, Guid? collectionId)
    {
        if (!TryIpc("penumbra collection assignment failed", () => this.setCollectionForObject.Invoke(objectIndex, collectionId, true, true), out var result))
            return false;

        if (result.Item1 is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged)
            return true;

        Service.Log.Debug("penumbra rejected the collection assignment");
        return false;
    }

    public bool ApplyGlamourerDesign(ushort objectIndex, Guid designId)
    {
        GlamourerApiEc result;
        // in case of disaster
        if (designId == Guid.Empty)
        {
            if (!TryIpc("glamourer revert failed", () => this.revertState.Invoke(objectIndex, 0, ApplyFlagEx.RevertDefault), out result))
                return false;

            return result is GlamourerApiEc.Success or GlamourerApiEc.NothingDone;
        }

        if (!TryIpc("glamourer design apply failed", () => this.applyDesign.Invoke(designId, objectIndex, 0, ApplyFlagEx.DesignDefault), out result))
            return false;

        if (result == GlamourerApiEc.Success)
            return true;

        Service.Log.Debug("glamourer rejected the design");
        return false;
    }

    public bool RedrawPenumbraObject(ushort objectIndex) =>
        TryIpc("penumbra redraw failed", () =>
        {
            this.redrawObject.Invoke(objectIndex, RedrawType.Redraw);
        });

    public bool OpenGlamourerEditor(ushort objectIndex) =>
        TryIpc("glamourer editor did not open", () =>
        {
            this.openEquipmentBar.Invoke(true, objectIndex);
        });

    public bool TryGetGlamourerState(ushort objectIndex, out string stateBase64)
    {
        stateBase64 = string.Empty;
        if (!TryIpc("glamourer state read failed", () => this.getStateBase64.Invoke(objectIndex, 0), out var result) ||
            result.Item1 != GlamourerApiEc.Success ||
            string.IsNullOrWhiteSpace(result.Item2))
            return false;

        stateBase64 = result.Item2;
        return true;
    }

    public bool ApplyGlamourerState(ushort objectIndex, string stateBase64)
    {
        if (string.IsNullOrWhiteSpace(stateBase64))
            return false;

        if (!TryIpc("glamourer state apply failed", () => this.applyState.Invoke(stateBase64, objectIndex, 0, ApplyFlagEx.StateDefault), out var result))
            return false;

        return result is GlamourerApiEc.Success or GlamourerApiEc.NothingDone;
    }

    public bool ApplyCustomizePlusProfile(ushort objectIndex, string profileJson)
    {
        if (!TryIpc("customize+ profile apply failed", () =>
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
        // cleanups stay independent through this
        TryIpc("glamourer revert failed", () =>
        {
            this.revertState.Invoke(objectIndex, 0, ApplyFlagEx.RevertDefault);
        });

        TryIpc("customize+ profile cleanup failed", () =>
        {
            this.deleteCPlusProfile.InvokeFunc(objectIndex);
        });

        TryIpc("penumbra collection cleanup failed", () =>
        {
            this.setCollectionForObject.Invoke(objectIndex, null, false, true);
        });
    }

    public void Dispose()
    {
        this.gameObjectRedrawn.Disable();
        this.gameObjectRedrawn.Dispose();
    }

    private void OnPenumbraObjectRedrawn(nint _, int objectIndex)
        => this.PenumbraObjectRedrawn?.Invoke(objectIndex);

    private IReadOnlyList<NpcAppearancePreset> TryLoadPenumbraCollections()
    {
        if (!TryIpc("penumbra collections unavailable", this.getCollections.Invoke, out var collections))
            return [];

        return collections
            .Select(pair => new NpcAppearancePreset(pair.Key, pair.Value))
            .OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<NpcAppearancePreset> TryLoadGlamourerDesigns()
    {
        if (!TryIpc("glamourer designs unavailable", this.getDesignList.Invoke, out var designs))
            return [];

        return designs
            .Select(pair => new NpcAppearancePreset(pair.Key, pair.Value))
            .OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<NpcAppearancePreset> TryLoadCustomizePlusProfiles()
    {
        if (!TryIpc("customize+ profiles unavailable", this.getCPlusProfiles.InvokeFunc, out var profiles))
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
