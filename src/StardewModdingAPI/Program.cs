﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
#if SMAPI_FOR_WINDOWS
using System.Windows.Forms;
#endif
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json;
using StardewModdingAPI.AssemblyRewriters;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Logging;
using StardewModdingAPI.Framework.Models;
using StardewModdingAPI.Framework.Serialisation;
using StardewValley;
using Monitor = StardewModdingAPI.Framework.Monitor;

namespace StardewModdingAPI
{
    /// <summary>The main entry point for SMAPI, responsible for hooking into and launching the game.</summary>
    internal class Program
    {
        /*********
        ** Properties
        *********/
        /// <summary>The target game platform.</summary>
        private static readonly Platform TargetPlatform =
#if SMAPI_FOR_WINDOWS
        Platform.Windows;
#else
        Platform.Mono;
#endif

        /// <summary>The full path to the Stardew Valley executable.</summary>
        private static readonly string GameExecutablePath = Path.Combine(Constants.ExecutionPath, Program.TargetPlatform == Platform.Windows ? "Stardew Valley.exe" : "StardewValley.exe");

        /// <summary>The full path to the folder containing mods.</summary>
        private static readonly string ModPath = Path.Combine(Constants.ExecutionPath, "Mods");

        /// <summary>The log file to which to write messages.</summary>
        private static readonly LogFileManager LogFile = new LogFileManager(Constants.LogPath);

        /// <summary>Manages console output interception.</summary>
        private static readonly ConsoleInterceptionManager ConsoleManager = new ConsoleInterceptionManager();

        /// <summary>The core logger for SMAPI.</summary>
        private static readonly Monitor Monitor = new Monitor("SMAPI", Program.ConsoleManager, Program.LogFile);

        /// <summary>The user settings for SMAPI.</summary>
        private static UserSettings Settings;

        /// <summary>Tracks whether the game should exit immediately and any pending initialisation should be cancelled.</summary>
        private static readonly CancellationTokenSource CancellationTokenSource = new CancellationTokenSource();

        /// <summary>Whether the game is currently running.</summary>
        private static bool ready;

        /// <summary>The underlying game assembly.</summary>
        private static Assembly StardewAssembly;

        /// <summary>The underlying <see cref="StardewValley.Program"/> type.</summary>
        private static Type StardewProgramType;

        /// <summary>The field containing game's main instance.</summary>
        private static FieldInfo StardewGameInfo;


        /*********
        ** Accessors
        *********/
        /// <summary>The underlying game instance.</summary>
        internal static SGame gamePtr;

        /// <summary>The number of mods currently loaded by SMAPI.</summary>
        internal static int ModsLoaded;

        /// <summary>Tracks the installed mods.</summary>
        internal static readonly ModRegistry ModRegistry = new ModRegistry();

        /// <summary>Manages deprecation warnings.</summary>
        internal static readonly DeprecationManager DeprecationManager = new DeprecationManager(Program.Monitor, Program.ModRegistry);

        /// <summary>Manages console commands.</summary>
        internal static readonly CommandManager CommandManager = new CommandManager();


        /*********
        ** Public methods
        *********/
        /// <summary>The main entry point which hooks into and launches the game.</summary>
        /// <param name="args">The command-line arguments.</param>
        private static void Main(string[] args)
        {
            // initialise logging
            Program.Monitor.WriteToConsole = !args.Contains("--no-terminal");
            Thread.CurrentThread.CurrentCulture = CultureInfo.CreateSpecificCulture("en-GB"); // for consistent log formatting
            Program.Monitor.Log($"SMAPI {Constants.ApiVersion} with Stardew Valley {Game1.version} on {Environment.OSVersion}", LogLevel.Info);

            // read config
            {
                string settingsPath = Constants.ApiConfigPath;
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    Program.Settings = JsonConvert.DeserializeObject<UserSettings>(json);
                }
                else
                    Program.Settings = new UserSettings();

                File.WriteAllText(settingsPath, JsonConvert.SerializeObject(Program.Settings, Formatting.Indented));
            }

            // redirect direct console output
            {
                Monitor monitor = Program.GetSecondaryMonitor("Console.Out");
                monitor.WriteToFile = false; // not useful for troubleshooting mods per discussion
                if (monitor.WriteToConsole)
                    Program.ConsoleManager.OnLineIntercepted += line => monitor.Log(line, LogLevel.Trace);
            }

            // add warning headers
            if (Program.Settings.DeveloperMode)
            {
                Program.Monitor.ShowTraceInConsole = true;
                Program.Monitor.Log($"You configured SMAPI to run in developer mode. The console may be much more verbose. You can disable developer mode by installing the non-developer version of SMAPI, or by editing or deleting {Constants.ApiConfigPath}.", LogLevel.Warn);
            }
            if (!Program.Settings.CheckForUpdates)
                Program.Monitor.Log($"You configured SMAPI to not check for updates. Running an old version of SMAPI is not recommended. You can enable update checks by editing or deleting {Constants.ApiConfigPath}.", LogLevel.Warn);
            if (!Program.Monitor.WriteToConsole)
                Program.Monitor.Log("Writing to the terminal is disabled because the --no-terminal argument was received. This usually means launching the terminal failed.", LogLevel.Warn);

            // print file paths
            Program.Monitor.Log($"Mods go here: {Program.ModPath}");

            // initialise legacy log
            Log.Monitor = Program.GetSecondaryMonitor("legacy mod");
            Log.ModRegistry = Program.ModRegistry;

            // hook into & launch the game
            try
            {
                // verify version
                if (String.Compare(Game1.version, Constants.MinimumGameVersion, StringComparison.InvariantCultureIgnoreCase) < 0)
                {
                    Program.Monitor.Log($"Oops! You're running Stardew Valley {Game1.version}, but the oldest supported version is {Constants.MinimumGameVersion}. Please update your game before using SMAPI. If you're on the Steam beta channel, note that the beta channel may not receive the latest updates.", LogLevel.Error);
                    return;
                }

                // initialise
                Program.Monitor.Log("Loading SMAPI...");
                Console.Title = Constants.ConsoleTitle;
                Program.VerifyPath(Program.ModPath);
                Program.VerifyPath(Constants.LogDir);
                if (!File.Exists(Program.GameExecutablePath))
                {
                    Program.Monitor.Log($"Couldn't find executable: {Program.GameExecutablePath}", LogLevel.Error);
                    Program.PressAnyKeyToExit();
                    return;
                }

                // check for update when game loads
                if (Program.Settings.CheckForUpdates)
                    GameEvents.GameLoaded += (sender, e) => Program.CheckForUpdateAsync();

                // launch game
                Program.StartGame();
            }
            catch (Exception ex)
            {
                Program.Monitor.Log($"Critical error: {ex.GetLogSummary()}", LogLevel.Error);
            }
            Program.PressAnyKeyToExit();
        }

        /// <summary>Immediately exit the game without saving. This should only be invoked when an irrecoverable fatal error happens that risks save corruption or game-breaking bugs.</summary>
        /// <param name="module">The module which requested an immediate exit.</param>
        /// <param name="reason">The reason provided for the shutdown.</param>
        internal static void ExitGameImmediately(string module, string reason)
        {
            Program.Monitor.LogFatal($"{module} requested an immediate game shutdown: {reason}");
            Program.CancellationTokenSource.Cancel();
            if (Program.ready)
            {
                Program.gamePtr.Exiting += (sender, e) => Program.PressAnyKeyToExit();
                Program.gamePtr.Exit();
            }
        }

        /// <summary>Get a monitor for legacy code which doesn't have one passed in.</summary>
        [Obsolete("This method should only be used when needed for backwards compatibility.")]
        internal static IMonitor GetLegacyMonitorForMod()
        {
            string modName = Program.ModRegistry.GetModFromStack() ?? "unknown";
            return Program.GetSecondaryMonitor(modName);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Asynchronously check for a new version of SMAPI, and print a message to the console if an update is available.</summary>
        private static void CheckForUpdateAsync()
        {
            new Thread(() =>
            {
                try
                {
                    GitRelease release = UpdateHelper.GetLatestVersionAsync(Constants.GitHubRepository).Result;
                    ISemanticVersion latestVersion = new SemanticVersion(release.Tag);
                    if (latestVersion.IsNewerThan(Constants.ApiVersion))
                        Program.Monitor.Log($"You can update SMAPI from version {Constants.ApiVersion} to {latestVersion}", LogLevel.Alert);
                }
                catch (Exception ex)
                {
                    Program.Monitor.Log($"Couldn't check for a new version of SMAPI. This won't affect your game, but you may not be notified of new versions if this keeps happening.\n{ex.GetLogSummary()}");
                }
            }).Start();
        }

        /// <summary>Hook into Stardew Valley and launch the game.</summary>
        private static void StartGame()
        {
            try
            {
                // load the game assembly
                Program.Monitor.Log("Loading game...");
                Program.StardewAssembly = Assembly.UnsafeLoadFrom(Program.GameExecutablePath);
                Program.StardewProgramType = Program.StardewAssembly.GetType("StardewValley.Program", true);
                Program.StardewGameInfo = Program.StardewProgramType.GetField("gamePtr");
                Game1.version += $" | SMAPI {Constants.ApiVersion}";

                // add error interceptors
#if SMAPI_FOR_WINDOWS
                Application.ThreadException += (sender, e) => Program.Monitor.Log($"Critical thread exception: {e.Exception.GetLogSummary()}", LogLevel.Error);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
#endif
                AppDomain.CurrentDomain.UnhandledException += (sender, e) => Program.Monitor.Log($"Critical app domain exception: {e.ExceptionObject}", LogLevel.Error);

                // initialise game instance
                Program.gamePtr = new SGame(Program.Monitor) { IsMouseVisible = false };
                Program.gamePtr.Exiting += (sender, e) => Program.ready = false;
                Program.gamePtr.Window.ClientSizeChanged += (sender, e) => GraphicsEvents.InvokeResize(Program.Monitor, sender, e);
                Program.gamePtr.Window.Title = $"Stardew Valley - Version {Game1.version}";
                Program.StardewGameInfo.SetValue(Program.StardewProgramType, Program.gamePtr);

                // patch graphics
                Game1.graphics.GraphicsProfile = GraphicsProfile.HiDef;

                // load mods
                Program.LoadMods();
                if (Program.CancellationTokenSource.IsCancellationRequested)
                {
                    Program.Monitor.Log("Shutdown requested; interrupting initialisation.", LogLevel.Error);
                    return;
                }

                // initialise console after game launches
                new Thread(() =>
                {
                    // wait for the game to load up
                    while (!Program.ready) Thread.Sleep(1000);

                    // register help command
                    Program.CommandManager.Add("SMAPI", "help", "Lists all commands | 'help <cmd>' returns command description", Program.HandleHelpCommand);

                    // listen for command line input
                    Program.Monitor.Log("Starting console...");
                    Program.Monitor.Log("Type 'help' for help, or 'help <cmd>' for a command's usage", LogLevel.Info);
                    Thread consoleInputThread = new Thread(Program.ConsoleInputLoop);
                    consoleInputThread.Start();
                    while (Program.ready)
                        Thread.Sleep(1000 / 10); // Check if the game is still running 10 times a second

                    // abort the console thread, we're closing
                    if (consoleInputThread.ThreadState == ThreadState.Running)
                        consoleInputThread.Abort();
                }).Start();

                // start game loop
                Program.Monitor.Log("Starting game...");
                if (Program.CancellationTokenSource.IsCancellationRequested)
                {
                    Program.Monitor.Log("Shutdown requested; interrupting initialisation.", LogLevel.Error);
                    return;
                }
                try
                {
                    Program.ready = true;
                    Program.gamePtr.Run();
                }
                finally
                {
                    Program.ready = false;
                }
            }
            catch (Exception ex)
            {
                Program.Monitor.Log($"The game encountered a fatal error:\n{ex.GetLogSummary()}", LogLevel.Error);
            }
        }

        /// <summary>Create a directory path if it doesn't exist.</summary>
        /// <param name="path">The directory path.</param>
        private static void VerifyPath(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                Program.Monitor.Log($"Couldn't create a path: {path}\n\n{ex.GetLogSummary()}", LogLevel.Error);
            }
        }

        /// <summary>Load and hook up all mods in the mod directory.</summary>
        private static void LoadMods()
        {
            Program.Monitor.Log("Loading mods...");

            // get JSON helper
            JsonHelper jsonHelper = new JsonHelper();

            // get assembly loader
            AssemblyLoader modAssemblyLoader = new AssemblyLoader(Program.TargetPlatform, Program.Monitor);
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => modAssemblyLoader.ResolveAssembly(e.Name);

            // get known incompatible mods
            IDictionary<string, IncompatibleMod> incompatibleMods;
            try
            {
                incompatibleMods = File.Exists(Constants.ApiModMetadataPath)
                    ? JsonConvert.DeserializeObject<IncompatibleMod[]>(File.ReadAllText(Constants.ApiModMetadataPath)).ToDictionary(p => p.ID, p => p)
                    : new Dictionary<string, IncompatibleMod>(0);
            }
            catch (Exception ex)
            {
                incompatibleMods = new Dictionary<string, IncompatibleMod>();
                Program.Monitor.Log($"Couldn't read metadata file at {Constants.ApiModMetadataPath}. SMAPI will still run, but some features may be disabled.\n{ex}", LogLevel.Warn);
            }

            // load mod assemblies
            List<Action> deprecationWarnings = new List<Action>(); // queue up deprecation warnings to show after mod list
            foreach (string directoryPath in Directory.GetDirectories(Program.ModPath))
            {
                // passthrough empty directories
                DirectoryInfo directory = new DirectoryInfo(directoryPath);
                while (!directory.GetFiles().Any() && directory.GetDirectories().Length == 1)
                    directory = directory.GetDirectories().First();

                // check for cancellation
                if (Program.CancellationTokenSource.IsCancellationRequested)
                {
                    Program.Monitor.Log("Shutdown requested; interrupting mod loading.", LogLevel.Error);
                    return;
                }

                // get manifest path
                string manifestPath = Path.Combine(directory.FullName, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    Program.Monitor.Log($"Ignored folder \"{directory.Name}\" which doesn't have a manifest.json.", LogLevel.Warn);
                    continue;
                }
                string errorPrefix = $"Couldn't load mod for manifest '{manifestPath}'";

                // read manifest
                Manifest manifest;
                try
                {
                    // read manifest text
                    string json = File.ReadAllText(manifestPath);
                    if (string.IsNullOrEmpty(json))
                    {
                        Program.Monitor.Log($"{errorPrefix}: manifest is empty.", LogLevel.Error);
                        continue;
                    }

                    // deserialise manifest
                    manifest = jsonHelper.ReadJsonFile<Manifest>(Path.Combine(directory.FullName, "manifest.json"), null);
                    if (manifest == null)
                    {
                        Program.Monitor.Log($"{errorPrefix}: the manifest file does not exist.", LogLevel.Error);
                        continue;
                    }
                    if (string.IsNullOrEmpty(manifest.EntryDll))
                    {
                        Program.Monitor.Log($"{errorPrefix}: manifest doesn't specify an entry DLL.", LogLevel.Error);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Program.Monitor.Log($"{errorPrefix}: manifest parsing failed.\n{ex.GetLogSummary()}", LogLevel.Error);
                    continue;
                }

                // validate known incompatible mods
                IncompatibleMod compatibility;
                if (incompatibleMods.TryGetValue(!string.IsNullOrWhiteSpace(manifest.UniqueID) ? manifest.UniqueID : manifest.EntryDll, out compatibility))
                {
                    if (!compatibility.IsCompatible(manifest.Version))
                    {
                        string reasonPhrase = compatibility.ReasonPhrase ?? "this version is not compatible with the latest version of the game";
                        string warning = $"Skipped {compatibility.Name} {manifest.Version} because {reasonPhrase}. Please check for a newer version of the mod here:";
                        if (!string.IsNullOrWhiteSpace(compatibility.UpdateUrl))
                            warning += $"{Environment.NewLine}- official mod: {compatibility.UpdateUrl}";
                        if (!string.IsNullOrWhiteSpace(compatibility.UnofficialUpdateUrl))
                            warning += $"{Environment.NewLine}- unofficial update: {compatibility.UnofficialUpdateUrl}";

                        Program.Monitor.Log(warning, LogLevel.Error);
                        continue;
                    }
                }

                // validate SMAPI version
                if (!string.IsNullOrWhiteSpace(manifest.MinimumApiVersion))
                {
                    try
                    {
                        ISemanticVersion minVersion = new SemanticVersion(manifest.MinimumApiVersion);
                        if (minVersion.IsNewerThan(Constants.ApiVersion))
                        {
                            Program.Monitor.Log($"{errorPrefix}: this mod requires SMAPI {minVersion} or later. Please update SMAPI to the latest version to use this mod.", LogLevel.Error);
                            continue;
                        }
                    }
                    catch (FormatException ex) when (ex.Message.Contains("not a valid semantic version"))
                    {
                        Program.Monitor.Log($"{errorPrefix}: the mod specified an invalid minimum SMAPI version '{manifest.MinimumApiVersion}'. This should be a semantic version number like {Constants.ApiVersion}.", LogLevel.Error);
                        continue;
                    }
                }

                // create per-save directory
                if (manifest.PerSaveConfigs)
                {
                    deprecationWarnings.Add(() => Program.DeprecationManager.Warn(manifest.Name, $"{nameof(Manifest)}.{nameof(Manifest.PerSaveConfigs)}", "1.0", DeprecationLevel.Info));
                    try
                    {
                        string psDir = Path.Combine(directory.FullName, "psconfigs");
                        Directory.CreateDirectory(psDir);
                        if (!Directory.Exists(psDir))
                        {
                            Program.Monitor.Log($"{errorPrefix}: couldn't create the per-save configuration directory ('psconfigs') requested by this mod. The failure reason is unknown.", LogLevel.Error);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Monitor.Log($"{errorPrefix}: couldn't create the per-save configuration directory ('psconfigs') requested by this mod.\n{ex.GetLogSummary()}", LogLevel.Error);
                        continue;
                    }
                }

                // validate mod path to simplify errors
                string assemblyPath = Path.Combine(directory.FullName, manifest.EntryDll);
                if (!File.Exists(assemblyPath))
                {
                    Program.Monitor.Log($"{errorPrefix}: the entry DLL '{manifest.EntryDll}' does not exist.", LogLevel.Error);
                    continue;
                }

                // preprocess & load mod assembly
                Assembly modAssembly;
                try
                {
                    modAssembly = modAssemblyLoader.Load(assemblyPath);
                }
                catch (Exception ex)
                {
                    Program.Monitor.Log($"{errorPrefix}: an error occurred while preprocessing '{manifest.EntryDll}'.\n{ex.GetLogSummary()}", LogLevel.Error);
                    continue;
                }

                // validate assembly
                try
                {
                    if (modAssembly.DefinedTypes.Count(x => x.BaseType == typeof(Mod)) == 0)
                    {
                        Program.Monitor.Log($"{errorPrefix}: the mod DLL does not contain an implementation of the 'Mod' class.", LogLevel.Error);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Program.Monitor.Log($"{errorPrefix}: an error occurred while reading the mod DLL.\n{ex.GetLogSummary()}", LogLevel.Error);
                    continue;
                }

                // initialise mod
                Mod mod;
                try
                {
                    // get implementation
                    TypeInfo modEntryType = modAssembly.DefinedTypes.First(x => x.BaseType == typeof(Mod));
                    mod = (Mod)modAssembly.CreateInstance(modEntryType.ToString());
                    if (mod == null)
                    {
                        Program.Monitor.Log($"{errorPrefix}: the mod's entry class could not be instantiated.");
                        continue;
                    }

                    // inject data
                    // get helper
                    mod.ModManifest = manifest;
                    mod.Helper = new ModHelper(manifest.Name, directory.FullName, jsonHelper, Program.ModRegistry, Program.CommandManager);
                    mod.Monitor = Program.GetSecondaryMonitor(manifest.Name);
                    mod.PathOnDisk = directory.FullName;

                    // track mod
                    Program.ModRegistry.Add(mod);
                    Program.ModsLoaded += 1;
                    Program.Monitor.Log($"Loaded mod: {manifest.Name} by {manifest.Author}, v{manifest.Version} | {manifest.Description}", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Program.Monitor.Log($"{errorPrefix}: an error occurred while loading the target DLL.\n{ex.GetLogSummary()}", LogLevel.Error);
                    continue;
                }
            }

            // log deprecation warnings
            foreach (Action warning in deprecationWarnings)
                warning();
            deprecationWarnings = null;

            // initialise mods
            foreach (Mod mod in Program.ModRegistry.GetMods())
            {
                try
                {
                    // call entry methods
                    mod.Entry(); // deprecated since 1.0
                    mod.Entry(mod.Helper);

                    // raise deprecation warning for old Entry() methods
                    if (Program.DeprecationManager.IsVirtualMethodImplemented(mod.GetType(), typeof(Mod), nameof(Mod.Entry), new[] { typeof(object[]) }))
                        Program.DeprecationManager.Warn(mod.ModManifest.Name, $"{nameof(Mod)}.{nameof(Mod.Entry)}(object[]) instead of {nameof(Mod)}.{nameof(Mod.Entry)}({nameof(IModHelper)})", "1.0", DeprecationLevel.Notice);
                }
                catch (Exception ex)
                {
                    Program.Monitor.Log($"The {mod.ModManifest.Name} mod failed on entry initialisation. It will still be loaded, but may not function correctly.\n{ex.GetLogSummary()}", LogLevel.Warn);
                }
            }

            // print result
            Program.Monitor.Log($"Loaded {Program.ModsLoaded} mods.");
            Console.Title = Constants.ConsoleTitle;
        }

        // ReSharper disable once FunctionNeverReturns
        /// <summary>Run a loop handling console input.</summary>
        private static void ConsoleInputLoop()
        {
            while (true)
            {
                string input = Console.ReadLine();
                if (!Program.CommandManager.Trigger(input))
                    Program.Monitor.Log("Unknown command; type 'help' for a list of available commands.", LogLevel.Error);
            }
        }

        /// <summary>The method called when the user submits the help command in the console.</summary>
        /// <param name="name">The command name.</param>
        /// <param name="arguments">The command arguments.</param>
        private static void HandleHelpCommand(string name, string[] arguments)
        {
            if (arguments.Any())
            {

                Framework.Command result = Program.CommandManager.Get(arguments[0]);
                if (result == null)
                    Program.Monitor.Log("There's no command with that name.", LogLevel.Error);
                else
                    Program.Monitor.Log($"{result.Name}: {result.Documentation}\n(Added by {result.ModName}.)", LogLevel.Info);
            }
            else
                Program.Monitor.Log("Commands: " + string.Join(", ", Program.CommandManager.GetAll().Select(p => p.Name)), LogLevel.Info);
        }

        /// <summary>Show a 'press any key to exit' message, and exit when they press a key.</summary>
        private static void PressAnyKeyToExit()
        {
            Program.Monitor.Log("Game has ended. Press any key to exit.", LogLevel.Info);
            Thread.Sleep(100);
            Console.ReadKey();
            Environment.Exit(0);
        }

        /// <summary>Get a monitor instance derived from SMAPI's current settings.</summary>
        /// <param name="name">The name of the module which will log messages with this instance.</param>
        private static Monitor GetSecondaryMonitor(string name)
        {
            return new Monitor(name, Program.ConsoleManager, Program.LogFile) { WriteToConsole = Program.Monitor.WriteToConsole, ShowTraceInConsole = Program.Settings.DeveloperMode };
        }
    }
}
