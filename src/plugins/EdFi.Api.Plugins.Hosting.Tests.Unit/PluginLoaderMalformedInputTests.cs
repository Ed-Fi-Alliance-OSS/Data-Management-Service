// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// A dependency manifest is a file a third party produced and an operator dropped into a directory, so
/// anything about its shape can be wrong. Every case here goes through the public loader path rather
/// than through the reader directly, because what matters is that an operator gets the named refusal
/// and one line on the channel, not that a parser method returns something.
/// </summary>
[TestFixture]
public class Given_a_dependency_manifest_of_the_wrong_shape
{
    private static readonly object[] Malformed =
    [
        new object[] { "a JSON array rather than an object", "[]" },
        new object[] { "a null runtimeTarget", """{"runtimeTarget": null, "targets": {}}""" },
        new object[]
        {
            "a runtimeTarget name that is not a string",
            """{"runtimeTarget": {"name": 10}, "targets": {}}""",
        },
        new object[]
        {
            "targets that are not an object",
            """{"runtimeTarget": {"name": "t"}, "targets": "t"}""",
        },
        new object[]
        {
            "a selected target that is not an object",
            """{"runtimeTarget": {"name": "t"}, "targets": {"t": 3}}""",
        },
        new object[]
        {
            "a library that is not an object",
            """{"runtimeTarget": {"name": "t"}, "targets": {"t": {"a/1.0.0": 3}}}""",
        },
        new object[]
        {
            "a runtime section that is not an object",
            """{"runtimeTarget": {"name": "t"}, "targets": {"t": {"a/1.0.0": {"runtime": 3}}}}""",
        },
        new object[]
        {
            "libraries that are not an object",
            """{"runtimeTarget": {"name": "t"}, "targets": {"t": {}}, "libraries": 3}""",
        },
    ];

    [TestCaseSource(nameof(Malformed))]
    public void It_refuses_with_the_named_manifest_failure(string description, string manifest)
    {
        using TemporaryPluginRoot root = TemporaryPluginRoot.Create();
        root.Add(PluginFixtures.Good);
        root.WriteManifest(PluginFixtures.Good, manifest);

        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(root.RootPath, PluginFixtures.Good);

        // Reading a value of the wrong JSON kind throws InvalidOperationException, which would leave the
        // loader with an unlabelled exception and an empty channel. This is the case described as
        // '{description}'.
        run.Failure!.Reason.Should().Be(PluginLoadFailure.DepsJsonUnreadable, description);
        run.Failure!.Message.Should().Contain(PluginFixtures.Good);
    }

    [TestCaseSource(nameof(Malformed))]
    public void It_writes_exactly_one_failure_line(string description, string manifest)
    {
        using TemporaryPluginRoot root = TemporaryPluginRoot.Create();
        root.Add(PluginFixtures.Good);
        root.WriteManifest(PluginFixtures.Good, manifest);

        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(root.RootPath, PluginFixtures.Good);

        run.DiagnosticLines.Should().ContainSingle(description);
        run.DiagnosticLines[0].Should().StartWith($"plugin '{PluginFixtures.Good}' failed:");
    }
}

[TestFixture]
public class Given_a_declared_assembly_version_that_is_not_a_version
{
    [Test]
    public void It_refuses_a_version_that_does_not_parse()
    {
        using TemporaryPluginRoot root = TemporaryPluginRoot.Create();
        root.Add(PluginFixtures.Good);
        root.ReplaceFirstDeclaredAssemblyVersion(PluginFixtures.Good, JsonValue.Create("not-a-version"));

        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(root.RootPath, PluginFixtures.Good);

        // Dropping it silently would take an assembly out of the skew preflight with nobody told, which
        // is the one outcome the preflight exists to prevent: the plugin would load and the comparison
        // it was supposed to fail would simply never have happened.
        run.Failure!.Reason.Should().Be(PluginLoadFailure.DepsJsonUnreadable);
        run.Failure!.Message.Should().Contain("not-a-version");
    }

    [Test]
    public void It_refuses_a_version_that_is_not_even_a_string()
    {
        using TemporaryPluginRoot root = TemporaryPluginRoot.Create();
        root.Add(PluginFixtures.Good);
        root.ReplaceFirstDeclaredAssemblyVersion(PluginFixtures.Good, JsonValue.Create(10));

        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(root.RootPath, PluginFixtures.Good);

        run.Failure!.Reason.Should().Be(PluginLoadFailure.DepsJsonUnreadable);
    }

    [Test]
    public void It_refuses_a_version_written_as_a_json_null()
    {
        // Present and not a version, which is the same case as the two above: a null is a value the
        // manifest chose to write, not an absence.
        using TemporaryPluginRoot root = TemporaryPluginRoot.Create();
        root.Add(PluginFixtures.Good);
        root.ReplaceFirstDeclaredAssemblyVersion(PluginFixtures.Good, null);

        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(root.RootPath, PluginFixtures.Good);

        run.Failure!.Reason.Should().Be(PluginLoadFailure.DepsJsonUnreadable);
    }

    [Test]
    public void It_still_accepts_a_declaration_that_carries_no_version_at_all()
    {
        // An absent assemblyVersion is legitimate and common: a framework-dependent publish writes none
        // for the project's own entry, so treating absence as malformed would refuse every plugin. This
        // is what keeps the three refusals above from being a blanket rule about the field.
        using TemporaryPluginRoot root = TemporaryPluginRoot.Create();
        root.Add(PluginFixtures.Good);
        root.ReplaceFirstDeclaredAssemblyVersion(PluginFixtures.Good, null, remove: true);

        PluginLoaderRun run = PluginLoaderProbe.Run(root.RootPath, PluginFixtures.Good);

        run.Failure.Should().BeNull();
        run.Result!.Plugins.Should().ContainSingle();
    }
}

/// <summary>
/// The contract declares <c>Name</c> non-nullable and a third-party assembly is not constrained by that
/// at runtime, so every plugin-controlled boundary the loader reads has to be treated as something a
/// careless implementer can get wrong.
/// </summary>
[TestFixture]
public class Given_a_plugin_that_returns_null_from_its_name
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.NullName);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.NullName);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_with_the_named_mismatch_rather_than_dereferencing_it()
    {
        // A null is a name that is not the directory name. Quoting it for the message is what used to
        // throw, replacing the refusal an operator needs with an exception from inside the loader.
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.PluginNameMismatch);
        _run.Failure.Should().BeOfType<PluginLoadException>();
    }

    [Test]
    public void It_renders_the_null_truthfully()
    {
        _run.Failure!.Message.Should().Contain("<null>").And.Contain(PluginFixtures.NullName);
    }

    [Test]
    public void It_writes_exactly_one_failure_line()
    {
        _run.DiagnosticLines.Should().ContainSingle();
    }
}

[TestFixture]
public class Given_a_plugin_that_throws_when_its_name_is_read
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.ThrowingName);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.ThrowingName);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_reports_the_plugins_own_message_and_keeps_the_cause()
    {
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.PluginNameUnavailable);
        _run.Failure!.Message.Should().Contain("refuses to say its name");
        _run.Failure!.InnerException.Should().BeOfType<InvalidOperationException>();
    }
}

[TestFixture]
public class Given_a_plugin_that_throws_from_its_constructor
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.ThrowingCtor);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.ThrowingCtor);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_reports_the_plugins_own_message_rather_than_the_reflection_wrapper()
    {
        // Activator wraps anything a constructor throws in a TargetInvocationException whose own message
        // says only that an invocation target threw, which tells an operator nothing about the plugin.
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.PluginActivationFailed);
        _run.Failure!.Message.Should().Contain("refuses to be constructed");
        _run.Failure!.InnerException.Should().BeOfType<InvalidOperationException>();
    }
}
