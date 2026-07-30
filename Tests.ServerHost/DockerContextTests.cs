// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt.

using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Galaxies.Tests.ServerHost
{
    /// <summary>
    /// Every Dockerfile must copy enough of the repository for its publish to
    /// succeed.
    ///
    /// This exists because all four images were unbuildable at once and nobody
    /// noticed. Common.csproj and Server.csproj each compile the repository-root
    /// VersionInfo.cs by link rather than by project reference, and no Dockerfile
    /// copied it, so restore succeeded and publish failed with CS2001 in every
    /// image. A linked file outside the project directory is invisible to anyone
    /// reading the Dockerfile, which is exactly why this needs to be checked
    /// mechanically rather than by review.
    ///
    /// This runs in CI with no Docker daemon: it reads the Dockerfiles and the
    /// project files as text. It cannot prove an image builds, only that the copy
    /// list is not missing a linked source file.
    /// </summary>
    [TestFixture]
    public class DockerContextTests
    {
        private static readonly string[] Dockerfiles =
        {
            "ServerHost/Dockerfile",
            "Api/Dockerfile",
            "AiService/Dockerfile",
            "Participants/NovaDefault/Dockerfile",
        };

        [Test]
        [TestCaseSource(nameof(Dockerfiles))]
        public void DockerfileCopiesEveryLinkedSourceFileItsProjectsNeed(string dockerfile)
        {
            string repoRoot = FixturePaths.RepoRoot();
            string path = Path.Combine(repoRoot, dockerfile.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"{dockerfile} does not exist.");

            IReadOnlyList<string> copied = BuildStageCopySources(File.ReadAllLines(path));
            Assert.IsNotEmpty(copied, $"{dockerfile} has no COPY lines in its build stage.");

            // Every project file inside a copied directory.
            List<string> projects = new();
            foreach (string source in copied)
            {
                string absolute = Path.Combine(repoRoot, source.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(absolute))
                {
                    projects.AddRange(Directory.GetFiles(absolute, "*.csproj", SearchOption.AllDirectories));
                }
            }

            foreach (string project in projects)
            {
                foreach (string linked in LinkedFilesOutsideProject(repoRoot, project))
                {
                    Assert.IsTrue(
                        copied.Any(c => PathCovers(c, linked)),
                        $"{dockerfile} builds {Path.GetFileName(project)}, which compiles the out-of-project "
                        + $"file '{linked}' by link, but no COPY line brings '{linked}' into the build context. "
                        + "The image will restore and then fail to publish with CS2001.");
                }
            }
        }

        /// <summary>
        /// COPY sources from the build stage only. Anything after the second FROM is
        /// the runtime stage, which copies from the build stage rather than from the
        /// context, so it cannot be missing a repository file.
        /// </summary>
        private static IReadOnlyList<string> BuildStageCopySources(string[] lines)
        {
            List<string> sources = new();
            int fromCount = 0;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase))
                {
                    fromCount++;
                    if (fromCount >= 2)
                    {
                        break;
                    }
                }

                if (!line.StartsWith("COPY ", StringComparison.OrdinalIgnoreCase) || line.Contains("--from"))
                {
                    continue;
                }

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    sources.Add(parts[1].TrimEnd('/'));
                }
            }

            return sources;
        }

        /// <summary>
        /// Files a project compiles from outside its own directory, as repository
        /// relative paths. Project references are excluded: those are followed by the
        /// SDK and would be caught as their own project.
        /// </summary>
        private static IEnumerable<string> LinkedFilesOutsideProject(string repoRoot, string projectPath)
        {
            string projectDir = Path.GetDirectoryName(projectPath)!;

            foreach (Match match in Regex.Matches(
                File.ReadAllText(projectPath), @"Include=""(\.\.[^""]*)"""))
            {
                string include = match.Groups[1].Value;
                if (include.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string absolute = Path.GetFullPath(
                    Path.Combine(projectDir, include.Replace('\\', Path.DirectorySeparatorChar)));

                // Only files that actually exist; a glob or a missing optional item
                // is not this test's business.
                if (!File.Exists(absolute))
                {
                    continue;
                }

                yield return Path.GetRelativePath(repoRoot, absolute).Replace(Path.DirectorySeparatorChar, '/');
            }
        }

        /// <summary>True when a COPY source is, or contains, the given file.</summary>
        private static bool PathCovers(string copySource, string file)
            => string.Equals(copySource, file, StringComparison.OrdinalIgnoreCase)
               || file.StartsWith(copySource + "/", StringComparison.OrdinalIgnoreCase);
    }
}
