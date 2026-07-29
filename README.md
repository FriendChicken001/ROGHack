# ROGHack
A MelonLoader mod for Ragnarok Origin Global, allows for some cheats
## Installation
From releases page or from the [Forum post](https://www.unknowncheats.me/forum/other-mmorpg-and-strategy/624052-ragnarok-origin-global-pc-cheat.html), download the DLL, install MelonLoader **0.6.2** to ROO_PC (if you cant find ro.exe, you'll need to enable hidden files in file explorer), put the DLL in the Mods folder and run the game.
## Compile
In case you want to compile it yourself. You will need to 
* install MelonLoader on ro.exe from ROO_PC
* launch the game with melonloader at least once and wait for it to generate the decompiled DLLs
* download the source
* open the .sln with visual studio
* add all DLLs from ROO_PC/MelonLoader/Il2CppAssemblies/ as references to the project
* build it with visual studio
## Other stuff
There's also the dumpfile in the releases and forum post. Feel free to use how'd you like

## Debugging / finding new hooks
The `dump.cs` file only has method/class signatures (no logic), so figuring out what a getter/setter actually does or confirming a singleton's real accessor name means guessing from names. For that, install [UnityExplorer](https://github.com/yukieiji/UnityExplorer) (IL2CPP build) as another MelonLoader mod:
* download the IL2CPP release DLL matching your MelonLoader version
* drop it in the same `Mods` folder as this mod's DLL
* launch the game — UnityExplorer adds an in-game UI with a Scene Explorer, an Object Inspector, and a C# console you can run snippets in live

This lets you confirm things at runtime (e.g. `MEntityMgr.singleton`, live field values, calling a method to see what it actually does) instead of guessing from `dump.cs` alone.
