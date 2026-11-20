using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Verse;
using RimWorld;

namespace RimTestHarness.Telemetry
{
    public static class TelemetryCommands
    {
        static TelemetryStore ReqStore()
            => TelemetryStore.Instance ?? throw new CommandException("no telemetry run open; call telemetry_start first (the last run's db stays on disk for the runner's sql command)");

        static string TelemetryDir => Path.Combine(GenFilePaths.SaveDataFolderPath, "telemetry");

        public static void RegisterAll()
        {
            Commands.Register("telemetry_start", "Open <savedatafolder>/telemetry/<runId>.db and start capturing. args: runId (auto), note", a =>
            {
                if (TelemetryStore.Instance != null) throw new CommandException("telemetry run '" + TelemetryStore.Instance.RunId + "' is already open; call telemetry_stop first");
                var runId = a.Str("runId") ?? DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                foreach (var ch in Path.GetInvalidFileNameChars()) runId = runId.Replace(ch, '_');
                int tick = Current.Game?.tickManager?.TicksGame ?? -1;
                var meta = "note=" + (a.Str("note") ?? "") + "|version=" + VersionControl.CurrentVersionStringWithRev + "|mods=" + string.Join(",", LoadedModManager.RunningModsListForReading.Select(m => m.PackageId)) + "|ceProbe=" + CeProbe.Available;
                var store = new TelemetryStore(runId, TelemetryDir, tick, meta);
                CeProbe.ResetPerRunState();
                TelemetryStore.Instance = store;
                TelemetryStore.Active = true;
                return new { runId, db = store.DbPath, startTick = tick, ceProbe = CeProbe.Available, probeLog = CeProbe.InstallLog, sqlite = SqliteNative.LoadedPath };
            });

            Commands.Register("telemetry_stop", "Stop capturing, flush and close the db.", a =>
            {
                var store = ReqStore();
                int tick = Current.Game?.tickManager?.TicksGame ?? -1;
                store.Stop(tick);
                return new { runId = store.RunId, db = store.DbPath, stopTick = tick, written = new { proj = store.WrittenProj, turret = store.WrittenTurret, events = store.WrittenEvents }, dropped = new { proj = store.Proj.Dropped, turret = store.Turret.Dropped, events = store.Events.Dropped }, error = store.Error };
            });

            Commands.Register("telemetry_status", "Telemetry run status and counters.", a => TelemetryStore.StatusSummary(), offThread: true);

            Commands.Register("track_summary", "Summarise one projectile's track. args: projId", a =>
            {
                var store = ReqStore(); store.Flush();
                int id = a.ReqInt("projId");
                var samples = store.Query("SELECT tick,x,y,z,height,speed,shot_angle,shot_rotation,flight_ticks,ticks_to_impact FROM proj_samples WHERE id=$id ORDER BY tick", ("$id", id));
                var events = store.Query("SELECT seq,tick,type,other_id,x,y,z,f1,f2,tx,tz,detail FROM events WHERE id=$id ORDER BY seq", ("$id", id));
                if (samples.Count == 0 && events.Count == 0) throw new CommandException($"no samples or events for projectile {id} in run {store.RunId}");
                var launch = events.FirstOrDefault(e => (string)e["type"] == "launch");
                var impact = events.FirstOrDefault(e => (string)e["type"] == "impact");
                var destroy = events.FirstOrDefault(e => (string)e["type"] == "destroy");
                var first = samples.FirstOrDefault();
                var last = samples.LastOrDefault();
                double? maxH = samples.Count > 0 ? samples.Max(s => D(s["height"])) : (double?)null;
                var maxRow = samples.Count > 0 ? samples.OrderByDescending(s => D(s["height"])).First() : null;
                object missDistance = null; object intendedCell = null;
                if (launch != null && D(launch["tx"]) >= 0)
                {
                    double tx = D(launch["tx"]) + 0.5, tz = D(launch["tz"]) + 0.5;
                    intendedCell = new { x = (int)D(launch["tx"]), z = (int)D(launch["tz"]) };
                    var end = impact ?? destroy;
                    if (end != null) missDistance = Math.Sqrt(Math.Pow(D(end["x"]) - tx, 2) + Math.Pow(D(end["z"]) - tz, 2));
                    else if (last != null) missDistance = Math.Sqrt(Math.Pow(D(last["x"]) - tx, 2) + Math.Pow(D(last["z"]) - tz, 2));
                }
                return new
                {
                    projId = id,
                    runId = store.RunId,
                    def = launch != null ? ((string)launch["detail"])?.Split('|')[0] : null,
                    launch = launch == null ? null : new { tick = L(launch["tick"]), cell = new { x = (int)Math.Floor(D(launch["x"])), z = (int)Math.Floor(D(launch["z"])) }, pos = new { x = D(launch["x"]), y = D(launch["y"]), z = D(launch["z"]) }, launcherId = L(launch["other_id"]), shotAngle = D(launch["f1"]), shotRotation = D(launch["f2"]), detail = launch["detail"] },
                    intendedTargetCell = intendedCell,
                    impact = impact == null ? null : new { tick = L(impact["tick"]), cell = new { x = (int)Math.Floor(D(impact["x"])), z = (int)Math.Floor(D(impact["z"])) }, pos = new { x = D(impact["x"]), y = D(impact["y"]), z = D(impact["z"]) }, hitThingId = L(impact["other_id"]), hitThing = impact["detail"], speedAtImpact = D(impact["f1"]), flightTicks = (int)D(impact["f2"]) },
                    destroy = destroy == null ? null : new { tick = L(destroy["tick"]), flightTicks = (int)D(destroy["f1"]) },
                    sampleCount = samples.Count,
                    firstTick = first != null ? L(first["tick"]) : (long?)null,
                    lastTick = last != null ? L(last["tick"]) : (long?)null,
                    flightTicks = last != null ? L(last["flight_ticks"]) : (long?)null,
                    maxHeight = maxH,
                    maxHeightAtTick = maxRow != null ? L(maxRow["tick"]) : (long?)null,
                    speedStart = first != null ? D(first["speed"]) : (double?)null,
                    speedEnd = last != null ? D(last["speed"]) : (double?)null,
                    speedDecayPct = first != null && last != null && D(first["speed"]) > 0 ? (1 - D(last["speed"]) / D(first["speed"])) * 100 : (double?)null,
                    missDistance,
                    events = events.Select(e => new { tick = L(e["tick"]), type = e["type"], detail = e["detail"] }).ToList(),
                };
            }, offThread: true);

            Commands.Register("turret_timeline", "State transitions for one turret (edge-triggered samples merged with its events). args: id, limit (500), sinceTick", a =>
            {
                var store = ReqStore(); store.Flush();
                int id = a.ReqInt("id"); int limit = Math.Min(a.Int("limit", 500), 10000); long since = a.Long("sinceTick", -1);
                var samples = store.Query("SELECT tick,rotation,target_id,target_x,target_z,warmup,cooldown,mag,flags FROM turret_samples WHERE id=$id AND tick>$s ORDER BY tick LIMIT $l", ("$id", id), ("$s", since), ("$l", limit));
                var events = store.Query("SELECT tick,type,other_id,f1,f2,tx,tz,detail FROM events WHERE id=$id AND tick>$s ORDER BY seq LIMIT $l", ("$id", id), ("$s", since), ("$l", limit));
                var timeline = new List<object>();
                foreach (var s in samples)
                {
                    long fl = L(s["flags"]);
                    timeline.Add(new { tick = L(s["tick"]), kind = "state", rotation = D(s["rotation"]), targetId = L(s["target_id"]), targetX = L(s["target_x"]), targetZ = L(s["target_z"]), warmup = L(s["warmup"]), cooldown = L(s["cooldown"]), mag = L(s["mag"]), warmingUp = (fl & 1) != 0, coolingDown = (fl & 2) != 0, hasTarget = (fl & 4) != 0, reloading = (fl & 8) != 0 });
                }
                foreach (var e in events)
                    timeline.Add(new { tick = L(e["tick"]), kind = (string)e["type"], otherId = L(e["other_id"]), f1 = D(e["f1"]), f2 = D(e["f2"]), tx = D(e["tx"]), tz = D(e["tz"]), detail = e["detail"] });
                var ordered = timeline.OrderBy(o => (long)o.GetType().GetProperty("tick").GetValue(o)).Take(limit).ToList();
                var totals = store.Query("SELECT type, COUNT(*) n FROM events WHERE id=$id GROUP BY type", ("$id", id)).ToDictionary(r => (string)r["type"], r => L(r["n"]));
                return new { id, runId = store.RunId, stateChanges = samples.Count, eventCounts = totals, timeline = ordered };
            }, offThread: true);

            Commands.Register("samples", "Decimated projectile samples. args: projId, every (1 = every tick), fields (comma list), limit (500, max 5000)", a =>
            {
                var store = ReqStore(); store.Flush();
                int id = a.ReqInt("projId"); int every = Math.Max(1, a.Int("every", 1)); int limit = Math.Min(a.Int("limit", 500), 5000);
                var allowed = new[] { "tick", "x", "y", "z", "height", "speed", "shot_angle", "shot_rotation", "flight_ticks", "ticks_to_impact" };
                var fields = a.StrList("fields") ?? allowed;
                var bad = fields.Where(f => !allowed.Contains(f)).ToList();
                if (bad.Count > 0) throw new CommandException("unknown fields: " + string.Join(",", bad) + ". Allowed: " + string.Join(",", allowed));
                if (!fields.Contains("tick")) fields = new[] { "tick" }.Concat(fields).ToArray();
                var rows = store.Query($"SELECT {string.Join(",", fields)} FROM proj_samples WHERE id=$id AND (flight_ticks % $e)=0 ORDER BY tick LIMIT $l", ("$id", id), ("$e", every), ("$l", limit));
                var total = store.Scalar("SELECT COUNT(*) FROM proj_samples WHERE id=$id", ("$id", id));
                return new { projId = id, every, fields, totalSamples = total, returned = rows.Count, rows };
            }, offThread: true);

            Commands.Register("events", "Telemetry events. args: since (seq, 0), type, id, limit (200, max 5000)", a =>
            {
                var store = ReqStore(); store.Flush();
                long since = a.Long("since", 0); int limit = Math.Min(a.Int("limit", 200), 5000);
                var where = "seq>$s"; var ps = new List<(string, object)> { ("$s", since), ("$l", limit) };
                if (a.Has("type")) { where += " AND type=$t"; ps.Add(("$t", a.Str("type"))); }
                if (a.Has("id")) { where += " AND id=$id"; ps.Add(("$id", a.Int("id", -1))); }
                var rows = store.Query($"SELECT seq,tick,type,id,other_id,x,y,z,f1,f2,tx,tz,detail FROM events WHERE {where} ORDER BY seq LIMIT $l", ps.ToArray());
                long next = rows.Count > 0 ? L(rows[rows.Count - 1]["seq"]) : since;
                return new { count = rows.Count, next, events = rows };
            }, offThread: true);

            Commands.Register("telemetry_sql", "Read-only SQL against the open telemetry db (SELECT only). args: sql, limit (1000)", a =>
            {
                var store = ReqStore(); store.Flush();
                var sql = a.Req("sql").Trim();
                if (!sql.StartsWith("select", StringComparison.OrdinalIgnoreCase) && !sql.StartsWith("with", StringComparison.OrdinalIgnoreCase)) throw new CommandException("only SELECT/WITH queries are allowed");
                // One statement only: later statements would run (and be silently dropped by query_only) when the reader is disposed.
                if (sql.TrimEnd().TrimEnd(';').Contains(';')) throw new CommandException("only a single statement is allowed (remove the ';')");
                var rows = store.Query(sql);
                int limit = a.Int("limit", 1000);
                return new { count = rows.Count, rows = rows.Take(limit).ToList(), truncated = rows.Count > limit };
            }, offThread: true);
        }

        static double D(object o) => o == null ? double.NaN : Convert.ToDouble(o);
        static long L(object o) => o == null ? -1 : Convert.ToInt64(o);
    }
}
