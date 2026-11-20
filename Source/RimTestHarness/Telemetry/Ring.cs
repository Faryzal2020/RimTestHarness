using System.Threading;

namespace RimTestHarness.Telemetry
{
    /// <summary>Single-producer (main thread) / single-consumer (flush thread) ring of structs. No allocation after construction.</summary>
    public sealed class Ring<T> where T : struct
    {
        readonly T[] buf;
        readonly int mask;
        long head; // written by producer
        long tail; // written by consumer
        public long Dropped;

        public Ring(int capacityPow2)
        {
            buf = new T[capacityPow2];
            mask = capacityPow2 - 1;
            if ((capacityPow2 & mask) != 0) throw new System.ArgumentException("capacity must be a power of two");
        }

        public int Capacity => buf.Length;
        public long Count => Volatile.Read(ref head) - Volatile.Read(ref tail);

        public bool TryPush(in T item)
        {
            long h = head;
            if (h - Volatile.Read(ref tail) >= buf.Length) { Dropped++; return false; }
            buf[h & mask] = item;
            Volatile.Write(ref head, h + 1);
            return true;
        }

        public bool TryPop(out T item)
        {
            long t = tail;
            if (t >= Volatile.Read(ref head)) { item = default; return false; }
            item = buf[t & mask];
            Volatile.Write(ref tail, t + 1);
            return true;
        }
    }

    public struct ProjSample
    {
        public int tick, id;
        public float x, y, z, height, speed, angle, rot;
        public int flight, tti;
    }

    public struct TurretSample
    {
        public int tick, id;
        public float rot;
        public int targetId, tx, tz, warmup, cooldown, mag, flags; // flags: 1 warming up, 2 cooling down, 4 has target, 8 reloading
    }

    public enum EvType : byte { launch, impact, destroy, burst_begin, burst_complete, target_change, reload, mode_swap, note }

    public struct Evt
    {
        public int tick;
        public EvType type;
        public int id, otherId;
        public float x, y, z, f1, f2, tx, tz;
        public string detail;
    }
}
