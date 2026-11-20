# AI-driven in-game mod testing for RimWorld 1.7 — approaches and how-tos

Date: 2026-10-16. Target: RimWorld 1.7.4980 rev60, macOS arm64, this install.
Evidence: decompiled `Assembly-CSharp.dll`, `RimWorld.dll` (host), `UnityStubs.dll`, `Waylith.dll` with ilspycmd, plus two real headless launches. DecompilerServer and the rimsearcher Def snapshot are not set up yet, so anything not backed by decompiled source or a launch is tagged `[UNVERIFIED]`.

---

## 1. Ground truth about 1.7 that changes every "how"

| Fact | Evidence | Consequence |
|---|---|---|
| 1.7 is **not Unity**. It runs on a patched CoreCLR (`.NET 10.0.10-waylithclr.dev`) inside a custom Vulkan/GLFW engine ("Waylith"). | `RimWorld.app/Contents/MacOS/RimWorld.runtimeconfig.json`, `libcoreclr.dylib`, `Waylith.dll`, no `_Data/Managed` folder | Unity tricks (`-batchmode`, `UnityEngine` internals, Doorstop/BepInEx-style preloaders) do not apply. |
| Unity APIs are **shimmed**: `UnityStubs.dll` + `Waylith.UnityCompat.dll`; assembly-name redirects `UnityEngine*=UnityStubs`. | `runtimeconfig.json` `WaylithClr.AssemblyNameRedirects` | Existing net472 mod DLLs (incl. your AMC build) still load unchanged. |
| **Harmony works.** The runtime explicitly lists `JitPatchableAssemblies: Assembly-CSharp` and sets `MONOMOD_DMDType=dynamicmethod`; the Harmony workshop mod ships a `1.7/Assemblies/0Harmony.dll`. | `runtimeconfig.json`, `RimWorldWaylith.Program.Main`, workshop 2009463077 | A C# harness mod can patch anything it needs. |
| Mod DLLs are loaded with plain `Assembly.LoadFrom`, no `AssemblyLoadContext`. | `Verse.ModAssemblyHandler.ReloadAll` | **No DLL hot reload.** Code changes = restart the process. XML/Def hot reload exists (`PlayDataLoader.HotReloadDefs`). |
| The host has first-class **headless** and **after-boot** switches. | `RimWorldWaylith.Program.Main`, `Bootstrap.RunAfterBootSwitches`, `Waylith.CLI.EngineSwitches` | See §2. These are Ludeon's own automation hooks; copying their pattern is the lowest-risk path. |
| In headless mode **`OnGUI` is never dispatched**. `UnityWorld.DispatchOnGUI` is only called from the ImGui host, which is not created when `Headless`. `Update`/`LateUpdate` (the simulation) do run. | `Bootstrap.Initialize`, `ImguiDispatch.cs:428`, `UnityRuntime.Step` | Headless = full simulation, zero UI. Windows, dialogs, `Widgets`, anything living in `DoWindowContents` is dead. Good for logic tests, useless for UI tests. |
| The Unity Input stubs expose **public synthetic-input queueing**. | `UnityEngine.InputSystem.InputSystem.QueueKeyChange / QueueMouseMove / QueueMouseButton / QueueMouseScroll / QueueTextInput` | A mod can drive the real UI with fake clicks and keys, windowed only. |
| The **debug-action tree is addressable by path** from code. | `LudeonTK.Dialog_Debug.GetNode(string path)` and `DebugActionNode.action` | Hundreds of vanilla dev tools (spawn, incidents, kill, heal, weather, research...) are callable without UI. |
| Direct launch from the terminal works, with Steam running. | `SteamManager.InitIfNeeded` (warns and continues if `SteamAPI.Init` fails), smoke run below | No need to go through the Steam client to start test runs. |

### Verified command-line switches

Host/engine (`RimWorldWaylith.Program`, `EngineSwitches`): `-Headless`, `-NoWindow`, `-WindowSize=WxH`, `-WindowPos=X,Y`, `-Verbose`, `-NoCrashDialog`, `-NoFileLog`, `-DisableLogStackTraces`, `-CrashHandler`, `-SuppressUnityStubs`, `-ColdBoot`.
Game (`GenCommandLine`, `Bootstrap`): `-quicktest`, `-savedatafolder=<dir>`, `-QuitAfterBoot`, `-ScreenshotAfterBoot=<file>`, `-MemoryReportAfterBoot`, `-legacy-xml-deserializer`.
Also: a save named `autostart.rws` is loaded automatically at boot when dev mode is on (`SaveGameFilesUtility.GetAutostartSaveFile`).

### Smoke results (this machine)

```
RimWorld.app/Contents/MacOS/RimWorld -Headless -QuitAfterBoot -NoCrashDialog -savedatafolder=<tmp>
```

Core only, no window, no GPU: exit code 0 after about 5 seconds including the 180-frame post-boot wait. All play data loaded (defs, languages, texture atlases). One harmless error (`Resolution too small (0x0)`), a warning that 666 textures have no pixels (expected without a device). Log written to `~/Library/Application Support/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log` with a parseable header (version, runtime, command line).

Headless `-quicktest` (map generation + ticking without a GPU): see §5, "Headless quicktest".

---

## 2. The problem has three layers

1. **Launch and lifecycle** (outside the game): start the process with the right mod list, flags and isolated save-data folder; detect boot completion; collect logs; enforce timeouts; kill and retry.
2. **Control plane** (inside the game): a bridge from the agent to the game's main thread, plus a command vocabulary (load game, tick N, spawn X, run debug action, query state, screenshot, quit).
3. **Observation and assertions**: structured log capture, state queries, screenshots, pass/fail reporting with an exit code.

Every approach below is a different split of these layers between "external script" and "in-game mod".

---

## 3. Approaches

### A. No mod: external runner + flags + log parsing
Launch with `-Headless -QuitAfterBoot` (or `-ScreenshotAfterBoot`), a templated `ModsConfig.xml` in a throwaway `-savedatafolder`, then grep `Player.log`.
- Covers: "does this mod set load without errors", XML/Def/patch errors, missing textures, Harmony patch exceptions at startup, boot-time regressions, a boot screenshot.
- Cost: a few hours. Zero in-game code.
- Limits: nothing after the main menu; no map, no gameplay.
- Verdict: do this first regardless. It is the CI gate.

### B. Harness mod with a local RPC server (recommended core)
A C# mod that opens `HttpListener` on `127.0.0.1:<port>` (or a Unix socket / stdio pipe) on a background thread, parses JSON commands, and **enqueues them for the main thread**. Responses are returned when the handler completes.
- Main-thread marshalling options, in order of preference:
  1. A `ConcurrentQueue<Action>` drained from a `GameComponent.GameComponentUpdate` (only while a game is loaded) **and** from a Harmony postfix on `Verse.Root.Update` (covers the main menu). `Root.Update` runs in headless mode too.
  2. `LongEventHandler.ExecuteWhenFinished(Action)` for "run once play data is loaded".
  3. `LongEventHandler.QueueLongEvent(...)` for heavy work that must block the game (map gen, loading).
- The engine sets a `MainThreadSynchronizationContext` on the main thread and pumps it every frame (`EngineApplication.RunLoop`/`RunHeadlessLoop`), so `SynchronizationContext.Post` from the listener thread would also work. `[UNVERIFIED]` that the context captured in a mod's static constructor is that one; the explicit queue is safer.
- Command vocabulary (all backed by public API found in the decompile):
  - lifecycle: `status` (ProgramState, LongEventHandler.AnyEventNowOrWaiting, TicksGame), `load_save(name)` → `GameDataSaveLoader.LoadGame`, `new_quicktest_game` → `Root_Play.SetupForQuickTestPlay` / `SceneManager.LoadScene("Play")`, `main_menu` → `GenScene.GoToMainMenu`, `save(name)`, `quit(exitCode)` → `Root.Shutdown()` after setting `UnityRuntime.App.ExitCode` (the host passes it through; `[UNVERIFIED]` exact path for a custom exit code).
  - time: `pause`, `set_speed(TimeSpeed)`, `tick(n)` → loop `Find.TickManager.DoSingleTick()` with the game paused (deterministic, frame-independent), `wait_ticks(n)` (asynchronous, returns when TicksGame advances).
  - debug: `debug_action(path)` → `Dialog_Debug.GetNode(@"Spawning\Spawn thing")?.action?.Invoke()`; `set_debug(name, bool)` → reflection on `DebugSettings` fields (`godMode`, `enableStoryteller`, `enableRandomDiseases`, `pauseOnError`, `fastResearch`...); `dev_mode(true)` → `Prefs.DevMode = true`.
  - spawning: `GenSpawn.Spawn`, `ThingMaker.MakeThing`, `PawnGenerator.GeneratePawn`, `DebugThingPlaceHelper`, `ArenaUtility.SpawnPawnSet`, incidents via `IncidentDef.Worker.TryExecute(parms)`.
  - query: `defs(type, filter)`, `things(map, defName)` via `map.listerThings`, `pawn(id)` health/needs/skills, `thing(id)` comps and stats via `StatExtension.GetStatValue`. Serialise with a bounded reflection walker; the RimSearcher DataMod's `DefJsonSerializer`/`ReflectionTraversalPolicy` is a ready-made pattern for this.
  - logs: `log(since, level)` → snapshot `Log.Messages` (or subscribe to `Application.logMessageReceivedThreaded`, which `Verse.Log` itself uses) and return new entries with stack traces; `assert_no_errors`.
  - defs: `hot_reload_defs` → `PlayDataLoader.HotReloadDefs()` for XML-only iteration without a restart.
  - visual (windowed only): `screenshot(path)` → `ScreenCapture.CaptureScreenshot`; `click(x,y)`, `key(KeyCode)`, `type(text)` via `InputSystem.Queue*`; `camera(x,z,zoom)` via `Find.CameraDriver.JumpToCurrentMapLoc`.
- Security: bind to loopback only, random port written to a file in the save-data folder, optional token. This mod must never ship enabled in a release build.
- Verdict: this is the piece that makes "AI agent drives the game" real. Everything else wraps it.

### C. Script-file runner (batch / CI mode)
Same handlers as B, but the mod reads a JSON/YAML test script at boot from `-TestScript=<path>`, executes steps with per-step timeouts, writes a JUnit-style result file, and exits with a non-zero code on failure. Mirrors Ludeon's own `RunAfterBootSwitches` pattern (wait for `PlayDataLoader.Loaded && !LongEventHandler.AnyEventNowOrWaiting`, count frames, act).
- Best for: nightly runs, pre-release gates, "run the 40 turret scenarios".
- Verdict: build it as a thin layer over B's command table; the agent authors scripts and reads result files instead of holding a live socket.

### D. Debug-action bridge
Not a separate mod, but worth calling out: `Dialog_Debug.TrySetupNodeGraph()` + `GetNode(path)` gives the agent every vanilla and modded `[DebugAction]` for free, including `Autotests` (make colony, force-down x100, battle royale, time-to-down) and all `Incidents`. Expose `list_debug_actions` (walk `rootNode`, call `TrySetupChildren` lazily) so the agent can discover them. Caveat: actions of type `ToolMap`/`ToolMapForPawns` read `UI.MouseCell()`; the harness must set a synthetic mouse cell or call the underlying utility directly.

### E. UI-level automation (synthetic input + screenshots)
Windowed launch (`-WindowSize=1920x1080`, optionally `-NoWindow` to keep it hidden `[UNVERIFIED]` that `-NoWindow` still renders/captures), queue input through `InputSystem.Queue*`, capture with `ScreenCapture.CaptureScreenshot` (backed by `ScreenCaptureFeature`, registered only when not headless), and let the agent read the PNG. RimWorld's UI is immediate-mode with no accessibility tree, so an agent locates targets by screenshot or by asking the harness for widget rects it knows (e.g. `Find.WindowStack.Windows` and their `windowRect`).
- Good for: visual regressions of your turret textures/animations, settings windows, gizmos.
- Fragile and slow; keep it as a small, separate test tier.

### F. In-process test discovery (unit/integration tests inside the game)
Mark methods in the mod-under-test with an attribute (your own `[GameTest]`, discovered via `GenTypes.AllTypes` the same way `Dialog_Debug` discovers `[DebugAction]`). The harness runs them in a fresh quicktest map, each inside `Rand.PushState(seed)` for determinism, and reports via C. For your CE turret mod this is the natural home for "spawn turret + target, tick 600, assert N projectiles fired / hits / damage" using your existing `HarmonyPatches_BurstTelemetry` data. `ArenaUtility.BeginArenaFightSet` is a reusable template for repeated fights with statistics.

### G. MCP wrapper for agents
A Node/Python MCP server (like your `TurretEditor` `npm run mcp`) that owns process launch (A), proxies B's RPC as typed tools, and tails `Player.log`. Agents then get `launch_game`, `load_save`, `tick`, `spawn`, `query`, `screenshot`, `run_script`, `stop_game`. This also lets one agent orchestrate restart-after-rebuild cycles, which the in-game mod cannot do for itself.

---

## 4. Recommended architecture and order

```
agent (Claude / MCP client)
   │  MCP tools
   ▼
external runner (Node, reuses TurretEditor tooling)      ← A + G
   ├─ writes ModsConfig.xml into an isolated -savedatafolder
   ├─ spawns RimWorld with -Headless|-WindowSize, -quicktest or autostart save, -TestScript=...
   ├─ tails Player.log, enforces timeouts, kills on hang
   └─ HTTP/JSON ──► harness mod (C#, loopback listener)   ← B + C + D
                      ├─ main-thread queue (Root.Update postfix / GameComponent)
                      ├─ command table (lifecycle, time, debug, spawn, query, log, screenshot, input)
                      └─ script runner + result file + exit code
mod under test (AMC CE) ── [GameTest] methods, telemetry hooks  ← F
```

Phases:
1. **Runner + log gate (A).** Headless boot of the real mod list, fail on `ERROR` lines, archive logs. Half a day; immediately useful.
2. **Harness mod MVP (B).** Listener, main-thread queue, `status/log/load_save/new_quicktest_game/tick/debug_action/spawn/query/quit`. Build for net472 like AMC (`Krafs.Rimworld.Ref` must move from 1.5.4104 to a 1.7 package if published; otherwise reference `RimWorld.app/Contents/MacOS/Assembly-CSharp.dll` directly). `[UNVERIFIED]` whether a net10.0-targeted mod loads cleanly; net472 is the proven path.
3. **Script mode + MCP wrapper (C + G).** Agent writes a scenario file, runner executes it headless, returns the result JSON and the log delta.
4. **Turret test library (F).** Scenarios: spawn each AMC turret with ammo + a dummy pawn at range, tick, assert telemetry (burst count, hit ratio, barrel rotation clamp, smoke/muzzle flash spawned). Run both headless (logic) and windowed (visual, screenshot per turret).
5. **UI tier (E)**, only if visual regressions bite.

---

## 5. Risks, caveats and open questions

- **Headless quicktest**: map generation and the play loop ran without a GPU for 50 s with zero errors (§6). Tick advancement itself is still `[UNVERIFIED]` until the harness reports `TicksGame`. Camera and rendering code paths (`CameraDriver`, `MapDrawer`, texture atlases) remain the likely failure points once mods with custom rendering (CE, muzzle flash) are in the list; run the same smoke test with the real mod list before building on it.
- **No OnGUI in headless**: anything that only progresses from inside a window (`Dialog_MessageBox` confirmations, mod settings) must be bypassed by the harness calling the underlying method.
- **No DLL hot reload**: iteration on C# = rebuild + restart. Cold headless boot is ~5 s for Core; with CE + AMC expect longer (measure once). XML changes can use `HotReloadDefs`.
- **Static state leaks between tests in one process**: prefer "one process per scenario file" for CI and a long-lived process only for interactive agent sessions.
- **Determinism**: wrap scenario bodies in `Rand.PushState(seed)`/`PopState`; disable storyteller and random diseases via `DebugSettings`; tick with `DoSingleTick` rather than wall-clock speed.
- **1.7 is new on this machine (installed today)**: engine internals (`Waylith.*`, `UnityStubs.*`) are not a public API and may change between builds. Pin the harness to the game version in `About.xml` and keep engine-touching code (input queueing, screenshot) behind feature flags.
- **Steam**: direct launch works while the Steam client runs. Without Steam, `SteamAPI.Init` fails with a warning and the game continues; workshop mods are still found through the on-disk workshop folder (`ModLister` scans it). `[UNVERIFIED]` for a fully logged-out Steam.
- **rimsearcher**: the DataMod zip declares `supportedVersions` 1.6; it should load on 1.7 with a version warning, but its SQLite native library (`libe_sqlite3.arm64.dylib`) is loaded via `dlopen` from the mod folder and has not been exercised on CoreCLR here. Export once and verify with `rimsearcher.exe mods`.

### Pending environment steps (rimsearcher skill)
Workspace created at `Mods/.rimsearcher/` (CLI 3.2.0 + README, status Incomplete). Still needed from you: configure the DecompilerServer MCP (point it at `RimWorld.app/Contents/MacOS/Assembly-CSharp.dll`), confirm installing `RimSearcher_DataMod` into `Mods/`, export the Def database in-game, and copy `defs.db` beside the README.

---

## 6. Headless quicktest result

```
RimWorld.app/Contents/MacOS/RimWorld -Headless -quicktest -NoCrashDialog -savedatafolder=<tmp>
```

Core only, no window, no GPU. Timeline from the log: process start 15:48:03, play data loaded ~15:48:08, `Game.InitNewGame` ("Initializing new game with mods") 15:48:11, `Root_Play.Update` running by 15:48:14 (so world + map generation took about 3 s). The process then ran for ~50 s with **no errors and no exceptions** until the 60 s timeout sent SIGTERM, after which the engine shut down cleanly ("HeadlessShutdown" lifecycle). The only error in the whole run is the harmless `Resolution too small (0x0)`; the 200 warnings are "texture has no pixels" lines from the missing GPU.

Conclusion: headless map generation and the play loop work on this build. Whether `TicksGame` was advancing is `[UNVERIFIED]` from the log alone (quicktest should start unpaused, but nothing in vanilla logs ticks); the harness mod's `status` command is the first thing to confirm it. Treat headless gameplay tests as viable, with the UI/OnGUI limits from §1.

---

## 7. Scope mapping: pawns, buildings, items, AMC turrets, per-tick telemetry

Added 2026-10-16 after the follow-up question. Evidence: decompiled Assembly-CSharp, CombatExtended.dll (workshop 2890901044), AMC sources.

### Which approaches
- Everything in both scopes is reachable with **B (harness RPC) + D (debug-action bridge) + F (in-process test methods)**, all headless-capable. E (UI automation) is not needed for any of it; keep it only for visual checks of textures/animations.
- The per-tick monitoring needs one extra component, a **telemetry probe** inside the harness mod (not inside AMC), described below.

### Scope 1, vanilla
| Need | How (verified API) |
|---|---|
| Spawn colonist / enemy | `PawnGenerator.GeneratePawn(kindDef, faction)` + `GenSpawn.Spawn(pawn, cell, map)`. Enemy: a hostile faction from `Find.FactionManager` or `IncidentDefOf.RaidEnemy.Worker.TryExecute(parms)` for a real raid. Or `Dialog_Debug.GetNode(@"Pawns\Spawn pawn\...")`. |
| Draft / move / attack | `pawn.drafter.Drafted = true`; `pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, cell))`; `JobDefOf.AttackStatic` / `UseVerbOnThing` with a target. |
| Haul / operate | `JobDefOf.HaulToCell` / `HaulToContainer` via `HaulAIUtility`, `JobDefOf.ManTurret` for mannable turrets; for AMC's loader path use your own `JobDriver_LoadTurretFCS` JobDef. |
| Buildings | Instant: `GenSpawn.Spawn(ThingMaker.MakeThing(def, stuff), cell, map, rot)` (exactly what `CompTurretModeSwap` already does). Realistic: `GenConstruct.CanPlaceBlueprintAt` + `PlaceBlueprintForBuild`, then a builder pawn, to test cost/recipe/placement rules. |
| Items | `DebugThingPlaceHelper.DebugSpawn(def, cell, stackCount)` or `ThingMaker.MakeThing` + `stackCount` + `GenPlace.TryPlaceThing`. |
| Power | `DebugSettings.unlimitedPower = true` for most tests; a spawned battery with `CompPowerBattery.SetStoredEnergyPct(1f)` + conduit for power-path tests. |

### Scope 2, AMC / CE
| Need | How |
|---|---|
| Build turret | instant spawn as above; `Building_TurretGunCE` fields are public: `top.CurRotation`, `currentTargetInt`, `burstWarmupTicksLeft`, `burstCooldownTicksLeft`, `CompAmmo`, `CompFireModes`. |
| Ammo | `CompAmmoUser.ResetAmmoCount(ammoDef)` (fills the magazine), `LoadAmmo(thing)`, `TryStartReload()`, `TryUnload()`; read `CurMagCount`, `CurrentAmmo`, `MagsLeft`. |
| Fire / aim modes | `CompFireModes.CurrentFireMode` / `CurrentAimMode` setters, `AvailableFireModes`. |
| Mode swap | call the swap method of `CompTurretModeSwap` directly (it destroys + respawns `alternateDef`); verify by reading the thing at the cell afterwards. |
| Force fire | `turret.OrderAttack(LocalTargetInfo)`; or spawn a hostile pawn in range and tick. |
| Turret behaviour | sample per tick from a Harmony postfix on `Building_TurretGunCE.Tick`: rotation, target id, warmup/cooldown, mag count, power. Emit **edge-triggered** records only (rotation delta > ε, target change, burst begin/complete via your existing `BeginBurst`/`BurstComplete` patches). |
| Projectile behaviour | postfix `ProjectileCE.Tick` (`ExactPosition`, `Height`, `shotAngle`, `shotRotation`, `shotSpeed`, `FlightTicks`, `ticksToImpact`, `launcher`), `Launch` (already patched in AMC), `Impact(Thing hitThing)` postfix, `Destroy` prefix. Do **not** rely on `PostDraw`/`DrawPos` based tracers (`CompProjectileTracer`): draw callbacks do not run headless. |

### Handling the per-tick surge
Problem with the current AMC approach: `Postfix_ProjectileCE_Tick` writes through `Log.Message`. `Verse.Log` captures a stack trace per message, keeps messages in a UI queue, and **stops logging at 10,000 messages** (`StopLoggingAtMessageCount`). Fifty projectiles at 60 ticks/s exhaust that in about 3 seconds, and an LLM cannot read it anyway.

Design:
1. **Two channels.** *Events* (sparse: launch, impact, destroy, burst begin/complete, target change, reload, mode swap, errors) and *samples* (dense: one struct per projectile per tick, one per turret per tick). Events always go to the agent; samples never do directly.
2. **Zero-allocation capture.** Preallocated struct ring buffers (`tick, id, x, y, z, height, speed, angle, rotation`) filled in the Harmony postfix; a background thread drains them to disk. Target: capture cost under a few microseconds per entity per tick so it does not perturb the thing being measured.
3. **Storage = SQLite or NDJSON in the save-data folder**, keyed by run id. SQLite is preferred: RimSearcher's DataMod already proves `Microsoft.Data.Sqlite` + `libe_sqlite3.arm64.dylib` load inside a mod on this runtime, and the agent can then ask SQL questions (`SELECT max(height) ... GROUP BY proj_id`) through a CLI, exactly the rimsearcher pattern. Tables: `runs`, `events`, `proj_samples`, `turret_samples`.
4. **Server-side reduction for the agent.** Harness commands return summaries, not rows: `track_summary(projId)` (launch/impact cells, hit thing, max height, flight ticks, speed decay, miss distance from `intendedTarget`), `turret_timeline(id)` (state transitions only), `samples(projId, every=N, fields=[...])` with decimation and a hard row cap, `assert(expr)` evaluated in-game. Optional `screenshot_every(n)` windowed for a visual spot check.
5. **Pull, not push, and deterministic ticks.** Tests run `tick(n)` with the game paused and read results afterwards; capture is bounded by the test, not by wall-clock. Seed with `Rand.PushState(seed)`.
6. **Budget.** 50 projectiles × 60 ticks/s × ~40 bytes ≈ 120 KB/s: trivial for a file or SQLite, fatal for `Log` or an LLM context.

Where the probe lives: in the harness mod, patching CE types by name (as AMC's reflection-based patches already do) so AMC can ship without its logging patches, and so other CE mods can reuse it.
