# RimTestHarness

A development-only RimWorld 1.7 mod plus a Node runner that let an AI agent (or a script) drive the game for automated mod testing: launch headless with an isolated save-data folder, run commands on the game's main thread over loopback HTTP/JSON, and capture per-tick Combat Extended turret/projectile telemetry into SQLite. It implements §4 phases 1-2 and §7 of [`docs/AI_MOD_TESTING_ANALYSIS.md`](docs/AI_MOD_TESTING_ANALYSIS.md).

Built and verified on 2026-10-16 against RimWorld **1.7.4980 rev60** (macOS arm64, CoreCLR/WaylithClr runtime), Harmony workshop build 3.0.0-rc.3, Combat Extended workshop 2890901044, and `Faryzal2020.AMCCE_copy` (AbsolutelyMoreCannonsCE). Anything not covered by the verification record at the bottom is marked **[UNVERIFIED]**.

**Never ship this mod in a release mod list.** It opens an HTTP server (loopback only, no auth) and patches the game.

## Install

Clone into the game's `Mods` folder so the folder name is `RimTestHarness`:

```bash
cd "<RimWorld install>/Mods" && git clone https://github.com/Faryzal2020/RimTestHarness.git
```

The prebuilt `Assemblies/RimTestHarness.dll` is committed, so the mod works without building. The runner assumes it lives at `<GameRoot>/Mods/RimTestHarness/runner`; from anywhere else pass `--game=<GameRoot>` (and `--exe=` if needed).

## Platforms

| | macOS | Windows | Linux |
|---|---|---|---|
| Mod DLL | verified (arm64) | **[UNVERIFIED]** | **[UNVERIFIED]** |
| Runner (`launch`/`stop`/`quit`) | verified | **[UNVERIFIED]** | **[UNVERIFIED]** |
| Native SQLite | `libe_sqlite3.arm64.dylib` / `.dylib` | `e_sqlite3.dll` (x64) | `libe_sqlite3.so` |

Platform handling:

- **Game binary.** The runner tries `RimWorld.app/Contents/MacOS/RimWorld` (macOS), `RimWorldWin64.exe` then `RimWorld.exe` (Windows), `RimWorldLinux` then `RimWorld` (Linux). Override with `--exe=PATH` or `RIMTEST_GAME_EXE`.
- **Game assemblies for the build.** The csproj looks for `Assembly-CSharp.dll` in `RimWorld.app/Contents/MacOS`, then next to the executable, then `RimWorldWin64_Data/Managed` / `RimWorldLinux_Data/Managed`. Override with `dotnet build -c Release -p:GameManaged=<folder>`; `-p:GameRoot=` and `-p:WorkshopRoot=` override the game and Workshop roots.
- **Stopping the game.** POSIX uses SIGTERM then SIGKILL; Windows uses `taskkill /T`, then `taskkill /T /F`.
- **Player.log fallback.** macOS `~/Library/Application Support/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log`; Windows `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log`. Once the game is up the runner uses the path the game reports (`status.logPath`).

**Check first on Windows/Linux:** this mod targets the 1.7 CoreCLR engine (net10.0). If the install has no `UnityStubs.dll` next to `Assembly-CSharp.dll`, it is a different engine build and the harness DLL will most likely not load. Also confirm the command-line switches (`-Headless`, `-quicktest`, `-savedatafolder=`, `-NoCrashDialog`, `-WindowSize=`) exist there.

## Using it from an AI agent

Any agent that can run terminal commands (Claude Code, Google Antigravity, ...) can drive the game today through `node runner/runner.js ...`; point it at this README. An MCP server wrapper is planned; it will be a stdio server you can register in Claude Code or in Antigravity's `mcp_config.json`.

```
RimTestHarness/
  About/About.xml                 packageId rimtestharness.harness, loadAfter brrainz.harmony / CE / AMC
  Assemblies/                     RimTestHarness.dll (build output) + Microsoft.Data.Sqlite 8.0.31, SQLitePCLRaw.core 2.1.13,
                                  SQLitePCLRaw.provider.dynamic_cdecl 2.1.13 (copied from RimSearcher_DataMod)
  Native/                         libe_sqlite3.arm64.dylib, libe_sqlite3.dylib (x64), libe_sqlite3.so, e_sqlite3.dll (from RimSearcher_DataMod)
  Source/RimTestHarness/          C# project (net10.0)
    HarnessMod.cs                 Verse.Mod entry: Harmony, log capture, main-thread pump, CE probe, HTTP server, harness.port
    MainThread.cs                 ConcurrentQueue drained by a Harmony postfix on Verse.Root.Update; multi-frame jobs
    HarnessServer.cs              HttpListener on 127.0.0.1:<port>
    Commands.cs / Args.cs         command table (lifecycle, time, debug, spawn, query)
    TurretCommands.cs             CE/AMC turret commands (reflection by type name)
    Reflect.cs                    compiled expression-tree getters for by-name member access (no per-call allocation)
    LogCapture.cs                 cursor-addressable ring of every engine log line
    Telemetry/Ring.cs             SPSC struct ring buffers + sample/event structs
    Telemetry/TelemetryStore.cs   SQLite writer thread, schema, read connection
    Telemetry/SqliteNative.cs     loads Native/libe_sqlite3 and registers SQLitePCLRaw's dynamic_cdecl provider
    Telemetry/CeProbe.cs          Harmony hooks on CE projectile/turret methods (resolved by name)
    Telemetry/CeHeadlessShim.cs   headless-only finalizer on CE BoundsInjector.BoundMap (see "Headless CE")
    Telemetry/TelemetryCommands.cs telemetry_start/stop, track_summary, turret_timeline, samples, events, telemetry_sql
  docs/                           AI_MOD_TESTING_ANALYSIS.md (design analysis this mod implements)
  runner/runner.js                Node CLI: launch/stop/wait, every harness command, sql against the telemetry db
  runner/runs/<session>/          isolated -savedatafolder per launch (Config/ModsConfig.xml, harness.port, session.json,
                                  exit.json, runner-stdout.log, telemetry/<runId>.db)
```

## Build

```bash
cd "Source/RimTestHarness" && dotnet build -c Release
```

Requires the dotnet SDK (10.0.400 used). Output goes to `Assemblies/RimTestHarness.dll`. The csproj references the installed game's `Assembly-CSharp.dll` (see Platforms for where it looks), `UnityStubs.dll`, `Brutal.Core.Maths.dll` and the workshop 1.7 `0Harmony.dll` directly (exact build match); when those paths are absent it falls back to the `Krafs.Rimworld.Ref 1.7.4979-alpha` and `Lib.Harmony 2.3.3` (ExcludeAssets=runtime) NuGet packages **[UNVERIFIED fallback path]**. `CombatExtended.dll` is referenced optionally for IDE navigation only; no code references CE types, everything CE/AMC is resolved by name.

**Why net10.0 and not net472.** RimWorld 1.7 runs on CoreCLR; the 1.7 Harmony mod ships a net10.0 `0Harmony.dll` (Harmony 3.0.0-rc.3) and `Krafs.Rimworld.Ref` 1.7.x ships only `ref/net10.0`. A net10.0 class library loaded with `Assembly.LoadFrom` (what `ModAssemblyHandler.ReloadAll` does) is the same shape as Harmony's own DLL, and it compiles cleanly against the net10.0 game assemblies, which a net472 project cannot do without facade hacks. Verified: the DLL loads, patches apply, commands run (see record below). Existing net4x mods (CE net40, AMC net472) load alongside it unchanged.

No DLL hot reload exists in 1.7: rebuild, then quit and relaunch the game.

## Run

```bash
cd runner
node runner.js launch                                   # core + Harmony + harness, headless, -quicktest, waits for Playing
node runner.js launch --mods=ludeon.rimworld,brrainz.harmony,ceteam.combatextended,faryzal2020.amcce_copy,rimtestharness.harness
node runner.js launch --windowed=1280x720 --port=18575 --savedata=runs/mywin   # windowed (GPU) run on another port
node runner.js status
node runner.js tick 600
node runner.js spawn_pawn kindDef=Colonist faction=player x=125 z=125
node runner.js debug_action 'path=Actions\Finish All Research'
node runner.js telemetry-start myrun; node runner.js tick 600; node runner.js telemetry-stop
node runner.js track-summary 36395
node runner.js turret-timeline 36380
node runner.js sql "SELECT id, count(*), max(height) FROM proj_samples GROUP BY id" --table
node runner.js quit 7          # prints the process exit code from exit.json
node runner.js stop            # SIGTERM/SIGKILL the game if it hangs
```

Node >= 22.5 (26.7 used); no npm dependencies (`node:sqlite`, `fetch`). `launch` writes `Config/ModsConfig.xml` (`ModsConfigData{version, activeMods, knownExpansions}`, version taken from `<GameRoot>/Version.txt`) into a fresh folder under `runner/runs/<timestamp>` (or `--savedata=DIR`), starts a detached supervisor that spawns

```
RimWorld.app/Contents/MacOS/RimWorld -Headless -quicktest -NoCrashDialog -savedatafolder=<dir> -TestPort=<port>
```

(`-WindowSize=WxH` replaces `-Headless` with `--windowed`), records the game pid in `session.json` and the exit code in `exit.json`, then polls `harness.port` and `status` until `programState=Playing && !anyLongEvent` (`--wait=menu|port|none`, `--timeout=S`; on timeout the game is killed). All later commands use the last launched session or `--savedata=DIR`; the generic form `node runner.js <command> key=value ...` reaches every harness command, `--json='{...}'` for nested arguments, `--raw` for the full `{ok,result|error}` envelope.

The HTTP protocol is `POST http://127.0.0.1:<port>/<command>` with a JSON object body (or `POST /` with `{"cmd","args","timeout_ms"}`); `GET /help` lists commands. Every response is `{"ok":true,"result":...}` or `{"ok":false,"error":"...","type":"..."}`. Commands run on the main thread (Harmony postfix on `Verse.Root.Update`, which both `Root_Entry.Update` and `Root_Play.Update` call via `base.Update()`, in headless mode too) unless marked off-thread; exceptions are caught and returned, never thrown into the game loop.

## Commands

| Command | Arguments | Notes |
|---|---|---|
| `status` | | programState, scene, anyLongEvent, ticksGame, paused, timeSpeed, mapId/mapCount/mapSize, errors/warnings, headless, gameVersion, saveDataFolder, logPath (`Application.consoleLogPath`), ceProbe, ceBoundsShim, telemetry, activeMods |
| `log` | since, level (message/warning/error), limit, stack, contains | off-thread; own 50k-entry ring fed by `Application.logMessageReceivedThreaded` (the hook Verse.Log itself uses), cursor `next` |
| `load_save` | name | `GameDataSaveLoader.LoadGame`; file must exist in `<savedata>/Saves` **[UNVERIFIED: not exercised]** |
| `new_quicktest_game` | | queues the Play scene with `Current.Game=null` so `Root_Play.Start` runs `SetupForQuickTestPlay` **[UNVERIFIED: not exercised; quicktest was always started by `-quicktest`]** |
| `main_menu` | | `GenScene.GoToMainMenu()` **[UNVERIFIED]** |
| `pause`, `set_speed` | speed 0-4 or name | `TickManager.Pause()`, `CurTimeSpeed` setter |
| `tick` | n, budgetMs (250) | pauses, then `TickManager.DoSingleTick()` n times, spread over frames by wall-clock budget; returns ticked/startTick/ticksGame |
| `dev_mode` | on | `Prefs.DevMode` |
| `set_debug` | field, value | any public static bool on `DebugSettings` (unlimitedPower, godMode, enableStoryteller, ...) |
| `list_debug_actions` | path, depth, limit | walks `Dialog_Debug.rootNode` (`TrySetupNodeGraph`/`TrySetupChildren`); paths like `Actions\Finish All Research`, `Settings\Unlimited Power` |
| `debug_action` | path, pawnId | `Dialog_Debug.GetNode(path).action()`; `ToolMapForPawns` nodes take pawnId; `ToolMap`/`ToolWorld` nodes are refused (they read `UI.MouseCell()`) |
| `spawn_thing` | defName, x, z, stuff, count, rot, faction (player for buildings), power | `ThingMaker.MakeThing` + `SetFactionDirect` + `GenSpawn.Spawn`; forces `CompPowerTrader.PowerOn` when `power` or `DebugSettings.unlimitedPower` (unconnected traders are never ticked by a PowerNet) |
| `spawn_pawn` | kindDef (Colonist), faction (player/hostile/none/defName/name), x, z, nearest (true), radius | `PawnGenerator.GeneratePawn` + `GenSpawn.Spawn`; moves to `CellFinder.StandableCellNear` if the cell is blocked |
| `draft` | pawnId, on | `pawn.drafter.Drafted` |
| `order_job` | pawnId, jobDef (Goto), targetId or targetX/targetZ, count | `JobMaker.MakeJob` + `Pawn_JobTracker.TryTakeOrderedJob`; also returns `reachable` (`Reachability.CanReach`) and `standable` |
| `place_blueprint` | defName, x, z, rot, stuff | `GenConstruct.CanPlaceBlueprintAt` then `PlaceBlueprintForBuild` **[UNVERIFIED: not exercised]** |
| `query_things` | id, defName, class, mapId, limit, deep | summary: id, def, class, pos, rot, hp, faction, stack, comps; pawn block (kind, name, dead, downed, drafted, curJob, health); turret block (targets, topRotation, gun, verb, warmup/cooldown, CE ammo/fire mode/active/holdFire, powerOn); `deep` adds inspect strings |
| `cell_info`, `find_cell` | x, z, radius, clearRadius | terrain/standable/things at a cell; nearest standable cell with a clear radius |
| `defs` | type, contains, limit | DefDatabase listing |
| `power` | id, on | force a `CompPowerTrader` on/off |
| `screenshot` | path | `ScreenCapture.CaptureScreenshot`; windowed only, refused headless |
| `quit` | exitCode | responds first, then 400 ms later `SteamManager.ShutdownSteam()` + `Application.Quit(exitCode)`; the host's quit handler passes `UnityRuntime.App.ExitCode` to the process exit |
| `turret_ammo` | id, ammoDef | CE `CompAmmoUser.ResetAmmoCount(AmmoDef)`; validates ammoDef against `CurAmmoSet.ammoTypes` |
| `turret_fire_mode` | id, mode, aimMode | CE `CompFireModes.CurrentFireMode/CurrentAimMode` setters **[UNVERIFIED: not exercised]** |
| `turret_order_attack` | id, targetId or x/z, clear | `Building_Turret.OrderAttack(LocalTargetInfo)` **[UNVERIFIED: not exercised]** |
| `turret_fcs` | id, defName (AMC_FCS_Basic), replace | AMC: adds an FCS item into `CompTurretFCS.GetDirectlyHeldThings()` exactly as `JobDriver_LoadTurretFCS` does |
| `turret_mode_swap` | id | AMC `CompTurretModeSwap.TrySwapMode()` via reflection; returns the replacement turret at the cell **[UNVERIFIED: not exercised]** |
| `telemetry_start` | runId, note | opens `<savedata>/telemetry/<runId>.db` |
| `telemetry_stop` | | flushes, writes `runs.stopped_tick`, checkpoints WAL, closes |
| `telemetry_status` | | counters, pending, dropped |
| `track_summary` | projId | launch/impact/destroy cells and positions, hit thing, max height, flight ticks, speed start/end/decay, miss distance from `intendedTarget` cell centre |
| `turret_timeline` | id, limit, sinceTick | edge-triggered state rows merged with the turret's events |
| `samples` | projId, every, fields, limit (max 5000) | decimated by `flight_ticks % every` |
| `events` | since (seq), type, id, limit | |
| `telemetry_sql` | sql (SELECT/WITH only), limit | read-only query on the open db; the runner's `sql` reads the file directly with `node:sqlite` |

## Telemetry design (§7)

Two channels. **Events** (sparse): `launch`, `impact`, `destroy`, `burst_begin`, `burst_complete`, `target_change`, `reload`, `mode_swap`, `note`. **Samples** (dense): one `ProjSample` struct per CE projectile per tick (tick, id, x, y, z, height, speed, shotAngle, shotRotation, flightTicks, ticksToImpact) and one `TurretSample` per CE turret, edge-triggered only (rotation delta > 0.01°, target change, warmup/cooldown 0-crossings, magazine change, reloading flag).

Capture is allocation-free on the hot path: Harmony postfixes read fields through cached `AccessTools.FieldRef` and compiled expression-tree getters, build a struct and `TryPush` it into a preallocated single-producer/single-consumer ring (`1<<17` projectile samples, `1<<15` turret samples, `1<<14` events; overflow increments `Dropped`, never blocks). A background thread drains the rings every 200 ms (or on `Flush`) into SQLite inside one transaction with prepared statements. WAL mode lets the in-game read connection and the runner's `node:sqlite` read the file while the writer appends. Nothing per-tick goes through `Verse.Log`.

Schema (`<savedata>/telemetry/<runId>.db`):

```sql
runs(run_id, started_tick, started_at, stopped_tick, stopped_at, meta, dropped_proj, dropped_turret, dropped_events)
events(seq, run_id, tick, type, id, other_id, x, y, z, f1, f2, tx, tz, detail)
proj_samples(run_id, tick, id, x, y, z, height, speed, shot_angle, shot_rotation, flight_ticks, ticks_to_impact)
turret_samples(run_id, tick, id, rotation, target_id, target_x, target_z, warmup, cooldown, mag, flags)  -- flags 1 warming, 2 cooling, 4 target, 8 reloading
```

Event payloads: `launch` id=projectile, other_id=launcher, xyz=origin, f1=shotAngle, f2=shotRotation, tx/tz=intended target cell, detail=`def|speed=|launcher=|target=`; `impact` other_id=hit thing (-1 ground), f1=speed, f2=flightTicks, detail=`def|pawn|up/downed/dead` or `ground`; `destroy` f1=flightTicks; `burst_begin` id=turret, other_id=target, f1=burstShotCount, f2=top rotation, tx/tz target cell; `burst_complete` f1=cooldown ticks set, f2=mag; `target_change` other_id=new target (0=cell, -1=none); `reload` id=turret (or gun), f1=mag after; `mode_swap` id=old turret, other_id=new turret.

Hooks (all resolved by name, missing CE = probe disabled, harness still loads): `ProjectileCE.Launch(Thing, Vector2, Thing)` (the terminal overload the 8-argument `Launch` calls), `ProjectileCE.Tick()`, `ProjectileCE.Impact(Thing hitThing)`, `ThingWithComps.Destroy(DestroyMode)` prefix filtered to `ProjectileCE` instances, `Building_TurretGunCE.Tick()/BeginBurst()/BurstComplete()`, `CompAmmoUser.LoadAmmo(Thing, bool)`, and AMC `CompTurretModeSwap.TrySwapMode()` prefix/postfix.

### Headless CE: the bounds shim

Without a GPU, CE's `BoundsInjector.CollectTextures` fails for every pawn/plant texture (`Combat Extended :: AsyncGPUReadback failed for <tex>`, 425-546 errors at boot) and every later `BoundsInjector.BoundMap(graphic, type)` throws `BoundMap(,)`. That exception escapes `Verb_LaunchProjectileCE.CanHitTargetFrom` inside `Building_TurretGunCE.Tick`, so CE turrets never acquire a target headless (observed: `Exception ticking Turret_U20mmFlak38_Base ... BoundMap(,)`, zero bursts in 600 ticks). `CeHeadlessShim` installs a Harmony finalizer on `BoundMap` that swallows the exception and returns `(0.75, 0.75)` (the method returns the trimmed sprite's width/height fraction, later scaled by `drawSize` / `visualSizeRange`). It is active only with `-Headless` (or `-CEBoundsShim`; disable with `-NoCEBoundsShim`; `-CEBoundsFallback=0.6` changes the value) and `status.ceBoundsShim.shimmedCalls` counts its use. Consequence: headless CE collision heights for pawns/plants are approximate; use a windowed run for height-sensitive ballistics checks. **[UNVERIFIED]** how far the 0.75 default is from real per-texture bounds.

## Verification record (2026-10-16)

Everything below was executed on this machine through `runner.js`; outputs are in `runner/runs/*/` (session.json, exit.json, runner-stdout.log, telemetry/*.db).

Verified:

- `dotnet build -c Release` of the net10.0 project succeeds; `RimTestHarness.dll` loads in 1.7.4980, the Mod constructor runs, Harmony patches apply (Root.Update pump, log capture, CE probe when CE is active).
- Headless boot with `ludeon.rimworld, brrainz.harmony, rimtestharness.harness` (`-Headless -quicktest`): harness.port written, `status` reaches `Playing` in 49-54 s cold / 24 s warm; the only error is the known harmless `Resolution too small (0x0)`; `TicksGame` advances at Normal speed headless.
- `tick 300`: startTick 1684 -> ticksGame 1984 in 28 ms with the game paused (1 frame); `tick 240` and 3x`tick 60/120/120` likewise exact.
- `list_debug_actions` depth 2 returned 774 nodes (539 directly invokable); `debug_action path=Actions\Destroy non-colonists` ran: pawns on map 49 -> 3 (only Colonists left).
- `spawn_pawn Colonist` at a `find_cell` standable cell, `draft`, `order_job Goto` to a reachable cell 13 cells away: `accepted=true, reachable=true, curJob=Goto`; position after +60/+180/+300 ticks: (128,124), (135,123), (138,125) = goal; job then `Wait_Combat`.
- `quit 7` -> process exit code 7 (also 0, 3, 5 in later runs) via `exit.json`; no RimWorld process left afterwards.
- CE + AMC (`ceteam.combatextended, faryzal2020.amcce_copy`) load headless and reach Playing; all 546 boot errors are CE `AsyncGPUReadback failed` / `CropVertical` / `BoundMap` texture errors from the missing GPU; AMC and the harness logged none. **Windowed** (`-WindowSize=1280x720`) the same mod list boots with **0 errors, 0 warnings**.
- Scenario (headless with shim, and windowed without shim, both): `set_debug unlimitedPower`, `spawn_thing Turret_U20mmFlak38_Base` (powerOn true via forced `CompPowerTrader.PowerOn`), `turret_fcs AMC_FCS_Basic` (turret inspect goes from `INOPERABLE: Missing Fire Control System` to `FCS Loaded`), `turret_ammo` (mag 0 -> 160 of `Ammo_20x138mm_AP`), `spawn_pawn Pirate faction=hostile` 22 cells east, `telemetry_start`, `tick 300` x2 (88-154 ms per 300 ticks). Result in both runs: turret acquired the pawn, `burst_begin` x4, `burst_complete` x2, 6 projectiles launched, magazine 160 -> 154, `target_change` x12 as the pawn walked; headless db: 118 proj_samples / 20 turret_samples / 35 events, 5 impacts (3 on the pawn, 2 on sandstone), 0 dropped; windowed db: 158 / 20 / 32, 3 impacts (all on the pawn), 0 dropped. `track_summary 36395`: launch (124,127) at height 0.70, impact tick +8 at (145.52,126.5) on `Human|pawn|up`, maxHeight 0.695, speed 165 -> 165, missDistance 3.98 from intended cell (149,126). `turret_timeline` shows warmup 165 -> 0, bursts, cooldown 59 -> 0 transitions only. `samples every=2/4` decimation works. `sql` through `node:sqlite` on both files (runs, per-projectile aggregates, impacts).
- `screenshot` windowed wrote a 2880x1800 PNG of the map; headless it is refused with a clear error.
- SQLite inside the game on CoreCLR: `Native/libe_sqlite3.arm64.dylib` loaded via `NativeLibrary.Load`, `SQLite3Provider_dynamic_cdecl` registered, Microsoft.Data.Sqlite 8.0.31 (netstandard2.0) works; WAL + concurrent in-game reads + external `node:sqlite` reads work.
- Headless `Root.Update` runs ~300k frames/s (busy loop), so the pump latency is negligible; `tick` jobs honour the per-frame budget.

Not verified / open:

- **[UNVERIFIED]** `load_save`, `new_quicktest_game`, `main_menu`, `place_blueprint`, `turret_fire_mode`, `turret_order_attack`, `turret_mode_swap`, `events since` paging, `telemetry_sql`: implemented against decompiled signatures but not exercised in a run.
- **[UNVERIFIED]** `reload` events: `CompAmmoUser.LoadAmmo` never fired in the scenario (the magazine never emptied). `mode_swap` events: not exercised.
- **[UNVERIFIED]** the NuGet fallback references (Krafs 1.7.4979-alpha, Lib.Harmony 2.3.3) compile the project when the game/workshop paths are absent; only the direct-reference path was built.
- **[UNVERIFIED]** behaviour with Steam not running; all runs had the Steam client open.
- **[UNVERIFIED]** CE's `burst_begin` fires twice per visible burst (e.g. ticks 3213 and 3295 before shots at 3295-3315): recorded as-is, cause not investigated (CE calls `BeginBurst` when `burstWarmupTicksLeft` reaches 0 and `TryStartShootSomething(canBeginBurstImmediately)` may call it again).
- **[UNVERIFIED]** three recorded 20mm AP impacts on the pirate left `health = 1.00` and the pawn standing (`RunForCover`); whether that is CE armour/overpenetration, a `DebugSettings` interaction, or something in the AMC projectile is for the mod author to investigate with this data.
- Headless `ScreenCapture`, synthetic input (`InputSystem.Queue*`), `-NoWindow`, the script-file runner (§3 C) and the MCP wrapper (§3 G) are out of this MVP's scope.
- Windows/Linux native SQLite libraries are bundled but untested here.

## Evidence (decompiled with ilspycmd, paths relative to the decompile roots)

Assembly-CSharp: `Verse/Root.cs:114` Update; `Verse/Root_Play.cs:60-85` Update/Start/SetupForQuickTestPlay; `Verse/Root_Entry.cs:33`; `Verse/TickManager.cs:361` DoSingleTick, `:241` Pause, `:191` CurTimeSpeed; `LudeonTK/Dialog_Debug.cs:292` GetNode, `:169` TrySetupNodeGraph; `LudeonTK/DebugActionNode.cs` (label, actionType, action, pawnAction, children, Path, TrySetupChildren); `Verse/Log.cs:49,91,224` Messages / logMessageReceivedThreaded subscription / Notify_LogMessageReceived, `LogMessageQueue.maxMessages=1000`, `StopLoggingAtMessageCount=10000`; `Verse/GenCommandLine.cs:9,22`; `Verse/GenFilePaths.cs:93,178,200` SaveDataFolderPath/ConfigFolderPath/ModsConfigFilePath; `Verse/ModsConfig.cs:88-135` (ModsConfigData, version reset rule); `Verse/ModAssemblyHandler.cs:29` Assembly.LoadFrom; `Verse/LoadedModManager.cs:64` CreateModClasses after assemblies load; `Verse/GameDataSaveLoader.cs:153`; `Verse/GenScene.cs:16`; `Verse/PawnGenerator.cs:121`; `Verse/GenSpawn.cs:86`; `Verse/ThingMaker.cs:8`; `Verse/JobMaker.cs:23`; `Verse.AI/Pawn_JobTracker.cs:889` TryTakeOrderedJob; `RimWorld/Pawn_DraftController.cs:19`; `RimWorld/GenConstruct.cs:157,359`; `Verse/Thing.cs:1744` SetFactionDirect; `RimWorld/CompPowerTrader.cs:74` PowerOn setter, `RimWorld/PowerNet.cs:183,240` unlimitedPower only inside a net; `RimWorld/Building_Turret.cs` (CurrentTarget, ForcedTarget, OrderAttack, AttackVerb); `RimWorld/TurretTop.cs:28` CurRotation; `RimWorld/VersionControl.cs:27`.
UnityStubs: `UnityEngine/Application.cs:18,140,279` LogCallback / logMessageReceivedThreaded / Quit(int) -> `UnityRuntime.Current.App.ExitCode`; `UnityEngine/ScreenCapture.cs:24`. Host `RimWorldWaylith/Bootstrap.cs:107` `app.Quit(_unityRuntime.App.ExitCode)`.
CombatExtended: `Building_TurretGunCE.cs:20` (`: Building_Turret`), `:28,30,32,38,62` fields, `:139` Gun, `:185,197` CompAmmo/CompFireModes, `:335` OrderAttack, `:398` Tick, `:633` BeginBurst, `:674` BurstComplete; `ProjectileCE.cs:18` (`: ThingWithComps`), `:66-122` fields, `:207` ticksToImpact, `:231` Height, `:240` ExactPosition, `:856,883` Launch, `:1772` Tick, `:2031` Impact; `ProjectileCE_Explosive.cs:62` base.Impact; `CompAmmoUser.cs:85,151,249,403,798,885`; `CompFireModes.cs:79,91`; `FireMode.cs`; `AmmoSetDef.cs:11`, `AmmoLink.cs:9`; `BoundsInjector.cs:26-50,73,371,446`.
AMC source: `CompTurretModeSwap.cs:90` TrySwapMode; `CompTurretFCS.cs:20,35` LoadedFCSItem/GetDirectlyHeldThings; `JobDriver_LoadTurretFCS.cs:67`.
RimSearcher_DataMod (decompiled): `NativeLibrary.cs` (dlopen + `SQLite3Provider_dynamic_cdecl.Setup("e_sqlite3", resolver)` + `raw.SetProvider`).
