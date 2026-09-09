// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

[TestFixture]
public class Given_an_entry_assembly_with_no_plugin_type
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.NoSubclass);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.NoSubclass);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_because_the_operator_allowlisted_something_that_contributes_nothing()
    {
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.NoPluginType);
    }

    [Test]
    public void It_throws_rather_than_logging_and_continuing()
    {
        _run.Result.Should().BeNull();
    }
}

[TestFixture]
public class Given_an_entry_assembly_with_two_plugin_types
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.TwoSubclasses);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.TwoSubclasses);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_because_choosing_between_them_would_be_arbitrary()
    {
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.MultiplePluginTypes);
    }

    [Test]
    public void It_names_what_it_found()
    {
        _run.Failure!.Message.Should().Contain("FirstPlugin").And.Contain("SecondPlugin");
    }
}

[TestFixture]
public class Given_an_entry_assembly_whose_only_other_subclasses_cannot_be_constructed
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.NonCandidates);
        _run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.NonCandidates);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_loads_the_one_type_the_host_can_construct()
    {
        // An abstract type, an open generic, a type with no public parameterless constructor and a type
        // that is not exported are each impossible for the host to construct and call. Counting any of
        // them would turn this into a multiple-plugin-types failure.
        _run.Failure.Should().BeNull();
        _run.Result!.Plugins.Should().ContainSingle();
        _run.Result!.Plugins[0].Instance.GetType().Name.Should().Be("TheOnePlugin");
    }
}

[TestFixture]
public class Given_an_entry_assembly_whose_subclasses_all_cannot_be_constructed
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.OnlyNonCandidates);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.OnlyNonCandidates);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_reports_that_none_was_found_rather_than_that_several_were()
    {
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.NoPluginType);
    }

    [Test]
    public void It_names_the_subclasses_it_could_not_use()
    {
        // Without this an implementer whose only subclass is abstract reads "no plugin type" and has
        // nothing to act on.
        _run.Failure!.Message.Should().Contain("AbstractPlugin");
        _run.Failure!.Message.Should().Contain("GenericPlugin");
        _run.Failure!.Message.Should().Contain("ParameterizedPlugin");
    }
}

[TestFixture]
public class Given_a_plugin_whose_name_disagrees_with_its_directory
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.NameMismatch);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.NameMismatch);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_and_names_both()
    {
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.PluginNameMismatch);
        _run.Failure!.Message.Should().Contain(PluginFixtures.NameMismatch).And.Contain("Some.Other.Name");
    }
}

[TestFixture]
public class Given_a_plugin_whose_name_differs_from_its_directory_only_in_case
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.CaseName);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.CaseName);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_because_the_comparison_is_ordinal()
    {
        // A plugin's identity must not depend on which filesystem the image happened to be built on, so
        // the comparison is ordinal even where the filesystem is not.
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.PluginNameMismatch);
        _run.Failure!.Message.Should().Contain("acme.casename");
    }
}

[TestFixture]
public class Given_an_entry_assembly_published_under_a_different_file_name
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        // The directory and the file name agree, and the assembly's own name does not. That is the one
        // of the four equalities the file name cannot prove, and a plugin shaped this way satisfies
        // every other check.
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Good, asName: "Acme.Renamed");
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, "Acme.Renamed");
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_and_names_both()
    {
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.AssemblyNameMismatch);
        _run.Failure!.Message.Should().Contain("Acme.Renamed").And.Contain(PluginFixtures.Good);
    }
}

[TestFixture]
public class Given_an_entry_assembly_that_is_not_a_managed_assembly
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.AddCorruptPlugin("Acme.Corrupt");
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, "Acme.Corrupt");
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_with_the_load_failure_rather_than_letting_it_escape_raw()
    {
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.EntryAssemblyUnreadable);
        _run.Failure!.InnerException.Should().NotBeNull();
    }
}
