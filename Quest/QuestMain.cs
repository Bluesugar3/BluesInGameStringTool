using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppAlta.Crafting;
using Il2CppAlta.Networking.Scripts.Player;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(BluesUnrestrictedCrafting.Main), "Blue's Unrestricted Crafting", "0.4.1", "Blue")]
[assembly: MelonGame("Alta", "Att_Quest")]
[assembly: HarmonyDontPatchAll]

// Quest (IL2CPP): the game's half of the PC mod (..\Main.cs), which the headset needs. A Quest game only joins a PC server, and the
// server decides every attach, so nothing of the server's half is here. Same rules, IL2CPP spelling: private game fields are public
// members, `is` on a game object is TryCast, a delegate handed to the game is converted and kept, the struct method
// PlayerTextMessage.Serialize cannot be patched (the mode is read where the game shows the message instead).
namespace BluesUnrestrictedCrafting {
    public enum Mode { Off, OpenSlots, Free }

    public class Main : MelonMod {
        internal static Mode Mode;   // what the server said (Off until it says)
        internal const float Spot = 0.1f;
        static IntPtr lastPlayer;

        // The server's blank message: 14 + mode spaces (see ..\Main.cs).
        internal static bool IsSignal(string? text) => text != null && text.Length >= 14 && text.Length <= 16 && text.Trim().Length == 0;

        public override void OnInitializeMelon() {
            int applied = 0, total = 0;
            foreach (Type t in typeof(Main).Assembly.GetTypes()) {   // one at a time: a patch the Quest build cannot take must not stop the rest
                if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                total++;
                try { HarmonyInstance.CreateClassProcessor(t).Patch(); applied++; }
                catch (Exception e) { LoggerInstance.Error($"patch NOT applied: {t.Name}: {(e.InnerException ?? e).Message}"); }
            }
            LoggerInstance.Msg($"Quest game, the server sets the mode; {applied} of {total} patches applied");
        }

        public override void OnUpdate() {
            IPlayer now = Player.Current;
            IntPtr id = now == null || now.TryCast<UnityEngine.Object>() is UnityEngine.Object o && o == null ? IntPtr.Zero : now.Pointer;
            if (lastPlayer != IntPtr.Zero && id != lastPlayer) Mode = Mode.Off;   // left that server
            lastPlayer = id;
        }
    }

    [HarmonyPatch(typeof(JointSlot), nameof(JointSlot.Supports))]
    static class AnyPieceFits {
        static void Postfix(ref bool __result) => __result |= Main.Mode == Mode.Free;
    }

    [HarmonyPatch(typeof(JointPoint), nameof(JointPoint.IsAvailable))]
    static class EverySlotOpen {
        static void Postfix(JointPoint __instance, bool isIgnoringPreConfirmation, ref bool __result) {
            if (__instance.TryCast<JointInsert>() != null) {   // a base's plug: a game never calls it available (the server decides)
                if (__result && __instance.GetComponent<JointSlot>() != null) __result = false;
                return;
            }
            JointSlot slot = __instance.TryCast<JointSlot>();
            if (__result || Main.Mode == Mode.Off || slot == null || slot.IsUsed || slot.IsConnected
                || (slot.IsPreConfirmationConnected && !isIgnoringPreConfirmation) || !slot.gameObject.activeInHierarchy) return;
            if (Main.Mode == Mode.Free) { __result = true; return; }
            CraftingPart part = slot.CraftingPart;
            if (part == null) return;
            foreach (JointSlot other in part.Slots)
                if (other != null && other.IsUsed && (other.transform.position - slot.transform.position).sqrMagnitude < Main.Spot * Main.Spot) return;
            __result = true;
        }
    }

    [HarmonyPatch(typeof(JointPoint), nameof(JointPoint.SetObjectsActive))]
    static class SlotsStayOn {
        static bool Prefix(JointPoint __instance) => __instance.TryCast<JointSlot>() == null || Main.Mode == Mode.Off;
    }

    // The 3-argument overload the game's message handler calls: read the mode here and never show the message.
    [HarmonyPatch(typeof(PlayerMessageDisplay), nameof(PlayerMessageDisplay.Display), new[] { typeof(string), typeof(float), typeof(DisplayMessageType) })]
    static class HearMode {
        static bool Prefix(string message) {
            if (!Main.IsSignal(message)) return true;
            Mode heard = (Mode)(message.Length - 14);
            if (heard != Main.Mode) {
                Main.Mode = heard;
                foreach (JointSlot slot in Resources.FindObjectsOfTypeAll<JointSlot>())   // slots switched off before this game heard
                    if (slot.gameObject.scene.IsValid()) slot.gameObject.SetActive(true);
                MelonLogger.Msg($"server mode: {heard}");
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(CraftingPart), nameof(CraftingPart.Awake))]
    static class BasesGetAPlug {
        // One handler for every backpack and pouch, kept for the whole run (the game holds only a pointer to it).
        static readonly Il2CppSystem.Action<Interactable, Interactor> Grabbed =
            DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Interactable, Interactor>>(new Action<Interactable, Interactor>(OnGrab));

        static void Postfix(CraftingPart __instance) {
            if (__instance.insert == null && __instance.Slots != null && __instance.Slots.Length > 0 && __instance.Slots[0] != null)
                __instance.insert = __instance.Slots[0].gameObject.AddComponent<JointInsert>();
            if (!__instance.isBasedOnInteraction && __instance.Pickup != null) __instance.Pickup.add_InteractionStarted(Grabbed);
        }

        static void OnGrab(Interactable grabbed, Interactor hand) {
            try {
                if (Main.Mode != Mode.Free) return;
                CraftingPart part = grabbed.GetComponent<CraftingPart>();
                Pickup other = hand.Controller.Hand.OtherHand.Interactor.InteractingWith?.TryCast<Pickup>();
                CraftingPart otherPart = other?.GetComponent<CraftingPart>();
                if (part != null && otherPart != null) CraftingOptions.Create(hand.Controller.PlayerController, part, otherPart);
            } catch (Exception e) { MelonLogger.Warning($"grab check failed: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(JointPoint), nameof(JointPoint.Awake))]
    static class PlugIsWhole {
        static void Prefix(JointPoint __instance) {
            if (__instance.glow == null && __instance.TryCast<JointInsert>() != null && __instance.GetComponent<JointSlot>() is JointSlot slot)
                __instance.glow = slot.glow;
            __instance.connected ??= new JointPointEvent();
            __instance.disconnected ??= new JointPointEvent();
            __instance.connectedObjects ??= new Il2CppSystem.Collections.Generic.List<GameObject>();
            __instance.disconnectedObjects ??= new Il2CppSystem.Collections.Generic.List<GameObject>();
        }
    }

    [HarmonyPatch(typeof(JointSlot), nameof(JointSlot.CalculateConnectedRotation))]
    static class PlugFacesOut {
        static void Postfix(JointSlot __instance, CraftingPart insertPart, ref Quaternion __result) {
            Trace.Step($"attaching {insertPart.name} to {__instance.name}");
            Transform plug = insertPart.Insert.transform;
            if (plug.GetComponent<JointSlot>() != null)
                __result = __instance.transform.rotation * Quaternion.Euler(180f, 0f, 0f) * Quaternion.Inverse(plug.rotation) * insertPart.transform.rotation;
        }
    }

    [HarmonyPatch(typeof(CraftingOptions), nameof(CraftingOptions.Create))]
    static class BaseOnBase {
        static bool IsLooseBase(CraftingPart part) => part.Insert != null && part.Insert.ConnectedSlot == null && part.Insert.GetComponent<JointSlot>() != null;
        static bool CanPlug(CraftingPart part) => part.Pickup.IsInteractedWith && part.PartCount == 1 && IsLooseBase(part);

        static bool Prefix(PlayerController player, CraftingPart primaryCraftingPart, CraftingPart secondaryCraftingPart, ref bool __result) {
            CraftingPart plug = CanPlug(primaryCraftingPart) ? primaryCraftingPart : secondaryCraftingPart;
            CraftingPart host = plug.Pointer == primaryCraftingPart.Pointer ? secondaryCraftingPart : primaryCraftingPart;
            if (Main.Mode != Mode.Free || plug.Pointer == host.Pointer || !CanPlug(plug) || !IsLooseBase(host)
                || plug.PotentialConnection != null || host.PotentialConnection != null) return true;
            Trace.Step($"plugging {plug.name} into {host.name}");
            new CraftingOptions(player, plug, host);
            __result = true;
            return false;
        }
    }

    // Letting go of either hand attaches. On PC this is a prefix on CraftingOptions.Cancel, but Cancel is one call long: on Quest a
    // patch's jump is bigger than its native code, overwrites the code beside it and the game dies when you let go (the archery port's
    // "never patch an empty game method"). So it hangs on Destroy, which Cancel calls: when the slot's side was let go while the plug
    // is still held, it attaches (RequestCraft) instead of cancelling.
    [HarmonyPatch(typeof(CraftingOptions), nameof(CraftingOptions.Destroy))]
    static class EitherHandAttaches {
        static bool busy;   // RequestCraft calls Destroy itself
        static bool Prefix(CraftingOptions __instance) {
            if (busy || Main.Mode != Mode.Free || __instance.insert == null || __instance.insert.GetComponent<JointSlot>() == null
                || __instance.insertPart?.Pickup == null || __instance.slotPart?.Pickup == null
                || !__instance.insertPart.Pickup.IsInteractedWith || __instance.slotPart.Pickup.IsInteractedWith) return true;
            Trace.Step("let go of the slot side: attaching");
            busy = true;
            try { __instance.RequestCraft(null, null); }
            catch (Exception e) { MelonLogger.Warning($"attach on letting go failed: {e.Message}"); }
            finally { busy = false; }
            return false;
        }
    }

    // A native crash leaves no stack trace: the last of these lines in the log says where it was.
    static class Trace {
        internal static void Step(string what) => MelonLogger.Msg($"step: {what}");
    }
}
