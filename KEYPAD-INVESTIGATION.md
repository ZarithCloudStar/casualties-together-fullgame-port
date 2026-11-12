# Drop capsule keypad investigation — 2026-11-10

## Status

Source fix implemented; ShipFullgame and PlaytestFullgame compiled. Gameplay reproduction and regression remain UNVERIFIED. Do not treat this report as a two-instance test pass.

Both existing test executables were launched with --mp-nosteam --mp-verbose and windowed 960x600. Native UI control failed twice with:

```
Computer Use native pipe is unavailable: failed to connect native pipe: 系统找不到指定的文件。 (os error 2)
```

The independent CUA inventory reported the same unavailable native pipe. Both game logs reached the main menu; no room was created and no capsule was generated. Consequently there are NO reproduction [keypad]/[mg] logs to quote. Test-copy DLLs were not replaced. Steam's installation was not modified.

## Definite source defect

* ScavPrototypeMP/Patches/MinigamePatches.cs, Server_KeypadMinigame_Keypress: successful password previously called building.SetHealth(0, false).
* ScavPrototypeMP/Compatibility/FullgameCompatibilityExtensions.cs:36-39: false writes _health but skips CheckDamage.
* F:/cu-port/decomp/fullgame/BuildingEntity.cs:54-63: the fullgame health property setter calls CheckDamage immediately.
* The same file:109-185: CheckDamage owns effects, loot and Object.Destroy; didDestroyRoutine guards repeats.
* The same file:187: Update only optimizes physics. It does NOT poll health and finish destruction as the old implementation did.
* F:/cu-port/decomp/fullgame/KeypadMinigame.cs, Update: the stock success branch uses the health PROPERTY then ends the UI. Thus an active host keypad UI can supply the missing server CheckDamage call, while a client UI supplies only local effects. This explains a host-UI dependency at source level; the exact reported sequence still needs runtime confirmation.

Missing registration is not proven. AnyObjectNetId type 3 carries the shared SyncInfo id, not a Unity instance id (Helpers/MoreStructs.cs). Existing building registration is spatial (BuildingSynchronizer.cs Start, ObjectSync.cs near stamps and ShouldKeepRegistered); snapshot restore also registers in simulation range. Existing session Init already generates missing Openable.code on the SERVER and OnPlayerJoin sends it to clients. This change retains that authority. No global registration or snapshot protocol changes were made.

## Final implementation

* Call SetHealth(0), allowing CheckDamage to run on the server.
* Suppress stock multiplayer keypad Update's local health-property write. Retain screen text and hand/button updates; await the server's destruction packet.
* Route keypresses and input echoes through ReliableOrdered. Wait for authoritative code before accepting client keypresses; avoid local optimistic input/destruction.
* Require the requested target to equal the player's keypad session object. Validate conscious player, live keypad building, UsableObject/reach, session state and digit range. Mark successful sessions completed before invoking destruction to reject queued duplicate clicks.
* Recover an existing live id via TryGetSyncInfoLive; ensure the exact validated server object is registered. Host target lookup may register its exact object. No coordinate search or alternate-target unlock exists.
* Remove the unsafe Unlock receiver and sender. Keep ___6 reserved without renumbering messages.
* Add verbose [mg] traces: keypad target, start/mapped, init, code received, keypress, unlock/checkDamage=true. Retain [keypad] rejection diagnostics. Normal release logging remains gated.

The previous three-tile fallback was inappropriate: it accepted a client's success assertion without password/session verification, could select another nearby keypad, and still used SetHealth(0, false), so registration could not cure the missing destruction call.

## Build evidence

See build/keypad-ship.log and build/keypad-playtest.log. Build output was staged INSIDE this repository with ModPluginOutputDir overridden; the test-copy plugin directories were not changed. Final shipping DLL: build/artifacts/CasualtiesTogether/bin/ShipFullgame/CasualtiesMP.dll.

Only production source files changed: ScavPrototypeMP/Patches/MinigamePatches.cs and ScavPrototypeMP/Net/MSG_ID.cs. Player.ApplyNameAndColor, Steam transport, logging framework and build definitions were not edited.

## captain acceptance steps

1. Exit both test instances. Copy the same ShipFullgame DLL to A and B BepInEx/plugins/Together/CasualtiesMP.dll. Preserve baseline logs before relaunching.
2. Launch both with --mp-nosteam --mp-verbose; A hosts, B connects to 127.0.0.1:7790.
3. On A use the console to execute Utils.Create("dropcapsule", pos, 0) at a position accessible to B (ConsoleCommands.cs:3830 demonstrates the API). A must not open its keypad UI. B opens it and enters the displayed server code.
4. Verify both sides remove the capsule and see one authoritative loot drop. On A expect [mg] keypad start ... mapped=True, keypad init, keypresses, keypad unlock ... checkDamage=true; B should show keypad code received. These are EXPECTED markers, not collected test results.
5. Repeat on a fresh capsule with A interacting first, then B completing it. Also test A opening alone, wrong code/clear/correct code, closing and reopening, rapid final clicks/two participants, and two nearby capsules (only the session target opens).
6. Move away or become unconscious while in the UI; verify requests cannot open the object. If a SyncInfo/session denial occurs, retain both complete logs and exact interaction sequence rather than enabling a position unlock.
7. Repeat the key two cases over Steam with both peers running the same DLL. Preserve A/B BepInEx/LogOutput.log, including [mg], [keypad] and synchronization messages.
