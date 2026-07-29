# ROGHack

MelonLoader cheat mod for Ragnarok Origin Global (Unity IL2CPP game, client `rooc.exe`). Assembly name `ROCSpeedHack`, namespace `ROCSpeedHack`. Distributed on unknowncheats.me.

## Layout
- `MainMod.cs` — `MelonMod` entry point. Draggable `GUILayout.Window` overlay (toggle with Delete key) holding all toggles/sliders, ESP drawing, auto-buy UI, and config save/load (`ROGHack_config.txt`, plain `key=value` lines).
- `Patches.cs` — Harmony postfix patches on `Il2CppMoonClient` types (run speed, health bar visibility, skill cooldown, camera distance).
- `Resources/*.lua` — embedded via `Properties/Resources.resx` + `Resources.Designer.cs` + `<None Include=...>` in the csproj. `hooks.lua` auto-runs on the "GameEntry" scene; `inject.lua` and `autobuy.lua` run on demand from GUI buttons via `runLuaFile()`.
- `dump.cs` (if present on disk, not in repo) is an IL2CppDumper-style signature dump of the whole game — useful for grepping class/method signatures, but has no method bodies, so behavior must be inferred from names/types. It won't reflect Lua-only APIs (`ShopMgr`, `MgrMgr`, `TableUtil`, etc.) since those are pure-Lua modules with no C#/IL2CPP presence at all.

## Local build environment (this machine)

MelonLoader is installed at `D:\JoyMaker\JoyMakerGame\games\roocalive\exe\MelonLoader`. The `.csproj` `HintPath`s point there. `Il2CppAssemblies\` under that folder only exists after `rooc.exe` has been launched at least once through MelonLoader (first launch does IL2CPP dumping, can take a few minutes).

Getting a working build on this machine required stacking several fixes — **don't re-derive these from scratch**, the reasoning is in the conversation history if needed:

1. **No Visual Studio / modern MSBuild.** Only the ancient `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe` is present, whose bundled `csc.exe` doesn't support C# 6+ (string interpolation, etc.) used throughout this codebase.
2. **No .NET Framework 4.8 targeting pack**, so implicit mscorlib resolution falls back to the wrong (v4.0) mscorlib.
3. **MelonLoader's `net6\` support DLLs** (`MelonLoader.dll`, `Il2CppInterop.*.dll`) are .NET 6 assemblies, referenced from a .NET Framework 4.8 class library. This needs a `netstandard`/`System.Runtime` **compat facade** (not the raw .NET 6 ref-assembly, and not a naive `NETStandard.Library` netstandard.dll — both cause `CS0433` ambiguity against mscorlib). The one that actually works cleanly is `build/netstandard2.0/ref/*.dll` from the NuGet package `NETStandard.Library` 2.0.3 (these are true forwarders to mscorlib, not independent type declarations).
4. MSBuild's own `ResolveAssemblyReferences` silently drops most third-party `<Reference>` HintPaths once `FrameworkPathOverride` is set (root cause not fully diagnosed — some interaction with the ancient ToolsVersion-4.0 targets). Workaround: **compile directly with `csc.exe` via a response file**, bypassing MSBuild reference resolution entirely, rather than trying to get MSBuild's RAR to behave.
5. `GUILayout.Window(...)`'s third parameter (`UnityEngine.GUI.WindowFunction`) is an IL2Cpp `MulticastDelegate`, not a plain .NET delegate — method groups don't implicitly convert. Cast explicitly: `(System.Action<int>)MethodName`. General pattern for other Unity/IL2Cpp delegate params if this recurs: reflect the type (`ReflectionOnlyLoadFrom` + resolve deps from the same MelonLoader folders) to find its `op_Implicit` conversion operator — most IL2Cpp delegates expose one from a plain `System.Action`/`System.Func`.

### Reproducing the build

All the fetched tooling (Roslyn `csc.exe`, `.NETFramework.ReferenceAssemblies.net48`, `NETStandard.Library`) was downloaded via NuGet into `.build-tools/` (gitignored, machine-local, not checked in). If that folder is missing, re-fetch:
- `Microsoft.Net.Compilers.Toolset` (any recent 4.x) → `tasks/net472/csc.exe`
- `Microsoft.NETFramework.ReferenceAssemblies.net48` → `build/.NETFramework/v4.8/` (mscorlib, System*, Facades\netstandard.dll)
- `NETStandard.Library` 2.0.3 → `build/netstandard2.0/ref/*.dll` (the facade set, including `System.Runtime.dll`)

Then build with a `csc.exe @response-file.rsp` invocation referencing: the three sets above, `net6\MelonLoader.dll`, `net6\0Harmony.dll`, `net6\Il2CppInterop.{Common,HarmonySupport,Runtime}.dll`, `Dependencies\SupportModules\Il2Cpp.dll`, and every `*.dll` under `Il2CppAssemblies\`. Compile the `.resx` to `.resources` first (MSBuild's `GenerateResource` target can still be used standalone: `msbuild ROCSpeedHack.csproj /t:PrepareResources`, or just resgen.exe).

Output DLL identity to sanity-check after building (`ReflectionOnlyLoadFrom` + `GetReferencedAssemblies()`): should reference `MelonLoader`, `0Harmony`, `Il2CppMoonClient`, `UnityEngine.CoreModule`/`IMGUIModule`/`InputLegacyModule` — matches a known-good release build fetched from unknowncheats.me for comparison.

Concretely, from the repo root:
```
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" ROCSpeedHack.csproj /t:PrepareResources /p:Configuration=Release
& ".\.build-tools\compiler\tasks\net472\csc.exe" "@.build-tools\build.rsp"
```
This produces `bin\Release\ROCSpeedHack.dll`.

### Deploying a build

MelonLoader loads mods from `D:\JoyMaker\JoyMakerGame\games\roocalive\exe\Mods`. After a successful build, copy the DLL there to test it in-game:
```
cp "bin\Release\ROCSpeedHack.dll" "D:\JoyMaker\JoyMakerGame\games\roocalive\exe\Mods\ROCSpeedHack.dll"
```

## Lua gotcha: bare global assignment is silently dropped

The game's Lua sandbox (via `MLuaClientHelper.DoLuaString` / `LuaInterface.LuaState.DoString`)
intercepts plain global writes — `Foo = value` at the top level — and the write never becomes
visible to a later read of `Foo`, **even within the same chunk/call**. Reads of *existing*
game globals (`TableUtil`, `MgrMgr`, etc.) work fine; it's specifically new bare-name writes
that vanish. Confirmed via a throwaway in-game debug button that tested several approaches;
`local X = value` and `rawset(_G, "Foo", value)` / `rawget(_G, "Foo")` both work and persist
across separate `DoLuaString` calls. Existing-table field assignment (`SomeExistingGlobal.newField = value`)
also works and persists, since it's not a name write through `_ENV`.

**Rule of thumb for any new Lua script that needs to define something reusable across calls**:
never do `Name = ...` — use `rawset(_G, "Name", ...)` to define, `rawget(_G, "Name")` to read/call.
`autobuy.lua` follows this pattern (`rawset(_G, "AutoBuyItem", ...)`); `MainMod.StartAutoBuy` reads
it back via `rawget(_G, 'AutoBuyItem')`. Plain `local` variables are unaffected and always safe
within a single `DoLuaString` call, they just don't survive to the next call.

## Feature/behavior notes
- Damage/HP/god-mode-style cheats are **not feasible client-side** — combat is server-authoritative; client patches only affect the local display, not actual server state.
- Same applies to `ShopMgr.RequestBuyShopItem` (used by `autobuy.lua`): the server validates proximity to
  the shop NPC and rejects the purchase if the character is too far away, even though the client-side
  request itself succeeds with no error. Confirmed via the "found commodity ... shop ..." debug log firing
  successfully while the item still didn't appear in the bag. `AutoBuyItem` only works while physically
  standing at the shop — it automates the "buy when low" click, it does not bypass the location check.
- `ShopMgr`, `MgrMgr`, `TableUtil`, `Network.Handler` etc. are Lua-only APIs with no C# equivalent — can't be discovered via `dump.cs`, only via extracting/decompiling the game's Lua assets or live inspection (see UnityExplorer note in `README.md`).
- Any new Lua script that talks to the server (buys/sells/network RPC hijacking) should be gated behind an explicit GUI button, never auto-run on scene load — established preference after the Cat Caravan bot discussion.
