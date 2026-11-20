using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RimTestHarness
{
    /// <summary>Typed access to a JSON argument object. Missing required values raise CommandException.</summary>
    public sealed class Args
    {
        public readonly JsonObject O;
        public Args(JsonObject o) { O = o ?? new JsonObject(); }
        public bool Has(string k) => O[k] != null;

        public string Str(string k, string def = null)
        {
            var n = O[k];
            if (n == null) return def;
            if (n is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s)) return s;
                return v.ToJsonString().Trim('"');
            }
            return n.ToJsonString();
        }
        public string Req(string k) => Str(k) ?? throw new CommandException($"missing argument '{k}'");

        public int Int(string k, int def)
        {
            var n = O[k];
            if (n == null) return def;
            if (n is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l)) return (int)l;
                if (v.TryGetValue<double>(out var d)) return (int)d;
                if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var i)) return i;
            }
            throw new CommandException($"argument '{k}' must be an integer");
        }
        public int ReqInt(string k) => Has(k) ? Int(k, 0) : throw new CommandException($"missing argument '{k}'");
        public int? IntOrNull(string k) => Has(k) ? Int(k, 0) : (int?)null;

        public long Long(string k, long def)
        {
            var n = O[k];
            if (n == null) return def;
            if (n is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<double>(out var d)) return (long)d;
                if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var i)) return i;
            }
            throw new CommandException($"argument '{k}' must be an integer");
        }

        public float Float(string k, float def)
        {
            var n = O[k];
            if (n == null) return def;
            if (n is JsonValue v)
            {
                if (v.TryGetValue<double>(out var d)) return (float)d;
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<string>(out var s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f;
            }
            throw new CommandException($"argument '{k}' must be a number");
        }

        public bool Bool(string k, bool def)
        {
            var n = O[k];
            if (n == null) return def;
            if (n is JsonValue v)
            {
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<long>(out var l)) return l != 0;
                if (v.TryGetValue<string>(out var s)) return s == "true" || s == "1" || s == "yes" || s == "on";
            }
            throw new CommandException($"argument '{k}' must be a boolean");
        }

        public string[] StrList(string k)
        {
            var n = O[k];
            if (n == null) return null;
            if (n is JsonArray a)
            {
                var r = new string[a.Count];
                for (int i = 0; i < a.Count; i++) r[i] = a[i]?.ToString();
                return r;
            }
            return Str(k).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }
}
