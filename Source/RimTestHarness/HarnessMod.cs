using System;
using System.IO;
using HarmonyLib;
using Verse;

namespace RimTestHarness
{
    /// <summary>
    /// Entry point. Verse instantiates Mod subclasses in LoadedModManager.CreateModClasses(), after every
    /// active mod's assemblies were loaded (ModAssemblyHandler.ReloadAll via Assembly.LoadFrom) and before
    /// defs are parsed, so Harmony and (if active) CombatExtended are already loaded here.
    /// </summary>
    public class HarnessMod : Mod
    {
        public const int DefaultPort = 18573;
        public static HarnessMod Instance;
        public static string ModRoot;
        public static Harmony Harmony;
        public static int Port;
        public static string PortFile;

        public HarnessMod(ModContentPack content) : base(content)
        {
            Instance = this;
            ModRoot = content.RootDir;
            try
            {
                Harmony = new Harmony("rimtestharness.harness");
                LogCapture.Install();
                MainThread.Install(Harmony);
                Telemetry.CeProbe.TryInstall(Harmony);
                Telemetry.CeHeadlessShim.TryInstall(Harmony);
                Commands.RegisterAll();

                Port = DefaultPort;
                if (GenCommandLine.TryGetCommandLineArg("TestPort", out var p) && int.TryParse(p, out var parsed))
                {
                    Port = parsed;
                }
                var actualPort = HarnessServer.Start(Port);
                Port = actualPort;
                var saveData = GenFilePaths.SaveDataFolderPath;
                Directory.CreateDirectory(saveData);
                PortFile = Path.Combine(saveData, "harness.port");
                File.WriteAllText(PortFile, actualPort.ToString());
                Log.Message($"[RimTestHarness] listening on http://127.0.0.1:{actualPort}/ (port file {PortFile}); CE probe {(Telemetry.CeProbe.Available ? "installed" : "not available")}; CE bounds shim: {Telemetry.CeHeadlessShim.Log}");
            }
            catch (Exception ex)
            {
                Log.Error("[RimTestHarness] failed to start: " + ex);
            }
        }
    }
}
