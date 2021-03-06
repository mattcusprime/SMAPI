﻿using System;
using System.Collections.Generic;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework;

namespace StardewModdingAPI
{
    /// <summary>A command that can be submitted through the SMAPI console to interact with SMAPI.</summary>
    [Obsolete("Use " + nameof(IModHelper) + "." + nameof(IModHelper.ConsoleCommands))]
    public class Command
    {
        /*********
        ** Properties
        *********/
        /****
        ** SMAPI
        ****/
        /// <summary>The commands registered with SMAPI.</summary>
        private static readonly IDictionary<string, Command> LegacyCommands = new Dictionary<string, Command>(StringComparer.InvariantCultureIgnoreCase);

        /// <summary>The event raised when this command is submitted through the console.</summary>
        public event EventHandler<EventArgsCommand> CommandFired;

        /****
        ** Command
        ****/
        /// <summary>The name of the command.</summary>
        public string CommandName;

        /// <summary>A human-readable description of what the command does.</summary>
        public string CommandDesc;

        /// <summary>A human-readable list of accepted arguments.</summary>
        public string[] CommandArgs;

        /// <summary>The actual submitted argument values.</summary>
        public string[] CalledArgs;


        /*********
        ** Public methods
        *********/
        /****
        ** Command
        ****/
        /// <summary>Construct an instance.</summary>
        /// <param name="name">The name of the command.</param>
        /// <param name="description">A human-readable description of what the command does.</param>
        /// <param name="args">A human-readable list of accepted arguments.</param>
        public Command(string name, string description, string[] args = null)
        {
            this.CommandName = name;
            this.CommandDesc = description;
            if (args == null)
                args = new string[0];
            this.CommandArgs = args;
        }

        /// <summary>Trigger this command.</summary>
        public void Fire()
        {
            if (this.CommandFired == null)
                throw new InvalidOperationException($"Can't run command '{this.CommandName}' because it has no registered handler.");
            this.CommandFired.Invoke(this, new EventArgsCommand(this));
        }


        /****
        ** SMAPI
        ****/
        /// <summary>Parse a command string and invoke it if valid.</summary>
        /// <param name="input">The command to run, including the command name and any arguments.</param>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        public static void CallCommand(string input, IMonitor monitor)
        {
            Program.DeprecationManager.Warn("Command.CallCommand", "1.9", DeprecationLevel.Notice);
            Program.CommandManager.Trigger(input);
        }

        /// <summary>Register a command with SMAPI.</summary>
        /// <param name="name">The name of the command.</param>
        /// <param name="description">A human-readable description of what the command does.</param>
        /// <param name="args">A human-readable list of accepted arguments.</param>
        public static Command RegisterCommand(string name, string description, string[] args = null)
        {
            name = name?.Trim().ToLower();

            // raise deprecation warning
            Program.DeprecationManager.Warn("Command.RegisterCommand", "1.9", DeprecationLevel.Notice);

            // validate
            if (Command.LegacyCommands.ContainsKey(name))
                throw new InvalidOperationException($"The '{name}' command is already registered!");

            // add command
            string modName = Program.ModRegistry.GetModFromStack() ?? "<unknown mod>";
            string documentation = args?.Length > 0
                ? $"{description} - {string.Join(", ", args)}"
                : description;
            Program.CommandManager.Add(modName, name, documentation, Command.Fire);

            // add legacy command
            Command command = new Command(name, description, args);
            Command.LegacyCommands.Add(name, command);
            return command;
        }

        /// <summary>Find a command with the given name.</summary>
        /// <param name="name">The command name to find.</param>
        public static Command FindCommand(string name)
        {
            Program.DeprecationManager.Warn("Command.FindCommand", "1.9", DeprecationLevel.Notice);
            if (name == null)
                return null;

            Command command;
            Command.LegacyCommands.TryGetValue(name.Trim(), out command);
            return command;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Trigger this command.</summary>
        /// <param name="name">The command name.</param>
        /// <param name="args">The command arguments.</param>
        private static void Fire(string name, string[] args)
        {
            Command command;
            if (!Command.LegacyCommands.TryGetValue(name, out command))
                throw new InvalidOperationException($"Can't run command '{name}' because there's no such legacy command.");
            command.Fire();
        }
    }
}
