// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

namespace Nova.Common.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Xml;

    /// <summary>
    /// Resolves an order command's wire "Type" to a concrete <see cref="ICommand"/>
    /// (design Section E, "Command registry (retire the hardcoded switch)").
    ///
    /// The desktop console dispatched command types with a hardcoded switch in
    /// OrderReader. Every new command type meant editing that switch, and there was
    /// no shared dispatch for the cloud API or the AI JSON adapter. This registry is
    /// the single dispatch point: the XML order path (OrderReader) and the API's
    /// order-ingestion path both resolve through it, so adding a command type is a
    /// registration, not a switch edit.
    ///
    /// Built-in types register at construction. External command types (community
    /// or LLM participant commands) register through <see cref="Register"/>.
    /// </summary>
    public sealed class CommandRegistry
    {
        private static readonly CommandRegistry instance = new CommandRegistry();

        /// <summary>The process-wide registry.</summary>
        public static CommandRegistry Instance
        {
            get { return instance; }
        }

        private readonly Dictionary<string, Func<XmlNode, ICommand>> factories =
            new Dictionary<string, Func<XmlNode, ICommand>>(StringComparer.OrdinalIgnoreCase);

        private CommandRegistry()
        {
            RegisterBuiltIns();
        }

        /// <summary>
        /// Register (or replace) the factory for a command wire type. Type match is
        /// case-insensitive, matching the old switch's ToLower dispatch.
        /// </summary>
        public void Register(string type, Func<XmlNode, ICommand> factory)
        {
            if (string.IsNullOrEmpty(type))
            {
                throw new ArgumentException("Command type must be non-empty.", "type");
            }
            if (factory == null)
            {
                throw new ArgumentNullException("factory");
            }
            factories[type] = factory;
        }

        /// <summary>True if a factory is registered for the wire type.</summary>
        public bool IsRegistered(string type)
        {
            return type != null && factories.ContainsKey(type);
        }

        /// <summary>Known command wire types, for diagnostics and API validation.</summary>
        public IEnumerable<string> KnownTypes
        {
            get { return factories.Keys; }
        }

        /// <summary>
        /// Build a command from its XML node. Throws <see cref="UnknownCommandException"/>
        /// if the type is not registered, so the caller (API) can map it to a 400.
        /// </summary>
        public ICommand Create(string type, XmlNode node)
        {
            Func<XmlNode, ICommand> factory;
            if (type != null && factories.TryGetValue(type, out factory))
            {
                return factory(node);
            }
            throw new UnknownCommandException(type);
        }

        private void RegisterBuiltIns()
        {
            Register("research", node => new ResearchCommand(node));
            Register("waypoint", node => new WaypointCommand(node));
            Register("design", node => new DesignCommand(node));
            Register("production", node => new ProductionCommand(node));
            Register("renamefleet", node => new RenameFleetCommand(node));
        }
    }

    /// <summary>
    /// Thrown when an order carries a command type no registered factory knows.
    /// The API maps this to HTTP 400 (design Section E.5 error table).
    /// </summary>
    public class UnknownCommandException : Exception
    {
        public string CommandType { get; private set; }

        public UnknownCommandException(string commandType)
            : base("Unrecognised command type: " + (commandType ?? "(null)"))
        {
            CommandType = commandType;
        }
    }
}
