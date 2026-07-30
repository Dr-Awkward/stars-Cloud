// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.AiContract;

using System.Globalization;
using System.Xml;

using Nova.Common;
using Nova.Common.Commands;
using Nova.Common.Components;
using Nova.Common.DataStructures;
using Nova.Common.Waypoints;

/// <summary>
/// Why an order was dropped. Kept as data rather than a log line so the run
/// record can say exactly what a participant got wrong, which is most of the
/// value of the replay harness.
/// </summary>
/// <param name="Index">Position in the participant's orders array.</param>
/// <param name="Type">The order type token, as sent.</param>
/// <param name="Reason">Human-readable reason, safe to log.</param>
public sealed record DroppedOrder(int Index, string Type, string Reason);

/// <summary>The result of mapping one participant response.</summary>
public sealed class MappedOrders
{
    /// <summary>Commands that built cleanly and passed IsValid for this seat.</summary>
    public List<ICommand> Accepted { get; } = new();

    /// <summary>Everything rejected, with the reason.</summary>
    public List<DroppedOrder> Dropped { get; } = new();

    /// <summary>
    /// True when the participant sent orders but not one of them survived. The
    /// dispatch worker treats this as a failed turn and falls back to held
    /// orders, rather than submitting an empty turn that looks deliberate.
    /// </summary>
    public bool AllInvalid { get; init; }

    public int AcceptedCount => Accepted.Count;

    public int DroppedCount => Dropped.Count;
}

/// <summary>
/// Turns a participant's JSON orders into engine commands, and engine commands
/// into the orders XML a human client would have submitted
/// (AI-PARTICIPANTS.md Section F.1.4).
///
/// This is the trust boundary. Nothing a participant sends is believed:
///
///   - The seat is stamped from the dispatch record. The response's empire_id is
///     compared for equality and otherwise ignored, so a participant cannot act
///     on a seat it was not handed.
///   - Every order is built into a real ICommand and run through the command's
///     own IsValid against this empire's data. That is the identical check the
///     engine's order reader performs on a human's orders, so keys are validated
///     against the seat's own owned fleets, stars, and designs and a participant
///     cannot forge an order for an object it does not own.
///   - Orders are capped, so a participant cannot spam the queue.
///
/// Commands are then serialized with the engine's own ToXml, which means the
/// bytes an AI submits are the same shape a desktop player submits. The turn
/// generator cannot tell them apart, and that is deliberate: one validation
/// path, not two.
/// </summary>
public static class OrderMapper
{
    /// <summary>Default anti-spam cap, overridable per deployment.</summary>
    public const int DefaultMaxOrders = 2000;

    /// <summary>
    /// Map a participant response into validated engine commands.
    /// </summary>
    /// <param name="response">What the participant returned.</param>
    /// <param name="empire">
    /// This seat's own empire data, reconstructed from its intel. Supplies the
    /// context every IsValid needs.
    /// </param>
    /// <param name="expectedTurnYear">The turn the dispatch was for.</param>
    /// <param name="expectedEmpireId">The seat the dispatch was for.</param>
    /// <param name="maxOrders">Anti-spam cap.</param>
    public static MappedOrders Map(
        ActResponse? response,
        EmpireData empire,
        int expectedTurnYear,
        int expectedEmpireId,
        int maxOrders = DefaultMaxOrders)
    {
        ArgumentNullException.ThrowIfNull(empire);

        MappedOrders result = new();

        if (response is null)
        {
            return result;
        }

        // A response for the wrong turn or the wrong seat is not a partial
        // failure, it is a wholesale mismatch. Drop the lot; the seat falls back
        // to held orders.
        if (response.TurnYear != expectedTurnYear)
        {
            result.Dropped.Add(new DroppedOrder(-1, "(response)",
                $"Response is for turn {response.TurnYear}, expected {expectedTurnYear}."));
            return new MappedOrders { AllInvalid = true }.CopyDropped(result);
        }

        if (response.EmpireId != expectedEmpireId)
        {
            result.Dropped.Add(new DroppedOrder(-1, "(response)",
                $"Response names empire {response.EmpireId}, expected {expectedEmpireId}."));
            return new MappedOrders { AllInvalid = true }.CopyDropped(result);
        }

        if (!ContractVersions.IsSupported(response.ContractVersion))
        {
            result.Dropped.Add(new DroppedOrder(-1, "(response)",
                $"Unsupported contract version '{response.ContractVersion}'."));
            return new MappedOrders { AllInvalid = true }.CopyDropped(result);
        }

        List<OrderDto> orders = response.Orders ?? new List<OrderDto>();

        int considered = 0;
        for (int i = 0; i < orders.Count; i++)
        {
            if (considered >= maxOrders)
            {
                result.Dropped.Add(new DroppedOrder(i, orders[i].Type ?? "(none)",
                    $"Order cap of {maxOrders} reached; remaining orders dropped."));
                break;
            }

            considered++;
            OrderDto order = orders[i];

            ICommand? command;
            string? failure;
            try
            {
                command = Build(order, empire, out failure);
            }
            catch (Exception e)
            {
                command = null;
                failure = "Malformed order: " + e.Message;
            }

            if (command is null)
            {
                result.Dropped.Add(new DroppedOrder(i, order.Type ?? "(none)",
                    failure ?? "Could not build a command from this order."));
                continue;
            }

            bool valid;
            try
            {
                valid = command.IsValid(empire);
            }
            catch (Exception e)
            {
                valid = false;
                failure = "IsValid threw: " + e.Message;
            }

            if (!valid)
            {
                result.Dropped.Add(new DroppedOrder(i, order.Type ?? "(none)",
                    failure ?? "Rejected by the command's own validation."));
                continue;
            }

            result.Accepted.Add(command);

            // Apply as we go so later orders in the same turn see the effect of
            // earlier ones. Without this, a design added and then produced in
            // the same turn would fail validation on the production order, which
            // is exactly the sequence the built-in AI emits.
            try
            {
                command.ApplyToState(empire);
            }
            catch (Exception)
            {
                // The command was valid; a failure to project it forward only
                // costs later orders their context, so keep going.
            }
        }

        bool allInvalid = orders.Count > 0 && result.Accepted.Count == 0;
        MappedOrders final = new() { AllInvalid = allInvalid };
        final.Accepted.AddRange(result.Accepted);
        final.Dropped.AddRange(result.Dropped);
        return final;
    }

    private static MappedOrders CopyDropped(this MappedOrders target, MappedOrders source)
    {
        target.Dropped.AddRange(source.Dropped);
        return target;
    }

    /// <summary>
    /// Build one engine command from one contract order. Returns null with a
    /// reason when the order cannot be represented.
    /// </summary>
    private static ICommand? Build(OrderDto order, EmpireData empire, out string? failure)
    {
        failure = null;

        if (string.IsNullOrWhiteSpace(order.Type))
        {
            failure = "Order carries no type.";
            return null;
        }

        // Reject any type the engine's registry does not know, using the same
        // registry the XML order path resolves through, so the two can never
        // drift apart on what counts as a known command.
        if (!CommandRegistry.Instance.IsRegistered(order.Type))
        {
            failure = $"Unknown command type '{order.Type}'.";
            return null;
        }

        switch (order.Type.ToLowerInvariant())
        {
            case "research":
                return BuildResearch(order, empire, out failure);
            case "waypoint":
                return BuildWaypoint(order, empire, out failure);
            case "renamefleet":
                return BuildRenameFleet(order, empire, out failure);
            case "production":
                return BuildProduction(order, empire, out failure);
            case "design":
                return BuildDesign(order, empire, out failure);
            default:
                failure = $"No mapping for command type '{order.Type}'.";
                return null;
        }
    }

    private static ICommand? BuildResearch(OrderDto order, EmpireData empire, out string? failure)
    {
        failure = null;

        ResearchCommand command = new()
        {
            Budget = order.Budget ?? empire.ResearchBudget,
        };

        TechLevel topics = new(0);
        if (order.Topics is not null)
        {
            foreach (KeyValuePair<string, int> topic in order.Topics)
            {
                if (Enum.TryParse(topic.Key, ignoreCase: true, out TechLevel.ResearchField field))
                {
                    topics[field] = topic.Value;
                }
            }
        }
        else if (empire.ResearchTopics is not null)
        {
            topics = empire.ResearchTopics.Clone();
        }

        command.Topics = topics;
        return command;
    }

    private static ICommand? BuildWaypoint(OrderDto order, EmpireData empire, out string? failure)
    {
        failure = null;

        if (!EmpireViewTranscoder.TryParseKey(order.FleetKey, out long fleetKey))
        {
            failure = $"Waypoint order carries an unparseable fleet key '{order.FleetKey}'.";
            return null;
        }

        CommandMode mode = ParseMode(order.Mode);
        int index = order.Index ?? 0;

        if (mode == CommandMode.Delete)
        {
            return new WaypointCommand(mode, fleetKey, index);
        }

        if (order.Waypoint is null)
        {
            failure = "Waypoint order carries no waypoint.";
            return null;
        }

        Waypoint? waypoint = BuildWaypointObject(order.Waypoint, out failure);
        if (waypoint is null)
        {
            return null;
        }

        return new WaypointCommand(mode, waypoint, fleetKey, index);
    }

    private static Waypoint? BuildWaypointObject(WaypointView view, out string? failure)
    {
        failure = null;

        Waypoint waypoint = new()
        {
            Position = new NovaPoint { X = view.X, Y = view.Y },
            WarpFactor = view.Warp,
            Destination = view.Destination ?? string.Empty,
        };

        IWaypointTask? task = BuildTask(view, out failure);
        if (task is null)
        {
            return null;
        }

        waypoint.Task = task;
        return waypoint;
    }

    private static IWaypointTask? BuildTask(WaypointView view, out string? failure)
    {
        failure = null;
        string name = (view.Task ?? WaypointTasks.None).Trim();

        // An unrecognized task is dropped rather than quietly replaced with
        // "do nothing". Substituting a different order than the participant
        // asked for would be a worse failure than refusing it.
        switch (name.ToLowerInvariant())
        {
            case "none":
            case "notask":
            case "":
                return new NoTask();

            case "colonise":
            case "colonize":
                return new ColoniseTask();

            case "invade":
                return new InvadeTask();

            case "lay mines":
            case "laymines":
                return new LayMinesTask();

            case "scrap":
                return new ScrapTask();

            case "load cargo":
            case "unload cargo":
            case "cargo":
                return BuildCargoTask(view, name, out failure);

            default:
                failure = $"Unsupported waypoint task '{name}'.";
                return null;
        }
    }

    private static IWaypointTask? BuildCargoTask(WaypointView view, string name, out string? failure)
    {
        failure = null;

        CargoMode mode;
        if (name.StartsWith("unload", StringComparison.OrdinalIgnoreCase))
        {
            mode = CargoMode.Unload;
        }
        else if (name.StartsWith("load", StringComparison.OrdinalIgnoreCase))
        {
            mode = CargoMode.Load;
        }
        else if (!Enum.TryParse(view.CargoMode, ignoreCase: true, out mode))
        {
            failure = $"Cargo task carries an unknown cargo mode '{view.CargoMode}'.";
            return null;
        }

        CargoTask task = new() { Mode = mode };

        Cargo amount = new();
        if (view.Cargo is not null)
        {
            amount.Ironium = view.Cargo.Ironium;
            amount.Boranium = view.Cargo.Boranium;
            amount.Germanium = view.Cargo.Germanium;
        }

        amount.ColonistsInKilotons = view.Colonists;
        task.Amount = amount;
        return task;
    }

    private static ICommand? BuildRenameFleet(OrderDto order, EmpireData empire, out string? failure)
    {
        failure = null;

        if (!EmpireViewTranscoder.TryParseKey(order.FleetKey, out long fleetKey))
        {
            failure = $"Rename order carries an unparseable fleet key '{order.FleetKey}'.";
            return null;
        }

        // The engine's rename command takes the fleet itself, so an unowned key
        // cannot even construct one. That is the ownership check, enforced by
        // the type system rather than by a rule we could forget.
        if (!empire.OwnedFleets.TryGetValue(fleetKey, out Fleet fleet))
        {
            failure = $"Fleet {order.FleetKey} is not owned by this empire.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(order.NewName))
        {
            failure = "Rename order carries an empty name.";
            return null;
        }

        return new RenameFleetCommand(fleet, order.NewName);
    }

    private static ICommand? BuildProduction(OrderDto order, EmpireData empire, out string? failure)
    {
        failure = null;

        if (string.IsNullOrWhiteSpace(order.StarKey))
        {
            failure = "Production order carries no star key.";
            return null;
        }

        if (!empire.OwnedStars.ContainsKey(order.StarKey))
        {
            failure = $"Star '{order.StarKey}' is not owned by this empire.";
            return null;
        }

        CommandMode mode = ParseMode(order.Mode);
        int index = order.Index ?? 0;

        if (mode == CommandMode.Delete)
        {
            // A delete needs no unit; the index identifies the queue entry.
            return new ProductionCommand(mode, new ProductionOrder(0, new NoProductionUnit(), false),
                order.StarKey, index);
        }

        if (order.Order is null)
        {
            failure = "Production order carries no unit.";
            return null;
        }

        IProductionUnit? unit = BuildProductionUnit(order.Order, empire, out failure);
        if (unit is null)
        {
            return null;
        }

        int quantity = Math.Max(1, order.Order.Quantity);
        ProductionOrder production = new(quantity, unit, false);
        return new ProductionCommand(mode, production, order.StarKey, index);
    }

    private static IProductionUnit? BuildProductionUnit(
        ProductionOrderDto dto, EmpireData empire, out string? failure)
    {
        failure = null;
        string unit = (dto.Unit ?? string.Empty).Trim();

        switch (unit.ToLowerInvariant())
        {
            case "factory":
                if (empire.Race is null)
                {
                    failure = "Cannot cost a factory without race data.";
                    return null;
                }

                return new FactoryProductionUnit(empire.Race);

            case "mine":
                if (empire.Race is null)
                {
                    failure = "Cannot cost a mine without race data.";
                    return null;
                }

                return new MineProductionUnit(empire.Race);

            case "defenses":
            case "defence":
            case "defenses ":
                return new DefenseProductionUnit();

            default:
                return BuildShipUnit(dto, empire, unit, out failure);
        }
    }

    private static IProductionUnit? BuildShipUnit(
        ProductionOrderDto dto, EmpireData empire, string unit, out string? failure)
    {
        failure = null;

        // Prefer an explicit design key; fall back to matching the design by
        // name, which is what the built-in AI emits.
        ShipDesign? design = null;

        if (EmpireViewTranscoder.TryParseKey(dto.DesignKey, out long designKey)
            && empire.Designs.TryGetValue(designKey, out ShipDesign byKey))
        {
            design = byKey;
        }
        else
        {
            foreach (ShipDesign candidate in empire.Designs.Values)
            {
                if (string.Equals(candidate.Name, unit, StringComparison.Ordinal))
                {
                    design = candidate;
                    break;
                }
            }
        }

        if (design is null)
        {
            failure = $"No design named '{unit}' is owned by this empire.";
            return null;
        }

        return new ShipProductionUnit(design);
    }

    private static ICommand? BuildDesign(OrderDto order, EmpireData empire, out string? failure)
    {
        failure = null;
        CommandMode mode = ParseMode(order.Mode);

        if (order.Design is null)
        {
            failure = "Design order carries no design.";
            return null;
        }

        if (mode == CommandMode.Delete)
        {
            if (!EmpireViewTranscoder.TryParseKey(order.Design.Key, out long deleteKey))
            {
                failure = $"Design delete carries an unparseable key '{order.Design.Key}'.";
                return null;
            }

            return new DesignCommand(mode, deleteKey);
        }

        ShipDesign? design = BuildShipDesign(order.Design, empire, mode, out failure);
        if (design is null)
        {
            return null;
        }

        return new DesignCommand(mode, design);
    }

    private static ShipDesign? BuildShipDesign(
        DesignView view, EmpireData empire, CommandMode mode, out string? failure)
    {
        failure = null;

        if (empire.AvailableComponents is null)
        {
            failure = "This empire has no component list to design against.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(view.Hull))
        {
            failure = "Design carries no hull.";
            return null;
        }

        if (!empire.AvailableComponents.TryGetValue(view.Hull, out Component hull))
        {
            failure = $"Hull '{view.Hull}' is not available to this empire.";
            return null;
        }

        // On Add the engine assigns the next key, so a participant cannot claim
        // an arbitrary design key. On Edit the key must already be one of ours.
        long key;
        if (mode == CommandMode.Add)
        {
            key = empire.GetNextDesignKey();
        }
        else if (!EmpireViewTranscoder.TryParseKey(view.Key, out key)
                 || !empire.Designs.ContainsKey(key))
        {
            failure = $"Design key '{view.Key}' is not owned by this empire.";
            return null;
        }

        ShipDesign design = new(key)
        {
            Name = string.IsNullOrWhiteSpace(view.Name) ? hull.Name : view.Name,
            Blueprint = hull,
        };

        try
        {
            Hull? hullProperty = design.Hull;
            if (hullProperty?.Modules is null)
            {
                failure = $"Component '{view.Hull}' is not a hull.";
                return null;
            }

            List<HullModule> modules = hullProperty.Modules;
            foreach (ModuleView requested in view.Modules)
            {
                if (requested.Slot < 0 || requested.Slot >= modules.Count)
                {
                    failure = $"Design references slot {requested.Slot}, which this hull does not have.";
                    return null;
                }

                if (string.IsNullOrWhiteSpace(requested.Component))
                {
                    continue;
                }

                if (!empire.AvailableComponents.TryGetValue(requested.Component, out Component part))
                {
                    failure = $"Component '{requested.Component}' is not available to this empire.";
                    return null;
                }

                HullModule module = modules[requested.Slot];
                module.AllocatedComponent = part;
                module.ComponentCount = Math.Max(1, requested.Count);
            }

            design.Update();
        }
        catch (Exception e)
        {
            failure = "Could not assemble the design: " + e.Message;
            return null;
        }

        return design;
    }

    private static CommandMode ParseMode(string? mode)
        => Enum.TryParse(mode, ignoreCase: true, out CommandMode parsed) ? parsed : CommandMode.Add;

    /// <summary>
    /// Serialize validated commands into the orders XML a human client submits.
    ///
    /// The engine's order reader expects ROOT/Turn, ROOT/Id, and a ROOT/Orders
    /// element whose children are Command elements carrying a Type attribute. It
    /// also expects newest first, because it pushes onto a stack and the server
    /// then pops oldest first to apply them in issue order. We accepted commands
    /// in issue order, so we write them reversed.
    /// </summary>
    public static string BuildOrdersXml(int turnYear, int empireId, IEnumerable<ICommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        XmlDocument doc = new();
        doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", null));

        XmlElement root = doc.CreateElement("ROOT");
        doc.AppendChild(root);

        Global.SaveData(doc, root, "Turn", turnYear.ToString(CultureInfo.InvariantCulture));
        Global.SaveData(doc, root, "Id", empireId.ToString(CultureInfo.InvariantCulture));

        XmlElement orders = doc.CreateElement("Orders");
        root.AppendChild(orders);

        List<ICommand> ordered = new(commands);
        ordered.Reverse();

        foreach (ICommand command in ordered)
        {
            orders.AppendChild(command.ToXml(doc));
        }

        using StringWriter writer = new(CultureInfo.InvariantCulture);
        using (XmlTextWriter xml = new(writer))
        {
            xml.Formatting = Formatting.Indented;
            doc.WriteTo(xml);
        }

        return writer.ToString();
    }
}
