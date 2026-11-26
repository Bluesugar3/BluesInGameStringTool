# Blue's Unrestricted Crafting

A MelonLoader mod for A Township Tale, for PC and Quest. Players the server owner allows can build "string" items by hand:
any piece in any slot, handles on handles, guards on guards, backpacks on weapons. No outside string tool, no spawn command.
Everyone else plays the normal game.

![Handles joined into a frame, with guards and pommels in every slot](screenshot.webp)

## What allowed players can do

| | |
|---|---|
| Any piece, any slot | Blades, guards, pommels and side pieces go in any slot, and every slot can be used, even several at one spot. |
| Slotted things plug in | Anything with slots (handles, sticks, backpacks, pouches, bows, arrow shafts, shield handles) plugs into anything else. |
| Either hand | With two things in your hands, the bare one (nothing on it yet) plugs in, and letting go of either hand attaches it. |
| Bags from the hand | Backpacks and pouches attach from the hand, not only on the bag machine. |

Two finished builds held together never fuse. There is no way to take things apart again.

## Install

| | File | Where |
|---|---|---|
| Server | `BluesUnrestrictedCrafting.dll` | the server's `Mods` folder |
| PC players | `BluesUnrestrictedCrafting.dll` | the game's `Mods` folder |
| Quest players | `BluesUnrestrictedCrafting.Quest.dll` | the headset's `Mods` folder (Modding Tavern's patched game) |

Both files are on the [Releases](../../releases) page. A player without the mod can still join and plays the normal game.

## Who may build without limits

On the server's console (TavernLauncher's Server window, or a remote console with the server's console token):

| Command | Does |
|---|---|
| `unrestricted` | Lists who may. |
| `unrestricted give <name>` | Lets a player build without limits. Their game is told within a second. |
| `unrestricted take <name>` | Puts them back on the normal game. |

The list is the `Admins` line of the server's `UserData\BluesUnrestrictedCrafting.cfg` (names, comma separated, any capitals).
Editing it by hand works too, and takes effect within a few seconds.

## Good to know

| | |
|---|---|
| Who decides | The server checks every attach against the player who asked. A tampered game gains nothing. |
| Players without the mod | See most builds; one slotted thing plugged into another, and a second big guard on a short handle, look loose to them. |
| Saving | Built items save like any crafted item. With the mod removed from the server, slotted things plugged into each other come apart on load. |
| Looks | A piece in a slot not made for it sits however that slot points, and pieces can overlap. |
| Server log | One line per attach request: who, which piece, which slot. |
| Renamed | This was Blue's In-Game String Tool. Remove `BluesInGameStringTool.dll` when you install this; the server copies its settings over once. |
