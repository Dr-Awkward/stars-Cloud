// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.Participant.NovaDefault;

using System.Diagnostics;
using System.Globalization;
using System.Xml;

using Galaxies.AiContract;

using Nova.Ai;
using Nova.Client;
using Nova.Common;
using Nova.Common.Commands;
using Nova.Common.Components;

/// <summary>
/// What one call to <see cref="NovaDefaultParticipant.Act"/> produced: either a
/// response to serialize, or an HTTP status and a reason.
///
/// The adapter returns this instead of throwing or writing to the response
/// directly, so the transport layer in Program.cs owns status codes and the
/// adapter stays testable without a web host.
/// </summary>
internal sealed class ActOutcome
{
    public int StatusCode { get; init; } = 200;

    public ActResponse? Response { get; init; }

    /// <summary>Set only when StatusCode is not 200.</summary>
    public string? Error { get; init; }

    public static ActOutcome BadRequest(string reason)
        => new() { StatusCode = 400, Error = reason };

    public static ActOutcome Ok(ActResponse response)
        => new() { StatusCode = 200, Response = response };
}

/// <summary>
/// The adapter between the open participant contract and the built-in Stars!
/// Nova AI (AI-PARTICIPANTS.md Section F.2 kind a, galaxies-ai spec Section 3).
///
/// Three responsibilities and nothing else:
///
///   1. Validate the request enough to refuse an incoherent one loudly.
///   2. Rebuild a real engine object graph, run DefaultAi against it.
///   3. Translate the commands the AI pushed back into contract orders.
///
/// Step 3 is the direction OrderMapper does not provide. OrderMapper goes
/// orders[] to ICommand, because that is what the host needs. A first-party
/// participant needs ICommand to orders[], because it plays with the engine's
/// own command objects and has to hand them back over JSON. The two directions
/// are inverses and they have to agree exactly, which is why the key encoding
/// note below matters so much.
/// </summary>
internal static class NovaDefaultParticipant
{
    /// <summary>The manifest id this worker answers for.</summary>
    public const string ParticipantId = "galaxies.default-ai";

    /// <summary>The manifest version. Keep in step with manifest.json.</summary>
    public const string ParticipantVersion = "1.0.0";

    /// <summary>
    /// Answer one dispatch.
    /// </summary>
    /// <param name="request">The deserialized request body, or null.</param>
    /// <param name="maxOrders">Anti-spam cap, mirroring the host's own cap.</param>
    /// <param name="log">Where failures go. Never null in the host.</param>
    public static ActOutcome Act(ActRequest? request, int maxOrders, ILogger log)
    {
        Stopwatch clock = Stopwatch.StartNew();

        if (request is null)
        {
            return ActOutcome.BadRequest("Request body is empty or not valid JSON.");
        }

        // A contract version we do not speak is a 400, not a degraded answer. If
        // the host is on a newer contract, its orders schema may have moved
        // under us, and answering in the old shape would look like success while
        // quietly dropping the seat's turn.
        if (!ContractVersions.IsSupported(request.ContractVersion))
        {
            return ActOutcome.BadRequest(
                "Unsupported contract_version '" + (request.ContractVersion ?? "(null)")
                + "'. This participant speaks " + string.Join(", ", ContractVersions.Supported) + ".");
        }

        if (request.Seat is null)
        {
            return ActOutcome.BadRequest("Request carries no seat.");
        }

        if (request.EmpireView is null)
        {
            return ActOutcome.BadRequest("Request carries no empire_view.");
        }

        if (request.Game is null)
        {
            return ActOutcome.BadRequest("Request carries no game context.");
        }

        // The seat and turn on the response are stamped from the REQUEST, never
        // from anything the AI produced. The host compares them for equality and
        // stamps the real seat from its own dispatch record anyway, so echoing
        // request values is the only shape that can ever match. Reading them off
        // the reconstructed EmpireData instead would introduce a second source of
        // truth for the one field the trust boundary depends on.
        int empireId = request.Seat.EmpireId;
        int turnYear = request.Game.TurnYear;

        List<string> notes = new();
        string seedUsed = request.Game.SeatSeed ?? string.Empty;

        // Parse the seat seed even though, today, nothing consumes it. See the
        // long honesty note on SeedNote below: this call exists so the value is
        // validated and reported rather than silently ignored.
        int seed = SeatSeed.Parse(request.Game.SeatSeed);

        List<OrderDto> orders;
        try
        {
            orders = Play(request, seed, maxOrders, notes, log);
        }
        catch (Exception e)
        {
            // Never throw out of /v1/act. A participant that 500s costs the seat
            // its turn; a participant that returns no orders costs the seat only
            // the change it would have made, because the host falls back to held
            // orders. Degrading is strictly better than failing, so we degrade,
            // and we say so in the diagnostics rather than pretending the empty
            // list was a decision.
            log.LogError(e, "NovaDefault participant failed for game {GameId} seat {EmpireId} turn {TurnYear}.",
                request.Game.GameId, empireId, turnYear);

            orders = new List<OrderDto>();
            notes.Add("The AI threw and produced no orders: " + e.Message
                + ". This seat falls back to held orders.");
        }

        clock.Stop();

        return ActOutcome.Ok(new ActResponse
        {
            ContractVersion = ContractVersions.Current,
            RequestId = request.RequestId ?? string.Empty,
            EmpireId = empireId,
            TurnYear = turnYear,
            Orders = orders,
            Diagnostics = new DiagnosticsDto
            {
                Notes = string.Join(" ", notes),
                SeedUsed = seedUsed,
                ElapsedMs = clock.ElapsedMilliseconds,
            },
        });
    }

    /// <summary>
    /// Rebuild the seat's state, run the AI, and translate the result.
    /// </summary>
    private static List<OrderDto> Play(
        ActRequest request, int seed, int maxOrders, List<string> notes, ILogger log)
    {
        if (request.IntelNative is null)
        {
            // Deliberate refusal, not a missing feature.
            //
            // empire_view is a projection. It is complete enough for a community
            // or LLM participant to play well, and it is the contract everyone
            // can rely on, but it is not a lossless serialization of the engine's
            // domain model. Reconstructing an EmpireData from it would mean
            // inventing the parts the projection drops: hull module allocation
            // that did not survive a partially built design, production queue
            // remaining costs, race trait derivations, report ages. Every one of
            // those guesses would make this participant a quietly weaker opponent
            // than the desktop AI it claims to be, and nobody would ever see the
            // difference in a log.
            //
            // So the first-party participant declares that it needs the native
            // intel and refuses to guess without it. An empty orders list is an
            // honest "I did not play this turn"; a fabricated one is not.
            notes.Add(
                "No intel_native on the request. This first-party participant plays against the "
                + "engine's own object graph and does not reconstruct one from the lossy empire_view "
                + "projection, so it returned no orders. Dispatch it with include_native_intel set.");
            return new List<OrderDto>();
        }

        if (!ComponentDefinitions.Loaded)
        {
            notes.Add(
                "Component definitions are not loaded (" + (ComponentDefinitions.Failure ?? "unknown reason")
                + "), so no orders were produced.");
            return new List<OrderDto>();
        }

        // The lossless path: the engine's own Intel(XmlDocument) constructor, so
        // the graph the AI plays is what the turn generator wrote, byte for byte,
        // with no hand-written projection in the middle dropping fields.
        Intel intel = Envelope.ReadIntel(request.IntelNative);

        if (intel.EmpireState is null)
        {
            notes.Add("The native intel carried no empire state, so no orders were produced.");
            return new List<OrderDto>();
        }

        // A seat mismatch here means the dispatcher handed us the wrong intel.
        // Play it anyway (the host stamps the real seat and will drop the lot if
        // it disagrees), but say so, because this is a dispatcher bug and it
        // should not be invisible.
        if (intel.EmpireState.Id != request.Seat.EmpireId)
        {
            notes.Add("The native intel is for empire " + intel.EmpireState.Id
                + " but the dispatch names seat " + request.Seat.EmpireId + ".");
        }

        ClientData state = ClientData.FromIntel(intel);

        DefaultAi ai = new();
        ai.Initialize(state);
        ai.DoMove();

        notes.Add(SeedNote(seed));

        // The AI pushes onto a Stack, so enumerating it yields newest first.
        // OrderMapper.Map applies orders in list order and projects each onto the
        // empire as it goes, precisely so a design added this turn is visible to
        // a production order later in the same turn. Handing back LIFO order
        // would break that sequence and the production order would fail
        // validation. Reverse to issue order.
        List<ICommand> issued = new(ai.ClientState.Commands);
        issued.Reverse();

        return Serialize(issued, maxOrders, notes, log);
    }

    /// <summary>
    /// The honest determinism statement for this run.
    ///
    /// The task of "seeding the built-in AI" turns out to have no work in it,
    /// and that is worth stating precisely rather than claiming a feature.
    ///
    /// Nova/Ai/AbstractAI.cs, DefaultAi.cs, DefaultAIPlanner.cs, DefaultFleetAI.cs
    /// and DefaultPlanetAI.cs contain no `new Random()`, no Random.Shared, no
    /// Guid, no DateTime read, and no GetHashCode-derived branch. The AI is a
    /// pure function of the intel it is handed: it walks OwnedStars and
    /// OwnedFleets, applies a fixed research ladder, and picks the first
    /// habitable unowned star it sees. Two runs over identical intel produce
    /// identical orders because there is nothing in the code that could make
    /// them differ, not because a seed made them agree.
    ///
    /// The dictionary iteration the AI relies on is stable for the same reason:
    /// Dictionary enumerates its entries array in insertion order absent
    /// removals, and insertion order here comes from the deterministic XML read.
    /// String hash randomization changes bucket assignment, not that order.
    ///
    /// So the manifest's determinism: "seeded" is true as a property (replaying
    /// the same empire_view yields the same orders), and false as a mechanism
    /// (the seat seed is not consumed by anything). If randomness is ever added,
    /// the injection point is DefaultAi.DoMove, which constructs
    /// DefaultAIPlanner, DefaultPlanetAI and DefaultFleetAI; each would need to
    /// take a Random built from this seed, and this method would then have
    /// something real to report. Until then it reports what actually happened.
    /// </summary>
    private static string SeedNote(int seed) =>
        "Deterministic: this AI contains no RNG, so the run reproduces exactly for identical intel. "
        + "The seat seed (" + seed.ToString(CultureInfo.InvariantCulture)
        + ") was parsed but consumed by nothing, because there is no randomness to seed.";

    /// <summary>
    /// Translate engine commands into contract orders.
    ///
    /// KEY ENCODING, AND WHY GETTING IT BACKWARDS IS A SILENT BUG.
    ///
    /// The engine's XML writes fleet and design keys as HEXADECIMAL with no
    /// prefix (long.ToString("X"), read back with NumberStyles.HexNumber). The
    /// CONTRACT writes them as DECIMAL strings (EmpireViewTranscoder.Key, read
    /// back with TryParseKey and NumberStyles.Integer). Both forms are bare
    /// digits, so "1234" parses cleanly under either rule and yields two
    /// different fleets. Nothing throws, nothing logs, the order is simply
    /// applied to the wrong object or dropped as unowned.
    ///
    /// Therefore: anywhere below that a key comes out of XML it is parsed as hex
    /// and re-emitted as decimal, and anywhere a key comes off a public long
    /// property it goes straight through Key() and never touches hex at all.
    /// A test that round-trips one command of each type through Serialize and
    /// OrderMapper.Map and compares keys is the thing that keeps this honest.
    /// </summary>
    private static List<OrderDto> Serialize(
        List<ICommand> commands, int maxOrders, List<string> notes, ILogger log)
    {
        List<OrderDto> orders = new();
        int untranslated = 0;

        foreach (ICommand command in commands)
        {
            if (orders.Count >= maxOrders)
            {
                notes.Add("Order cap of " + maxOrders.ToString(CultureInfo.InvariantCulture)
                    + " reached; " + (commands.Count - orders.Count).ToString(CultureInfo.InvariantCulture)
                    + " further commands were not sent.");
                break;
            }

            OrderDto? order;
            try
            {
                order = Translate(command);
            }
            catch (Exception e)
            {
                // One bad command must not cost the seat the other forty. Drop it
                // and keep going.
                log.LogWarning(e, "Could not translate a {Type} command.", command.GetType().Name);
                order = null;
            }

            if (order is null)
            {
                untranslated++;
                continue;
            }

            orders.Add(order);
        }

        if (untranslated > 0)
        {
            notes.Add(untranslated.ToString(CultureInfo.InvariantCulture)
                + " command(s) could not be expressed as contract orders and were dropped.");
        }

        return orders;
    }

    /// <summary>
    /// One command to one order. Returns null for a command this contract cannot
    /// express, which is dropped rather than approximated.
    /// </summary>
    private static OrderDto? Translate(ICommand command) => command switch
    {
        ResearchCommand research => TranslateResearch(research),
        WaypointCommand waypoint => TranslateWaypoint(waypoint),
        RenameFleetCommand rename => TranslateRenameFleet(rename),
        DesignCommand design => TranslateDesign(design),
        ProductionCommand production => TranslateProduction(production),
        _ => null,
    };

    /// <summary>
    /// Research. Budget and Topics are both public, so this needs no XML.
    /// Topic keys are the ResearchField enum names, which is what
    /// OrderMapper.BuildResearch parses them back as.
    /// </summary>
    private static OrderDto TranslateResearch(ResearchCommand command)
    {
        Dictionary<string, int> topics = new();

        if (command.Topics is not null)
        {
            for (TechLevel.ResearchField field = TechLevel.FirstField;
                 field <= TechLevel.LastField;
                 field++)
            {
                topics[field.ToString()] = command.Topics[field];
            }
        }

        return new OrderDto
        {
            Type = OrderTypes.Research,
            Budget = command.Budget,
            Topics = topics,
        };
    }

    /// <summary>
    /// Waypoint. Every field is public, so the key goes through Key() directly
    /// and the hexadecimal XML form is never involved.
    /// </summary>
    private static OrderDto TranslateWaypoint(WaypointCommand command) => new()
    {
        Type = OrderTypes.Waypoint,
        Mode = command.Mode.ToString(),
        FleetKey = EmpireViewTranscoder.Key(command.FleetKey),
        Index = command.Index,
        Waypoint = command.Waypoint is null
            ? null
            : EmpireViewTranscoder.ProjectWaypoint(command.Waypoint),
    };

    /// <summary>Rename. Both fields public; decimal key, as above.</summary>
    private static OrderDto TranslateRenameFleet(RenameFleetCommand command) => new()
    {
        Type = OrderTypes.RenameFleet,
        FleetKey = EmpireViewTranscoder.Key(command.FleetKey),
        NewName = command.NewName,
    };

    /// <summary>
    /// Design. DesignCommand declares Design and Mode as "private set; get;", so
    /// the getters are public and this needs no XML either.
    ///
    /// On a Delete the command holds a bare ShipDesign carrying only the key, so
    /// Blueprint, Hull and Type are all absent. That is fine: OrderMapper reads
    /// only Design.Key on a delete. The projection below is best effort for the
    /// rest and never throws on a partially built design.
    /// </summary>
    private static OrderDto TranslateDesign(DesignCommand command) => new()
    {
        Type = OrderTypes.Design,
        Mode = command.Mode.ToString(),
        Design = ProjectDesign(command.Design),
    };

    /// <summary>
    /// Project a ShipDesign into the contract's design view.
    ///
    /// EmpireViewTranscoder has an equivalent private helper. It is private, so
    /// this duplicates it rather than widening the transcoder's public surface
    /// for one caller. If a third caller ever appears, promote that one and
    /// delete this.
    /// </summary>
    private static DesignView? ProjectDesign(ShipDesign? design)
    {
        if (design is null)
        {
            return null;
        }

        DesignView view = new()
        {
            Key = EmpireViewTranscoder.Key(design.Key),
            Name = design.Name ?? string.Empty,
            Hull = design.Blueprint?.Name ?? string.Empty,
        };

        // Type, Mass, Armor and the module walk all read through Blueprint and
        // the hull property bag, which throw on a design that was never fully
        // assembled. A design we cannot summarize is still worth sending by key
        // and name, so the detail is best effort.
        try
        {
            view.Type = design.Type.ToString();
            view.Mass = design.Mass;
            view.Armor = design.Armor;
        }
        catch (Exception)
        {
            // Leave the numeric summary at zero.
        }

        try
        {
            Nova.Common.Components.Hull? hull = design.Hull;
            if (hull?.Modules is not null)
            {
                int slot = 0;
                foreach (HullModule module in hull.Modules)
                {
                    view.Modules.Add(new ModuleView
                    {
                        Slot = slot++,
                        ComponentType = module.ComponentType ?? string.Empty,
                        Component = module.AllocatedComponent?.Name,
                        Count = module.ComponentCount,
                    });
                }
            }
        }
        catch (Exception)
        {
            // A design whose modules we cannot enumerate still carries its key,
            // which is all a delete or a rebuild-by-name needs.
        }

        return view;
    }

    /// <summary>
    /// Production. This is the one command whose state is not reachable through
    /// public getters: ProductionCommand declares both ProductionOrder and Mode
    /// as "private get; set;", so they can be written and never read. Index and
    /// StarKey do have public getters.
    ///
    /// ToXml is the way in. It is public, it is the engine's own serialization,
    /// and it necessarily writes everything the order reader needs, so reading
    /// the values back out of the element it returns always works and cannot
    /// drift from what the engine considers the command's state.
    /// </summary>
    private static OrderDto? TranslateProduction(ProductionCommand command)
    {
        XmlDocument doc = new();
        XmlElement element = command.ToXml(doc);

        string mode = Text(element, "Mode") ?? CommandMode.Add.ToString();

        OrderDto order = new()
        {
            Type = OrderTypes.Production,
            Mode = mode,
            // Public getters, so prefer them over the XML round trip.
            StarKey = command.StarKey,
            Index = command.Index,
        };

        XmlElement? production = Child(element, "ProductionOrder");
        if (production is not null)
        {
            order.Order = TranslateProductionOrder(production);
        }

        // A non-delete production order with no buildable unit is meaningless,
        // and the host would reject it anyway. Drop it here so the diagnostics
        // count it instead of the host's dropped list.
        if (order.Order is null
            && !string.Equals(mode, CommandMode.Delete.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return order;
    }

    /// <summary>
    /// Read one ProductionOrder element into the contract's production DTO.
    ///
    /// The engine's unit element names are FactoryUnit, MineUnit, DefenseUnit and
    /// ShipUnit; the contract's unit tokens are Factory, Mine, Defenses and the
    /// ship design's name. These are the exact tokens OrderMapper's
    /// BuildProductionUnit switches on, so the two sides agree by construction.
    /// </summary>
    private static ProductionOrderDto? TranslateProductionOrder(XmlElement production)
    {
        int quantity = 1;
        string? quantityText = Text(production, "Quantity");
        if (quantityText is not null
            && int.TryParse(quantityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            quantity = Math.Max(1, parsed);
        }

        if (Child(production, "FactoryUnit") is not null)
        {
            return new ProductionOrderDto { Unit = "Factory", Quantity = quantity };
        }

        if (Child(production, "MineUnit") is not null)
        {
            return new ProductionOrderDto { Unit = "Mine", Quantity = quantity };
        }

        if (Child(production, "DefenseUnit") is not null)
        {
            return new ProductionOrderDto { Unit = "Defenses", Quantity = quantity };
        }

        XmlElement? ship = Child(production, "ShipUnit");
        if (ship is null)
        {
            // NoProductionUnit and anything a future engine build adds. There is
            // no contract token for it, so drop rather than invent one.
            return null;
        }

        ProductionOrderDto dto = new()
        {
            Unit = Text(ship, "Name") ?? string.Empty,
            Quantity = quantity,
        };

        // THE HEX TO DECIMAL CONVERSION. ShipProductionUnit.ToXml writes
        // DesignKey as designKey.ToString("X"). ProductionOrderDto.DesignKey is a
        // DECIMAL string, because OrderMapper reads it with
        // EmpireViewTranscoder.TryParseKey, which is NumberStyles.Integer.
        // Passing the hex text through unconverted would resolve to a different
        // design, or to none, with no error anywhere.
        string? keyText = Text(ship, "DesignKey");
        if (keyText is not null
            && long.TryParse(keyText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long designKey))
        {
            dto.DesignKey = EmpireViewTranscoder.Key(designKey);
        }

        return dto;
    }

    /// <summary>First child element with this name, or null.</summary>
    private static XmlElement? Child(XmlElement parent, string name)
    {
        foreach (XmlNode node in parent.ChildNodes)
        {
            if (node is XmlElement element
                && string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return element;
            }
        }

        return null;
    }

    /// <summary>Text of the first child element with this name, or null.</summary>
    private static string? Text(XmlElement parent, string name)
        => Child(parent, name)?.FirstChild?.Value;
}
