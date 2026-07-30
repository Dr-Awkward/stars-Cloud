#region Copyright Notice
// ============================================================================
// Copyright (C) 2008 Ken Reed
// Copyright (C) 2009, 2010 stars-nova
// Copyright (C) 2026 Farehard (headless port).
//
// This file is part of Stars-Nova.
// See <http://sourceforge.net/projects/stars-nova/>.
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License version 2 as
// published by the Free Software Foundation.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>
// ===========================================================================
#endregion

#region Module Description
// ===========================================================================
// Error reporting. The desktop game popped a MessageBox for every message,
// which cannot exist headless. Messages now go to an IReporter sink: the cloud
// host installs a logging sink writing to Cloud Logging, and a desktop client
// installs a dialog sink. Common itself pops nothing and depends on no UI.
//
// The static Report facade is kept so the many existing Report.* call sites are
// unchanged (design Section A.2, M0.md step 4).
// ===========================================================================
#endregion

using System;
using System.Diagnostics;

namespace Nova.Common
{
    /// <summary>
    /// A destination for engine messages. Set one on <see cref="Report.Sink"/>
    /// at startup. The headless host writes these to structured logs; a desktop
    /// client raises dialogs.
    /// </summary>
    public interface IReporter
    {
        void Error(string text);
        void Information(string text);
        void FatalError(string text);
        void Debug(string text);
    }

    /// <summary>
    /// Raised by <see cref="Report.FatalError"/> in place of the old
    /// Thread.Abort (which throws PlatformNotSupportedException on modern .NET).
    /// The host turns this into a failed turn generation; it never tears the
    /// process down.
    /// </summary>
    public class NovaFatalException : Exception
    {
        public NovaFatalException(string message) : base(message) { }
        public NovaFatalException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// The default sink, used until a host installs its own. Writes to standard
    /// error so nothing is lost when the engine runs with no configured logger.
    /// </summary>
    public sealed class ConsoleReporter : IReporter
    {
        public void Error(string text) { Console.Error.WriteLine("Nova error: " + text); }
        public void Information(string text) { Console.Error.WriteLine("Nova: " + text); }
        public void FatalError(string text) { Console.Error.WriteLine("Nova fatal: " + text); }
        public void Debug(string text) { Console.Error.WriteLine("Nova debug: " + text); }
    }

    /// <summary>
    /// A static facade over the message sink, so the many existing Report.* call
    /// sites are unchanged.
    /// </summary>
    public static class Report
    {
        /// <summary>Where messages go. Replace at startup to redirect output.</summary>
        public static IReporter Sink { get; set; } = new ConsoleReporter();

        /// <summary>Report a non-fatal error.</summary>
        public static void Error(string text)
        {
            Sink.Error(text);
        }

        /// <summary>Report an informational message.</summary>
        public static void Information(string text)
        {
            Sink.Information(text);
        }

        /// <summary>
        /// Report a fatal error and abandon the current operation. This always
        /// throws, preserving the old "does not return" behaviour of the
        /// Thread.Abort it replaces, whatever the sink chooses to do.
        /// </summary>
        public static void FatalError(string text)
        {
            Sink.FatalError(text);
            throw new NovaFatalException(text);
        }

        /// <summary>Report a debug message. Compiled out of release builds.</summary>
        [Conditional("DEBUG")]
        public static void Debug(string text)
        {
            Sink.Debug(text);
        }
    }
}
