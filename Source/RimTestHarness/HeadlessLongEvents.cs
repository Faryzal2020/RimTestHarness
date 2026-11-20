using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimTestHarness
{
    /// <summary>
    /// Headless-only fix for synchronous long events that wait to be drawn before they run.
    ///
    /// A synchronous long event with text and no enumerator (QueuedLongEvent.UseStandardWindow) is not executed until
    /// QueuedLongEvent.alreadyDisplayed is true (ShouldWaitUntilDisplayed, checked first in
    /// LongEventHandler.UpdateCurrentSynchronousEvent). Only DrawLongEventWindowContents sets that flag, and only while
    /// handling an OnGUI repaint event (decompiled Verse/LongEventHandler.cs). Headless there is no repaint, so such an
    /// event stays the current event forever: AnyEventNowOrWaiting (and ForcePause) stay true, the game stays paused and
    /// the harness refuses `tick`/`save_game`. The daily autosave hits this every 60000 ticks
    /// (RimWorld.Autosaver.AutosaverTick queues DoAutosave as "Autosaving", doAsynchronously: false).
    ///
    /// A prefix on LongEventHandler.LongEventsUpdate (called from Root.Update every frame) marks the current event as
    /// displayed, which is what the missing repaint would have done; the event then runs on that same update.
    /// Enabled only with -Headless; windowed runs draw the window and are left untouched.
    /// </summary>
    public static class HeadlessLongEvents
    {
        public static bool Enabled;
        public static int Released;
        public static string LastReleased;

        static FieldInfo currentEventField;
        static FieldInfo alreadyDisplayedField;
        static PropertyInfo shouldWaitProp;
        static FieldInfo eventTextKeyField;

        public static void TryInstall(Harmony harmony)
        {
            if (!GenCommandLine.CommandLineArgPassed("Headless")) return;
            currentEventField = AccessTools.Field(typeof(LongEventHandler), "currentEvent");
            var evType = AccessTools.Inner(typeof(LongEventHandler), "QueuedLongEvent");
            alreadyDisplayedField = evType == null ? null : AccessTools.Field(evType, "alreadyDisplayed");
            shouldWaitProp = evType == null ? null : AccessTools.Property(evType, "ShouldWaitUntilDisplayed");
            eventTextKeyField = evType == null ? null : AccessTools.Field(evType, "eventTextKey");
            var target = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.LongEventsUpdate));
            if (currentEventField == null || alreadyDisplayedField == null || shouldWaitProp == null || target == null)
            {
                Log.Warning("[RimTestHarness] headless long-event fix not installed: LongEventHandler members not found");
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(HeadlessLongEvents), nameof(Prefix)));
            Enabled = true;
        }

        // Runs every frame; AnyEventNowOrWaiting is a cheap static check, reflection only while an event exists.
        static void Prefix()
        {
            try
            {
                if (!LongEventHandler.AnyEventNowOrWaiting) return;
                var ev = currentEventField.GetValue(null);
                // Only events that would otherwise block (alreadyDisplayed is read nowhere else).
                if (ev == null || !(bool)shouldWaitProp.GetValue(ev)) return;
                alreadyDisplayedField.SetValue(ev, true);
                Released++;
                LastReleased = eventTextKeyField?.GetValue(ev) as string;
            }
            catch (Exception)
            {
                // Never throw into the game loop.
            }
        }
    }
}
