// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard.
// Based on Stars! Nova (Copyright (C) 2008 Ken Reed; 2009-2012 The Stars-Nova
// Project), used under the GNU General Public License version 2. This file is
// likewise distributed under the GNU General Public License version 2.

using System.IO;
using Nova.Common;
using Nova.Server;
using NUnit.Framework;

namespace Nova.Tests.IntegrationTests
{
    /// <summary>
    /// The working-directory contract between the cloud host and the engine.
    ///
    /// The cloud turn generator hydrates a per-generation scratch directory,
    /// assigns it to ServerData.GameFolder, and expects every file the engine
    /// writes to land there so it can push the results back to storage. Nothing
    /// inside the engine may quietly relocate that folder: a server container has
    /// no Nova root to discover, and FileSearcher.GetFolder creates any folder it
    /// cannot find, so a relocation does not fail, it just writes the turn
    /// somewhere nobody reads.
    /// </summary>
    [TestFixture]
    public class IntelWriterFolderTests
    {
        private string workingDir;

        [SetUp]
        public void CreateWorkingDir()
        {
            workingDir = Path.Combine(Path.GetTempPath(), "galaxies-intel-" + Path.GetRandomFileName());
            Directory.CreateDirectory(workingDir);
        }

        [TearDown]
        public void RemoveWorkingDir()
        {
            if (Directory.Exists(workingDir))
            {
                Directory.Delete(workingDir, recursive: true);
            }
        }

        /// <summary>
        /// WriteIntel must write one intel file per empire into the folder the
        /// caller assigned, and must leave that assignment alone.
        ///
        /// Without the guard in IntelWriter.WriteIntel this fails twice over:
        /// GameFolder is reassigned to a discovered Nova root, and both intel files
        /// are written there instead, so the game store finds nothing to upload and
        /// no player ever receives a turn.
        /// </summary>
        [Test]
        public void WriteIntelHonoursTheAssignedGameFolder()
        {
            ServerData serverState = BuildTwoEmpireGame();
            serverState.GameFolder = workingDir;

            new IntelWriter(serverState, new Scores(serverState)).WriteIntel();

            Assert.AreEqual(
                workingDir,
                serverState.GameFolder,
                "WriteIntel relocated GameFolder. CleanupOrders runs after it and reads the same field.");

            Assert.IsTrue(
                File.Exists(Path.Combine(workingDir, "Tom" + Global.IntelExtension)),
                "No intel written for Tom in the assigned working directory.");
            Assert.IsTrue(
                File.Exists(Path.Combine(workingDir, "Dick" + Global.IntelExtension)),
                "No intel written for Dick in the assigned working directory.");
        }

        /// <summary>
        /// One intel file per empire and nothing else, so a per-empire view cannot
        /// be handed to the wrong seat by a stray extra file.
        /// </summary>
        [Test]
        public void WriteIntelWritesExactlyOneFilePerEmpire()
        {
            ServerData serverState = BuildTwoEmpireGame();
            serverState.GameFolder = workingDir;

            new IntelWriter(serverState, new Scores(serverState)).WriteIntel();

            string[] intelFiles = Directory.GetFiles(workingDir, "*" + Global.IntelExtension);
            Assert.AreEqual(2, intelFiles.Length, "Expected exactly one intel file per empire.");
        }

        /// <summary>
        /// A two-empire game at the starting year. The starting year matters: it is
        /// the branch that skips score generation, which keeps this fixture to the
        /// minimum the intel path needs.
        /// </summary>
        private static ServerData BuildTwoEmpireGame()
        {
            ServerData serverState = new ServerData();
            serverState.TurnYear = Global.StartingYear;

            EmpireData first = new EmpireData();
            first.Id = 1;
            first.Race.Name = "Tom";

            EmpireData second = new EmpireData();
            second.Id = 2;
            second.Race.Name = "Dick";

            serverState.AllEmpires[first.Id] = first;
            serverState.AllEmpires[second.Id] = second;

            return serverState;
        }
    }
}
