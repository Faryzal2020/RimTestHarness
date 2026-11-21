#!/usr/bin/env node
// RimTestHarness runner: launch RimWorld 1.7 headless (or windowed) with an isolated -savedatafolder,
// wait for the harness mod's loopback port, and expose every harness command as a CLI.
//
//   node runner.js launch [--mods=a,b,c] [--savedata=DIR] [--port=N] [--windowed=WxH] [--no-quicktest] [--timeout=S] [--wait=playing|menu|port|none]
//   node runner.js status | tick 600 | track-summary 1234 | turret-timeline 42 | log [--since=N] | quit [code] | stop
//   node runner.js sql "SELECT count(*) FROM proj_samples" [--db=path|--run=id]
//   node runner.js <any_harness_command> key=value ... [--json='{...}']
//
// Session state lives in <savedata>/session.json; runner/.last-session points at the most recent savedata dir.
import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const MOD_ROOT = path.resolve(__dirname, '..');
const DEFAULT_GAME_ROOT = path.resolve(MOD_ROOT, '..', '..');
const DEFAULT_MODS = ['ludeon.rimworld', 'brrainz.harmony', 'rimtestharness.harness'];
const DEFAULT_PORT = 18573;
const LAST_SESSION_FILE = path.join(__dirname, '.last-session');

// ------------------------------------------------------------------ arg parsing
function parseArgs(argv) {
  const opts = {}; const pos = [];
  for (const a of argv) {
    if (a.startsWith('--')) {
      const eq = a.indexOf('=');
      if (eq < 0) { const k = a.slice(2); if (k.startsWith('no-')) opts[k.slice(3)] = false; else opts[k] = true; }
      else opts[a.slice(2, eq)] = a.slice(eq + 1);
    } else pos.push(a);
  }
  return { opts, pos };
}
function autoCast(v) {
  if (v === 'true') return true; if (v === 'false') return false; if (v === 'null') return null;
  if (/^-?\d+$/.test(v)) return Number(v); if (/^-?\d*\.\d+$/.test(v)) return Number(v);
  if ((v.startsWith('{') && v.endsWith('}')) || (v.startsWith('[') && v.endsWith(']'))) { try { return JSON.parse(v); } catch { } }
  return v;
}
function kvArgs(pos, opts) {
  const args = {};
  for (const p of pos) { const eq = p.indexOf('='); if (eq > 0) args[p.slice(0, eq)] = autoCast(p.slice(eq + 1)); }
  if (opts.json) Object.assign(args, JSON.parse(opts.json));
  for (const [k, v] of Object.entries(opts)) if (!RUNNER_OPTS.has(k)) args[k] = typeof v === 'string' ? autoCast(v) : v;
  return args;
}
const RUNNER_OPTS = new Set(['json', 'savedata', 'port', 'timeout', 'db', 'run', 'table', 'raw', 'game', 'exe', 'mods', 'windowed', 'quicktest', 'wait', 'extra', 'version', 'help', 'session', 'follow']);

// ------------------------------------------------------------------ session
function sessionDirFromOpts(opts) {
  if (opts.savedata) return path.resolve(opts.savedata);
  if (process.env.RIMTEST_SAVEDATA) return path.resolve(process.env.RIMTEST_SAVEDATA);
  if (fs.existsSync(LAST_SESSION_FILE)) return fs.readFileSync(LAST_SESSION_FILE, 'utf8').trim();
  return null;
}
function readSession(opts) {
  const dir = sessionDirFromOpts(opts);
  if (!dir) throw new Error('no session: run `launch` first or pass --savedata=DIR');
  const f = path.join(dir, 'session.json');
  const s = fs.existsSync(f) ? JSON.parse(fs.readFileSync(f, 'utf8')) : { savedata: dir };
  s.savedata = dir;
  const portFile = path.join(dir, 'harness.port');
  if (fs.existsSync(portFile)) s.port = Number(fs.readFileSync(portFile, 'utf8').trim());
  if (opts.port) s.port = Number(opts.port);
  return s;
}
function writeSession(dir, s) { fs.writeFileSync(path.join(dir, 'session.json'), JSON.stringify(s, null, 2)); fs.writeFileSync(LAST_SESSION_FILE, dir); }
const IS_WIN = process.platform === 'win32';
function taskkill(pid, force) {
  try { execFileSync('taskkill', ['/PID', String(pid), '/T', ...(force ? ['/F'] : [])], { stdio: 'ignore', windowsHide: true }); } catch { }
}
function pidAlive(pid) { if (!pid) return false; try { process.kill(pid, 0); return true; } catch { return false; } }
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

// ------------------------------------------------------------------ harness client
async function call(session, cmd, args = {}, timeoutMs = 120000) {
  if (!session.port) throw new Error(`harness.port not found in ${session.savedata} (game not up yet?)`);
  const ctl = new AbortController();
  const t = setTimeout(() => ctl.abort(), timeoutMs + 5000);
  try {
    const res = await fetch(`http://127.0.0.1:${session.port}/${cmd}`, {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ ...args, timeout_ms: timeoutMs }), signal: ctl.signal,
    });
    const text = await res.text();
    let body; try { body = JSON.parse(text); } catch { throw new Error(`non-JSON response from harness: ${text.slice(0, 300)}`); }
    return body;
  } finally { clearTimeout(t); }
}
async function callOrThrow(session, cmd, args, timeoutMs) {
  const r = await call(session, cmd, args, timeoutMs);
  if (!r.ok) throw new Error(`${cmd} failed: ${r.error}${r.stack ? '\n' + r.stack : ''}`);
  return r.result;
}
async function tryStatus(session) { try { const r = await call(session, 'status', {}, 5000); return r.ok ? r.result : null; } catch { return null; } }

// ------------------------------------------------------------------ launch / supervise
function gameVersion(gameRoot) {
  try { return fs.readFileSync(path.join(gameRoot, 'Version.txt'), 'utf8').trim(); } catch { return null; }
}
function resolveModIds(mods, gameRoot) {
  return mods.map(m => {
    if (m.toLowerCase() === 'ceteam.combatextended') {
      const localCe = path.join(gameRoot, 'Mods', 'CombatExtended');
      const localDll = path.join(localCe, 'Assemblies', 'CombatExtended.dll');
      const workshopDll = path.resolve(gameRoot, '..', '..', 'workshop', 'content', '294100', '2890901044', 'Assemblies', 'CombatExtended.dll');
      if (fs.existsSync(localCe) && !fs.existsSync(localDll) && fs.existsSync(workshopDll)) {
        console.error('note: local Mods/CombatExtended is uncompiled; using ceteam.combatextended_steam (Workshop 2890901044)');
        return 'ceteam.combatextended_steam';
      }
    }
    return m;
  });
}
function writeModsConfig(savedata, mods, version) {
  const cfg = path.join(savedata, 'Config'); fs.mkdirSync(cfg, { recursive: true });
  // Format: Verse.ModsConfig.ModsConfigData { version, activeMods, knownExpansions } saved via DirectXmlSaver.
  const xml = `<?xml version="1.0" encoding="utf-8"?>\n<ModsConfigData>\n  <version>${version ?? ''}</version>\n  <activeMods>\n${mods.map(m => `    <li>${m}</li>`).join('\n')}\n  </activeMods>\n  <knownExpansions>\n  </knownExpansions>\n</ModsConfigData>\n`;
  fs.writeFileSync(path.join(cfg, 'ModsConfig.xml'), xml);
  const prefsPath = path.join(cfg, 'Prefs.xml');
  if (!fs.existsSync(prefsPath)) {
    fs.writeFileSync(prefsPath, `<?xml version="1.0" encoding="utf-8"?>\n<PrefsData>\n  <runInBackground>True</runInBackground>\n</PrefsData>\n`);
  }
  return path.join(cfg, 'ModsConfig.xml');
}
// Fallback only: once the game is up, `status.logPath` is used instead.
// macOS path verified on 1.7; Windows path from the 1.7 wiki; Linux path is the Unity-era default. Windows/Linux UNVERIFIED.
function defaultPlayerLog() {
  const rel = ['Ludeon Studios', 'RimWorld by Ludeon Studios'];
  if (process.platform === 'darwin') return path.join(os.homedir(), 'Library', 'Application Support', ...rel, 'Logs', 'Player.log');
  if (process.platform === 'win32') return path.join(os.homedir(), 'AppData', 'LocalLow', ...rel, 'Logs', 'Player.log');
  return path.join(os.homedir(), '.config', 'unity3d', ...rel, 'Player.log');
}

// Game executable: --exe=PATH or RIMTEST_GAME_EXE wins, else the first platform candidate that exists.
// macOS verified; the Windows/Linux names are the pre-1.7 ones (UNVERIFIED for 1.7).
function findGameExe(gameRoot, opts) {
  const explicit = opts.exe ?? process.env.RIMTEST_GAME_EXE;
  if (explicit) { const p = path.resolve(String(explicit)); if (!fs.existsSync(p)) throw new Error('game binary not found: ' + p); return p; }
  const candidates = {
    darwin: [['RimWorld.app', 'Contents', 'MacOS', 'RimWorld']],
    win32: [['RimWorldWin64.exe'], ['RimWorld.exe']],
    linux: [['RimWorldLinux'], ['RimWorld']],
  }[process.platform] ?? [['RimWorld']];
  const tried = candidates.map(c => path.join(gameRoot, ...c));
  const hit = tried.find(p => fs.existsSync(p));
  if (!hit) throw new Error(`game binary not found; tried:\n  ${tried.join('\n  ')}\npass --exe=PATH or set RIMTEST_GAME_EXE`);
  return hit;
}

async function cmdLaunch(opts) {
  const gameRoot = opts.game ? path.resolve(opts.game) : DEFAULT_GAME_ROOT;
  const exe = findGameExe(gameRoot, opts);
  const rawMods = opts.mods ? String(opts.mods).split(',').map(s => s.trim()).filter(Boolean) : DEFAULT_MODS;
  const mods = resolveModIds(rawMods, gameRoot);
  if (!mods.includes('rimtestharness.harness')) console.error('warning: rimtestharness.harness is not in the mod list; harness commands will not work');
  const stamp = new Date().toISOString().replace(/[-:]/g, '').replace(/\..+/, '').replace('T', '_');
  const savedata = opts.savedata ? path.resolve(opts.savedata) : path.join(__dirname, 'runs', stamp);
  fs.mkdirSync(savedata, { recursive: true });
  for (const f of ['harness.port', 'exit.json']) { try { fs.unlinkSync(path.join(savedata, f)); } catch { } }
  const port = Number(opts.port ?? DEFAULT_PORT);
  const version = opts.version ?? gameVersion(gameRoot);
  const modsConfig = writeModsConfig(savedata, mods, version);

  const args = [];
  if (opts.windowed) args.push(`-WindowSize=${opts.windowed}`); else args.push('-Headless');
  if (opts.quicktest !== false) args.push('-quicktest');
  args.push('-NoCrashDialog', `-savedatafolder=${savedata}`, `-TestPort=${port}`);
  if (opts.extra) args.push(...String(opts.extra).split(' ').filter(Boolean));

  const stdoutLog = path.join(savedata, 'runner-stdout.log');
  // The supervisor is a detached node process that owns the game child and records its exit code to exit.json.
  const sup = spawn(process.execPath, [fileURLToPath(import.meta.url), '_supervise', exe, savedata, stdoutLog, ...args], {
    detached: true, stdio: 'ignore', cwd: path.dirname(exe), windowsHide: true,
  });
  sup.unref();
  const session = { savedata, requestedPort: port, gameRoot, exe, args, mods, modsConfig, supervisorPid: sup.pid, startedAt: new Date().toISOString(), stdoutLog, playerLog: defaultPlayerLog() };
  writeSession(savedata, session);
  console.error(`launched supervisor pid ${sup.pid}: ${exe} ${args.join(' ')}`);
  console.error(`savedata: ${savedata}`);

  const wait = opts.wait ?? 'playing';
  const timeoutS = Number(opts.timeout ?? 300);
  if (wait === 'none') { print(session); return; }
  const result = await waitFor(session, wait, timeoutS * 1000);
  print({ ...result, session: readSession({ savedata }) });
}

async function cmdSupervise(pos) {
  const [exe, savedata, stdoutLog, ...args] = pos;
  const out = fs.openSync(stdoutLog, 'a');
  const child = spawn(exe, args, { stdio: ['ignore', out, out], cwd: path.dirname(exe) });
  const sf = path.join(savedata, 'session.json');
  try { const s = JSON.parse(fs.readFileSync(sf, 'utf8')); s.pid = child.pid; fs.writeFileSync(sf, JSON.stringify(s, null, 2)); } catch { fs.writeFileSync(sf, JSON.stringify({ pid: child.pid, savedata }, null, 2)); }
  const onSig = (sig) => { if (IS_WIN) taskkill(child.pid, true); else { try { child.kill(sig); } catch { } } };
  process.on('SIGTERM', () => onSig('SIGTERM')); process.on('SIGINT', () => onSig('SIGINT'));
  child.on('exit', (code, signal) => {
    fs.writeFileSync(path.join(savedata, 'exit.json'), JSON.stringify({ code, signal, at: new Date().toISOString() }, null, 2));
    process.exit(0);
  });
}

async function waitFor(session, target, timeoutMs) {
  const t0 = Date.now();
  const deadline = t0 + timeoutMs;
  let lastState = null;
  while (Date.now() < deadline) {
    const s = readSession({ savedata: session.savedata });
    const exitFile = path.join(session.savedata, 'exit.json');
    if (fs.existsSync(exitFile)) { const ex = JSON.parse(fs.readFileSync(exitFile, 'utf8')); throw new Error(`game exited early: ${JSON.stringify(ex)}\n${tailFile(s.stdoutLog, 30)}`); }
    if (s.port) {
      if (target === 'port') return { reached: 'port', port: s.port, elapsedMs: Date.now() - t0 };
      const st = await tryStatus(s);
      if (st) {
        const evTxt = st.longEventText ? `(${st.longEventText})` : '';
        const summary = `${st.programState} longEvent=${st.anyLongEvent}${evTxt} ticks=${st.ticksGame} errors=${st.errors}`;
        if (summary !== lastState) { console.error(`[${((Date.now() - t0) / 1000).toFixed(1)}s] ${summary}`); lastState = summary; }
        if (target === 'menu' && st.playDataLoaded && !st.anyLongEvent) return { reached: 'menu', status: st, elapsedMs: Date.now() - t0 };
        if (target === 'playing' && st.programState === 'Playing' && !st.anyLongEvent && st.mapCount > 0) return { reached: 'playing', status: st, elapsedMs: Date.now() - t0 };
      }
    }
    await sleep(500);
  }
  await killSession(session, 'timeout');
  throw new Error(`timeout after ${timeoutMs / 1000}s waiting for '${target}'. Last: ${lastState}\n${tailFile(readSession({ savedata: session.savedata }).stdoutLog, 30)}`);
}

async function killSession(session, reason) {
  const s = readSession({ savedata: session.savedata });
  const pids = [s.pid, s.supervisorPid].filter(Boolean);
  // POSIX: SIGTERM, then SIGKILL. Windows has no signals: taskkill /T (close request), then taskkill /T /F.
  for (const pid of pids) if (pidAlive(pid)) { if (IS_WIN) taskkill(pid, false); else { try { process.kill(pid, 'SIGTERM'); } catch { } } }
  const t0 = Date.now();
  while (Date.now() - t0 < 8000 && pids.some(pidAlive)) await sleep(200);
  for (const pid of pids) if (pidAlive(pid)) { if (IS_WIN) taskkill(pid, true); else { try { process.kill(pid, 'SIGKILL'); } catch { } } }
  console.error(`killed game (${reason}); pids ${pids.join(',')}`);
}

function tailFile(file, n) {
  try { const lines = fs.readFileSync(file, 'utf8').split('\n'); return lines.slice(-n).join('\n'); } catch { return `(no ${file})`; }
}

// ------------------------------------------------------------------ sql
function findDb(session, opts) {
  if (opts.db) return path.resolve(opts.db);
  const dir = path.join(session.savedata, 'telemetry');
  if (!fs.existsSync(dir)) throw new Error('no telemetry folder in ' + session.savedata);
  if (opts.run) return path.join(dir, `${opts.run}.db`);
  const dbs = fs.readdirSync(dir).filter(f => f.endsWith('.db')).map(f => ({ f, m: fs.statSync(path.join(dir, f)).mtimeMs })).sort((a, b) => b.m - a.m);
  if (!dbs.length) throw new Error('no .db in ' + dir);
  return path.join(dir, dbs[0].f);
}
async function cmdSql(session, query, opts) {
  const { DatabaseSync } = await import('node:sqlite');
  const file = findDb(session, opts);
  const db = new DatabaseSync(file, { readOnly: true });
  try {
    const stmt = db.prepare(query);
    const rows = stmt.all();
    if (opts.table) console.table(rows); else print({ db: file, count: rows.length, rows });
  } finally { db.close(); }
}

// A leading bare number maps to the named argument; key=value positionals are passed through as-is.
function firstNum(pos, key, def) { if (pos.length && !pos[0].includes('=')) return { [key]: Number(pos[0]) }; return def === undefined ? {} : { [key]: def }; }
function rest(pos) { return pos.length && !pos[0].includes('=') ? pos.slice(1) : pos; }

// ------------------------------------------------------------------ output
function print(obj) { console.log(JSON.stringify(obj, (k, v) => typeof v === 'bigint' ? Number(v) : v, 2)); }

// ------------------------------------------------------------------ main
const HELP = `RimTestHarness runner
  launch [--mods=a,b,c] [--savedata=DIR] [--port=N] [--windowed=WxH] [--no-quicktest] [--timeout=S] [--wait=playing|menu|port|none] [--extra="-Verbose"]
         [--game=GAME_ROOT] [--exe=PATH_TO_GAME_BINARY]   (or env RIMTEST_GAME_EXE)
  stop                         kill the game and supervisor of the current session
  wait [playing|menu|port] [--timeout=S]
  status | help                harness status / command list
  log [--since=N] [--level=error] [--limit=200] [--contains=txt]
  tail-log [n]                 last n lines of Player.log and runner-stdout.log
  tick N | pause | set-speed N
  track-summary PROJID | turret-timeline ID | samples PROJID [--every=N] | events [--since=N] [--type=T]
  telemetry-start [runId] | telemetry-stop
  sql "QUERY" [--db=path|--run=id] [--table]
  quit [exitCode]              ask the game to exit and report the process exit code
  <command> key=value ...      any harness command (see 'help'); --json='{...}' for nested args
Options: --savedata=DIR selects the session (default: last launched), --timeout=S per call`;

async function main() {
  const { opts, pos } = parseArgs(process.argv.slice(2));
  const cmd = pos.shift();
  if (!cmd || cmd === '--help' || opts.help) { console.log(HELP); return; }
  if (cmd === '_supervise') return cmdSupervise(pos);
  if (cmd === 'launch') return cmdLaunch(opts);

  const session = readSession(opts);
  const timeoutMs = Number(opts.timeout ?? 120) * 1000;
  switch (cmd) {
    case 'stop': return killSession(session, 'stop');
    case 'wait': return print(await waitFor(session, pos[0] ?? 'playing', timeoutMs));
    case 'tail-log': {
      const n = Number(pos[0] ?? 40);
      const st = await tryStatus(session);
      const playerLog = st?.logPath ?? session.playerLog ?? defaultPlayerLog();
      console.log(`--- ${playerLog}\n${tailFile(playerLog, n)}\n--- ${session.stdoutLog}\n${tailFile(session.stdoutLog, n)}`);
      return;
    }
    case 'sql': if (!pos[0]) throw new Error('sql needs a query'); return cmdSql(session, pos[0], opts);
    case 'tick': return print(await callOrThrow(session, 'tick', { ...firstNum(pos, 'n', 1), ...kvArgs(rest(pos), opts) }, timeoutMs));
    case 'set-speed': return print(await callOrThrow(session, 'set_speed', { speed: pos[0] ?? 1 }, timeoutMs));
    case 'track-summary': return print(await callOrThrow(session, 'track_summary', { ...firstNum(pos, 'projId'), ...kvArgs(rest(pos), opts) }, timeoutMs));
    case 'turret-timeline': return print(await callOrThrow(session, 'turret_timeline', { ...firstNum(pos, 'id'), ...kvArgs(rest(pos), opts) }, timeoutMs));
    case 'samples': return print(await callOrThrow(session, 'samples', { ...firstNum(pos, 'projId'), ...kvArgs(rest(pos), opts) }, timeoutMs));
    case 'telemetry-start': return print(await callOrThrow(session, 'telemetry_start', { ...(pos[0] ? { runId: pos[0] } : {}), ...kvArgs(pos.slice(1), opts) }, timeoutMs));
    case 'telemetry-stop': return print(await callOrThrow(session, 'telemetry_stop', {}, timeoutMs));
    case 'quit': {
      const code = Number(pos[0] ?? 0);
      const r = await callOrThrow(session, 'quit', { exitCode: code }, timeoutMs);
      const exitFile = path.join(session.savedata, 'exit.json');
      const t0 = Date.now();
      while (Date.now() - t0 < timeoutMs && !fs.existsSync(exitFile)) await sleep(250);
      if (!fs.existsSync(exitFile)) { console.error('game did not exit in time; killing'); await killSession(session, 'quit-timeout'); return print({ requested: r, exit: null }); }
      return print({ requested: r, exit: JSON.parse(fs.readFileSync(exitFile, 'utf8')) });
    }
    default: {
      const name = cmd.replace(/-/g, '_');
      const args = kvArgs(pos, opts);
      if (name === 'screenshot' && args.path) args.path = path.resolve(String(args.path));
      const r = await call(session, name, args, timeoutMs);
      if (opts.raw) return print(r);
      if (!r.ok) { console.error(`error (${r.type}): ${r.error}`); if (r.stack) console.error(r.stack); process.exitCode = 1; return; }
      return print(r.result);
    }
  }
}

main().catch(e => { console.error(e.message || e); process.exitCode = 1; });
