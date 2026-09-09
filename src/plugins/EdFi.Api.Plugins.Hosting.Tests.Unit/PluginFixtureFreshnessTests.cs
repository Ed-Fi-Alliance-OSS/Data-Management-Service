// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// Runs the real staging rules against an output tree of this test's own.
/// </summary>
/// <remarks>
/// <para>
/// The staging target decides two things no assertion over the staged output can see: whether a tree
/// is fresh, and which directories in it are unaccounted for. Both are wrong in ways that leave a
/// passing suite behind. A tree wrongly called stale republishes every fixture on every build, which
/// costs minutes and looks like nothing; a directory wrongly kept serves a probe built from sources
/// that have since changed, which is worse, because the answer is then about code nobody is looking at.
/// </para>
/// <para>
/// So the target itself is run, from a project written here that imports it and reports what it
/// decided, with the output and intermediate directories pointed at a temporary tree. Nothing in this
/// file asserts by reading the rules.
/// </para>
/// </remarks>
internal sealed class StagingProbe : IDisposable
{
    private static readonly TimeSpan _deadline = TimeSpan.FromMinutes(3);

    private readonly string _base;
    private readonly string _projectFile;
    private readonly string _reportFile;
    private readonly string _fingerprintFile;

    private StagingProbe(string basePath)
    {
        _base = basePath;
        _projectFile = Path.Combine(basePath, "StagingProbe.proj");
        _reportFile = Path.Combine(basePath, "report.txt");
        OutputDirectory = Path.Combine(basePath, "out");
        _fingerprintFile = Path.Combine(basePath, "obj", "fixtures", "staging.fingerprint");
        StageRoot = Path.Combine(OutputDirectory, "Fixtures");
        ProbeRoot = Path.Combine(OutputDirectory, "FixtureHosts");
    }

    /// <summary>What the staging target decided about the tree as it stands.</summary>
    internal sealed record Report(bool Stale, string Fingerprint);

    private string OutputDirectory { get; }

    /// <summary>Where the target stages plugin fixtures.</summary>
    internal string StageRoot { get; }

    /// <summary>Where the target stages the helper processes tests run.</summary>
    internal string ProbeRoot { get; }

    /// <summary>The staging rules under test, recorded into this assembly at build time.</summary>
    internal static string TargetsFile { get; } =
        typeof(StagingProbe)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(metadata => metadata.Key == "PluginFixtureTargetsFile")
            .Value!;

    /// <summary>A loader source file, which the staging rules treat as an input.</summary>
    internal static string LoaderSourceFile { get; } =
        Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(TargetsFile)!,
                "..",
                "EdFi.Api.Plugins.Hosting",
                "PluginLoader.cs"
            )
        );

    internal static StagingProbe Create()
    {
        string basePath = Path.Combine(
            Path.GetTempPath(),
            "edfi-plugin-staging",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(basePath);

        StagingProbe probe = new(basePath);

        File.WriteAllText(
            probe._projectFile,
            """
            <Project>
                <Import Project="$(PluginFixtureTargetsFile)" />
                <Target Name="ReportPluginFixtureStaging" DependsOnTargets="PrunePluginFixtureStaging">
                    <WriteLinesToFile
                        File="$(PluginFixtureStagingReportFile)"
                        Overwrite="true"
                        Lines="stale=$(PluginFixtureStagingIsStale);fingerprint=$(PluginFixtureFingerprint)"
                    />
                </Target>
            </Project>

            """
        );

        return probe;
    }

    /// <summary>Runs the staging rules over the tree as it stands and reports what they decided.</summary>
    internal Report Run()
    {
        ProcessStartInfo start = new()
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (
            !Path.GetFileNameWithoutExtension(start.FileName)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        )
        {
            start.FileName = "dotnet";
        }

        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(_projectFile);
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-verbosity:quiet");
        start.ArgumentList.Add("-target:ReportPluginFixtureStaging");
        start.ArgumentList.Add($"-property:PluginFixtureTargetsFile={TargetsFile}");
        start.ArgumentList.Add($"-property:PluginFixtureStagingReportFile={_reportFile}");
        start.ArgumentList.Add("-property:Configuration=Debug");
        start.ArgumentList.Add($"-property:OutDir={OutputDirectory}{Path.DirectorySeparatorChar}");
        start.ArgumentList.Add(
            $"-property:IntermediateOutputPath={Path.Combine(_base, "obj")}{Path.DirectorySeparatorChar}"
        );

        ChildProcess.Result result = ChildProcess.Run(start, _deadline, "The staging probe");

        if (result.ExitCode != 0 || !File.Exists(_reportFile))
        {
            throw new AssertionException($"The staging probe reported nothing. Output: {result.Output}");
        }

        Dictionary<string, string> reported = File.ReadAllLines(_reportFile)
            .Select(line => line.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

        // The target sets its staleness property only when the tree is stale, so an absent or empty
        // value is the fresh case rather than a missing answer.
        return new Report(
            reported.GetValueOrDefault("stale") == "true",
            reported.GetValueOrDefault("fingerprint", string.Empty)
        );
    }

    /// <summary>
    /// Leaves the tree the seed describes, recorded as fresh, which is the state an ordinary build
    /// finishes in.
    /// </summary>
    /// <remarks>
    /// The freshness record is written by the publish, which is far too expensive to run here, so it is
    /// produced the only other honest way: the tree is seeded, the rules are run over it to obtain the
    /// fingerprint of exactly that tree, and the tree is seeded again because that first run removed it.
    /// </remarks>
    internal void SeedAndSeal(Action seed)
    {
        seed();

        Report first = Run();

        if (!first.Stale)
        {
            throw new AssertionException(
                "A tree with no recorded fingerprint should be stale, so this probe is not measuring "
                    + "what it claims to measure."
            );
        }

        seed();

        Directory.CreateDirectory(Path.GetDirectoryName(_fingerprintFile)!);
        File.WriteAllText(_fingerprintFile, first.Fingerprint + Environment.NewLine);
    }

    /// <summary>Writes a file into the temporary tree, creating what it needs on the way.</summary>
    internal static void WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "staged");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not worth failing a passing test over a temporary directory.
        }
    }
}

/// <summary>
/// A staged tree nothing has changed since it was published.
/// </summary>
/// <remarks>
/// The expensive mistake is here rather than in the stale case: a rule that classifies a directory it
/// should have kept deletes the declared outputs of a tree that was up to date, so the next build
/// republishes every fixture, every time, while reporting nothing at all.
/// </remarks>
[TestFixture]
public class Given_a_staged_tree_that_has_not_changed
{
    private const string RemovedFixture = "Acme.Removed";
    private const string RemovedHost = "Acme.RemovedHost";

    private StagingProbe _probe = null!;
    private StagingProbe.Report _report = null!;

    [SetUp]
    public void Setup()
    {
        _probe = StagingProbe.Create();
        _probe.SeedAndSeal(Seed);
        _report = _probe.Run();
    }

    [TearDown]
    public void TearDown() => _probe.Dispose();

    private void Seed()
    {
        // One ordinary fixture, one portable fixture, one helper process, and one directory of each
        // kind that no longer belongs to anything.
        StagingProbe.WriteFile(
            Path.Combine(_probe.StageRoot, PluginFixtures.Good, $"{PluginFixtures.Good}.dll")
        );
        StagingProbe.WriteFile(
            Path.Combine(
                _probe.StageRoot,
                PluginFixtures.NativePortable,
                $"{PluginFixtures.NativePortable}.deps.json"
            )
        );
        StagingProbe.WriteFile(Path.Combine(_probe.StageRoot, RemovedFixture, "leftover.dll"));
        StagingProbe.WriteFile(
            Path.Combine(
                _probe.ProbeRoot,
                PluginFixtures.NativeProbeHostName,
                $"{PluginFixtures.NativeProbeHostName}.runtimeconfig.json"
            )
        );
        StagingProbe.WriteFile(Path.Combine(_probe.ProbeRoot, RemovedHost, "leftover.dll"));
    }

    [Test]
    public void It_does_not_call_the_tree_stale()
    {
        _report.Stale.Should().BeFalse();
    }

    [Test]
    public void It_keeps_an_ordinary_fixture_directory()
    {
        Directory.Exists(Path.Combine(_probe.StageRoot, PluginFixtures.Good)).Should().BeTrue();
    }

    [Test]
    public void It_keeps_a_portable_fixture_directory()
    {
        // The portable fixture is staged by a list of its own, and a kind left out of the expected set
        // is deleted here on every build however fresh the tree is. That removes the declared outputs
        // the publish is skipped on, so the whole collection republishes each time.
        Directory.Exists(Path.Combine(_probe.StageRoot, PluginFixtures.NativePortable)).Should().BeTrue();
    }

    [Test]
    public void It_keeps_a_helper_process_directory()
    {
        Directory
            .Exists(Path.Combine(_probe.ProbeRoot, PluginFixtures.NativeProbeHostName))
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_removes_a_fixture_directory_nothing_declares()
    {
        // The other half of the same rule, and the reason it exists: a fixture dropped from the list
        // leaves a directory behind that a test enumerating the root would still find.
        Directory.Exists(Path.Combine(_probe.StageRoot, RemovedFixture)).Should().BeFalse();
    }

    [Test]
    public void It_removes_a_helper_directory_nothing_declares()
    {
        Directory.Exists(Path.Combine(_probe.ProbeRoot, RemovedHost)).Should().BeFalse();
    }
}

/// <summary>
/// A staged helper process missing one of the files it was published with.
/// </summary>
/// <remarks>
/// A helper is a whole published program, and losing one file from it leaves an entry assembly that
/// still exists and still looks up to date while the process it belongs to can no longer start. The
/// staged file set is in the fingerprint so that this is a stale tree rather than a mysterious failure
/// in whatever test runs that helper.
/// </remarks>
[TestFixture]
public class Given_a_staged_helper_process_has_lost_a_file
{
    private StagingProbe _probe = null!;
    private StagingProbe.Report _report = null!;

    [SetUp]
    public void Setup()
    {
        _probe = StagingProbe.Create();
        _probe.SeedAndSeal(Seed);

        File.Delete(
            Path.Combine(
                _probe.ProbeRoot,
                PluginFixtures.NativeProbeHostName,
                $"{PluginFixtures.NativeProbeHostName}.runtimeconfig.json"
            )
        );

        _report = _probe.Run();
    }

    [TearDown]
    public void TearDown() => _probe.Dispose();

    private void Seed()
    {
        StagingProbe.WriteFile(
            Path.Combine(
                _probe.ProbeRoot,
                PluginFixtures.NativeProbeHostName,
                $"{PluginFixtures.NativeProbeHostName}.dll"
            )
        );
        StagingProbe.WriteFile(
            Path.Combine(
                _probe.ProbeRoot,
                PluginFixtures.NativeProbeHostName,
                $"{PluginFixtures.NativeProbeHostName}.runtimeconfig.json"
            )
        );
    }

    [Test]
    public void It_calls_the_tree_stale()
    {
        _report.Stale.Should().BeTrue();
    }

    [Test]
    public void It_removes_the_incomplete_helper_rather_than_leaving_it_to_be_run()
    {
        Directory
            .Exists(Path.Combine(_probe.ProbeRoot, PluginFixtures.NativeProbeHostName))
            .Should()
            .BeFalse();
    }
}

/// <summary>
/// A loader source edited after the fixtures and helpers were staged.
/// </summary>
/// <remarks>
/// A helper process references the loader by project, so its published copy answers with whatever the
/// loader was when it was staged. A probe carrying an older loader does not fail; it answers a question
/// nobody asked, which is why this has to be a stale tree.
/// </remarks>
[TestFixture]
public class Given_a_loader_source_has_been_edited_since_staging
{
    private StagingProbe _probe = null!;
    private StagingProbe.Report _report = null!;
    private DateTime _originalWriteTime;

    [SetUp]
    public void Setup()
    {
        _probe = StagingProbe.Create();
        _probe.SeedAndSeal(Seed);

        // An edit as the staging rules see one: they carry the modification time of every input, so
        // moving one is the smallest faithful way to make this file look edited. It is put back in the
        // teardown, and no byte of it is read or written.
        _originalWriteTime = File.GetLastWriteTimeUtc(StagingProbe.LoaderSourceFile);
        File.SetLastWriteTimeUtc(StagingProbe.LoaderSourceFile, _originalWriteTime + TimeSpan.FromMinutes(1));

        _report = _probe.Run();
    }

    [TearDown]
    public void TearDown()
    {
        File.SetLastWriteTimeUtc(StagingProbe.LoaderSourceFile, _originalWriteTime);
        _probe.Dispose();
    }

    private void Seed()
    {
        StagingProbe.WriteFile(
            Path.Combine(_probe.StageRoot, PluginFixtures.Good, $"{PluginFixtures.Good}.dll")
        );
        StagingProbe.WriteFile(
            Path.Combine(
                _probe.ProbeRoot,
                PluginFixtures.NativeProbeHostName,
                $"{PluginFixtures.NativeProbeHostName}.dll"
            )
        );
    }

    [Test]
    public void It_calls_the_tree_stale()
    {
        _report.Stale.Should().BeTrue();
    }

    [Test]
    public void It_removes_the_staged_helper_so_the_next_build_cannot_run_the_older_one()
    {
        Directory
            .Exists(Path.Combine(_probe.ProbeRoot, PluginFixtures.NativeProbeHostName))
            .Should()
            .BeFalse();
    }
}
