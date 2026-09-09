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

/// <summary>
/// Discovery is reflection over a third party's assembly from end to end, and every step of it can
/// bind another assembly. Asking a type for its parameterless constructor makes the runtime resolve
/// the parameter types of that type's other constructors, so an overload naming a type the runtime
/// cannot resolve fails there rather than at enumeration or at activation.
/// </summary>
[TestFixture]
public class Given_a_constructor_overload_naming_a_skewed_assembly
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.CtorSignatureSkew);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.CtorSignatureSkew);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_reports_the_named_skew_rather_than_escaping_unlabelled()
    {
        // The plugin shape is ordinary: a parameterless constructor beside an overload that takes a
        // dependency. Before the whole of discovery sat inside the classified handler, this escaped the
        // loader as a raw FileLoadException with nothing at all on the diagnostic channel.
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.DependencyVersionSkew);
        _run.Failure!.Message.Should().Contain("Acme.HostShared");
        _run.Failure!.Message.Should().Contain("2.0.0").And.Contain("1.0.0");
    }

    [Test]
    public void It_writes_exactly_one_failure_line()
    {
        _run.DiagnosticLines.Should().ContainSingle();
        _run.DiagnosticLines[0].Should().StartWith($"plugin '{PluginFixtures.CtorSignatureSkew}' failed:");
    }

    [Test]
    public void It_fails_at_constructor_resolution_rather_than_at_type_enumeration()
    {
        // Without this the two assertions above would pass just as well if the fixture happened to fail
        // at enumeration, which the handler already covered before this case existed. Enumerating this
        // assembly's exported types succeeds, because no type's own signature names the skewed
        // assembly; only asking a type for its parameterless constructor makes the runtime resolve the
        // parameter types of that type's other constructors.
        string entryAssemblyPath = Path.Combine(
            _root.RootPath,
            PluginFixtures.CtorSignatureSkew,
            $"{PluginFixtures.CtorSignatureSkew}.dll"
        );

        PluginLoadContext context = new(PluginFixtures.CtorSignatureSkew, entryAssemblyPath);
        Type[] exported = context.LoadFromAssemblyPath(entryAssemblyPath).GetExportedTypes();

        exported.Should().Contain(type => type.Name == "CtorSignatureSkewPlugin");

        Type pluginType = exported.Single(type => type.Name == "CtorSignatureSkewPlugin");

        Assert.Throws<FileLoadException>(() => pluginType.GetConstructor(Type.EmptyTypes));
    }
}

[TestFixture]
public class Given_a_constructor_overload_naming_an_assembly_nothing_carries
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.CtorSignatureMissing);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.CtorSignatureMissing);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_reports_the_unresolvable_type_rather_than_escaping_unlabelled()
    {
        // The general branch beside the skew one: nothing is version-skewed here, the assembly is simply
        // not there, and the refusal still has to name the plugin and say what could not be resolved.
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.PluginTypesUnloadable);
        _run.Failure!.Message.Should().Contain(PluginFixtures.CtorSignatureMissing);
        _run.Failure!.Message.Should().Contain("Acme.Private");
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
