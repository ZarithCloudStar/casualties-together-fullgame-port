# Release16 ending corrections — 2026-11-11

Status: source changes and both builds complete; real A/B runtime regression NOT executed.

## Changes and causes

1. Anesthesia: `DurationOf` reports remaining duration. Removed the elapsed-50-second gate. Harmony prefixes now capture the actual `CalculateDrain(amount)` localanesthetic ml before vanilla drains the stack. Only the cargo carrier torso, without a prosthesis, reports an eligible dose (`6 * ml > 50`). The server commits readiness immediately; authenticated client reports include dose in thousandths of ml, with phase/alive/conscious/range checks. Carrier CoUtils remaining-duration check remains a fallback. Readiness remains latched for this surgery as before.
2. Cooperative surgery: added serialized `surgeryOperators`, retaining `surgeon` only as a legacy presentation/save marker. Device use admits any eligible nearby player. Progress requests require membership and existing range/stage/coordinate checks. Removed exclusive-owner rejection and cross-player .15s milestone throttle. The server ORs seven monotonic bits, removes cargo at bit4, and commits completion at mask127. Each panel merges shared completed objects and stage without erasing pending local steps; a shared completed object releases local grasp. Dead/disconnected/out-of-range participants are removed without resetting progress or closing others. Session UI participation clears on restore. No production busy hint path remains.
3. Countdown: restored `timerTransparency.Evaluate(remaining) * Random.Range(.3f,.5f)` alpha.
4. Escape: restored vanilla's separate exit conditions: x>353 walk; x>366 trapdoor/ragdoll/scream; y>270 AND x<370 begins the existing party fade/narrative. `doorJoint` is destroyed only at the trapdoor stage, rather than immediately after surgery. `exitFalling` is synchronized; ragdoll presentation runs once on the walker's owning client. Server logs `[escape] exit walker=... pos=... falling=...` once per second and at the trapdoor transition.
5. Chipped carrier: preserved the existing server rule (`ChipState.IsUnchipped(KeyPlayer)`), not the shooter's or host's chip. A chipped carrier's scientist shot is denied with the existing hint and leaves Laboratory/normal Biopod exit available. No runtime reproduction has established an additional defect in this guard.

## Verification

- ShipFullgame: 0 errors, 104 existing warnings; `build/release16-ship.log`.
- PlaytestFullgame: 0 errors, 100 existing warnings; `build/release16-playtest.log`.
- Playtest output copied to `build/ending-playtest/CasualtiesMP.dll`; build artifact hash checked. Shipping DLL checked for absence of test fixture type names.
- Test-Release14 preflight invoked with run `release16-preflight`; refused because A/B already running (PIDs 3080, 56184). No scenes ran; no runtime PASS is claimed. User confirmation to terminate those existing instances is pending.
- Both final builds direct PostBuild output into repository build directories; no D:\steam path used.

## Added / updated runtime coverage (compiled, pending execution)

- r14-escape: non-core client injects host carrier; readiness within network latency; host and client open panels simultaneously, alternate four clamps and two hoses, share cargo removal and mask127.
- r14-remote: host non-core injects client carrier; readiness in same frame; two concurrent panels/alternating progress.
- r14-noncoredeath: core self injection immediately enables solo operation; seven steps still complete independently.
- Escape-success cases now assert x367/y243 remains Escaping with trapdoor released, then place the walker at x367/y271 to verify the final exit. These coordinate fixtures do not verify natural uninterrupted traversal.
- Countdown alpha <= .5 on client.
- New r14-chipped: chipped cargo holder plus real non-core gun RPC; both clients remain Laboratory; carrier reaches the normal Biopod exit and both receive ending1. Uses the existing laboratory direct settlement, not the escape alternative narrative.
- Existing merc/coredeath/capture/noncoredeath/timeout scenes remain in the default suite. Evidence checker accepts the new chipped scenario and an explicit shipping DLL path.

Resume after A/B are closed:

```powershell
.\scripts\Test-Release14.ps1 -Run release16-verified
python scripts\check_release14.py release16-verified build/ending-ship/CasualtiesMP.dll
```

## Remaining review / real-machine items

- Teammate translucent hands are not implemented. Simultaneous interaction and completed-step sharing are implemented, awaiting runtime validation.
- Natural surgery-door opening, trapdoor fall and full corridor traversal must be checked in live play; automated fixtures place coordinates explicitly.
- Anesthetic client reports are authenticated and range checked, but dose is still a client-reported medical event, as allowed by the requested protocol design. No independent server inventory-drain proof was introduced.
- Chip rejection needs the new real-gun test before declaring this verified. Proposed rule is carrier-only eligibility; deny escape while chipped and retain Biopod rather than silently switching endings on gunfire.
- Drop/blue-heart/sandbox/crystal/Merc story logic and Escape enable flag were not changed.
