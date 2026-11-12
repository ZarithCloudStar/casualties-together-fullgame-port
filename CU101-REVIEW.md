# Casualties: Unknown 1.0.1 compatibility (5.5.0-cu-port)

## Environment

- `F:\cu-port\decomp\fullgame` was renamed to `fullgame_100`; ILSpy decompiled the updated Steam assembly into a new `fullgame`.
- Updated A/B `CasualtiesUnknown_Data`, `MonoBleedingEdge`, game executable, UnityPlayer and crash handler from the Steam installation. Steam was read only. BepInEx and Doorstop were excluded.
- All 38 existing Together/config files retained their hashes. `build/cu101-preserved.json` records them. Builds and test loaders write into this repository rather than installed Together folders.
- Source and A/B Assembly-CSharp SHA256: `D6CA378A16A08A47C7F74CDAD0535D98F426B654FCEC1F88255B0ECDD636825C`.
- Complete managed-code comparison: `build/cu101-all.diff`. The old assembly is also retained as `build/Assembly-CSharp-100.dll`.

## Relevant changes and adaptation

| Area | Official 1.0.1 change / audit | Mod behavior |
| --- | --- | --- |
| Surgery | CargoMinigame and EndLabScript managed code are identical to 1.0.0. Original scene reports 60.75 seconds. GuardSpawner now preserves its configured shot delay (`noShootRandomize`) and propagates `noMelee`. Resource changes are included by the game-data update. | Use scene `timeLeft`, retaining it across the entrance/save state. Remove the additional 2-second completion gate. Keep the 0.25-second network reporting interval and the seven monotonic milestones. Native clamp/cargo/hose coordinates are unchanged. Timeout still releases guards and never causes an extra failure. |
| Guards / pacifist | `Die` sets `anyGuardKilled` only when `hurtByPlayer`; native player melee/gun hits set that flag. | Add attribution to server multiplayer replacement damage paths for host/remote players. Environmental/guard shots retain native attribution. Persist the native death verdict before settlement; preserve hurt/AI fields in guard saves. Optional reflection keeps 1.0 loadable. Results still uses the server verdict, without counting all dead guards. |
| Crystal | CrystalTeleport clamps candidate X to `[-halfWidth+10, halfWidth-10]`. CrystalBehaviour methods are unchanged. | Existing server effect invocation executes the new native clamp, then broadcasts the actual body position. No competing coordinate clamp was added. |
| Toxic rocks | WorldGeneration excludes toxic blocks within three block columns of the center (`Math.Abs(num16-halfWidth)>3`). Body.PlaceBody is unchanged. | Native terrain generation retains this condition. Existing layer10 bottom scan and layer11 ENDLABSPAWN paths remain. |
| Belt tools | Tweezers gains `belttool`; lockpickingkit corrects `belttol` to `belttool`. | Native item definitions remain authoritative. |
| Craft liquid | RecipeResult unloads a filled item when its containing inventory or per-item weight limit is exceeded. | Apply the same check in the multiplayer craft replacement after AddLiquid. Existing volume-space check remains. |
| Trader | Leaning additionally requires `startedConvo`. | Native Update still executes; stock/save synchronization patches do not replace this condition. |
| Blue heart / scanner | BlueHeart adds a one-shot missed-cargo warning; NanoTechScanner inserts depth into its initial line. | No method removals; existing synchronization hooks retained. These additions need visual/manual confirmation. |
| Escape crates | No managed-code crate diff found. | Updated scene/resource files are used; no global loot suppression added, preserving existing drop behavior. Manual confirmation remains. |

Full game version validation now accepts exactly `1.0` and `1.0.1`; demo validation remains `7.0.1`.

## Modified source files

- `ScavPrototypeMP/Plugin.cs`
- `ScavPrototypeMP/Compatibility/EndingFlow.Escape.cs`
- `ScavPrototypeMP/Compatibility/EndingFlow.Surgery.cs`
- `ScavPrototypeMP/Compatibility/EndingGuardSync.cs`
- `ScavPrototypeMP/SyncSystems/CombatStuff.cs`
- `ScavPrototypeMP/Patches/CraftingPatches.cs`
- `ScavPrototypeMP/Compatibility/Release14EndingTests.cs` (scene-derived timer assertion)
- This review document.

## Validation

| Configuration | Errors | Warnings | Log | Output DLL |
| --- | --- | --- | --- | --- |
| ShipFullgame | 0 | 103 | `build/cu101-ship.log` | `build/cu101-ship/CasualtiesMP.dll` |
| PlaytestFullgame | 0 | 100 | `build/cu101-playtest.log` | `build/ending-playtest/CasualtiesMP.dll` |

Built against the copied 1.0.1 assembly with the existing local NuGet cache (`--no-restore`). No game API rename/compile fix was required. Warnings remain; this is not a warning-free build.

Only two distinct scenarios were run. Initial attempts stopped on the old `<=60` timer assertion; that assertion was corrected to compare against the original scene value captured before escape. The following are the final reruns using the same Playtest DLL:

| Scenario | Full scenario result | Surgery evidence / failure |
| --- | --- | --- |
| `r14-escape` | **FAIL** | Scene-derived timer, seven milestones, authoritative cargo removal, client replication, door unlock, teammate exit and narrative entry passed. Then `timeout all-player narrative and settlement`. This legacy wrapper supplies `--mp-auto-narrative`, but the current input driver only runs for `r19-*`; the note remained waiting for input. Logs: `build/ending-tests/cu101-final-r14-r14-escape/`. |
| `r19-noncore-client` | **FAIL** | Seven shared milestones reached mask=127 with host/client operators; cargo removed; correct teammate exit and narrative entry passed. Then `escapenote1 callback/direct Skip/Submit/delayed pointer-up without edge all rejected` failed on both sides. Logs show no physical input edge at that check. Logs: `build/ending-tests/cu101-final-r19-r19-noncore-client/`. |

The R19 failure is **unresolved**, not a full pass or a proven pre-existing bug. The 1.0/1.0.1 ScrollableText managed code is identical and this patch does not change narrative/input production code. No baseline comparison or further scenarios were run to conserve quota. It needs investigation before claiming complete ending regression coverage.

Machine-readable results: `build/cu101-final-r14-results.json`, `build/cu101-final-r19-results.json`. Installed Together/config hashes were rechecked after testing and remain unchanged. Full regression testing was intentionally not run.

- Ship DLL SHA256: `7D11122BC2A3DA1D94803A6A6C28BFA9001CCC2BC2D566CAD3F2B355E4D049B3`
- Playtest DLL SHA256: `0B6C8AD795FAF0158B7E4F192BC9DCC31C3B3737C43FC6D79AE273A1B7965782`

## Remaining manual checks

- Resolve the R19 no-input rejection assertion and confirm both local note input and complete party settlement before calling this a fully validated release.
- Slow real-player surgery with two operators, reopening/rejoining, and a save/load during escape; confirm the revised native guard shooting/melee behavior under the deadline.
- Pacifist: guard friendly-fire-only death preserves eligibility; host/client melee and bullets revoke it; save/load retains that distinction. Existing 1.0.0 saves cannot reconstruct missing historical attribution.
- Teleport crystals near both map edges, including client-triggered teleport and late join.
- Layer3 toxic terrain and layer10/11 placements across several seeds.
- Belt placement, liquid filling across container weight limits, trader sleeping pose, escape-crate contents, scanner depth and missed-cargo line.
- Existing hover drops, blue-heart aura, ending 1/3, sandbox, MP3 and hint deduplication were not exhaustively rerun in this deliberately limited validation.
