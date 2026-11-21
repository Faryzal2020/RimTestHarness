using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Verse;

namespace RimTestHarness
{
    /// <summary>
    /// Marshals work onto the game's main thread. Jobs are drained by a Harmony postfix on Verse.Root.Update,
    /// which both Root_Entry.Update and Root_Play.Update call via base.Update() every frame, in headless mode too
    /// (decompiled Verse/Root.cs:114, Root_Entry.cs:33, Root_Play.cs:62). It runs even while a LongEvent is active,
    /// so handlers must check game state themselves.
    /// </summary>
    public static class MainThread
    {
        public sealed class Job
        {
            public Func<bool> Step;          // returns true when finished
            public Func<object> Result;      // evaluated after Step returns true
            public TaskCompletionSource<object> Tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            public string Name;
        }

        static readonly ConcurrentQueue<Job> queue = new ConcurrentQueue<Job>();
        static readonly List<Job> running = new List<Job>();
        static readonly List<(long dueMs, Action act)> delayed = new List<(long, Action)>();
        static readonly Stopwatch clock = Stopwatch.StartNew();
        public static long FrameCount;
        public static int MainThreadId = -1;
        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        public static void Install(Harmony harmony)
        {
            var update = AccessTools.Method(typeof(Root), "Update");
            if (update == null) throw new InvalidOperationException("Verse.Root.Update not found");
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(MainThread), nameof(Pump)));
        }

        public static Task<object> Run(string name, Func<object> fn)
        {
            var job = new Job { Name = name };
            object result = null;
            job.Step = () => { result = fn(); return true; };
            job.Result = () => result;
            queue.Enqueue(job);
            return job.Tcs.Task;
        }

        public static Task<object> RunStepped(string name, Func<bool> step, Func<object> result)
        {
            var job = new Job { Name = name, Step = step, Result = result };
            queue.Enqueue(job);
            return job.Tcs.Task;
        }

        public static void RunLater(int delayMs, Action act)
        {
            lock (delayed) delayed.Add((clock.ElapsedMilliseconds + delayMs, act));
        }

        // Harmony postfix on Verse.Root.Update. Must never throw.
        public static void Pump()
        {
            try
            {
                if (MainThreadId < 0) MainThreadId = Thread.CurrentThread.ManagedThreadId;
                if (!UnityEngine.Application.runInBackground)
                {
                    try { Prefs.RunInBackground = true; UnityEngine.Application.runInBackground = true; } catch { }
                }
                FrameCount++;

                while (queue.TryDequeue(out var job))
                {
                    StepJob(job);
                }
                for (int i = running.Count - 1; i >= 0; i--)
                {
                    StepJob(running[i], i);
                }

                if (delayed.Count > 0)
                {
                    List<Action> due = null;
                    lock (delayed)
                    {
                        var now = clock.ElapsedMilliseconds;
                        for (int i = delayed.Count - 1; i >= 0; i--)
                        {
                            if (delayed[i].dueMs <= now)
                            {
                                (due ??= new List<Action>()).Add(delayed[i].act);
                                delayed.RemoveAt(i);
                            }
                        }
                    }
                    if (due != null)
                    {
                        foreach (var a in due) { try { a(); } catch (Exception ex) { Log.Error("[RimTestHarness] delayed action failed: " + ex); } }
                    }
                }
            }
            catch (Exception ex)
            {
                try { Log.Error("[RimTestHarness] main-thread pump error: " + ex); } catch { }
            }
        }

        static void StepJob(Job job, int runningIndex = -1)
        {
            bool done;
            try
            {
                done = job.Step();
            }
            catch (Exception ex)
            {
                if (runningIndex >= 0) running.RemoveAt(runningIndex);
                job.Tcs.TrySetException(ex);
                return;
            }
            if (done)
            {
                if (runningIndex >= 0) running.RemoveAt(runningIndex);
                try { job.Tcs.TrySetResult(job.Result?.Invoke()); }
                catch (Exception ex) { job.Tcs.TrySetException(ex); }
            }
            else if (runningIndex < 0)
            {
                running.Add(job);
            }
        }
    }
}
