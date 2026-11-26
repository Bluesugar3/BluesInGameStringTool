using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Alta.Crafting;
using Alta.Networking.Scripts.Player;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(BluesUnrestrictedCrafting.Main), "Blue's Unrestricted Crafting", "1.0.0", "Blue")]
[assembly: MelonGame("Alta", "A Township Tale")]

// Lets players hand-build what normally needs a spawn string, by changing the answers to the game's own checks:
// "does this piece fit this slot" (JointSlot.Supports) and "is this slot free" (JointPoint.IsAvailable).
// Only the players on the server's admin list get it (Free); everyone else plays the normal game (Off). The server checks
// every attach itself, so the mode a game hears only decides which slots it lights up.
namespace BluesUnrestrictedCrafting {
    public enum Mode { Off = 0, Free = 2 }   // 2: the message length Free has had since 0.2.1 (1 was the removed OpenSlots)

    public class Main : MelonMod {
        // In a game: what the server said (Off until it says). On the server: the mode of the player whose attach is being
        // decided (Rules.During), else Free, which only matters while saved builds load (no checks then).
        internal static Mode Mode;
        internal static MelonLogger.Instance Log = null!;
        internal static bool Server => ApplicationManager.IsHeadless;   // not NetworkSceneManager.IsServer: a game's main menu sets that too
        static readonly Dictionary<uint, Mode> told = new Dictionary<uint, Mode>();
        static float nextTell;
        static IPlayer? lastPlayer;

        // The server tells a game its mode in a blank player message, so a game without the mod shows nothing: 14 + mode spaces (0.1.x read 11-12 spaces, so they never mistake it).
        internal static bool IsSignal(string? text) => text != null && text.Length >= 14 && text.Length <= 16 && text.Trim().Length == 0;

        public override void OnInitializeMelon() {
            Log = LoggerInstance;
            if (Server) {
                Rules.Load();
                try { Alta.Console.CommandService.CommandCollection.ProcessModule(typeof(Commands), typeof(Commands).Assembly); }
                catch (Exception e) { Log.Warning($"the unrestricted console commands could not be added: {e.Message}"); }
            }
            Log.Msg($"{(Server ? "server, Free for the admin list, the normal game for everyone else" : "game, the server sets the mode")}; {HarmonyInstance.GetPatchedMethods().Count()} game methods patched");
        }

        public override void OnUpdate() {
            if (Server) {
                if (Time.unscaledTime < nextTell || PlayerCommunicationManager.Instance == null) return;
                nextTell = Time.unscaledTime + 1f;
                Rules.ReloadAdmins();
                var online = Player.AllPlayers.OfType<Player>().Where(p => p != null).ToList();
                foreach (uint gone in told.Keys.Except(online.Select(p => p.Identifier)).ToList()) told.Remove(gone);   // a rejoin is told again
                foreach (Player p in online) {
                    Mode mode = Rules.For(p);
                    // the game drops a message to a player with no body yet; a mode is told again only when it changes
                    if (p.PlayerController == null || (told.TryGetValue(p.Identifier, out Mode was) && was == mode)) continue;
                    told[p.Identifier] = mode;
                    PlayerCommunicationManager.Instance.SendMessageToPlayer(p, new string(' ', 14 + (int)mode), 0f);
                    Log.Msg($"told {p.UserInfo?.Username}'s game: {mode}");
                }
                return;
            }
            IPlayer? now = Player.Current;
            if (now is UnityEngine.Object o && o == null) now = null;                        // a destroyed player is no player
            if (lastPlayer != null && !ReferenceEquals(now, lastPlayer)) Mode = Mode.Off;    // left that server
            lastPlayer = now;
        }
    }

    // ---- Free ----

    [HarmonyPatch(typeof(JointSlot), nameof(JointSlot.Supports))]
    static class AnyPieceFits {
        static void Postfix(ref bool __result) => __result |= Main.Mode == Mode.Free;
    }

    // The game says no for a free slot only through its slot groups ("one of these"). Free: every free slot is open.
    [HarmonyPatch(typeof(JointPoint), nameof(JointPoint.IsAvailable))]
    static class EverySlotOpen {
        static void Postfix(JointPoint __instance, bool isIgnoringPreConfirmation, ref bool __result) {
            if (__instance is JointInsert) {   // a base's plug (it sits on a slot's object): only a Free server calls it available
                if (__result && __instance.GetComponent<JointSlot>() != null) __result = Main.Server && Main.Mode == Mode.Free;
                return;
            }
            if (__result || Main.Mode == Mode.Off || !(__instance is JointSlot slot) || slot.IsUsed || slot.IsConnected
                || (slot.IsPreConfirmationConnected && !isIgnoringPreConfirmation) || !slot.gameObject.activeInHierarchy) return;
            __result = Main.Mode == Mode.Free;
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
            if (Main.Server) {
                Main.Mode = Rules.During(__instance, out string who);
                Main.Log.Msg($"{who} ({Main.Mode}): {__instance.name} -> {slot.name} on {slot.CraftingPart?.name}: fits {slot.Supports(__instance.Insert?.Type)}, free {slot.IsAvailable(isIgnoringPreConfirmation)}{(own ? ", refused: its own build" : "")}");
            }
            return !own;
        }
        static void Finalizer() { if (Main.Server) Main.Mode = Rules.Idle; }
    }

    // ---- a game hears the server's mode ----

    [HarmonyPatch(typeof(PlayerTextMessage), nameof(PlayerTextMessage.Serialize))]
    static class HearMode {
        static void Postfix(ref PlayerTextMessage __instance, Alta.Serialization.Stream stream) {
            if (!stream.IsReading || Main.Server || !Main.IsSignal(__instance.Message)) return;
            Main.Mode = __instance.Message.Length == 16 ? Mode.Free : Mode.Off;
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

    // ---- Server: the players on the admin list get Free, everyone else the normal game ----
    // The game's own admin rights live in Alta's web API, which these servers no longer reach, so admins are a list of names.
    static class Rules {
        internal const Mode Idle = Mode.Free;
        static MelonPreferences_Category file = null!;
        static MelonPreferences_Entry<string> adminList = null!;
        static HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string? lastList;
        static float nextRead;
        static readonly ConditionalWeakTable<CraftingPart, IPlayer> asker = new ConditionalWeakTable<CraftingPart, IPlayer>();

        internal static void Load() {
            // Renamed from Blue's In-Game String Tool (0.3.0): its settings, admin list included, are copied once, never moved.
            string dir = MelonEnvironment.UserDataDirectory, cfg = System.IO.Path.Combine(dir, "BluesUnrestrictedCrafting.cfg"),
                oldCfg = System.IO.Path.Combine(dir, "BluesInGameStringTool.cfg");
            if (!System.IO.File.Exists(cfg) && System.IO.File.Exists(oldCfg)) {
                System.IO.File.WriteAllText(cfg, System.IO.File.ReadAllText(oldCfg).Replace("[BluesInGameStringTool]", "[BluesUnrestrictedCrafting]"));
                Main.Log.Msg("settings copied from BluesInGameStringTool.cfg");
            }
            file = MelonPreferences.CreateCategory("BluesUnrestrictedCrafting");
            file.SetFilePath(cfg, true, false);
            adminList = file.CreateEntry("Admins", "", description: "The players who may craft without limits: names, separated by commas (any capitals). Read again within a few seconds of saving, or use the console: unrestricted give <name>.");
            file.SaveToFile(false);
            Main.Mode = Idle;
            Names();
        }

        // ponytail: re-reads the whole small file every 5 s; a file watcher if that ever shows in a profile.
        internal static void ReloadAdmins() {
            if (Time.unscaledTime < nextRead) return;
            nextRead = Time.unscaledTime + 5f;
            try { file.LoadFromFile(false); } catch (Exception e) { Main.Log.Warning($"could not read the settings file: {e.Message}"); }
            Names();
        }

        static void Names() {
            if (adminList.Value == lastList) return;
            lastList = adminList.Value;
            names = new HashSet<string>(lastList.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
            Main.Log.Msg($"admins: {(names.Count == 0 ? "nobody" : string.Join(", ", names))}");
        }

        internal static Mode For(Player p) => names.Contains(p.UserInfo?.Username ?? "") ? Mode.Free : Mode.Off;

        internal static IEnumerable<string> Listed => names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        // The console's give and take: the same list, written back to the file (players are told within a second).
        internal static string Set(string name, bool admin) {
            name = name.Trim().Trim('"');
            if (name.Length == 0 || name.Contains(',')) return "Give a player name: unrestricted give <name> / unrestricted take <name>";
            if (admin == names.Contains(name)) return $"{name} {(admin ? "already has" : "does not have")} unrestricted crafting.";
            var next = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            if (admin) next.Add(name); else next.Remove(name);
            adminList.Value = string.Join(", ", next.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            file.SaveToFile(false);
            Names();
            return $"{name} {(admin ? "now has" : "no longer has")} unrestricted crafting.";
        }

        internal static void Asked(CraftingPart part, IPlayer player) {
            if (player == null) return;
            asker.Remove(part);
            asker.Add(part, player);
        }

        // The mode of the player who asked for this piece's attach. Unknown (a world loading): Idle.
        internal static Mode During(CraftingPart part, out string who) {
            if (asker.TryGetValue(part, out IPlayer p) && p is Player player && player != null) {
                who = player.UserInfo?.Username ?? "?";
                return For(player);
            }
            who = "nobody";
            return Idle;
        }
    }

    // Who asked: the plain attach request, and the bag machine's nailed attach (it completes later, when the nails are in).
    [HarmonyPatch(typeof(CraftingPart), "AttemptCraft", typeof(IPlayer), typeof(Alta.Serialization.Stream), typeof(uint))]
    static class AskedToAttach {
        static void Prefix(CraftingPart __instance, IPlayer player) {   // the plug's own check comes first, so the whole request runs in their mode
            Rules.Asked(__instance, player);
            if (Main.Server) Main.Mode = Rules.During(__instance, out _);
        }
        static void Finalizer() { if (Main.Server) Main.Mode = Rules.Idle; }
    }

    [HarmonyPatch(typeof(ConfirmationCraftingPart), "SyncAttemptCraft")]
    static class AskedToNail { static void Prefix(CraftingPart __instance, IPlayer player) => Rules.Asked(__instance, player); }

    // Server console (the TavernLauncher Server window's Console, or the remote console with the owner's token): TavernLib switches the
    // game's in-game console off and has no permission list, so this is where admins are given.
    [Alta.Console.PlayOnly, Alta.Console.ServerOnly, Alta.Console.Module("unrestricted", "Blue's Unrestricted Crafting: who may craft without limits, give, take")]
    static class Commands {
        [Alta.Console.Command(null, "Who may craft without limits")]
        static string Status() => $"Unrestricted crafting: {(Rules.Listed.Any() ? string.Join(", ", Rules.Listed) : "nobody")}. Everyone else: the normal game.";

        [Alta.Console.Command("give", "Lets a player craft without limits: unrestricted give <name>")]
        static string Give(string name = "") => Rules.Set(name, true);

        [Alta.Console.Command("take", "Puts a player back on the normal game: unrestricted take <name>")]
        static string Take(string name = "") => Rules.Set(name, false);
    }
}
