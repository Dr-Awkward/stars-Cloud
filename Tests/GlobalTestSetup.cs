// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using System;
using System.IO;
using NUnit.Framework;
using Nova.Common.Components;

namespace Nova.Tests
{
    /// <summary>
    /// Runs once before any test in Nova.Tests. Points the headless component
    /// loader at the components.xml copied next to the test assembly, so
    /// AllComponents.Restore needs no nova.conf, no registry, and no file dialog
    /// (design Section A.2). Without this, every test that touches the component
    /// database fails to locate the file in headless mode.
    /// </summary>
    [SetUpFixture]
    public class GlobalTestSetup
    {
        [OneTimeSetUp]
        public void SetUp()
        {
            string components = Path.Combine(AppContext.BaseDirectory, "components.xml");
            if (File.Exists(components))
            {
                AllComponents.ComponentFilePathOverride = components;
            }
        }
    }
}
