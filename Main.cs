using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Alta.Crafting;
using Alta.Networking.Scripts.Player;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(BluesInGameStringTool.Main), "Blue's In-Game String Tool", "0.1.2", "Blue")]
[assembly: MelonGame("Alta", "A Township Tale")]

// Lets players hand-build what normally needs a spawn string, by changing the answers to the game's own checks:
// "does this piece fit this slot" (JointSlot.Supports) and "is this slot free" (JointPoint.IsAvailable).
// The server checks every attach itself, so the mode a game hears only decides which slots it lights up. See DESIGN.md.
namespace BluesInGameStringTool {
    public enum Mode { Off, OpenSlots, Free }

    public class Main : MelonMod {
        internal static Mode Mode;          // the server's setting; in a game, what the server said (Off until it says)
        internal const float Spot = 0.1f;   // ponytail: slots closer than this (metres) are one spot. Game data: same spot up to 0.08, other end from 0.155. A setting if a modded handle needs it.
        internal static MelonLogger.Instance Log = null!;
        internal static bool Server => ApplicationManager.IsHeadless;   // not NetworkSceneManager.IsServer: a game's main menu sets that too
        static readonly HashSet<uint> told = new HashSet<uint>();
        static float nextTell;
        static IPlayer? lastPlayer;

        // The server tells a game its mode in a blank player message, so a game without the mod shows nothing: 10 + mode spaces.
        internal static bool IsSignal(string? text) => text != null && text.Length > 10 && text.Length <= 12 && text.Trim().Length == 0;

        public override void OnInitializeMelon() {
            Log = LoggerInstance;
            if (Server) {
                var file = MelonPreferences.CreateCategory("BluesInGameStringTool");
                file.SetFilePath(System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "BluesInGameStringTool.cfg"), true, false);
                string text = file.CreateEntry("Mode", "Free", description: "Free, OpenSlots or Off. Restart the server to change it.").Value;
                file.SaveToFile(false);
                if (!Enum.TryParse(text.Replace(" ", ""), true, out Mode) || !Enum.IsDefined(typeof(Mode), Mode)) {
                    Mode = Mode.Off;
                    Log.Warning($"Mode '{text}' is not Free, OpenSlots or Off: running as Off.");
                }
            }
            Log.Msg($"{(Server ? $"server, mode {Mode}" : "game, the server sets the mode")}; {HarmonyInstance.GetPatchedMethods().Count()} game methods patched");
        }

        public override void OnUpdate() {
            if (Server) {
                if (Mode == Mode.Off || Time.unscaledTime < nextTell || PlayerCommunicationManager.Instance == null) return;
                nextTell = Time.unscaledTime + 1f;
                var online = Player.AllPlayers.OfType<Player>().Where(p => p != null).ToList();
                told.IntersectWith(online.Select(p => p.Identifier));   // a player who rejoins is a new one to tell
                foreach (Player p in online)
                    if (p.PlayerController != null && told.Add(p.Identifier)) {   // the game drops a message to a player with no body yet
                        PlayerCommunicationManager.Instance.SendMessageToPlayer(p, new string(' ', 10 + (int)Mode), 0f);
                        Log.Msg($"told {p.UserInfo?.Username}'s game: {Mode}");
                    }
                return;
            }
            IPlayer? now = Player.Current;
            if (now is UnityEngine.Object o && o == null) now = null;                        // a destroyed player is no player
            if (lastPlayer != null && !ReferenceEquals(now, lastPlayer)) Mode = Mode.Off;    // left that server
            lastPlayer = now;
        }
    }

    // ---- the two modes ----

    [HarmonyPatch(typeof(JointSlot), nameof(JointSlot.Supports))]
    static class AnyPieceFits {
        static void Postfix(ref bool __result) => __result |= Main.Mode == Mode.Free;
    }

    // The game says no for a free slot only through its slot groups ("one of these"). Free: every free slot is open.
    // OpenSlots: open unless a used slot of the same piece shares its spot, which keeps one piece per spot and drops "only one end".
    [HarmonyPatch(typeof(JointPoint), nameof(JointPoint.IsAvailable))]
    static class EverySlotOpen {
        static void Postfix(JointPoint __instance, bool isIgnoringPreConfirmation, ref bool __result) {
            if (__instance is JointInsert) {   // a base's plug (it sits on a slot's object): only a Free server calls it available
                if (__result && __instance.GetComponent<JointSlot>() != null) __result = Main.Server && Main.Mode == Mode.Free;
                return;
            }
            if (__result || Main.Mode == Mode.Off || !(__instance is JointSlot slot) || slot.IsUsed || slot.IsConnected
                || (slot.IsPreConfirmationConnected && !isIgnoringPreConfirmation) || !slot.gameObject.activeInHierarchy) return;
            __result = Main.Mode == Mode.Free || slot.CraftingPart?.Slots.Any(other => other.IsUsed
                && (other.transform.position - slot.transform.position).sqrMagnitude < Main.Spot * Main.Spot) == false;
        }
    }

    // The two short handles switch their other end's slots off when one end is used. Not while a mode is on, or a piece put
    // there would be hidden and would not re-attach when the world loads.
    // ponytail: skips everything a slot switches; in the game's own data that is only those slots.
    [HarmonyPatch(typeof(JointPoint), "SetObjectsActive")]
    static class SlotsStayOn {
        static bool Prefix(JointPoint __instance) => !(__instance is JointSlot) || Main.Mode == Mode.Off;
    }

    // Server: one line per attach request, so "it would not attach" can be read from the log. And no piece ever plugs into
    // its own build: with every slot open the game would loop for ever (a tampered game could ask for it).
    [HarmonyPatch(typeof(CraftingPart), "CraftWithSlot")]
    static class SayRequest {
        static bool Prefix(CraftingPart __instance, JointSlot slot, bool isIgnoringPreConfirmation) {
            if (slot == null) return true;
            bool own = slot.CraftingPart?.GetTopLevel() == __instance;
            if (Main.Server) Main.Log.Msg($"{__instance.name} -> {slot.name} on {slot.CraftingPart?.name}: fits {slot.Supports(__instance.Insert?.Type)}, free {slot.IsAvailable(isIgnoringPreConfirmation)}{(own ? ", refused: its own build" : "")}");
            return !own;
        }
    }

    // ---- a game hears the server's mode ----

    [HarmonyPatch(typeof(PlayerTextMessage), nameof(PlayerTextMessage.Serialize))]
    static class HearMode {
        static void Postfix(ref PlayerTextMessage __instance, Alta.Serialization.Stream stream) {
            if (!stream.IsReading || Main.Server || !Main.IsSignal(__instance.Message)) return;
            Main.Mode = (Mode)(__instance.Message.Length - 10);
            foreach (JointSlot slot in Resources.FindObjectsOfTypeAll<JointSlot>())   // slots switched off before this game heard (the two short handles)
                if (slot.gameObject.scene.IsValid()) slot.gameObject.SetActive(true);
            Main.Log.Msg($"server mode: {Main.Mode}");
        }
    }

    [HarmonyPatch(typeof(PlayerMessageDisplay), nameof(PlayerMessageDisplay.Display), typeof(string), typeof(float), typeof(DisplayMessageType))]
    static class HideSignal {
        static bool Prefix(string message) => !Main.IsSignal(message);
    }

    // ---- Free: anything that has slots plugs into anything ----
    // A base (handle, backpack, bow, arrow shaft, shield handle) only has slots. Each gets a plug (a JointInsert) on its first
    // slot's object, which already has the NetworkEntity a JointInsert needs. A game never calls the plug available
    // (EverySlotOpen), so the game's own choice of which held piece is the plug is unchanged; only a Free server does, when
    // the request arrives.

    [HarmonyPatch(typeof(CraftingPart), "Awake")]
    static class BasesGetAPlug {
        static void Postfix(CraftingPart __instance, ref JointInsert ___insert, bool ___isBasedOnInteraction) {
            if (___insert == null && __instance.Slots != null && __instance.Slots.Length > 0 && __instance.Slots[0] != null)
                ___insert = __instance.Slots[0].gameObject.AddComponent<JointInsert>();
            // A backpack and its pouches never look for a slot when grabbed: the game builds them only at the bag machine.
            // In Free they look like every other piece does (CraftingPart.CheckPossibleConnections).
            if (!___isBasedOnInteraction)
                __instance.Pickup.InteractionStarted += (_, hand) => {
                    if (Main.Mode == Mode.Free && hand.Controller.Hand.OtherHand.Interactor.InteractingWith is Pickup { CraftingPart: CraftingPart other } && other != null)
                        CraftingOptions.Create(hand.Controller.PlayerController, __instance, other);
                };
        }
    }

    // A JointInsert added while the game runs has none of the parts the game's Awake and events expect.
    [HarmonyPatch(typeof(JointPoint), "Awake")]
    static class PlugIsWhole {
        static readonly FieldInfo Glow = AccessTools.Field(typeof(JointPoint), "glow");
        static void Prefix(JointPoint __instance, ref Renderer ___glow, ref JointPointEvent ___connected, ref JointPointEvent ___disconnected,
                           ref List<GameObject> ___connectedObjects, ref List<GameObject> ___disconnectedObjects) {
            if (___glow == null && __instance is JointInsert && __instance.GetComponent<JointSlot>() is JointSlot slot) ___glow = (Renderer)Glow.GetValue(slot);
            ___connected ??= new JointPointEvent();
            ___disconnected ??= new JointPointEvent();
            ___connectedObjects ??= new List<GameObject>();
            ___disconnectedObjects ??= new List<GameObject>();
        }
    }

    // A slot points into its piece and a plug must point out of it, so a base's plug is turned half round.
    [HarmonyPatch(typeof(JointSlot), nameof(JointSlot.CalculateConnectedRotation))]
    static class PlugFacesOut {
        static void Postfix(JointSlot __instance, CraftingPart insertPart, ref Quaternion __result) {
            Transform plug = insertPart.Insert.transform;
            if (plug.GetComponent<JointSlot>() != null)
                __result = __instance.transform.rotation * Quaternion.Euler(180f, 0f, 0f) * Quaternion.Inverse(plug.rotation) * insertPart.transform.rotation;
        }
    }

    // Two loose bases: the plug is the bare one that is in a hand (bare, so two built weapons held together never fuse); when
    // both are, the one grabbed last. At the bag machine the docked bag comes first and is in no hand, so the held one is the
    // plug. Never a base with itself: two hands on one item also arrive here as two parts.
    [HarmonyPatch(typeof(CraftingOptions), nameof(CraftingOptions.Create))]
    static class BaseOnBase {
        static readonly ConstructorInfo Options = AccessTools.Constructor(typeof(CraftingOptions), new[] { typeof(PlayerController), typeof(CraftingPart), typeof(CraftingPart) });
        static bool IsLooseBase(CraftingPart part) => part.Insert != null && part.Insert.ConnectedSlot == null && part.Insert.GetComponent<JointSlot>() != null;

        static bool CanPlug(CraftingPart part) => part.Pickup.IsInteractedWith && part.PartCount == 1 && IsLooseBase(part);

        static bool Prefix(PlayerController player, CraftingPart primaryCraftingPart, CraftingPart secondaryCraftingPart, ref bool __result) {
            CraftingPart plug = CanPlug(primaryCraftingPart) ? primaryCraftingPart : secondaryCraftingPart;
            CraftingPart host = plug == primaryCraftingPart ? secondaryCraftingPart : primaryCraftingPart;
            if (Main.Mode != Mode.Free || plug == host || !CanPlug(plug) || !IsLooseBase(host)
                || plug.PotentialConnection != null || host.PotentialConnection != null) return true;
            Options.Invoke(new object[] { player, plug, host });
            __result = true;
            return false;
        }
    }

    // The game attaches only when the plug is let go; letting go of the other item cancels. With two bases nobody can tell
    // which one is "the plug", so letting go of either attaches.
    [HarmonyPatch(typeof(CraftingOptions), "Cancel")]
    static class EitherHandAttaches {
        static readonly MethodInfo Attempt = AccessTools.Method(typeof(CraftingOptions), "AttemptCraft");
        static bool Prefix(CraftingOptions __instance, Interactable dropped, Interactor interactor, JointInsert ___insert) {
            if (Main.Mode != Mode.Free || ___insert == null || ___insert.GetComponent<JointSlot>() == null) return true;
            Attempt.Invoke(__instance, new object[] { dropped, interactor });
            return false;
        }
    }
}
