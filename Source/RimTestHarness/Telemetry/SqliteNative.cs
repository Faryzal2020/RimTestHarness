using System;
using System.IO;
using System.Runtime.InteropServices;
using SQLitePCL;

namespace RimTestHarness.Telemetry
{
    /// <summary>
    /// Loads the bundled e_sqlite3 native library from the mod's Native/ folder and registers it with SQLitePCLRaw
    /// through SQLite3Provider_dynamic_cdecl, mirroring RimSearcher_DataMod's NativeLibrary.Initialize (decompiled
    /// RimSearcher.DataMod/NativeLibrary.cs). RimSearcher uses dlopen/LoadLibrary P/Invoke; on CoreCLR the equivalent
    /// System.Runtime.InteropServices.NativeLibrary API is used, with a dlopen fallback.
    /// </summary>
    public static class SqliteNative
    {
        sealed class Resolver : IGetFunctionPointer
        {
            readonly IntPtr handle;
            public Resolver(IntPtr h) { handle = h; }
            public IntPtr GetFunctionPointer(string name)
            {
                if (NativeLibrary.TryGetExport(handle, name, out var p)) return p;
                return IntPtr.Zero;
            }
        }

        static bool initialized;
        public static string LoadedPath;

        public static void Initialize(string modRoot)
        {
            if (initialized) return;
            string file;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) file = "e_sqlite3.dll";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) file = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "libe_sqlite3.arm64.dylib" : "libe_sqlite3.dylib";
            else file = "libe_sqlite3.so";
            var path = Path.Combine(modRoot, "Native", file);
            if (!File.Exists(path)) throw new FileNotFoundException("SQLite native library not found", path);
            IntPtr handle;
            try { handle = NativeLibrary.Load(path); }
            catch (Exception ex) { throw new InvalidOperationException("NativeLibrary.Load failed for " + path, ex); }
            SQLite3Provider_dynamic_cdecl.Setup("e_sqlite3", new Resolver(handle));
            raw.SetProvider(new SQLite3Provider_dynamic_cdecl());
            LoadedPath = path;
            initialized = true;
        }
    }
}
