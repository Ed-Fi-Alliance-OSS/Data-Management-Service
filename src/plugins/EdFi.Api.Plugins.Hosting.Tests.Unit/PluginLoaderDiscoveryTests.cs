// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

[TestFixture]
public class Given_a_plugin_root_that_does_not_exist_and_an_empty_allowlist
{
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _run = PluginLoaderProbe.Run(
            Path.Combine(Path.GetTempPath(), "edfi-plugin-tests", Guid.NewGuid().ToString("N")),
            allowed: string.Empty
        );
    }

    [Test]
    public void It_returns_no_plugins()
    {
        // The shipped default, and the case that has to keep working for every deployment whose image
        // never created a plugin directory.
        _run.Failure.Should().BeNull();
        _run.Result!.Plugins.Should().BeEmpty();
    }

    [Test]
    public void It_writes_nothing_at_all()
    {
        _run.Diagnostics.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_a_plugin_root_that_does_not_exist_and_a_populated_allowlist
{
    private string _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "edfi-plugin-tests", Guid.NewGuid().ToString("N"));
        _run = PluginLoaderProbe.RunExpectingFailure(_root, PluginFixtures.Good);
    }

    [Test]
    public void It_refuses_because_plugins_were_asked_for_and_cannot_be_delivered()
    {
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.PluginRootMissing);
    }

    [Test]
    public void It_names_the_root_the_operator_has_to_create()
    {
        _run.Failure!.Message.Should().Contain(_root);
    }

    [Test]
    public void It_does_not_surface_the_raw_resolution_failure()
    {
        // Directory.ResolveLinkTarget throws DirectoryNotFoundException for a path that does not exist,
        // so resolving the root before checking that it exists would produce that instead of a message
        // naming the path. The order is the criterion, and this is what pins it.
        _run.Failure.Should().NotBeOfType<DirectoryNotFoundException>();
        _run.Failure!.InnerException.Should().BeNull();
    }
}

[TestFixture]
public class Given_a_misspelled_allowlist_entry
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Good);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, "Acme.Godo");
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_with_the_missing_directory_failure()
    {
        // Not a raw DirectoryNotFoundException out of the resolution step, which is what a check that
        // resolved before testing for existence would produce.
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.PluginDirectoryMissing);
        _run.Failure!.InnerException.Should().BeNull();
    }

    [Test]
    public void It_names_the_path_it_expected()
    {
        _run.Failure!.Message.Should().Contain(Path.Combine(_root.RootPath, "Acme.Godo"));
    }
}

[TestFixture]
public class Given_a_plugin_directory_missing_its_entry_assembly
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Good);
        _root.Remove(PluginFixtures.Good, $"{PluginFixtures.Good}.dll");
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.Good);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_and_names_the_expected_path()
    {
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.EntryAssemblyMissing);
        _run.Failure!.Message.Should().Contain($"{PluginFixtures.Good}.dll");
    }
}

[TestFixture]
public class Given_a_plugin_directory_missing_its_dependency_manifest
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Good);
        _root.Remove(PluginFixtures.Good, $"{PluginFixtures.Good}.deps.json");
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.Good);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_rather_than_letting_the_closure_fail_at_first_use()
    {
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.DepsJsonMissing);
        _run.Failure!.Message.Should().Contain($"{PluginFixtures.Good}.deps.json");
    }
}

[TestFixture]
public class Given_two_well_formed_plugins_in_allowlist_order
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Good);
        _root.Add(PluginFixtures.NonCandidates);

        // Written in the reverse of alphabetical order, so a sorted result would be visibly wrong.
        _run = PluginLoaderProbe.Run(_root.RootPath, $"{PluginFixtures.NonCandidates},{PluginFixtures.Good}");
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_returns_one_instance_each_in_the_order_written()
    {
        _run.Failure.Should().BeNull();
        _run.Result!.Plugins.Select(plugin => plugin.Name)
            .Should()
            .Equal(PluginFixtures.NonCandidates, PluginFixtures.Good);
    }

    [Test]
    public void It_returns_the_plugin_instance_the_directory_names()
    {
        _run.Result!.Plugins.Select(plugin => plugin.Instance.Name)
            .Should()
            .Equal(PluginFixtures.NonCandidates, PluginFixtures.Good);
    }

    [Test]
    public void It_writes_one_line_per_allowlisted_name_in_order()
    {
        _run.DiagnosticLines.Should().HaveCount(2);
        _run.DiagnosticLines[0].Should().Contain(PluginFixtures.NonCandidates).And.Contain("loaded from");
        _run.DiagnosticLines[1].Should().Contain(PluginFixtures.Good).And.Contain("loaded from");
    }
}

[TestFixture]
public class Given_a_plugin_that_fails_after_one_has_loaded
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Good);
        _root.Add(PluginFixtures.NoSubclass);

        _run = PluginLoaderProbe.RunExpectingFailure(
            _root.RootPath,
            $"{PluginFixtures.Good},{PluginFixtures.NoSubclass}"
        );
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_reports_the_outcome_of_every_name_it_reached()
    {
        _run.DiagnosticLines.Should().HaveCount(2);
        _run.DiagnosticLines[0].Should().Contain(PluginFixtures.Good).And.Contain("loaded from");
        _run.DiagnosticLines[1].Should().Contain(PluginFixtures.NoSubclass).And.Contain("failed:");
    }

    [Test]
    public void It_stops_at_the_failing_entry()
    {
        // An allowlisted plugin that does not load is fatal, so nothing after it is reached. That is
        // inherent rather than incidental, and it is why the channel carries no third line.
        _run.Failure!.PluginName.Should().Be(PluginFixtures.NoSubclass);
    }
}

[TestFixture]
public class Given_a_directory_nobody_allowlisted
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Good);
        _root.Add(PluginFixtures.NameMismatch, asName: "Acme.NobodyAsked");

        _run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.Good);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_loads_only_what_was_allowlisted()
    {
        _run.Failure.Should().BeNull();
        _run.Result!.Plugins.Select(plugin => plugin.Name).Should().Equal(PluginFixtures.Good);
    }

    [Test]
    public void It_warns_and_names_the_directory_it_ignored()
    {
        // The directory holds a plugin whose Name disagrees with its directory, so if anything inside it
        // had been opened the run would have failed rather than warned. That is the assertion: discovery
        // is driven by the allowlist, and an unasked-for directory is never opened, probed, or read.
        _run.DiagnosticLines.Should().HaveCount(2);
        _run.DiagnosticLines[1].Should().Contain("ignoring directories not in the allowlist");
        _run.DiagnosticLines[1].Should().Contain("Acme.NobodyAsked");
    }
}

/// <summary>
/// The allowlist is validated in full before anything touches the filesystem. Each case here would fail
/// differently if it were not.
/// </summary>
[TestFixture]
public class Given_an_allowlist_whose_later_entry_is_invalid
{
    private TemporaryPluginRoot _root = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Good);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_the_entry_without_loading_the_valid_one_before_it()
    {
        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(
            _root.RootPath,
            $"{PluginFixtures.Good},../sibling"
        );

        run.Failure!.Reason.Should().Be(PluginLoadFailure.InvalidAllowlistName);

        // Nothing was loaded and no per-name line was written, so the well-formed first entry was never
        // processed. A loader that validated entry by entry would have loaded it and written its line.
        run.DiagnosticLines.Should().ContainSingle();
        run.DiagnosticLines[0].Should().StartWith("plugins:");
    }

    [Test]
    public void It_refuses_the_entry_without_asking_whether_the_root_exists()
    {
        // Against a root that does not exist, a loader that probed the filesystem before validating the
        // allowlist would report the missing root. Reporting the entry instead is what proves the order.
        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(
            Path.Combine(Path.GetTempPath(), "edfi-plugin-tests", Guid.NewGuid().ToString("N")),
            $"{PluginFixtures.Good},sub/dir"
        );

        run.Failure!.Reason.Should().Be(PluginLoadFailure.InvalidAllowlistName);
    }

    [Test]
    public void It_refuses_a_later_duplicate_without_loading_the_first_entry()
    {
        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(
            _root.RootPath,
            $"{PluginFixtures.Good},acme.good"
        );

        run.Failure!.Reason.Should().Be(PluginLoadFailure.DuplicateAllowlistEntry);
        run.DiagnosticLines.Should().ContainSingle();
        run.DiagnosticLines[0].Should().StartWith("plugins:");
    }
}

/// <summary>
/// A filesystem answers <c>Directory.Exists</c> and <c>File.Exists</c>, and a case-insensitive one
/// answers true for a name that is not the name it was asked about. The three names the loader composes
/// from an allowlist entry therefore have to be read back from the directory and compared ordinally,
/// like every other identity comparison here.
/// </summary>
[TestFixture]
public class Given_a_staged_name_that_differs_only_in_case
{
    private const string WrongCaseDirectory = "acme.good";
    private const string WrongCaseEntryAssembly = "acme.good.dll";
    private const string WrongCaseManifest = "acme.good.deps.json";

    private TemporaryPluginRoot _root = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();

        // Recorded as evidence rather than asserted, and measured in this tree rather than inferred from
        // the operating system's name. Where it is false the wrong-cased staging below could never have
        // been opened at all, and these cases refuse for that reason instead; the refusal being the same
        // either way is what the correction is for.
        TestContext.Out.WriteLine($"filesystem folds case: {_root.FilesystemFoldsCase()}");
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    /// <summary>
    /// Asserts the staging actually produced the spelling the case is about.
    /// </summary>
    /// <remarks>
    /// A case-only rename is a no-op on some filesystems. Without this, such a case would pass for the
    /// wrong reason: the loader would be refusing a name that is not there at all rather than one that
    /// is there under another spelling.
    /// </remarks>
    private static void RequirePhysicalEntry(string parent, string expected)
    {
        Directory.EnumerateFileSystemEntries(parent).Select(Path.GetFileName).Should().Contain(expected);
    }

    private void ItRefusesWithOneLine(PluginLoadFailure reason, string actualSpelling)
    {
        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.Good);

        run.Failure!.Reason.Should().Be(reason);
        run.Failure!.Message.Should().Contain(actualSpelling);
        run.Failure!.Message.Should().Contain(PluginFixtures.Good);
        run.DiagnosticLines.Should().ContainSingle();
        run.DiagnosticLines[0].Should().StartWith($"plugin '{PluginFixtures.Good}' failed:");
    }

    [Test]
    public void It_refuses_a_plugin_directory_spelled_differently()
    {
        // The self-contradiction this removes: the run used to load this directory and then announce, on
        // the same channel, that it was ignoring a directory not in the allowlist, naming the very one it
        // had just loaded.
        _root.AddUnderDirectoryName(PluginFixtures.Good, WrongCaseDirectory);
        RequirePhysicalEntry(_root.RootPath, WrongCaseDirectory);

        ItRefusesWithOneLine(PluginLoadFailure.PluginDirectoryMissing, WrongCaseDirectory);
    }

    [Test]
    public void It_refuses_an_entry_assembly_spelled_differently()
    {
        // This one used to load and then report EntryAssemblyFileName as the allowlist spelling, which is
        // a file name that is not on disk.
        _root.Add(PluginFixtures.Good);
        _root.RenameFileInPlugin(PluginFixtures.Good, $"{PluginFixtures.Good}.dll", WrongCaseEntryAssembly);
        RequirePhysicalEntry(Path.Combine(_root.RootPath, PluginFixtures.Good), WrongCaseEntryAssembly);

        ItRefusesWithOneLine(PluginLoadFailure.EntryAssemblyMissing, WrongCaseEntryAssembly);
    }

    [Test]
    public void It_refuses_a_dependency_manifest_spelled_differently()
    {
        _root.Add(PluginFixtures.Good);
        _root.RenameFileInPlugin(PluginFixtures.Good, $"{PluginFixtures.Good}.deps.json", WrongCaseManifest);
        RequirePhysicalEntry(Path.Combine(_root.RootPath, PluginFixtures.Good), WrongCaseManifest);

        ItRefusesWithOneLine(PluginLoadFailure.DepsJsonMissing, WrongCaseManifest);
    }

    [Test]
    public void It_still_loads_when_every_name_is_exact()
    {
        // The control, without which the three refusals above would be satisfied by a loader that had
        // simply stopped accepting plugins.
        _root.Add(PluginFixtures.Good);

        PluginLoaderRun run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.Good);

        run.Failure.Should().BeNull();
        run.Result!.Plugins.Should().ContainSingle();
        run.Result!.Plugins[0].EntryAssemblyFileName.Should().Be($"{PluginFixtures.Good}.dll");
        run.DiagnosticLines.Should().ContainSingle();
    }
}
