using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace RimTestHarness.Telemetry
{
    /// <summary>
    /// Owns one telemetry run: three ring buffers filled on the main thread and a background thread that drains them
    /// into <savedatafolder>/telemetry/<runId>.db (tables runs, events, proj_samples, turret_samples).
    /// Nothing here ever goes through Verse.Log per row.
    /// </summary>
    public sealed class TelemetryStore
    {
        public static TelemetryStore Instance;
        public static volatile bool Active;

        public readonly string RunId;
        public readonly string DbPath;
        public readonly Ring<ProjSample> Proj = new Ring<ProjSample>(1 << 17);
        public readonly Ring<TurretSample> Turret = new Ring<TurretSample>(1 << 15);
        public readonly Ring<Evt> Events = new Ring<Evt>(1 << 14);

        SqliteConnection writer;
        SqliteConnection reader;
        readonly object readerGate = new object();
        Thread thread;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly ManualResetEventSlim flushed = new ManualResetEventSlim(false);
        volatile bool stop;
        volatile int flushRequested;
        public long WrittenProj, WrittenTurret, WrittenEvents;
        public int StartTick, StopTick = -1;
        public string Error;

        SqliteCommand insProj, insTurret, insEvt;
        SqliteParameter[] pProj, pTurret, pEvt;

        public static object StatusSummary()
        {
            var s = Instance;
            if (s == null) return new { active = false };
            return new
            {
                active = Active,
                runId = s.RunId,
                db = s.DbPath,
                startTick = s.StartTick,
                stopTick = s.StopTick,
                written = new { proj = s.WrittenProj, turret = s.WrittenTurret, events = s.WrittenEvents },
                pending = new { proj = s.Proj.Count, turret = s.Turret.Count, events = s.Events.Count },
                dropped = new { proj = s.Proj.Dropped, turret = s.Turret.Dropped, events = s.Events.Dropped },
                error = s.Error,
            };
        }

        public TelemetryStore(string runId, string dir, int startTick, string meta)
        {
            RunId = runId;
            Directory.CreateDirectory(dir);
            DbPath = Path.Combine(dir, runId + ".db");
            StartTick = startTick;
            SqliteNative.Initialize(HarnessMod.ModRoot);
            writer = new SqliteConnection($"Data Source={DbPath}");
            writer.Open();
            Exec(writer, "PRAGMA journal_mode=WAL; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY;");
            Exec(writer, @"
CREATE TABLE IF NOT EXISTS runs(run_id TEXT PRIMARY KEY, started_tick INTEGER, started_at TEXT, stopped_tick INTEGER, stopped_at TEXT, meta TEXT, dropped_proj INTEGER, dropped_turret INTEGER, dropped_events INTEGER);
CREATE TABLE IF NOT EXISTS events(seq INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT, tick INTEGER, type TEXT, id INTEGER, other_id INTEGER, x REAL, y REAL, z REAL, f1 REAL, f2 REAL, tx REAL, tz REAL, detail TEXT);
CREATE TABLE IF NOT EXISTS proj_samples(run_id TEXT, tick INTEGER, id INTEGER, x REAL, y REAL, z REAL, height REAL, speed REAL, shot_angle REAL, shot_rotation REAL, flight_ticks INTEGER, ticks_to_impact INTEGER);
CREATE TABLE IF NOT EXISTS turret_samples(run_id TEXT, tick INTEGER, id INTEGER, rotation REAL, target_id INTEGER, target_x INTEGER, target_z INTEGER, warmup INTEGER, cooldown INTEGER, mag INTEGER, flags INTEGER);
CREATE INDEX IF NOT EXISTS ix_proj ON proj_samples(id, tick);
CREATE INDEX IF NOT EXISTS ix_turret ON turret_samples(id, tick);
CREATE INDEX IF NOT EXISTS ix_events_id ON events(id, tick);
CREATE INDEX IF NOT EXISTS ix_events_type ON events(type, tick);");
            using (var c = writer.CreateCommand())
            {
                c.CommandText = "INSERT OR REPLACE INTO runs(run_id, started_tick, started_at, meta) VALUES($r,$t,$a,$m)";
                c.Parameters.AddWithValue("$r", runId); c.Parameters.AddWithValue("$t", startTick); c.Parameters.AddWithValue("$a", DateTime.UtcNow.ToString("o")); c.Parameters.AddWithValue("$m", meta ?? "");
                c.ExecuteNonQuery();
            }
            insProj = Prepare(writer, "INSERT INTO proj_samples VALUES($r,$tick,$id,$x,$y,$z,$h,$s,$a,$rot,$f,$tti)", out pProj,
                "$r", "$tick", "$id", "$x", "$y", "$z", "$h", "$s", "$a", "$rot", "$f", "$tti");
            insTurret = Prepare(writer, "INSERT INTO turret_samples VALUES($r,$tick,$id,$rot,$tid,$tx,$tz,$w,$c,$m,$fl)", out pTurret,
                "$r", "$tick", "$id", "$rot", "$tid", "$tx", "$tz", "$w", "$c", "$m", "$fl");
            insEvt = Prepare(writer, "INSERT INTO events(run_id,tick,type,id,other_id,x,y,z,f1,f2,tx,tz,detail) VALUES($r,$tick,$type,$id,$oid,$x,$y,$z,$f1,$f2,$tx,$tz,$d)", out pEvt,
                "$r", "$tick", "$type", "$id", "$oid", "$x", "$y", "$z", "$f1", "$f2", "$tx", "$tz", "$d");
            pProj[0].Value = runId; pTurret[0].Value = runId; pEvt[0].Value = runId;

            reader = new SqliteConnection($"Data Source={DbPath}");
            reader.Open();
            // The reader serves telemetry_sql, which accepts caller SQL. Microsoft.Data.Sqlite runs every statement in a
            // command text, so a "SELECT 1; DROP TABLE ..." would otherwise write. query_only makes the connection refuse writes.
            Exec(reader, "PRAGMA query_only=ON;");

            thread = new Thread(Loop) { IsBackground = true, Name = "RimTestHarness.Telemetry" };
            thread.Start();
        }

        static SqliteCommand Prepare(SqliteConnection c, string sql, out SqliteParameter[] ps, params string[] names)
        {
            var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            ps = new SqliteParameter[names.Length];
            for (int i = 0; i < names.Length; i++) { ps[i] = cmd.Parameters.Add(names[i], SqliteType.Real); }
            return cmd;
        }

        static void Exec(SqliteConnection c, string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        // ---- producers (main thread)
        public void PushEvent(in Evt e) { Events.TryPush(e); }

        // ---- consumer
        void Loop()
        {
            while (!stop)
            {
                wake.WaitOne(200);
                try { Drain(); }
                catch (Exception ex) { Error = ex.ToString(); }
                if (flushRequested != 0) { flushRequested = 0; flushed.Set(); }
            }
            try { Drain(); } catch (Exception ex) { Error = ex.ToString(); }
            flushed.Set();
        }

        void Drain()
        {
            if (Proj.Count == 0 && Turret.Count == 0 && Events.Count == 0) return;
            using var tx = writer.BeginTransaction();
            insProj.Transaction = tx; insTurret.Transaction = tx; insEvt.Transaction = tx;
            while (Proj.TryPop(out var s))
            {
                pProj[1].Value = s.tick; pProj[2].Value = s.id; pProj[3].Value = s.x; pProj[4].Value = s.y; pProj[5].Value = s.z;
                pProj[6].Value = s.height; pProj[7].Value = s.speed; pProj[8].Value = s.angle; pProj[9].Value = s.rot; pProj[10].Value = s.flight; pProj[11].Value = s.tti;
                insProj.ExecuteNonQuery(); WrittenProj++;
            }
            while (Turret.TryPop(out var t))
            {
                pTurret[1].Value = t.tick; pTurret[2].Value = t.id; pTurret[3].Value = t.rot; pTurret[4].Value = t.targetId; pTurret[5].Value = t.tx; pTurret[6].Value = t.tz;
                pTurret[7].Value = t.warmup; pTurret[8].Value = t.cooldown; pTurret[9].Value = t.mag; pTurret[10].Value = t.flags;
                insTurret.ExecuteNonQuery(); WrittenTurret++;
            }
            while (Events.TryPop(out var e))
            {
                pEvt[1].Value = e.tick; pEvt[2].Value = e.type.ToString(); pEvt[3].Value = e.id; pEvt[4].Value = e.otherId; pEvt[5].Value = e.x; pEvt[6].Value = e.y; pEvt[7].Value = e.z;
                pEvt[8].Value = e.f1; pEvt[9].Value = e.f2; pEvt[10].Value = e.tx; pEvt[11].Value = e.tz; pEvt[12].Value = (object)e.detail ?? DBNull.Value;
                insEvt.ExecuteNonQuery(); WrittenEvents++;
            }
            tx.Commit();
        }

        /// <summary>Block until everything pushed so far is committed.</summary>
        public void Flush(int timeoutMs = 10000)
        {
            if (thread == null || !thread.IsAlive) return;
            flushed.Reset();
            flushRequested = 1;
            wake.Set();
            flushed.Wait(timeoutMs);
        }

        public void Stop(int stopTick = -1)
        {
            if (stop) return;
            Active = false;
            StopTick = stopTick;
            Flush();
            stop = true;
            wake.Set();
            thread?.Join(5000);
            try
            {
                using var c = writer.CreateCommand();
                c.CommandText = "UPDATE runs SET stopped_tick=$t, stopped_at=$a, dropped_proj=$dp, dropped_turret=$dt, dropped_events=$de WHERE run_id=$r";
                c.Parameters.AddWithValue("$t", stopTick); c.Parameters.AddWithValue("$a", DateTime.UtcNow.ToString("o"));
                c.Parameters.AddWithValue("$dp", Proj.Dropped); c.Parameters.AddWithValue("$dt", Turret.Dropped); c.Parameters.AddWithValue("$de", Events.Dropped); c.Parameters.AddWithValue("$r", RunId);
                c.ExecuteNonQuery();
                Exec(writer, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch (Exception ex) { Error = ex.ToString(); }
            lock (readerGate) { reader?.Close(); reader?.Dispose(); reader = null; }
            writer?.Close(); writer?.Dispose(); writer = null;
            SqliteConnection.ClearAllPools();
            if (Instance == this) Instance = null;
        }

        /// <summary>Run a read query (reader connection; safe while the writer thread is appending in WAL mode).</summary>
        public List<Dictionary<string, object>> Query(string sql, params (string, object)[] args)
        {
            lock (readerGate)
            {
                if (reader == null) throw new CommandException("telemetry store closed");
                using var cmd = reader.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
                using var rd = cmd.ExecuteReader();
                var rows = new List<Dictionary<string, object>>();
                while (rd.Read())
                {
                    var row = new Dictionary<string, object>(rd.FieldCount);
                    for (int i = 0; i < rd.FieldCount; i++) row[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                    rows.Add(row);
                }
                return rows;
            }
        }

        public object Scalar(string sql, params (string, object)[] args)
        {
            var rows = Query(sql, args);
            if (rows.Count == 0) return null;
            foreach (var v in rows[0].Values) return v;
            return null;
        }
    }
}
