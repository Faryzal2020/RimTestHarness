using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimTestHarness.Telemetry
{
    /// <summary>
    /// Combat Extended telemetry probe. Every CE type is resolved by name; when CE is absent nothing is patched and
    /// Available stays false. Hot paths (Tick postfixes) use precompiled field/property accessors and push structs into
    /// ring buffers: no allocation per tick, no Verse.Log.
    ///
    /// Patched (decompiled CombatExtended.dll):
    ///   ProjectileCE.Launch(Thing launcher, Vector2 origin, Thing equipment)        ProjectileCE.cs:883  (the 8-arg Launch at :856 calls it)
    ///   ProjectileCE.Tick()                                                          ProjectileCE.cs:1772
    ///   ProjectileCE.Impact(Thing hitThing)                                          ProjectileCE.cs:2031 (subclasses call base.Impact, e.g. ProjectileCE_Explosive.cs:62)
    ///   ThingWithComps.Destroy(DestroyMode) prefix, filtered to ProjectileCE          Verse/ThingWithComps.cs:323 (ProjectileCE : ThingWithComps, does not override Destroy)
    ///   Building_TurretGunCE.Tick() / BeginBurst() / BurstComplete()                 Building_TurretGunCE.cs:398,633,674
    ///   CompAmmoUser.LoadAmmo(Thing ammo, bool emptyMag)                             CompAmmoUser.cs:798
    ///   AbsolutelyMoreCannons.CompTurretModeSwap.TrySwapMode() (AMC, optional)       AMC source CompTurretModeSwap.cs:90
    /// Fields read: exactPosition (private Vector3, backing ExactPosition/Height), shotAngle, shotRotation, shotSpeed,
    ///   FlightTicks, intTicksToImpact, launcher, intendedTarget (ProjectileCE.cs:104-122,66-68,86,100);
    ///   burstWarmupTicksLeft, burstCooldownTicksLeft, isReloading, top (Building_TurretGunCE.cs:28,30,62; TurretTop.CurRotation RimWorld/TurretTop.cs:28)
    /// </summary>
    public static class CeProbe
    {
        public static bool Available;
        public static string InstallLog = "";
        public const float RotationEpsilon = 0.01f;

        static Type tProj, tTurret, tAmmoUser, tModeSwap;
        static AccessTools.FieldRef<object, Vector3> fExactPos;
        static AccessTools.FieldRef<object, float> fShotAngle, fShotRot, fShotSpeed;
        static AccessTools.FieldRef<object, int> fFlightTicks, fTti, fWarmup, fCooldown;
        static AccessTools.FieldRef<object, Thing> fLauncher;
        static AccessTools.FieldRef<object, LocalTargetInfo> fIntendedTarget;
        static AccessTools.FieldRef<object, bool> fIsReloading;
        static Func<object, object> gCompAmmo;
        static Func<object, int> gCurMagCount;
        static Func<object, Def> gCurrentAmmo;
        static Func<object, TurretTop> gTop;
        static Func<object, Thing> gGun;

        static readonly Dictionary<int, TurretSample> lastTurret = new Dictionary<int, TurretSample>();
        static readonly HashSet<int> launched = new HashSet<int>();

        static TelemetryStore Store => TelemetryStore.Instance;
        static int Tick => Current.Game?.tickManager?.TicksGame ?? -1;

        public static void ResetPerRunState() { lastTurret.Clear(); launched.Clear(); }

        public static void TryInstall(Harmony harmony)
        {
            tProj = Reflect.T("CombatExtended.ProjectileCE");
            tTurret = Reflect.T("CombatExtended.Building_TurretGunCE");
            tAmmoUser = Reflect.T("CombatExtended.CompAmmoUser");
            tModeSwap = Reflect.T("AbsolutelyMoreCannons.CompTurretModeSwap");
            if (tProj == null || tTurret == null)
            {
                InstallLog = "CombatExtended types not found; probe disabled";
                return;
            }
            try
            {
                fExactPos = AccessTools.FieldRefAccess<Vector3>(tProj, "exactPosition");
                fShotAngle = AccessTools.FieldRefAccess<float>(tProj, "shotAngle");
                fShotRot = AccessTools.FieldRefAccess<float>(tProj, "shotRotation");
                fShotSpeed = AccessTools.FieldRefAccess<float>(tProj, "shotSpeed");
                fFlightTicks = AccessTools.FieldRefAccess<int>(tProj, "FlightTicks");
                fTti = AccessTools.FieldRefAccess<int>(tProj, "intTicksToImpact");
                fLauncher = AccessTools.FieldRefAccess<Thing>(tProj, "launcher");
                fIntendedTarget = AccessTools.FieldRefAccess<LocalTargetInfo>(tProj, "intendedTarget");
                fWarmup = AccessTools.FieldRefAccess<int>(tTurret, "burstWarmupTicksLeft");
                fCooldown = AccessTools.FieldRefAccess<int>(tTurret, "burstCooldownTicksLeft");
                fIsReloading = AccessTools.FieldRefAccess<bool>(tTurret, "isReloading");
                gCompAmmo = Reflect.Getter<object>(tTurret, "CompAmmo");
                gTop = Reflect.Getter<TurretTop>(tTurret, "top");
                gGun = Reflect.GetterOrNull<Thing>(tTurret, "Gun");
                if (tAmmoUser != null)
                {
                    gCurMagCount = Reflect.Getter<int>(tAmmoUser, "CurMagCount");
                    gCurrentAmmo = Reflect.Getter<Def>(tAmmoUser, "CurrentAmmo");
                }

                var self = typeof(CeProbe);
                Patch(harmony, AccessTools.Method(tProj, "Launch", new[] { typeof(Thing), typeof(Vector2), typeof(Thing) }), postfix: nameof(Post_Launch));
                Patch(harmony, AccessTools.Method(tProj, "Tick", Type.EmptyTypes), postfix: nameof(Post_ProjTick));
                Patch(harmony, AccessTools.Method(tProj, "Impact", new[] { typeof(Thing) }), postfix: nameof(Post_Impact));
                Patch(harmony, AccessTools.Method(typeof(ThingWithComps), "Destroy", new[] { typeof(DestroyMode) }), prefix: nameof(Pre_Destroy));
                Patch(harmony, AccessTools.Method(tTurret, "Tick", Type.EmptyTypes), postfix: nameof(Post_TurretTick));
                Patch(harmony, AccessTools.Method(tTurret, "BeginBurst", Type.EmptyTypes), postfix: nameof(Post_BeginBurst));
                Patch(harmony, AccessTools.Method(tTurret, "BurstComplete", Type.EmptyTypes), postfix: nameof(Post_BurstComplete));
                if (tAmmoUser != null) Patch(harmony, AccessTools.Method(tAmmoUser, "LoadAmmo", new[] { typeof(Thing), typeof(bool) }), postfix: nameof(Post_LoadAmmo));
                if (tModeSwap != null)
                {
                    var swap = AccessTools.Method(tModeSwap, "TrySwapMode");
                    if (swap != null) harmony.Patch(swap, prefix: new HarmonyMethod(self, nameof(Pre_ModeSwap)), postfix: new HarmonyMethod(self, nameof(Post_ModeSwap)));
                    InstallLog += swap != null ? "AMC CompTurretModeSwap.TrySwapMode patched; " : "AMC TrySwapMode not found; ";
                }
                Available = true;
                InstallLog += "CE probe installed";
            }
            catch (Exception ex)
            {
                Available = false;
                InstallLog = "CE probe install failed: " + ex;
                Log.Error("[RimTestHarness] " + InstallLog);
            }
        }

        static void Patch(Harmony h, System.Reflection.MethodInfo target, string prefix = null, string postfix = null)
        {
            if (target == null) throw new MissingMethodException("CE probe: target method missing for " + (prefix ?? postfix));
            h.Patch(target,
                prefix: prefix != null ? new HarmonyMethod(typeof(CeProbe), prefix) : null,
                postfix: postfix != null ? new HarmonyMethod(typeof(CeProbe), postfix) : null);
        }

        // ------------------------------------------------------------ projectile hooks

        public static void Post_Launch(object __instance)
        {
            if (!TelemetryStore.Active) return;
            try
            {
                var store = Store; if (store == null) return;
                var t = (Thing)__instance;
                launched.Add(t.thingIDNumber);
                var p = fExactPos(__instance);
                var tgt = fIntendedTarget(__instance);
                var launcher = fLauncher(__instance);
                var e = new Evt
                {
                    tick = Tick, type = EvType.launch, id = t.thingIDNumber, otherId = launcher?.thingIDNumber ?? -1,
                    x = p.x, y = p.y, z = p.z, f1 = fShotAngle(__instance), f2 = fShotRot(__instance),
                    tx = tgt.IsValid ? tgt.Cell.x : -1, tz = tgt.IsValid ? tgt.Cell.z : -1,
                    detail = t.def.defName + "|speed=" + fShotSpeed(__instance).ToString("F2") + "|launcher=" + (launcher?.def?.defName ?? "null") + "|target=" + (tgt.IsValid ? (tgt.HasThing ? tgt.Thing.def.defName + "#" + tgt.Thing.thingIDNumber : "cell") : "none"),
                };
                store.PushEvent(e);
            }
            catch (Exception ex) { Note("launch hook error: " + ex.Message); }
        }

        public static void Post_ProjTick(object __instance)
        {
            if (!TelemetryStore.Active) return;
            var store = Store; if (store == null) return;
            var t = (Thing)__instance;
            var p = fExactPos(__instance);
            var s = new ProjSample
            {
                tick = Tick, id = t.thingIDNumber, x = p.x, y = p.y, z = p.z, height = p.y,
                speed = fShotSpeed(__instance), angle = fShotAngle(__instance), rot = fShotRot(__instance),
                flight = fFlightTicks(__instance), tti = fTti(__instance),
            };
            store.Proj.TryPush(s);
        }

        public static void Post_Impact(object __instance, Thing hitThing)
        {
            if (!TelemetryStore.Active) return;
            try
            {
                var store = Store; if (store == null) return;
                var t = (Thing)__instance;
                var p = fExactPos(__instance);
                store.PushEvent(new Evt
                {
                    tick = Tick, type = EvType.impact, id = t.thingIDNumber, otherId = hitThing?.thingIDNumber ?? -1,
                    x = p.x, y = p.y, z = p.z, f1 = fShotSpeed(__instance), f2 = fFlightTicks(__instance),
                    detail = hitThing != null ? hitThing.def.defName + (hitThing is Pawn pw ? "|pawn|" + (pw.Dead ? "dead" : pw.Downed ? "downed" : "up") : "") : "ground",
                });
            }
            catch (Exception ex) { Note("impact hook error: " + ex.Message); }
        }

        public static void Pre_Destroy(ThingWithComps __instance)
        {
            if (!TelemetryStore.Active || __instance == null || !tProj.IsInstanceOfType(__instance)) return;
            try
            {
                var store = Store; if (store == null) return;
                var p = fExactPos(__instance);
                store.PushEvent(new Evt { tick = Tick, type = EvType.destroy, id = __instance.thingIDNumber, otherId = -1, x = p.x, y = p.y, z = p.z, f1 = fFlightTicks(__instance), detail = __instance.def.defName });
                launched.Remove(__instance.thingIDNumber);
            }
            catch (Exception ex) { Note("destroy hook error: " + ex.Message); }
        }

        // ------------------------------------------------------------ turret hooks

        public static void Post_TurretTick(object __instance)
        {
            if (!TelemetryStore.Active) return;
            var store = Store; if (store == null) return;
            var turret = (Building_Turret)__instance;
            int id = turret.thingIDNumber;
            var target = turret.CurrentTarget;
            int warm = fWarmup(__instance), cool = fCooldown(__instance);
            bool reloading = fIsReloading(__instance);
            int mag = -1;
            var ammo = gCompAmmo(__instance);
            if (ammo != null && gCurMagCount != null) mag = gCurMagCount(ammo);
            var cur = new TurretSample
            {
                tick = Tick, id = id, rot = TopRot(__instance),
                targetId = target.IsValid ? (target.HasThing ? target.Thing.thingIDNumber : 0) : -1,
                tx = target.IsValid ? target.Cell.x : -1, tz = target.IsValid ? target.Cell.z : -1,
                warmup = warm, cooldown = cool, mag = mag,
                flags = (warm > 0 ? 1 : 0) | (cool > 0 ? 2 : 0) | (target.IsValid ? 4 : 0) | (reloading ? 8 : 0),
            };
            bool emit;
            if (!lastTurret.TryGetValue(id, out var last)) emit = true;
            else
            {
                emit = Mathf.Abs(cur.rot - last.rot) > RotationEpsilon
                    || cur.targetId != last.targetId || cur.tx != last.tx || cur.tz != last.tz
                    || (cur.flags & ~4) != (last.flags & ~4)
                    || cur.mag != last.mag;
                if (cur.targetId != last.targetId || cur.tx != last.tx || cur.tz != last.tz)
                {
                    store.PushEvent(new Evt { tick = cur.tick, type = EvType.target_change, id = id, otherId = cur.targetId, tx = cur.tx, tz = cur.tz, x = turret.Position.x, z = turret.Position.z, detail = target.IsValid && target.HasThing ? target.Thing.def.defName : (target.IsValid ? "cell" : "none") });
                }
            }
            if (emit)
            {
                store.Turret.TryPush(cur);
                lastTurret[id] = cur;
            }
        }

        public static void Post_BeginBurst(object __instance)
        {
            if (!TelemetryStore.Active) return;
            try
            {
                var store = Store; if (store == null) return;
                var turret = (Building_Turret)__instance;
                var target = turret.CurrentTarget;
                int shots = -1;
                try { shots = turret.AttackVerb?.verbProps?.burstShotCount ?? -1; } catch { }
                store.PushEvent(new Evt
                {
                    tick = Tick, type = EvType.burst_begin, id = turret.thingIDNumber, otherId = target.IsValid && target.HasThing ? target.Thing.thingIDNumber : -1,
                    x = turret.Position.x, z = turret.Position.z, f1 = shots, f2 = TopRot(__instance),
                    tx = target.IsValid ? target.Cell.x : -1, tz = target.IsValid ? target.Cell.z : -1,
                    detail = turret.def.defName + "|verb=" + (turret.AttackVerb?.GetType().Name ?? "null") + "|target=" + (target.IsValid ? (target.HasThing ? target.Thing.def.defName : "cell") : "none"),
                });
            }
            catch (Exception ex) { Note("burst_begin hook error: " + ex.Message); }
        }

        public static void Post_BurstComplete(object __instance)
        {
            if (!TelemetryStore.Active) return;
            try
            {
                var store = Store; if (store == null) return;
                var turret = (Building_Turret)__instance;
                int mag = -1; var ammo = gCompAmmo(__instance); if (ammo != null && gCurMagCount != null) mag = gCurMagCount(ammo);
                store.PushEvent(new Evt { tick = Tick, type = EvType.burst_complete, id = turret.thingIDNumber, otherId = -1, x = turret.Position.x, z = turret.Position.z, f1 = fCooldown(__instance), f2 = mag, detail = turret.def.defName });
            }
            catch (Exception ex) { Note("burst_complete hook error: " + ex.Message); }
        }

        public static void Post_LoadAmmo(object __instance)
        {
            if (!TelemetryStore.Active) return;
            try
            {
                var store = Store; if (store == null) return;
                var comp = (ThingComp)__instance;
                var gun = comp.parent;
                // The CompAmmoUser lives on the turret's gun thing; find the turret holding it, if any.
                Thing holder = gun;
                int turretId = -1;
                if (gun != null && !gun.Spawned)
                {
                    var map = Find.CurrentMap;
                    // Cheap heuristic: a Building_TurretGun whose gun == this comp's parent.
                    if (map != null && gGun != null)
                        foreach (var b in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
                            if (tTurret.IsInstanceOfType(b) && gGun(b) == gun) { holder = b; turretId = b.thingIDNumber; break; }
                }
                int mag = gCurMagCount != null ? gCurMagCount(__instance) : -1;
                var ammoDef = gCurrentAmmo?.Invoke(__instance);
                store.PushEvent(new Evt { tick = Tick, type = EvType.reload, id = turretId >= 0 ? turretId : (gun?.thingIDNumber ?? -1), otherId = gun?.thingIDNumber ?? -1, f1 = mag, x = holder?.Position.x ?? 0, z = holder?.Position.z ?? 0, detail = (ammoDef?.defName ?? "?") + "|gun=" + (gun?.def?.defName ?? "?") });
            }
            catch (Exception ex) { Note("reload hook error: " + ex.Message); }
        }

        [ThreadStatic] static int swapOldId; [ThreadStatic] static IntVec3 swapCell; [ThreadStatic] static Map swapMap; [ThreadStatic] static string swapOldDef;
        public static void Pre_ModeSwap(object __instance)
        {
            try
            {
                var comp = (ThingComp)__instance;
                swapOldId = comp.parent?.thingIDNumber ?? -1; swapCell = comp.parent?.Position ?? IntVec3.Invalid; swapMap = comp.parent?.Map; swapOldDef = comp.parent?.def?.defName;
            }
            catch { }
        }
        public static void Post_ModeSwap(object __instance)
        {
            if (!TelemetryStore.Active) return;
            try
            {
                var store = Store; if (store == null) return;
                Thing replacement = null;
                if (swapMap != null && swapCell.IsValid)
                    foreach (var th in swapMap.thingGrid.ThingsListAt(swapCell)) if (th is Building_Turret && th.thingIDNumber != swapOldId) { replacement = th; break; }
                store.PushEvent(new Evt { tick = Tick, type = EvType.mode_swap, id = swapOldId, otherId = replacement?.thingIDNumber ?? -1, x = swapCell.x, z = swapCell.z, detail = swapOldDef + "->" + (replacement?.def?.defName ?? "none") });
                if (replacement != null) lastTurret.Remove(swapOldId);
            }
            catch (Exception ex) { Note("mode_swap hook error: " + ex.Message); }
        }

        static float TopRot(object turret) { var top = gTop(turret); return top != null ? top.CurRotation : 0f; }

        public static void Note(string text)
        {
            var store = Store; if (store == null) return;
            store.PushEvent(new Evt { tick = Tick, type = EvType.note, id = -1, otherId = -1, detail = text });
        }
    }
}
