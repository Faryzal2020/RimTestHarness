using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimTestHarness
{
    /// <summary>
    /// Combat Extended / AMC turret commands. All CE access is by type name through reflection so the harness
    /// loads (and these commands fail with a clear error) when CE is not in the mod list.
    /// Evidence (decompiled CombatExtended.dll, workshop 2890901044):
    ///   Building_TurretGunCE.CompAmmo / CompFireModes properties (Building_TurretGunCE.cs:185,197)
    ///   CompAmmoUser.ResetAmmoCount(AmmoDef newAmmo = null) sets currentAmmoInt/selectedAmmo and CurMagCount = MagSize (CompAmmoUser.cs:885)
    ///   CompAmmoUser.CurMagCount, CurrentAmmo, MagSize, HasMagazine, SelectedAmmo (CompAmmoUser.cs:151,249,85,247,403)
    ///   CompFireModes.CurrentFireMode / CurrentAimMode setters (CompFireModes.cs:79,91); enum FireMode { AutoFire, BurstFire, SingleFire }
    ///   Building_TurretGunCE.OrderAttack(LocalTargetInfo) overrides Building_Turret.OrderAttack (Building_TurretGunCE.cs:335)
    /// AMC: AbsolutelyMoreCannons.CompTurretModeSwap.TrySwapMode() (private) destroys the parent and spawns Props.alternateDef.
    /// </summary>
    public static class TurretCommands
    {
        static Type TurretCE => Reflect.T("CombatExtended.Building_TurretGunCE");
        static Type AmmoDefType => Reflect.T("CombatExtended.AmmoDef");

        static object CompAmmo(Thing t) => TurretCE != null && TurretCE.IsInstanceOfType(t) ? Reflect.Get(t, "CompAmmo") : null;
        static object CompFireModes(Thing t) => TurretCE != null && TurretCE.IsInstanceOfType(t) ? Reflect.Get(t, "CompFireModes") : null;

        public static void AddCeInfo(Building_Turret t, Dictionary<string, object> into)
        {
            if (TurretCE == null || !TurretCE.IsInstanceOfType(t)) return;
            var ammo = CompAmmo(t);
            if (ammo != null)
            {
                into["ammo"] = new
                {
                    magCount = (int)Reflect.Get(ammo, "CurMagCount"),
                    magSize = (int)Reflect.Get(ammo, "MagSize"),
                    hasMagazine = (bool)Reflect.Get(ammo, "HasMagazine"),
                    currentAmmo = (Reflect.Get(ammo, "CurrentAmmo") as Def)?.defName,
                    selectedAmmo = (Reflect.Get(ammo, "SelectedAmmo") as Def)?.defName,
                    useAmmo = (bool)Reflect.Get(ammo, "UseAmmo"),
                    ammoSet = (Reflect.Get(ammo, "CurAmmoSet") as Def)?.defName,
                };
            }
            var fm = CompFireModes(t);
            if (fm != null)
            {
                into["fireMode"] = Reflect.Get(fm, "CurrentFireMode")?.ToString();
                into["aimMode"] = Reflect.Get(fm, "CurrentAimMode")?.ToString();
                into["availableFireModes"] = (Reflect.Get(fm, "AvailableFireModes") as System.Collections.IEnumerable)?.Cast<object>().Select(o => o.ToString()).ToList();
            }
            into["isReloading"] = Reflect.Get(t, "isReloading");
            into["topRotation"] = (Reflect.Get(t, "top") as TurretTop)?.CurRotation;
            into["gun"] = (Reflect.Get(t, "Gun") as Thing)?.def?.defName;
            into["verb"] = t.AttackVerb?.GetType().Name;
            into["warmupTicksLeft"] = Reflect.Get(t, "burstWarmupTicksLeft");
            into["cooldownTicksLeft"] = Reflect.Get(t, "burstCooldownTicksLeft");
            into["active"] = Reflect.Get(t, "Active");
            into["holdFire"] = Reflect.Get(t, "holdFire");
        }

        static Thing ReqTurretCE(Args a)
        {
            if (TurretCE == null) throw new CommandException("Combat Extended is not loaded (type CombatExtended.Building_TurretGunCE not found)");
            var t = Commands.ReqThing(a);
            if (!TurretCE.IsInstanceOfType(t)) throw new CommandException($"thing {t.thingIDNumber} ({t.def.defName}) is a {t.GetType().Name}, not a Building_TurretGunCE");
            return t;
        }

        public static void RegisterAll()
        {
            Commands.Register("turret_ammo", "Fill a CE turret magazine (CompAmmoUser.ResetAmmoCount). args: id, ammoDef (optional; must belong to the turret's ammo set)", a =>
            {
                var t = ReqTurretCE(a);
                var comp = CompAmmo(t) ?? throw new CommandException("turret gun has no CompAmmoUser");
                object ammoDef = null;
                if (a.Has("ammoDef"))
                {
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(a.Str("ammoDef")) ?? throw new CommandException("no ThingDef " + a.Str("ammoDef"));
                    if (AmmoDefType == null || !AmmoDefType.IsInstanceOfType(def)) throw new CommandException(def.defName + " is not a CombatExtended.AmmoDef");
                    var set = Reflect.Get(comp, "CurAmmoSet");
                    if (set != null)
                    {
                        var links = Reflect.Get(set, "ammoTypes") as System.Collections.IEnumerable;
                        var allowed = links?.Cast<object>().Select(l => (Reflect.Get(l, "ammo") as Def)?.defName).ToList();
                        if (allowed != null && !allowed.Contains(def.defName))
                            throw new CommandException($"{def.defName} is not in ammo set {((Def)set).defName}. Allowed: {string.Join(", ", allowed)}");
                    }
                    ammoDef = def;
                }
                Reflect.Call(comp, "ResetAmmoCount", ammoDef);
                var d = new Dictionary<string, object>();
                AddCeInfo((Building_Turret)t, d);
                return d;
            });

            Commands.Register("turret_fire_mode", "Set CE fire/aim mode. args: id, mode (AutoFire|BurstFire|SingleFire), aimMode (optional)", a =>
            {
                var t = ReqTurretCE(a);
                var fm = CompFireModes(t) ?? throw new CommandException("turret gun has no CompFireModes");
                if (a.Has("mode"))
                {
                    var enumType = Reflect.T("CombatExtended.FireMode");
                    var val = Enum.Parse(enumType, a.Str("mode"), true);
                    var available = Reflect.Get(fm, "AvailableFireModes") as System.Collections.IList;
                    if (available != null && !available.Contains(val)) throw new CommandException($"fire mode {val} not available; options: {string.Join(", ", available.Cast<object>())}");
                    Reflect.Set(fm, "CurrentFireMode", val);
                }
                if (a.Has("aimMode"))
                {
                    var enumType = Reflect.T("CombatExtended.AimMode");
                    Reflect.Set(fm, "CurrentAimMode", Enum.Parse(enumType, a.Str("aimMode"), true));
                }
                var d = new Dictionary<string, object>();
                AddCeInfo((Building_Turret)t, d);
                return d;
            });

            Commands.Register("turret_order_attack", "Force a turret to attack (Building_Turret.OrderAttack). args: id, targetId | x+z; pass clear=true to reset", a =>
            {
                var t = Commands.ReqThing(a) as Building_Turret ?? throw new CommandException("not a Building_Turret");
                LocalTargetInfo target;
                if (a.Bool("clear", false)) target = LocalTargetInfo.Invalid;
                else if (a.Has("targetId")) target = new LocalTargetInfo(Commands.ReqThing(a, "targetId"));
                else target = new LocalTargetInfo(Commands.CellArg(a, t.Map));
                t.OrderAttack(target);
                return new { id = t.thingIDNumber, forcedTarget = Commands.TargetSummary(t.ForcedTarget), currentTarget = Commands.TargetSummary(t.CurrentTarget) };
            });

            Commands.Register("turret_fcs", "Install an AMC Fire Control System item into a turret's CompTurretFCS (mirrors JobDriver_LoadTurretFCS: TryAdd into GetDirectlyHeldThings()). args: id, defName (AMC_FCS_Basic)", a =>
            {
                var t = Commands.ReqThing(a) as ThingWithComps ?? throw new CommandException("not a ThingWithComps");
                var comp = t.AllComps.FirstOrDefault(c => c.GetType().FullName == "AbsolutelyMoreCannons.CompTurretFCS")
                    ?? throw new CommandException("thing has no AbsolutelyMoreCannons.CompTurretFCS comp. Comps: " + string.Join(", ", t.AllComps.Select(c => c.GetType().Name)));
                var defName = a.Str("defName", "AMC_FCS_Basic");
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName) ?? throw new CommandException("no ThingDef " + defName);
                var holder = (IThingHolder)comp;
                var container = holder.GetDirectlyHeldThings();
                if (container.Count > 0 && !a.Bool("replace", false)) throw new CommandException("turret already has FCS " + container[0].def.defName + " (pass replace=true)");
                if (container.Count > 0) container.ClearAndDestroyContents();
                var item = ThingMaker.MakeThing(def);
                bool ok = container.TryAdd(item);
                Reflect.Set(comp, "targetFCSDef", null);
                return new { id = t.thingIDNumber, added = ok, fcs = (Reflect.Get(comp, "LoadedFCSItem") as Thing)?.def?.defName, inspect = comp.CompInspectStringExtra() };
            });

            Commands.Register("turret_mode_swap", "Invoke AMC CompTurretModeSwap.TrySwapMode (destroys and respawns alternateDef). args: id", a =>
            {
                var t = Commands.ReqThing(a) as ThingWithComps ?? throw new CommandException("not a ThingWithComps");
                var comp = t.AllComps.FirstOrDefault(c => c.GetType().FullName == "AbsolutelyMoreCannons.CompTurretModeSwap")
                    ?? throw new CommandException("thing has no AbsolutelyMoreCannons.CompTurretModeSwap comp. Comps: " + string.Join(", ", t.AllComps.Select(c => c.GetType().Name)));
                var map = t.Map; var cell = t.Position; int oldId = t.thingIDNumber;
                Reflect.Call(comp, "TrySwapMode");
                var replacement = map.thingGrid.ThingsListAt(cell).FirstOrDefault(x => x is Building_Turret && x.thingIDNumber != oldId);
                return new { oldId, oldDestroyed = t.Destroyed, replacement = replacement != null ? Commands.ThingSummary(replacement) : null };
            });
        }
    }
}
