using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LudeonTK;
using RimWorld;
using UnityEngine;
using UnityEngine.SceneManagement;
using Verse;
using Verse.AI;

namespace RimTestHarness
{
    /// <summary>A multi-frame job: handlers may return one to continue over several Root.Update frames.</summary>
    public sealed class Stepped
    {
        public Func<bool> Step;
        public Func<object> Result;
    }

    public static class Commands
    {
        public sealed class Def
        {
            public string Name;
            public string Help;
            public bool OffThread;
            public Func<Args, object> Handler;
        }

        static readonly SortedDictionary<string, Def> table = new SortedDictionary<string, Def>(StringComparer.OrdinalIgnoreCase);

        public static void Register(string name, string help, Func<Args, object> handler, bool offThread = false)
            => table[name] = new Def { Name = name, Help = help, Handler = handler, OffThread = offThread };

        public static object Describe() => table.Values.Select(d => new { d.Name, d.Help, mainThread = !d.OffThread }).ToList();

        public static Task<object> Execute(string cmd, JsonObject argsObj)
        {
            if (!table.TryGetValue(cmd, out var def))
                throw new CommandException($"unknown command '{cmd}'. Known: {string.Join(", ", table.Keys)}");
            var args = new Args(argsObj);
            if (def.OffThread)
            {
                return Task.FromResult(def.Handler(args));
            }
            return MainThread.Run(cmd, () => def.Handler(args)).ContinueWith(t =>
            {
                if (t.IsFaulted) throw t.Exception.InnerException ?? t.Exception;
                if (t.Result is Stepped st)
                {
                    return MainThread.RunStepped(cmd, st.Step, st.Result);
                }
                return Task.FromResult(t.Result);
            }).Unwrap();
        }

        // ------------------------------------------------------------------ helpers

        public static Map MapArg(Args a)
        {
            if (Current.Game == null) throw new CommandException("no game loaded (ProgramState=" + Current.ProgramState + ")");
            var id = a.IntOrNull("mapId");
            Map map = id.HasValue ? Find.Maps?.FirstOrDefault(m => m.uniqueID == id.Value) : Find.CurrentMap;
            if (map == null) throw new CommandException(id.HasValue ? $"no map with uniqueID {id}" : "no current map");
            return map;
        }

        public static IntVec3 CellArg(Args a, Map map, string xKey = "x", string zKey = "z")
        {
            var c = new IntVec3(a.ReqInt(xKey), 0, a.ReqInt(zKey));
            if (!c.InBounds(map)) throw new CommandException($"cell {c} is out of bounds for map {map.uniqueID} ({map.Size.x}x{map.Size.z})");
            return c;
        }

        public static Rot4 RotArg(Args a, string key = "rot")
        {
            var s = a.Str(key);
            if (s == null) return Rot4.North;
            switch (s.ToLowerInvariant())
            {
                case "north": case "n": case "0": return Rot4.North;
                case "east": case "e": case "1": return Rot4.East;
                case "south": case "s": case "2": return Rot4.South;
                case "west": case "w": case "3": return Rot4.West;
            }
            throw new CommandException("rot must be 0-3 or north/east/south/west");
        }

        public static Thing FindThing(int id)
        {
            var maps = Find.Maps;
            if (maps == null) return null;
            foreach (var map in maps)
            {
                var all = map.listerThings.AllThings;
                for (int i = 0; i < all.Count; i++) if (all[i].thingIDNumber == id) return all[i];
            }
            return null;
        }

        public static Thing ReqThing(Args a, string key = "id")
        {
            var id = a.ReqInt(key);
            return FindThing(id) ?? throw new CommandException($"no spawned thing with id {id}");
        }

        public static Pawn ReqPawn(Args a, string key = "pawnId")
        {
            var id = a.ReqInt(key);
            return FindThing(id) as Pawn ?? throw new CommandException($"no spawned pawn with id {id}");
        }

        public static Faction FactionArg(Args a, string key, Faction def)
        {
            var s = a.Str(key);
            if (s == null) return def;
            switch (s.ToLowerInvariant())
            {
                case "player": return Faction.OfPlayer;
                case "none": case "null": return null;
                case "hostile": case "enemy":
                    return Find.FactionManager.RandomEnemyFaction(allowHidden: false, allowDefeated: false, allowNonHumanlike: false)
                        ?? Find.FactionManager.RandomEnemyFaction()
                        ?? throw new CommandException("no hostile faction in this world");
            }
            var fdef = DefDatabase<FactionDef>.GetNamedSilentFail(s);
            if (fdef != null)
            {
                var f = Find.FactionManager.FirstFactionOfDef(fdef);
                if (f != null) return f;
            }
            var byName = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f.Name == s || f.def.defName == s);
            return byName ?? throw new CommandException($"faction '{s}' not found (use player|hostile|none|<FactionDef defName>|<faction name>)");
        }

        public static object Cell(IntVec3 c) => new { x = c.x, z = c.z };

        public static object TargetSummary(LocalTargetInfo t)
        {
            if (!t.IsValid) return null;
            if (t.HasThing) return new { id = t.Thing.thingIDNumber, def = t.Thing.def?.defName, label = t.Thing.LabelCap.ToString().StripTags(), x = t.Cell.x, z = t.Cell.z };
            return new { id = -1, def = (string)null, label = (string)null, x = t.Cell.x, z = t.Cell.z };
        }

        public static Dictionary<string, object> ThingSummary(Thing t, bool deep = false)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = t.thingIDNumber,
                ["def"] = t.def?.defName,
                ["class"] = t.GetType().FullName,
                ["label"] = t.LabelCap.ToString().StripTags(),
                ["x"] = t.Position.x,
                ["z"] = t.Position.z,
                ["rot"] = t.Rotation.AsInt,
                ["spawned"] = t.Spawned,
                ["destroyed"] = t.Destroyed,
                ["mapId"] = t.Map?.uniqueID ?? -1,
                ["faction"] = t.Faction?.Name,
                ["stackCount"] = t.stackCount,
            };
            if (t.def != null && t.def.useHitPoints) { d["hp"] = t.HitPoints; d["maxHp"] = t.MaxHitPoints; }
            if (t.Stuff != null) d["stuff"] = t.Stuff.defName;
            if (t is ThingWithComps twc && twc.AllComps != null && twc.AllComps.Count > 0)
                d["comps"] = twc.AllComps.Select(c => c.GetType().Name).ToList();
            if (t is Pawn p)
            {
                d["pawn"] = new
                {
                    kind = p.kindDef?.defName,
                    name = p.Name?.ToStringFull,
                    dead = p.Dead,
                    downed = p.Downed,
                    drafted = p.drafter?.Drafted ?? false,
                    curJob = p.jobs?.curJob?.def?.defName,
                    curJobTarget = p.jobs?.curJob != null ? TargetSummary(p.jobs.curJob.targetA) : null,
                    health = p.health?.summaryHealth?.SummaryHealthPercent,
                    hostileToPlayer = p.HostileTo(Faction.OfPlayer),
                };
            }
            if (t is Building_Turret bt)
            {
                var tur = new Dictionary<string, object>
                {
                    ["currentTarget"] = TargetSummary(bt.CurrentTarget),
                    ["forcedTarget"] = TargetSummary(bt.ForcedTarget),
                };
                if (t is Building_TurretGun bg)
                {
                    tur["topRotation"] = Reflect.GetterOrNull<TurretTop>(bg.GetType(), "top")?.Invoke(bg)?.CurRotation;
                    tur["gun"] = bg.gun?.def?.defName;
                    tur["verb"] = bg.AttackVerb?.GetType().Name;
                    tur["warmupTicksLeft"] = Reflect.GetterOrNull<int>(bg.GetType(), "burstWarmupTicksLeft")?.Invoke(bg);
                    tur["cooldownTicksLeft"] = Reflect.GetterOrNull<int>(bg.GetType(), "burstCooldownTicksLeft")?.Invoke(bg);
                }
                try { TurretCommands.AddCeInfo(bt, tur); } catch (Exception ex) { tur["ceError"] = ex.Message; }
                var power = t.TryGetComp<CompPowerTrader>();
                if (power != null) tur["powerOn"] = power.PowerOn;
                d["turret"] = tur;
            }
            if (deep && t is ThingWithComps twc2)
            {
                var comps = new Dictionary<string, object>();
                foreach (var c in twc2.AllComps)
                {
                    try { var s = c.CompInspectStringExtra(); if (!string.IsNullOrEmpty(s)) comps[c.GetType().Name] = s; } catch { }
                }
                d["compInspect"] = comps;
                try { d["inspect"] = t.GetInspectString(); } catch { }
            }
            return d;
        }

        // ------------------------------------------------------------------ registration

        public static void RegisterAll()
        {
            // -------- lifecycle
            Register("status", "Game/engine state summary.", a =>
            {
                var game = Current.Game;
                var tm = game?.tickManager;
                Map map = game?.CurrentMap;
                return new
                {
                    programState = Current.ProgramState.ToString(),
                    scene = SceneManager.GetActiveScene().name,
                    anyLongEvent = LongEventHandler.AnyEventNowOrWaiting,
                    longEventText = LongEventHandler.AnyEventNowOrWaiting ? SafeLongEventText() : null,
                    playDataLoaded = PlayDataLoader.Loaded,
                    ticksGame = tm?.TicksGame ?? -1,
                    paused = tm?.Paused,
                    timeSpeed = tm?.CurTimeSpeed.ToString(),
                    // TickManager.Paused is also true while any open window has forcePause (or a long event forces it).
                    windowsForcePause = SafeWindowsForcePause(),
                    windows = SafeWindowTypes(),
                    mapId = map?.uniqueID ?? -1,
                    mapCount = game?.Maps?.Count ?? 0,
                    mapSize = map != null ? new { x = map.Size.x, z = map.Size.z } : null,
                    errors = LogCapture.Errors,
                    warnings = LogCapture.Warnings,
                    logCount = LogCapture.Next,
                    frame = MainThread.FrameCount,
                    headless = GenCommandLine.CommandLineArgPassed("Headless"),
                    devMode = Prefs.DevMode,
                    gameVersion = VersionControl.CurrentVersionStringWithRev,
                    saveDataFolder = GenFilePaths.SaveDataFolderPath,
                    logPath = SafeLogPath(),
                    port = HarnessMod.Port,
                    ceProbe = Telemetry.CeProbe.Available,
                    ceProbeLog = Telemetry.CeProbe.InstallLog,
                    headlessLongEvents = new { enabled = HeadlessLongEvents.Enabled, released = HeadlessLongEvents.Released, last = HeadlessLongEvents.LastReleased },
                    ceBoundsShim = new { enabled = Telemetry.CeHeadlessShim.Enabled, shimmedCalls = Telemetry.CeHeadlessShim.ShimmedCalls, log = Telemetry.CeHeadlessShim.Log },
                    telemetry = Telemetry.TelemetryStore.StatusSummary(),
                    activeMods = LoadedModManager.RunningModsListForReading.Select(m => m.PackageId).ToList(),
                };
            });

            Register("log", "Log entries since cursor. args: since (seq), level (message|warning|error), limit (200), stack (false), contains", a =>
                LogCapture.Query(a.Long("since", 0), LevelArg(a.Str("level", "message")), a.Int("limit", 200), a.Bool("stack", false), a.Str("contains")), offThread: true);

            Register("save_game", "Save the current game to <savedatafolder>/Saves/<name>.rws (GameDataSaveLoader.SaveGame). args: name", a =>
            {
                ReqTick();
                if (LongEventHandler.AnyEventNowOrWaiting) throw new CommandException("a long event is running; wait for status.anyLongEvent=false");
                var name = a.Req("name");
                foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) if (name.IndexOf(ch) >= 0) throw new CommandException("invalid character in save name: " + ch);
                var file = GenFilePaths.FilePathForSavedGame(name);
                long errorsBefore = LogCapture.Errors;
                // SaveGame catches its own exceptions and only logs them, so check the error count and the file.
                GameDataSaveLoader.SaveGame(name);
                var fi = new System.IO.FileInfo(file);
                if (!fi.Exists) throw new CommandException("save did not produce " + file + "; see log level=error");
                return new { name, file, bytes = fi.Length, ticksGame = Find.TickManager.TicksGame, newErrors = LogCapture.Errors - errorsBefore };
            });

            Register("load_save", "Load <savedatafolder>/Saves/<name>.rws. args: name. Poll status until programState=Playing.", a =>
            {
                var name = a.Req("name");
                var file = GenFilePaths.FilePathForSavedGame(name);
                if (!System.IO.File.Exists(file))
                {
                    var dir = System.IO.Path.GetDirectoryName(file);
                    var have = System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, "*.rws").Select(System.IO.Path.GetFileNameWithoutExtension) : Enumerable.Empty<string>();
                    throw new CommandException($"save not found: {file}. Saves here: {string.Join(", ", have)}");
                }
                GameDataSaveLoader.LoadGame(name);
                return new { queued = true, file };
            });

            Register("new_quicktest_game", "Generate a fresh quicktest game (Root_Play.SetupForQuickTestPlay path). Poll status until Playing.", a =>
            {
                if (!PlayDataLoader.Loaded) throw new CommandException("play data not loaded yet");
                LongEventHandler.ClearQueuedEvents();
                Current.Game?.Dispose();
                // Mirrors GameDataSaveLoader.LoadGame: pre-load action, then load the Play scene. Root_Play.Start sees
                // Current.Game == null and calls SetupForQuickTestPlay() + InitNewGame() (decompiled Root_Play.cs:35-45).
                // ProgramState=Entry before the scene rebuild: otherwise the new CameraDriver.Awake -> ResetSize -> RootSize setter
                // sees ProgramState.Playing with no long event pending and takes the zoom-to-mouse path before the Camera
                // component exists (NullReferenceException in ApplyPositionToGameObject, observed on every Play->Play rebuild
                // through this command). SetupForQuickTestPlay sets Entry itself, just later (decompiled Root_Play.cs:87).
                LongEventHandler.QueueLongEvent(() => { Verse.Profile.MemoryUtility.ClearAllMapsAndWorld(); Current.Game = null; Current.ProgramState = ProgramState.Entry; }, "Play", "GeneratingMap", doAsynchronously: true, null);
                return new { queued = true };
            });

            Register("main_menu", "Return to the main menu (GenScene.GoToMainMenu).", a => { GenScene.GoToMainMenu(); return new { queued = true }; });

            Register("quit", "Shut the game down. args: exitCode (0). The response is sent before the process exits.", a =>
            {
                int code = a.Int("exitCode", 0);
                MainThread.RunLater(400, () =>
                {
                    try { Telemetry.TelemetryStore.Instance?.Stop(); } catch { }
                    try { Verse.Steam.SteamManager.ShutdownSteam(); } catch (Exception ex) { Log.Error("ShutdownSteam: " + ex); }
                    // UnityStubs Application.Quit(int) stores the code in UnityRuntime.App.ExitCode, which the host's quit
                    // handler passes to EngineApplication.Quit (decompiled Bootstrap.cs:107).
                    Application.Quit(code);
                });
                return new { quitting = true, exitCode = code };
            });

            // -------- time
            Register("pause", "Pause the game.", a => { ReqTick().Pause(); return new { paused = ReqTick().Paused }; });

            Register("set_speed", "Set time speed. args: speed (0-4 or Paused|Normal|Fast|Superfast|Ultrafast)", a =>
            {
                var tm = ReqTick();
                var s = a.Req("speed");
                TimeSpeed ts = int.TryParse(s, out var i) ? (TimeSpeed)i : Enum.Parse<TimeSpeed>(s, true);
                tm.CurTimeSpeed = ts;
                return new { timeSpeed = tm.CurTimeSpeed.ToString(), paused = tm.Paused };
            });

            Register("tick", "Advance exactly n ticks with the game paused (TickManager.DoSingleTick). args: n (1), budgetMs per frame (250)", a =>
            {
                var tm = ReqTick();
                if (LongEventHandler.AnyEventNowOrWaiting) throw new CommandException("a long event is running; wait for status.anyLongEvent=false");
                int n = a.Int("n", 1);
                if (n < 0 || n > 1_000_000) throw new CommandException("n must be 0..1000000");
                int budget = a.Int("budgetMs", 250);
                tm.Pause();
                int done = 0;
                int startTick = tm.TicksGame;
                var sw = Stopwatch.StartNew();
                int frames = 0;
                return new Stepped
                {
                    Step = () =>
                    {
                        frames++;
                        var frameStart = sw.ElapsedMilliseconds;
                        while (done < n)
                        {
                            Find.TickManager.DoSingleTick();
                            done++;
                            if (sw.ElapsedMilliseconds - frameStart > budget) break;
                        }
                        return done >= n;
                    },
                    Result = () => new { ticked = done, ticksGame = Find.TickManager.TicksGame, startTick, elapsedMs = sw.ElapsedMilliseconds, frames, paused = Find.TickManager.Paused },
                };
            });

            // -------- debug tools
            Register("dev_mode", "Set Prefs.DevMode. args: on (true)", a => { Prefs.DevMode = a.Bool("on", true); return new { devMode = Prefs.DevMode }; });

            Register("set_debug", "Set a DebugSettings static bool. args: field, value (true). e.g. unlimitedPower, godMode, enableStoryteller", a =>
            {
                var name = a.Req("field");
                var f = typeof(DebugSettings).GetField(name, BindingFlags.Public | BindingFlags.Static);
                if (f == null || f.FieldType != typeof(bool))
                    throw new CommandException($"DebugSettings has no public static bool '{name}'. Available: {string.Join(", ", typeof(DebugSettings).GetFields(BindingFlags.Public | BindingFlags.Static).Where(x => x.FieldType == typeof(bool)).Select(x => x.Name))}");
                f.SetValue(null, a.Bool("value", true));
                return new { field = name, value = (bool)f.GetValue(null) };
            });

            Register("list_debug_actions", "List debug-action nodes. args: path (backslash-separated, default root), depth (1), limit (500)", a =>
            {
                var path = a.Str("path");
                DebugActionNode node;
                if (string.IsNullOrEmpty(path)) { Dialog_Debug.TrySetupNodeGraph(); node = Dialog_Debug.rootNode; }
                else node = Dialog_Debug.GetNode(path) ?? throw new CommandException("no node at path: " + path);
                node.TrySetupChildren();
                int depth = a.Int("depth", 1), limit = a.Int("limit", 500);
                var list = new List<object>();
                void Walk(DebugActionNode n, int d)
                {
                    if (list.Count >= limit) return;
                    foreach (var c in n.children)
                    {
                        if (list.Count >= limit) return;
                        if (d > 1) c.TrySetupChildren();
                        list.Add(new { label = c.label, path = c.Path, type = c.actionType.ToString(), category = c.category, hasChildren = c.children.Count > 0 || d <= 1 && HasChildGetter(c), hasAction = c.action != null || c.pawnAction != null });
                        if (d > 1 && c.children.Count > 0) Walk(c, d - 1);
                    }
                }
                Walk(node, depth);
                return new { path = node.Path, count = list.Count, nodes = list };
            });

            Register("debug_action", "Invoke a debug action by path (Dialog_Debug.GetNode). args: path, pawnId (for ToolMapForPawns)", a =>
            {
                var path = a.Req("path");
                var node = Dialog_Debug.GetNode(path) ?? throw new CommandException("no debug action at path: " + path + ". Use list_debug_actions to browse.");
                var windowsBefore = Find.WindowStack.Windows.ToList();
                long before = LogCapture.Next;
                if (node.actionType == DebugActionType.ToolMapForPawns && node.pawnAction != null)
                {
                    var pawn = ReqPawn(a);
                    node.pawnAction(pawn);
                    return new { invoked = node.Path, type = node.actionType.ToString(), pawn = pawn.thingIDNumber, newLogEntries = LogCapture.Next - before, openedWindows = OpenedWindows(windowsBefore) };
                }
                if (node.action == null) throw new CommandException($"node '{node.Path}' has no direct action (type {node.actionType}, {node.children.Count} children)");
                if (node.actionType != DebugActionType.Action)
                    throw new CommandException($"node '{node.Path}' is a {node.actionType} tool that reads the mouse cell; the harness cannot invoke it headless");
                node.action();
                // Many "..." actions only open a menu (Dialog_DebugOptionListLister / FloatMenu). Headless nobody can click
                // it, and a forcePause window freezes the game, so report it: use choose_option or windows close=all.
                return new { invoked = node.Path, type = node.actionType.ToString(), newLogEntries = LogCapture.Next - before, openedWindows = OpenedWindows(windowsBefore) };
            });

            Register("windows", "List open UI windows (menus opened by debug actions stay open headless; forcePause ones freeze time). args: close (all | index | type name)", a =>
            {
                var ws = Find.WindowStack;
                var close = a.Str("close");
                var closed = new List<string>();
                if (close != null)
                {
                    var list = ws.Windows.ToList();
                    IEnumerable<Window> targets;
                    if (close.Equals("all", StringComparison.OrdinalIgnoreCase)) targets = list.Where(IsClosableWindow);
                    else if (int.TryParse(close, out var idx))
                    {
                        if (idx < 0 || idx >= list.Count) throw new CommandException($"window index {idx} out of range (0..{list.Count - 1})");
                        targets = new[] { list[idx] };
                    }
                    else targets = list.Where(w => w.GetType().Name.Equals(close, StringComparison.OrdinalIgnoreCase) || w.GetType().FullName == close);
                    foreach (var w in targets.ToList()) { if (ws.TryRemove(w, doCloseSound: false)) closed.Add(w.GetType().Name); }
                    if (closed.Count == 0 && !close.Equals("all", StringComparison.OrdinalIgnoreCase)) throw new CommandException("no open window matches '" + close + "'");
                }
                return new { closed, windowsForcePause = ws.WindowsForcePause, windows = WindowList() };
            });

            Register("choose_option", "Pick an option in an open menu window (FloatMenu or debug option list), as a click would. args: label (exact, else unique case-insensitive substring) | index, window (index in windows; default topmost menu)", a =>
            {
                var list = Find.WindowStack.Windows.ToList();
                Window w;
                if (a.Has("window"))
                {
                    int wi = a.Int("window", -1);
                    if (wi < 0 || wi >= list.Count) throw new CommandException($"window index {wi} out of range (0..{list.Count - 1})");
                    w = list[wi];
                }
                else w = list.LastOrDefault(x => x is FloatMenu || x is Dialog_DebugOptionListLister) ?? throw new CommandException("no menu window is open");
                var opts = MenuOptions(w) ?? throw new CommandException($"window {w.GetType().Name} is not a menu with options");
                int index;
                if (a.Has("index")) index = a.Int("index", -1);
                else
                {
                    var label = a.Req("label");
                    index = opts.FindIndex(o => o.label == label);
                    if (index < 0)
                    {
                        var hits = opts.Select((o, i) => (o, i)).Where(p => p.o.label != null && p.o.label.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        if (hits.Count == 1) index = hits[0].i;
                        else throw new CommandException(hits.Count == 0 ? $"no option '{label}'. Options: {string.Join(" | ", opts.Select(o => o.label))}" : $"'{label}' matches {hits.Count} options: {string.Join(" | ", hits.Select(h => h.o.label))}");
                    }
                }
                if (index < 0 || index >= opts.Count) throw new CommandException($"option index {index} out of range (0..{opts.Count - 1})");
                var chosen = opts[index];
                if (chosen.disabled) throw new CommandException($"option '{chosen.label}' is disabled");
                if (chosen.tool) throw new CommandException($"option '{chosen.label}' is a map tool that reads the mouse cell; the harness cannot invoke it headless");
                var before = Find.WindowStack.Windows.ToList();
                long logBefore = LogCapture.Next;
                // Same order as a click: Dialog_DebugOptionLister.DebugAction closes then runs the action; FloatMenu removes itself after Chosen.
                Find.WindowStack.TryRemove(w, doCloseSound: false);
                chosen.invoke();
                return new { chosen = chosen.label, index, window = w.GetType().Name, newLogEntries = LogCapture.Next - logBefore, openedWindows = OpenedWindows(before) };
            });

            // -------- spawning
            Register("spawn_thing", "Spawn a thing. args: defName, x, z, stuff, count (1), rot (0), faction (player for buildings)", a =>
            {
                var map = MapArg(a);
                var defName = a.Req("defName");
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName) ?? throw new CommandException("no ThingDef " + defName);
                var cell = CellArg(a, map);
                var stuff = StuffArg(a, def);
                var thing = ThingMaker.MakeThing(def, stuff);
                int count = a.Int("count", 1);
                if (count > 1) thing.stackCount = Math.Min(count, def.stackLimit);
                if (def.category == ThingCategory.Building || def.CanHaveFaction)
                {
                    var fac = FactionArg(a, "faction", def.category == ThingCategory.Building ? Faction.OfPlayer : null);
                    if (fac != null) thing.SetFactionDirect(fac);
                }
                var rot = RotArg(a);
                var spawned = GenSpawn.Spawn(thing, cell, map, rot, WipeMode.Vanish);
                // Unconnected CompPowerTraders are never ticked by a PowerNet, so DebugSettings.unlimitedPower alone leaves
                // them off (PowerNet.cs:183 only powers traders inside a net). Force PowerOn when requested or when unlimitedPower is set.
                if (a.Bool("power", DebugSettings.unlimitedPower)) ForcePower(spawned, true);
                var summary = ThingSummary(spawned);
                if (count > def.stackLimit) { summary["requestedCount"] = count; summary["note"] = $"count clamped to stackLimit {def.stackLimit} (one stack per call)"; }
                return summary;
            });

            Register("power", "Force a CompPowerTrader on/off (for unconnected test buildings). args: id, on (true)", a =>
            {
                var t = ReqThing(a);
                var comp = ForcePower(t, a.Bool("on", true)) ?? throw new CommandException("thing has no CompPowerTrader");
                return new { id = t.thingIDNumber, powerOn = comp.PowerOn, connectedToNet = comp.PowerNet != null };
            });

            Register("spawn_pawn", "Generate and spawn a pawn. args: kindDef (Colonist), faction (player|hostile|none|defName), x, z", a =>
            {
                var map = MapArg(a);
                var kindName = a.Str("kindDef", "Colonist");
                var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(kindName) ?? throw new CommandException("no PawnKindDef " + kindName);
                var faction = FactionArg(a, "faction", kind == PawnKindDefOf.Colonist ? Faction.OfPlayer : null);
                var cell = CellArg(a, map);
                if (a.Bool("nearest", true) && !cell.Standable(map)) cell = CellFinder.StandableCellNear(cell, map, a.Float("radius", 12f));
                if (!cell.IsValid) throw new CommandException("no standable cell near the requested position");
                var pawn = PawnGenerator.GeneratePawn(kind, faction);
                GenSpawn.Spawn(pawn, cell, map, WipeMode.Vanish);
                return ThingSummary(pawn);
            });

            Register("draft", "Draft/undraft a pawn. args: pawnId, on (true)", a =>
            {
                var pawn = ReqPawn(a);
                if (pawn.drafter == null) throw new CommandException("pawn has no drafter (not a player-controlled humanlike)");
                pawn.drafter.Drafted = a.Bool("on", true);
                return new { pawnId = pawn.thingIDNumber, drafted = pawn.drafter.Drafted };
            });

            Register("order_job", "Give a pawn an ordered job. args: pawnId, jobDef (Goto), targetId | targetX+targetZ, count", a =>
            {
                var pawn = ReqPawn(a);
                var jobName = a.Str("jobDef", "Goto");
                var jobDef = DefDatabase<JobDef>.GetNamedSilentFail(jobName) ?? throw new CommandException("no JobDef " + jobName);
                LocalTargetInfo target;
                if (a.Has("targetId")) target = new LocalTargetInfo(ReqThing(a, "targetId"));
                else target = new LocalTargetInfo(CellArg(a, pawn.Map, "targetX", "targetZ"));
                bool reachable = pawn.Map.reachability.CanReach(pawn.Position, target, PathEndMode.OnCell, TraverseParms.For(pawn));
                var job = JobMaker.MakeJob(jobDef, target);
                if (a.Has("count")) job.count = a.Int("count", -1);
                bool ok = pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                return new { accepted = ok, reachable, standable = target.Cell.Standable(pawn.Map), curJob = pawn.jobs.curJob?.def?.defName, target = TargetSummary(target) };
            });

            Register("place_blueprint", "Place a build blueprint. args: defName, x, z, rot, stuff", a =>
            {
                var map = MapArg(a);
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(a.Req("defName")) ?? throw new CommandException("no ThingDef " + a.Str("defName"));
                var cell = CellArg(a, map);
                var rot = RotArg(a);
                var stuff = StuffArg(a, def);
                var report = GenConstruct.CanPlaceBlueprintAt(def, cell, rot, map, godMode: false, null, null, stuff);
                if (!report.Accepted) throw new CommandException("cannot place: " + report.Reason);
                var bp = GenConstruct.PlaceBlueprintForBuild(def, cell, map, rot, Faction.OfPlayer, stuff);
                return ThingSummary(bp);
            });

            // -------- queries
            Register("query_things", "Find things. args: id | defName | class (type name substring), mapId, limit (50), deep (false)", a =>
            {
                int limit = a.Int("limit", 50);
                bool deep = a.Bool("deep", false);
                if (a.Has("id"))
                {
                    var t = FindThing(a.Int("id", -1));
                    return t == null ? new { count = 0, things = new List<object>() } : new { count = 1, things = new List<object> { ThingSummary(t, deep) } };
                }
                var map = MapArg(a);
                IEnumerable<Thing> src;
                if (a.Has("defName"))
                {
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(a.Str("defName")) ?? throw new CommandException("no ThingDef " + a.Str("defName"));
                    src = map.listerThings.ThingsOfDef(def);
                }
                else if (a.Has("class"))
                {
                    var cls = a.Str("class");
                    src = map.listerThings.AllThings.Where(t => t.GetType().FullName.IndexOf(cls, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                else throw new CommandException("give id, defName or class");
                var list = src.Take(limit).Select(t => (object)ThingSummary(t, deep)).ToList();
                return new { count = list.Count, things = list };
            });

            Register("cell_info", "Describe a map cell. args: x, z, mapId", a =>
            {
                var map = MapArg(a);
                var c = CellArg(a, map);
                return new
                {
                    x = c.x, z = c.z,
                    terrain = c.GetTerrain(map)?.defName,
                    standable = c.Standable(map),
                    walkable = c.Walkable(map),
                    roofed = c.Roofed(map),
                    roof = c.GetRoof(map)?.defName,
                    fogged = c.Fogged(map),
                    inHomeArea = map.areaManager.Home[c],
                    things = c.GetThingList(map).Select(t => new { id = t.thingIDNumber, def = t.def.defName, cls = t.GetType().Name }).ToList(),
                };
            });

            Register("find_cell", "Nearest standable, unroofed-or-roofed cell to x,z. args: x, z, radius (20), clearRadius (0: also require no buildings within r), mapId", a =>
            {
                var map = MapArg(a);
                var c = CellArg(a, map);
                float radius = a.Float("radius", 20f);
                int clear = a.Int("clearRadius", 0);
                IntVec3 best = IntVec3.Invalid; float bestD = float.MaxValue;
                foreach (var cell in GenRadial.RadialCellsAround(c, radius, true))
                {
                    if (!cell.InBounds(map) || !cell.Standable(map) || cell.Fogged(map)) continue;
                    if (clear > 0)
                    {
                        bool blocked = false;
                        foreach (var n in GenRadial.RadialCellsAround(cell, clear, true))
                        {
                            if (!n.InBounds(map) || !n.Standable(map) || n.GetEdifice(map) != null) { blocked = true; break; }
                        }
                        if (blocked) continue;
                    }
                    float d = (cell - c).LengthHorizontalSquared;
                    if (d < bestD) { bestD = d; best = cell; }
                }
                if (!best.IsValid) throw new CommandException("no matching cell within radius");
                return new { x = best.x, z = best.z, distance = Math.Sqrt(bestD), terrain = best.GetTerrain(map)?.defName };
            });

            Register("defs", "List def names of a Def type. args: type (ThingDef), contains, limit (200)", a =>
            {
                var typeName = a.Str("type", "ThingDef");
                var type = GenTypes.GetTypeInAnyAssembly(typeName) ?? GenTypes.GetTypeInAnyAssembly("Verse." + typeName) ?? GenTypes.GetTypeInAnyAssembly("RimWorld." + typeName)
                    ?? throw new CommandException("no Def type " + typeName);
                var dbType = typeof(DefDatabase<>).MakeGenericType(type);
                var all = (System.Collections.IEnumerable)dbType.GetProperty("AllDefs").GetValue(null);
                var contains = a.Str("contains");
                int limit = a.Int("limit", 200);
                var list = new List<object>();
                foreach (Verse.Def d in all)
                {
                    if (contains != null && d.defName.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0 && (d.label == null || d.label.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                    list.Add(new { d.defName, d.label, mod = d.modContentPack?.PackageId });
                    if (list.Count >= limit) break;
                }
                return new { type = type.FullName, count = list.Count, defs = list };
            });

            Register("screenshot", "Capture the window to a PNG (windowed runs only; headless has no framebuffer). args: path", a =>
            {
                var path = a.Req("path");
                if (GenCommandLine.CommandLineArgPassed("Headless")) throw new CommandException("running headless: no framebuffer to capture");
                ScreenCapture.CaptureScreenshot(path);
                return new { requested = path, note = "written asynchronously at end of frame" };
            });

            TurretCommands.RegisterAll();
            Telemetry.TelemetryCommands.RegisterAll();
        }

        /// <summary>Stuff for a def: the given defName (must exist and be allowed for the def), else the default stuff; null for non-stuff defs.</summary>
        static ThingDef StuffArg(Args a, ThingDef def)
        {
            var name = a.Str("stuff");
            if (!def.MadeFromStuff)
            {
                if (name != null) throw new CommandException($"{def.defName} is not made from stuff; omit 'stuff'");
                return null;
            }
            if (name == null) return GenStuff.DefaultStuffFor(def);
            var stuff = DefDatabase<ThingDef>.GetNamedSilentFail(name) ?? throw new CommandException("no stuff def " + name);
            if (!GenStuff.AllowedStuffsFor(def).Contains(stuff))
                throw new CommandException($"{name} is not an allowed stuff for {def.defName}. Allowed: {string.Join(", ", GenStuff.AllowedStuffsFor(def).Select(s => s.defName))}");
            return stuff;
        }

        static CompPowerTrader ForcePower(Thing t, bool on)
        {
            var comp = t.TryGetComp<CompPowerTrader>();
            if (comp == null) return null;
            comp.PowerOn = on;
            return comp;
        }

        static TickManager ReqTick()
        {
            if (Current.Game == null || Current.ProgramState != ProgramState.Playing) throw new CommandException("not in a playing game (ProgramState=" + Current.ProgramState + ")");
            return Find.TickManager;
        }

        // ------------------------------------------------------------------ windows

        sealed class MenuOpt { public string label; public bool disabled; public bool tool; public Action invoke; }

        static List<MenuOpt> MenuOptions(Window w)
        {
            if (w is FloatMenu fm)
            {
                var opts = HarmonyLib.Traverse.Create(fm).Field("options").GetValue<List<FloatMenuOption>>();
                return opts?.Select(o => new MenuOpt { label = o.Label?.StripTags(), disabled = o.Disabled, invoke = () => o.Chosen(false, null) }).ToList();
            }
            if (w is Dialog_DebugOptionListLister dl)
            {
                var opts = HarmonyLib.Traverse.Create(dl).Field("options").GetValue<List<DebugMenuOption>>();
                return opts?.Select(o => new MenuOpt { label = o.label, tool = o.mode == DebugMenuOptionMode.Tool, invoke = o.method }).ToList();
            }
            return null;
        }

        // The always-present windows (e.g. the play scene's own UI) are not closed by close=all.
        static bool IsClosableWindow(Window w) => w is FloatMenu || w is Dialog_DebugOptionLister || w.forcePause || w.absorbInputAroundWindow || w.closeOnClickedOutside;

        static object WindowSummary(Window w, int index)
        {
            var opts = MenuOptions(w);
            return new
            {
                index,
                type = w.GetType().FullName,
                forcePause = w.forcePause,
                optionCount = opts?.Count,
                options = opts?.Take(100).Select(o => o.tool ? o.label + " [tool]" : o.disabled ? o.label + " [disabled]" : o.label).ToList(),
            };
        }

        static List<object> WindowList()
        {
            var ws = Find.WindowStack?.Windows;
            if (ws == null) return new List<object>();
            return ws.Select((w, i) => WindowSummary(w, i)).ToList();
        }

        static List<object> OpenedWindows(List<Window> before)
        {
            var now = Find.WindowStack.Windows;
            var list = new List<object>();
            for (int i = 0; i < now.Count; i++) if (!before.Contains(now[i])) list.Add(WindowSummary(now[i], i));
            return list;
        }

        static int LevelArg(string level) => level?.ToLowerInvariant() switch { "warning" => 1, "error" => 2, _ => 0 };

        static bool HasChildGetter(DebugActionNode n)
        {
            try { return AccessToolsChildGetter?.GetValue(n) != null; } catch { return false; }
        }
        static readonly FieldInfo AccessToolsChildGetter = typeof(DebugActionNode).GetField("childGetter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        static string SafeLongEventText() { try { var ev = HarmonyLib.AccessTools.Field(typeof(LongEventHandler), "currentEvent")?.GetValue(null); return ev == null ? null : HarmonyLib.Traverse.Create(ev).Field("eventTextKey").GetValue<string>(); } catch { return null; } }
        static bool? SafeWindowsForcePause() { try { return Find.WindowStack?.WindowsForcePause; } catch { return null; } }
        static List<string> SafeWindowTypes() { try { return Find.WindowStack?.Windows.Select(w => w.GetType().Name).ToList(); } catch { return null; } }
        static string SafeLogPath() { try { return Application.consoleLogPath; } catch { return null; } }
    }
}
