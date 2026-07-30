// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.

namespace Galaxies.Participant.NovaDefault;

using Nova.Common;
using Nova.Common.Components;

/// <summary>
/// Loads the engine's component definitions once, at process start.
///
/// Why this is a separate concern rather than three lines in Program.cs.
///
/// Nova.Common.Components.AllComponents is a process-wide static. Its Restore()
/// has an internal isLoaded latch, so the first caller wins and every later
/// caller is a no-op against whatever the first one loaded. On the desktop that
/// was a convenience. In a server it is a correctness constraint: if the first
/// request into a cold container is the thing that triggers loading, then the
/// load happens on a request thread, its failure surfaces as one seat's bad
/// turn instead of a failed readiness check, and a concurrent second request
/// races an empty dictionary. So we load deliberately at startup, before the
/// listener opens, and /readyz reports the result.
///
/// The desktop path resolved components.xml through FileSearcher, which reads
/// nova.conf and (on Windows) the registry, and pops a file dialog when it
/// cannot find the file. None of that exists in a container. AllComponents
/// exposes ComponentFilePathOverride for exactly this case: set it and the
/// legacy resolution is never reached.
/// </summary>
internal static class ComponentDefinitions
{
    /// <summary>Environment variable naming the component definition file.</summary>
    public const string PathVariable = "GALAXIES_COMPONENT_FILE";

    private static readonly object Gate = new();

    /// <summary>True once the definitions are in memory and non-empty.</summary>
    public static bool Loaded { get; private set; }

    /// <summary>The file we loaded, for /readyz and for log lines.</summary>
    public static string? Path { get; private set; }

    /// <summary>How many components loaded. Zero is a failure, not a success.</summary>
    public static int Count { get; private set; }

    /// <summary>Why loading failed, or null. Safe to log and to return on /readyz.</summary>
    public static string? Failure { get; private set; }

    /// <summary>
    /// Load the component definitions. Safe to call more than once; the second
    /// call reports the first call's result rather than reloading.
    /// </summary>
    public static void Load()
    {
        lock (Gate)
        {
            if (Loaded)
            {
                return;
            }

            string? file = Resolve();
            if (file is null)
            {
                Failure =
                    "No component definition file found. Set " + PathVariable
                    + " to the path of components.xml, or place components.xml next to the assembly.";
                return;
            }

            Path = file;

            try
            {
                AllComponents.ComponentFilePathOverride = file;

                // The constructor's restore:true argument is what performs the
                // load. Report.FatalError throws NovaFatalException rather than
                // aborting the thread, so a missing or corrupt file arrives here
                // as an exception we can turn into a readiness failure.
                AllComponents all = new(restore: true);
                Count = all.GetAll.Count;

                if (Count == 0)
                {
                    Failure = "Component file '" + file + "' parsed but defined no components.";
                    return;
                }

                Loaded = true;
            }
            catch (NovaFatalException e)
            {
                Failure = "Component load failed: " + e.Message;
            }
            catch (Exception e)
            {
                Failure = "Component load failed: " + e.Message;
            }
        }
    }

    /// <summary>
    /// Find components.xml. Explicit configuration first, then the copy the build
    /// drops next to the assembly. We deliberately do not fall through to the
    /// desktop FileSearcher: in a container that either fails slowly or, worse,
    /// finds some other file.
    /// </summary>
    private static string? Resolve()
    {
        string? configured = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string beside = System.IO.Path.Combine(AppContext.BaseDirectory, "components.xml");
        return File.Exists(beside) ? beside : null;
    }
}
