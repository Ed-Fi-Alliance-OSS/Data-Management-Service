// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Runs the fixture staging rules over a temporary tree and reports what they decided.
/// </summary>
/// <remarks>
/// <para>
/// Both ways of getting freshness wrong are silent. A tree wrongly called stale republishes on every
/// build, which costs time and looks like nothing. A tree wrongly kept fresh serves a fixture plugin
/// built from sources that have since changed, and every test over it then answers a question about
/// bytes nobody is looking at. So the rules are run rather than read.
/// </para>
/// <para>
/// Only the prune target is run, never the publish, so these cases cost an MSBuild evaluation rather
/// than a fixture publish. The output and intermediate directories are pointed at a temporary tree, so
/// nothing here touches the real staged fixture or the real freshness record.
/// </para>
/// </remarks>
internal sealed class FrontendStagingProbe : IDisposable
{
    private static readonly TimeSpan _deadline = TimeSpan.FromMinutes(3);

    private readonly string _base;
    private readonly string _projectFile;
    private readonly string _reportFile;

    private FrontendStagingProbe(string basePath)
    {
        _base = basePath;
        _projectFile = Path.Combine(basePath, "FrontendStagingProbe.proj");
        _reportFile = Path.Combine(basePath, "report.txt");
        OutputDirectory = Path.Combine(basePath, "out");
        StageRoot = Path.Combine(OutputDirectory, "PluginFixtures");
        FingerprintFile = Path.Combine(basePath, "obj", "pluginfixtures", "staging.fingerprint");
    }

    /// <summary>What the rules decided about the tree as it stands.</summary>
    internal sealed record Report(bool Stale, string Fingerprint, IReadOnlyList<string> Inputs);

    private string OutputDirectory { get; }

    /// <summary>Where the rules stage the fixture plugin.</summary>
    internal string StageRoot { get; }

    /// <summary>Where the rules record that the tree is up to date.</summary>
    internal string FingerprintFile { get; }

    /// <summary>The staging rules under test, recorded into this assembly at build time.</summary>
    internal static string TargetsFile { get; } =
        typeof(FrontendStagingProbe)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(metadata => metadata.Key == "PluginFrontendFixtureTargetsFile")
            .Value!;

    /// <summary>The directory holding this repository's src tree, derived the way the rules derive it.</summary>
    internal static string RepositoryRoot { get; } =
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(TargetsFile)!, "..", "..", ".."));

    internal static FrontendStagingProbe Create()
    {
        string basePath = Path.Combine(
            Path.GetTempPath(),
            "dms-plugin-frontend-staging",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(basePath);

        FrontendStagingProbe probe = new(basePath);

        File.WriteAllText(
            probe._projectFile,
            """
            <Project>
                <Import Project="$(PluginFrontendFixtureTargetsFile)" />
                <Target Name="ReportStaging" DependsOnTargets="PrunePluginFrontendFixtureStaging">
                    <WriteLinesToFile
                        File="$(PluginFrontendFixtureStagingReportFile)"
                        Overwrite="true"
                        Lines="stale=$(PluginFrontendFixtureStale);fingerprint=$(PluginFrontendFixtureFingerprint);inputs=$(PluginFrontendFixtureInputPaths)"
                    />
                </Target>
            </Project>

            """
        );

        return probe;
    }

    /// <summary>Runs the rules over the tree as it stands.</summary>
    internal Report Run()
    {
        ProcessStartInfo start = new()
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(_projectFile);
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-verbosity:quiet");
        start.ArgumentList.Add("-target:ReportStaging");
        start.ArgumentList.Add($"-property:PluginFrontendFixtureTargetsFile={TargetsFile}");
        start.ArgumentList.Add($"-property:PluginFrontendFixtureStagingReportFile={_reportFile}");
        start.ArgumentList.Add("-property:Configuration=Debug");
        start.ArgumentList.Add($"-property:OutDir={OutputDirectory}{Path.DirectorySeparatorChar}");
        start.ArgumentList.Add(
            $"-property:IntermediateOutputPath={Path.Combine(_base, "obj")}{Path.DirectorySeparatorChar}"
        );

        using Process process =
            Process.Start(start) ?? throw new AssertionException("the staging probe did not start");

        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

        if (!process.WaitForExit(_deadline))
        {
            process.Kill(entireProcessTree: true);
            throw new AssertionException($"the staging probe did not finish. Output: {output}");
        }

        if (process.ExitCode != 0 || !File.Exists(_reportFile))
        {
            throw new AssertionException($"the staging probe reported nothing. Output: {output}");
        }

        // WriteLinesToFile splits a semicolon-joined value across lines, so the report arrives as one
        // line per part rather than as the single line it was written as. Reading it back by line and
        // rejoining on the separator the report used puts it back into one form to parse.
        string reportText = string.Join(";", File.ReadAllLines(_reportFile));
        char[] separators = [';'];

        Dictionary<string, string> reported = reportText
            .Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

        // The rules set the staleness property only when the tree is stale, so an absent value is the
        // fresh case rather than a missing answer.
        return new Report(
            reported.GetValueOrDefault("stale") == "true",
            reported.GetValueOrDefault("fingerprint", string.Empty),
            [
                .. reportText
                    .Split("inputs=", 2)[^1]
                    .Split(
                        separators,
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    ),
            ]
        );
    }

    /// <summary>
    /// Leaves the tree recorded as fresh, which is the state an ordinary build finishes in.
    /// </summary>
    /// <remarks>
    /// The record is written by the publish, which is far too expensive to run here, so it is produced
    /// the only other honest way: the rules are run to obtain the fingerprint of exactly this tree and
    /// that value is written where the publish would have written it.
    /// </remarks>
    internal Report Seal(Action? seed = null)
    {
        seed?.Invoke();

        // This run is stale by construction, because no record exists yet, and it removes whatever the
        // seed left behind. Its fingerprint is the fingerprint of the tree as the seed left it, because
        // the rules compute that before they remove anything.
        Report sealing = Run();

        // So the seed is applied again, and only then is the record written.
        seed?.Invoke();

        Directory.CreateDirectory(Path.GetDirectoryName(FingerprintFile)!);
        File.WriteAllText(FingerprintFile, sealing.Fingerprint + Environment.NewLine);

        return Run();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leaving a temporary directory behind is not worth failing a passing test over.
        }
    }
}

/// <summary>
/// Restores a file's last-write time when it goes out of scope.
/// </summary>
/// <remarks>
/// The freshness record is a fingerprint over each input's identity and modification time, so touching
/// one is how a test asks "would a change here be noticed?" without editing a real source file or a
/// real package version. Reversible by construction: the timestamp is put back.
/// </remarks>
internal sealed class TouchedFile : IDisposable
{
    private readonly string _path;
    private readonly DateTime _originalUtc;

    private TouchedFile(string path, DateTime originalUtc)
    {
        _path = path;
        _originalUtc = originalUtc;
    }

    internal static TouchedFile Touch(string path)
    {
        File.Exists(path).Should().BeTrue($"'{path}' has to exist to be a meaningful staging input");

        DateTime originalUtc = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, originalUtc.AddMinutes(1));

        return new TouchedFile(path, originalUtc);
    }

    public void Dispose() => File.SetLastWriteTimeUtc(_path, _originalUtc);
}

[TestFixture]
[NonParallelizable]
public class Given_the_frontend_fixture_staging_rules
{
    /// <summary>
    /// The inputs whose omission would leave the tree wrongly fresh. Each one can change what the
    /// fixture publish produces: the props the fixture inherits, the central package versions those
    /// chain to, the configuration a restore reads, and the project files of the contracts it
    /// references, including the one reached only through another reference.
    /// </summary>
    private static readonly string[] _closurePaths =
    [
        "plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/Fixtures/Directory.Build.props",
        "plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/Fixtures/Directory.Packages.props",
        "plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/Fixtures/Plugins/Acme.DmsContributor/Acme.DmsContributor.csproj",
        "plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/Fixtures/Plugins/Acme.DmsContributor/DmsContributorPlugin.cs",
        "plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/Fixtures/Plugins/Acme.RealHelpers/Acme.RealHelpers.csproj",
        "plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/Fixtures/Plugins/Acme.RealHelpers/RealHelpersPlugin.cs",
        "plugins/EdFi.Api.Plugins/EdFi.Api.Plugins.csproj",
        "plugins/EdFi.Api.Plugins.Hosting/EdFi.Api.Plugins.Hosting.csproj",
        "plugins/Directory.Build.props",
        "dms/core/EdFi.DataManagementService.CustomValidation/EdFi.DataManagementService.CustomValidation.csproj",
        "dms/core/EdFi.DataManagementService.Core.External/EdFi.DataManagementService.Core.External.csproj",
        "dms/backend/EdFi.DataManagementService.Backend.External/EdFi.DataManagementService.Backend.External.csproj",
        "dms/Directory.Build.props",
        "Directory.Packages.props",
        "nuget.config",
    ];

    [TestCaseSource(nameof(_closurePaths))]
    public void It_watches_every_input_the_published_fixture_depends_on(string relativePath)
    {
        using FrontendStagingProbe probe = FrontendStagingProbe.Create();

        FrontendStagingProbe.Report report = probe.Run();

        string expected = Path.GetFullPath(Path.Combine(FrontendStagingProbe.RepositoryRoot, relativePath));

        report
            .Inputs.Select(Path.GetFullPath)
            .Should()
            .Contain(expected, "a change there can alter what the fixture publish produces");
    }

    [Test]
    public void It_excludes_generated_output_from_what_it_watches()
    {
        using FrontendStagingProbe probe = FrontendStagingProbe.Create();

        FrontendStagingProbe.Report report = probe.Run();

        // A generated file changes on every build, so watching one would leave the tree permanently
        // out of date and republish the fixture forever.
        report
            .Inputs.Should()
            .NotContain(input =>
                input.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal
                )
                || input.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal
                )
                || input.Contains("/obj/", StringComparison.Ordinal)
                || input.Contains("/bin/", StringComparison.Ordinal)
            );
    }

    /// <summary>
    /// The case the previous input set failed: a change to a file the fixture inherits rather than
    /// contains. It has to make the recorded freshness stop matching.
    /// </summary>
    [Test]
    public void It_calls_the_tree_stale_when_an_inherited_props_file_changes()
    {
        using FrontendStagingProbe probe = FrontendStagingProbe.Create();
        FrontendStagingProbe.Report sealed_ = probe.Seal();

        sealed_.Stale.Should().BeFalse("the tree was just recorded as fresh");

        string inheritedProps = Path.Combine(
            FrontendStagingProbe.RepositoryRoot,
            "plugins",
            "EdFi.Api.Plugins.Hosting.Tests.Unit",
            "Fixtures",
            "Directory.Packages.props"
        );

        FrontendStagingProbe.Report afterTouch;
        using (TouchedFile.Touch(inheritedProps))
        {
            afterTouch = probe.Run();
        }

        afterTouch.Fingerprint.Should().NotBe(sealed_.Fingerprint);
        afterTouch.Stale.Should().BeTrue();
    }

    /// <summary>
    /// The second staged fixture's sources are watched too, and by derivation rather than by being
    /// named again: the input set is built from the fixture item list, so a fixture added without its
    /// sources being watched is not a state this file can be left in.
    /// </summary>
    [Test]
    public void It_calls_the_tree_stale_when_the_real_helper_fixture_changes()
    {
        using FrontendStagingProbe probe = FrontendStagingProbe.Create();
        FrontendStagingProbe.Report sealed_ = probe.Seal();

        string fixtureSource = Path.Combine(
            FrontendStagingProbe.RepositoryRoot,
            "plugins",
            "EdFi.Api.Plugins.Hosting.Tests.Unit",
            "Fixtures",
            "Plugins",
            "Acme.RealHelpers",
            "RealHelpersPlugin.cs"
        );

        FrontendStagingProbe.Report afterTouch;
        using (TouchedFile.Touch(fixtureSource))
        {
            afterTouch = probe.Run();
        }

        afterTouch.Fingerprint.Should().NotBe(sealed_.Fingerprint);
        afterTouch.Stale.Should().BeTrue();
    }

    /// <summary>
    /// Both fixtures are staged, so a rule that watched only the first would leave the second's
    /// directory unaccounted for.
    /// </summary>
    [Test]
    public void It_stages_both_fixtures()
    {
        using FrontendStagingProbe probe = FrontendStagingProbe.Create();

        FrontendStagingProbe.Report report = probe.Run();

        report.Fingerprint.Should().Contain("Acme.DmsContributor");
        report.Fingerprint.Should().Contain("Acme.RealHelpers");
    }

    [Test]
    public void It_calls_the_tree_stale_when_the_central_package_versions_change()
    {
        using FrontendStagingProbe probe = FrontendStagingProbe.Create();
        FrontendStagingProbe.Report sealed_ = probe.Seal();

        string centralVersions = Path.Combine(
            FrontendStagingProbe.RepositoryRoot,
            "Directory.Packages.props"
        );

        FrontendStagingProbe.Report afterTouch;
        using (TouchedFile.Touch(centralVersions))
        {
            afterTouch = probe.Run();
        }

        afterTouch.Fingerprint.Should().NotBe(sealed_.Fingerprint);
        afterTouch.Stale.Should().BeTrue();
    }

    /// <summary>
    /// The other half of the bargain: an unchanged tree stays fresh, so an ordinary second build does
    /// not republish the fixture. A rule that answered "stale" every time would satisfy every
    /// invalidation case above and be useless.
    /// </summary>
    [Test]
    public void It_calls_the_same_tree_fresh_again_when_nothing_changed()
    {
        using FrontendStagingProbe probe = FrontendStagingProbe.Create();
        FrontendStagingProbe.Report sealed_ = probe.Seal();

        sealed_.Stale.Should().BeFalse();

        FrontendStagingProbe.Report second = probe.Run();

        second.Fingerprint.Should().Be(sealed_.Fingerprint);
        second.Stale.Should().BeFalse("an unchanged tree must not republish the fixture");
    }

    /// <summary>
    /// And the fingerprint is a function of the inputs rather than of the moment: restoring a touched
    /// input restores it. Without this the invalidation cases above would pass for a value that simply
    /// changed on every run.
    /// </summary>
    /// <remarks>
    /// Staleness is not asserted after the restore, because it cannot be: a stale run deletes the
    /// freshness record so that the publish re-runs, which is the behaviour the invalidation cases
    /// depend on. The fingerprint is the part that has to come back.
    /// </remarks>
    [Test]
    public void It_derives_the_same_fingerprint_again_once_a_touched_input_is_restored()
    {
        using FrontendStagingProbe probe = FrontendStagingProbe.Create();
        FrontendStagingProbe.Report before = probe.Run();

        string nugetConfiguration = Path.Combine(FrontendStagingProbe.RepositoryRoot, "nuget.config");

        FrontendStagingProbe.Report touched;
        using (TouchedFile.Touch(nugetConfiguration))
        {
            touched = probe.Run();
        }

        touched.Fingerprint.Should().NotBe(before.Fingerprint);
        probe.Run().Fingerprint.Should().Be(before.Fingerprint);
    }

    /// <summary>
    /// A staged file removed by hand leaves the entry assembly and its manifest untouched, so a check
    /// on those two would see an up-to-date tree with an incomplete closure. The fingerprint carries
    /// the staged file set for that reason, and a stale tree is removed wholesale so the republish
    /// cannot inherit a leftover.
    /// </summary>
    [Test]
    public void It_calls_the_tree_stale_when_a_staged_file_is_missing_and_then_removes_the_tree()
    {
        using FrontendStagingProbe probe = FrontendStagingProbe.Create();

        string stagedDirectory = Path.Combine(probe.StageRoot, "Acme.DmsContributor");

        void Seed()
        {
            Directory.CreateDirectory(stagedDirectory);
            File.WriteAllText(Path.Combine(stagedDirectory, "Acme.DmsContributor.dll"), "staged");
            File.WriteAllText(Path.Combine(stagedDirectory, "Acme.DmsContributor.deps.json"), "staged");
            File.WriteAllText(Path.Combine(stagedDirectory, "a-dependency.dll"), "staged");
        }

        FrontendStagingProbe.Report sealed_ = probe.Seal(Seed);
        sealed_.Stale.Should().BeFalse("a fully staged tree with a matching record is up to date");

        // One file taken away, which leaves the entry assembly and its manifest exactly as they were.
        File.Delete(Path.Combine(stagedDirectory, "a-dependency.dll"));

        FrontendStagingProbe.Report afterLoss = probe.Run();

        afterLoss.Stale.Should().BeTrue("a staged file went missing");
        Directory
            .Exists(stagedDirectory)
            .Should()
            .BeFalse("a stale tree is removed wholesale so the republish cannot inherit a leftover");
    }
}
