using System;
using System.Collections.Generic;
using UnityEngine;

namespace RimTestHarness
{
    /// <summary>
    /// Captures every engine log line through Application.logMessageReceivedThreaded, the same hook Verse.Log uses to
    /// fill its own queue (decompiled Verse/Log.cs:91, UnityStubs Application.cs:140). Verse.Log keeps only the last
    /// 1000 messages (LogMessageQueue.maxMessages) and stops at 10,000, so the harness keeps its own cursor-addressable ring.
    /// </summary>
    public static class LogCapture
    {
        public struct Entry
        {
            public long seq;
            public byte type; // 0 message, 1 warning, 2 error
            public string text;
            public string stack;
            public DateTime at;
        }

        const int Capacity = 50000;
        static readonly Entry[] ring = new Entry[Capacity];
        static long next;
        static readonly object gate = new object();
        public static int Errors, Warnings, Messages;
        static bool installed;

        public static void Install()
        {
            if (installed) return;
            installed = true;
            Application.logMessageReceivedThreaded += OnLog;
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            byte t = type switch
            {
                LogType.Log => (byte)0,
                LogType.Warning => (byte)1,
                _ => (byte)2,
            };
            lock (gate)
            {
                ring[next % Capacity] = new Entry { seq = next, type = t, text = condition, stack = stackTrace, at = DateTime.UtcNow };
                next++;
                if (t == 0) Messages++; else if (t == 1) Warnings++; else Errors++;
            }
        }

        public static long Next { get { lock (gate) return next; } }

        public static object Query(long since, int minLevel, int limit, bool includeStack, string contains)
        {
            var list = new List<object>();
            long cursor;
            lock (gate)
            {
                long start = Math.Max(since, next - Capacity);
                if (start < 0) start = 0;
                cursor = start;
                for (long s = start; s < next && list.Count < limit; s++)
                {
                    var e = ring[s % Capacity];
                    cursor = s + 1;
                    if (e.type < minLevel) continue;
                    if (contains != null && (e.text == null || e.text.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                    list.Add(new
                    {
                        seq = e.seq,
                        type = e.type == 0 ? "message" : e.type == 1 ? "warning" : "error",
                        text = e.text,
                        stack = includeStack ? e.stack : null,
                        at = e.at.ToString("o"),
                    });
                }
                return new { entries = list, next = cursor, total = next, errors = Errors, warnings = Warnings, messages = Messages, truncated = since < next - Capacity };
            }
        }
    }
}
