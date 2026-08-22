using Brio.Capabilities.Actor;
using Brio.Config;
using Brio.Entities;
using Brio.Entities.Actor;
using Brio.Game.Actor.Appearance;
using Brio.Resources;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin;
using Glamourer.Api.Enums;
using Lumina.Excel.Sheets;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using System;

namespace Brio.IPC;

public sealed class PenumbraChangedItemEquipService : IDisposable
{
    private const ulong GlamourerCustomItemFlag = 1UL << 48;

    private enum PenumbraArmorType : byte
    {
        Head = 1,
        Body = 2,
        Hands = 3,
        Legs = 4,
        Feet = 5,
        Ears = 6,
        Neck = 7,
        Wrists = 8,
        Finger = 9,
    }

    private readonly ConfigurationService _configurationService;
    private readonly EntityManager _entityManager;
    private readonly GameDataProvider _gameDataProvider;
    private readonly PenumbraService _penumbraService;
    private readonly GlamourerService _glamourerService;

    private readonly EventSubscriber<ChangedItemType, uint> _tooltipSubscriber;
    private readonly EventSubscriber<MouseButton, ChangedItemType, uint> _clickSubscriber;

    private bool IsEnabled
        => _configurationService.Configuration.IPC.EnablePenumbraChangedItemQuickEquip
            && _configurationService.Configuration.IPC.AllowPenumbraIntegration
            && _configurationService.Configuration.IPC.AllowGlamourerIntegration;

    public PenumbraChangedItemEquipService(
        IDalamudPluginInterface pluginInterface,
        ConfigurationService configurationService,
        EntityManager entityManager,
        GameDataProvider gameDataProvider,
        PenumbraService penumbraService,
        GlamourerService glamourerService)
    {
        _configurationService = configurationService;
        _entityManager = entityManager;
        _gameDataProvider = gameDataProvider;
        _penumbraService = penumbraService;
        _glamourerService = glamourerService;

        _tooltipSubscriber = ChangedItemTooltip.Subscriber(pluginInterface, OnChangedItemTooltip);
        _clickSubscriber = ChangedItemClicked.Subscriber(pluginInterface, OnChangedItemClicked);
    }

    private void OnChangedItemTooltip(ChangedItemType type, uint id)
    {
        try
        {
            DrawChangedItemTooltip(type, id);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, "Failed to draw the Brio Penumbra changed-item tooltip");
        }
    }

    private void DrawChangedItemTooltip(ChangedItemType type, uint id)
    {
        if(!IsEnabled || !TryResolveItem(type, id, useLeftRing: false, out var slot, out _))
            return;

        if(!TryGetSelectedActor(out var actor))
        {
            ImGui.TextUnformatted(Localize.Get(
                "ui.penumbraQuickEquip.selectActorTooltip",
                "[Brio] Select an actor in Brio to use middle-click equip."));
            return;
        }

        if(slot is ApiEquipSlot.RFinger)
        {
            ImGui.TextUnformatted(Format(
                "ui.penumbraQuickEquip.rightRingTooltip",
                "[Brio] Middle-click: equip on {0} (right finger).",
                actor.FriendlyName));
            ImGui.TextUnformatted(Format(
                "ui.penumbraQuickEquip.leftRingTooltip",
                "[Brio] Shift + middle-click: equip on {0} (left finger).",
                actor.FriendlyName));
            return;
        }

        ImGui.TextUnformatted(Format(
            "ui.penumbraQuickEquip.equipTooltip",
            "[Brio] Middle-click: equip on {0} through Glamourer.",
            actor.FriendlyName));
    }

    private void OnChangedItemClicked(MouseButton button, ChangedItemType type, uint id)
    {
        try
        {
            HandleChangedItemClicked(button, type, id);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, "Failed to handle a Penumbra changed-item click");
            ShowWarning(Localize.Get(
                "ui.penumbraQuickEquip.failedUnexpectedly",
                "Penumbra quick equip failed unexpectedly."));
        }
    }

    private void HandleChangedItemClicked(MouseButton button, ChangedItemType type, uint id)
    {
        if(button is not MouseButton.Middle || !IsEnabled)
            return;

        var useLeftRing = ImGui.GetIO().KeyShift;
        if(!TryResolveItem(type, id, useLeftRing, out var slot, out var itemId))
            return;

        if(!TryGetSelectedActor(out var actor))
        {
            ShowWarning(Localize.Get(
                "ui.penumbraQuickEquip.selectActor",
                "Select an actor in Brio before using Penumbra quick equip."));
            return;
        }

        if(!_penumbraService.IsAvailable || !_glamourerService.IsAvailable)
        {
            ShowWarning(Localize.Get(
                "ui.penumbraQuickEquip.integrationUnavailable",
                "Penumbra or Glamourer integration is unavailable."));
            return;
        }

        var result = _glamourerService.ApplyItem(actor.GameObject, slot, itemId);
        if(result is not GlamourerApiEc.Success)
            ShowWarning(GetFailureMessage(result, actor.FriendlyName));
    }

    private bool TryGetSelectedActor(out ActorEntity actor)
    {
        if(_entityManager.TryGetCapabilityFromSelectedEntity<ActorAppearanceCapability>(
            out var capability,
            considerParents: true)
            && capability.Actor.GameObject.Address != nint.Zero)
        {
            actor = capability.Actor;
            return true;
        }

        actor = null!;
        return false;
    }

    private bool TryResolveItem(
        ChangedItemType type,
        uint id,
        bool useLeftRing,
        out ApiEquipSlot slot,
        out ulong itemId)
    {
        slot = ApiEquipSlot.Unknown;
        itemId = 0;

        switch(type)
        {
            case ChangedItemType.Item:
            case ChangedItemType.ItemOffhand:
            {
                if(!_gameDataProvider.GetExcelSheet<Item>().TryGetRow(id, out var item))
                    return false;

                if(type is ChangedItemType.ItemOffhand)
                {
                    slot = ApiEquipSlot.OffHand;
                }
                else
                {
                    var slots = item.EquipSlotCategory.ValueNullable?.GetEquipSlots() ?? ActorEquipSlot.None;
                    if(!TryGetApiSlot(slots, useLeftRing, out slot))
                        return false;
                }

                itemId = id;
                return true;
            }
            case ChangedItemType.CustomArmor:
            {
                var model = (ushort)(id & 0xFFFF);
                var variant = (byte)((id >> 16) & 0xFF);
                var armorType = (PenumbraArmorType)(byte)(id >> 24);
                if(!TryGetCustomArmorSlot(armorType, useLeftRing, out slot))
                    return false;

                // Penumbra supplies model, variant and equipment type in a compact
                // uint. Glamourer's custom-item ID uses the same values in wider fields.
                itemId = model
                    | ((ulong)variant << 32)
                    | ((ulong)armorType << 40)
                    | GlamourerCustomItemFlag;
                return true;
            }
            default:
                return false;
        }
    }

    private static bool TryGetApiSlot(ActorEquipSlot slots, bool useLeftRing, out ApiEquipSlot slot)
    {
        slot = ApiEquipSlot.Unknown;

        if(slots.HasFlag(ActorEquipSlot.RightRing) || slots.HasFlag(ActorEquipSlot.LeftRing))
        {
            slot = useLeftRing && slots.HasFlag(ActorEquipSlot.LeftRing)
                ? ApiEquipSlot.LFinger
                : slots.HasFlag(ActorEquipSlot.RightRing)
                    ? ApiEquipSlot.RFinger
                    : ApiEquipSlot.LFinger;
            return true;
        }

        slot = slots switch
        {
            var value when value.HasFlag(ActorEquipSlot.MainHand) => ApiEquipSlot.MainHand,
            var value when value.HasFlag(ActorEquipSlot.OffHand) => ApiEquipSlot.OffHand,
            var value when value.HasFlag(ActorEquipSlot.Head) => ApiEquipSlot.Head,
            var value when value.HasFlag(ActorEquipSlot.Body) => ApiEquipSlot.Body,
            var value when value.HasFlag(ActorEquipSlot.Hands) => ApiEquipSlot.Hands,
            var value when value.HasFlag(ActorEquipSlot.Legs) => ApiEquipSlot.Legs,
            var value when value.HasFlag(ActorEquipSlot.Feet) => ApiEquipSlot.Feet,
            var value when value.HasFlag(ActorEquipSlot.Ears) => ApiEquipSlot.Ears,
            var value when value.HasFlag(ActorEquipSlot.Neck) => ApiEquipSlot.Neck,
            var value when value.HasFlag(ActorEquipSlot.Wrists) => ApiEquipSlot.Wrists,
            _ => ApiEquipSlot.Unknown,
        };

        return slot is not ApiEquipSlot.Unknown;
    }

    private static bool TryGetCustomArmorSlot(PenumbraArmorType armorType, bool useLeftRing, out ApiEquipSlot slot)
    {
        slot = armorType switch
        {
            PenumbraArmorType.Head => ApiEquipSlot.Head,
            PenumbraArmorType.Body => ApiEquipSlot.Body,
            PenumbraArmorType.Hands => ApiEquipSlot.Hands,
            PenumbraArmorType.Legs => ApiEquipSlot.Legs,
            PenumbraArmorType.Feet => ApiEquipSlot.Feet,
            PenumbraArmorType.Ears => ApiEquipSlot.Ears,
            PenumbraArmorType.Neck => ApiEquipSlot.Neck,
            PenumbraArmorType.Wrists => ApiEquipSlot.Wrists,
            PenumbraArmorType.Finger when useLeftRing => ApiEquipSlot.LFinger,
            PenumbraArmorType.Finger => ApiEquipSlot.RFinger,
            _ => ApiEquipSlot.Unknown,
        };

        return slot is not ApiEquipSlot.Unknown;
    }

    private static string GetFailureMessage(GlamourerApiEc result, string actorName)
    {
        return result switch
        {
            GlamourerApiEc.InvalidKey => Format(
                "ui.penumbraQuickEquip.locked",
                "Glamourer's state for {0} is locked by another plugin.",
                actorName),
            GlamourerApiEc.ActorNotHuman => Format(
                "ui.penumbraQuickEquip.notHuman",
                "{0} is not a human actor that can equip this item.",
                actorName),
            GlamourerApiEc.ItemInvalid => Localize.Get(
                "ui.penumbraQuickEquip.invalidItem",
                "Glamourer could not identify this item."),
            GlamourerApiEc.ActorNotFound => Format(
                "ui.penumbraQuickEquip.actorNotFound",
                "Glamourer could not find {0}.",
                actorName),
            _ => Format(
                "ui.penumbraQuickEquip.failed",
                "Could not equip the item on {0} through Glamourer.",
                actorName),
        };
    }

    private static string Format(string key, string fallback, params object?[] arguments)
        => string.Format(Localize.Get(key, fallback), arguments);

    private static void ShowWarning(string message)
        => Brio.PopToast(
            message,
            Localize.Get("ui.penumbraQuickEquip.title", "Penumbra Quick Equip"),
            NotificationType.Warning);

    public void Dispose()
    {
        _tooltipSubscriber.Dispose();
        _clickSubscriber.Dispose();
        GC.SuppressFinalize(this);
    }
}
