using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimTestHarness.Telemetry
{
    /// <summary>
    /// Headless compatibility shim for Combat Extended's texture-derived collision bounds.
    /// Without a GPU, CE's BoundsInjector.CollectTextures fails ("AsyncGPUReadback failed for ...") for every pawn/plant
    /// texture, so every later BoundsInjector.BoundMap(graphic, type) call throws "BoundMap(,)" (BoundsInjector.cs:26-50).
    /// That exception propagates out of Verb_LaunchProjectileCE.CanHitTargetFrom -> Building_TurretGunCE.Tick, which aborts
    /// turret targeting on every tick (observed 2026-10-16: "Exception ticking Turret_U20mmFlak38_Base ... BoundMap(,)").
    /// The shim is a Harmony finalizer on BoundMap that swallows the exception and returns a fixed fraction of the sprite
    /// (ExtractBounds returns (widthFraction, heightFraction) of the trimmed texture, later scaled by drawSize in ForPawn /
    /// visualSizeRange in ForPlant). It is enabled only with -Headless (or -CEBoundsShim) and never in windowed runs.
    /// [UNVERIFIED] the 0.75 default is an approximation; real CE bounds depend on each texture's opaque extent.
    /// </summary>
    public static class CeHeadlessShim
    {
        public static bool Enabled;
        public static long ShimmedCalls;
        public static string Log = "";
        public static Vector2 Fallback = new Vector2(0.75f, 0.75f);

        public static void TryInstall(Harmony harmony)
        {
            bool wanted = GenCommandLine.CommandLineArgPassed("Headless") || GenCommandLine.CommandLineArgPassed("CEBoundsShim");
            if (GenCommandLine.CommandLineArgPassed("NoCEBoundsShim")) wanted = false;
            if (!wanted) { Log = "not requested (windowed run or -NoCEBoundsShim)"; return; }
            var t = Reflect.T("CombatExtended.BoundsInjector");
            if (t == null) { Log = "CombatExtended.BoundsInjector not found"; return; }
            var m = AccessTools.Method(t, "BoundMap");
            if (m == null) { Log = "BoundsInjector.BoundMap not found"; return; }
            if (GenCommandLine.TryGetCommandLineArg("CEBoundsFallback", out var fb) && float.TryParse(fb, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                Fallback = new Vector2(f, f);
            harmony.Patch(m, finalizer: new HarmonyMethod(typeof(CeHeadlessShim), nameof(Finalizer)));
            Enabled = true;
            Log = $"BoundsInjector.BoundMap finalizer installed, fallback {Fallback.x}";
        }

        public static Exception Finalizer(Exception __exception, ref Vector2 __result)
        {
            if (__exception == null) return null;
            ShimmedCalls++;
            __result = Fallback;
            return null; // suppress
        }
    }
}
