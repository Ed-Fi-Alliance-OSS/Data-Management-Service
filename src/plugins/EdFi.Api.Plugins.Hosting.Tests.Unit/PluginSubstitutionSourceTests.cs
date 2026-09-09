// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// Where the substitution record gets the declaration it compares against.
/// </summary>
/// <remarks>
/// The skew preflight reads top-level <c>runtime</c> entries only, which is a policy about what to
/// refuse and is documented as a limitation. The record is a different question: it says what the
/// plugin declared for an assembly the host actually served. Deriving it from the preflight's narrower
/// set would report no declaration for a version the manifest plainly states, which is what these
/// cases exist to prevent.
/// </remarks>
[TestFixture]
public class Given_a_declaration_the_skew_preflight_does_not_read
{
    private TemporaryPluginRoot _root = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Substitution);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    private static HostFirstSubstitution ResolveAndRecord(LoadedPlugin plugin)
    {
        plugin
            .Instance.GetType()
            .Assembly.GetType("Acme.Substitution.SubstitutionPlugin")!
            .GetMethod("DescribeHostShared")!
            .Invoke(null, null);

        return plugin.MaterializeSubstitutions().Single(record => record.AssemblyName == "Acme.HostShared");
    }

    [Test]
    public void It_still_reaches_the_record_when_declared_only_in_a_runtime_targets_row()
    {
        // The manifest declares the version here and nowhere else. Reporting no declaration would be
        // untrue of the file, and it is exactly what deriving the record from the preflight's set does.
        _root.MoveDeclarationToRuntimeTargets(PluginFixtures.Substitution, "Acme.HostShared", "0.1.0.0");

        PluginLoaderRun run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.Substitution);
        run.Failure.Should().BeNull();

        HostFirstSubstitution substitution = ResolveAndRecord(run.Result!.Plugins[0]);

        substitution.DeclaredVersion.Should().Be(new Version(0, 1, 0, 0));
        substitution.HostVersion.Should().Be(new Version(1, 0, 0, 0));
    }

    [Test]
    public void It_keeps_the_declaration_and_the_reference_apart_when_they_disagree()
    {
        // The reference the entry assembly carries says 0.5.0.0 and the manifest says 0.1.0.0. Both are
        // real and neither stands in for the other, so both are on the record.
        _root.SetDeclaredVersion(PluginFixtures.Substitution, "Acme.HostShared", "0.1.0.0");

        PluginLoaderRun run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.Substitution);
        run.Failure.Should().BeNull();

        HostFirstSubstitution substitution = ResolveAndRecord(run.Result!.Plugins[0]);

        substitution.DeclaredVersion.Should().Be(new Version(0, 1, 0, 0));
        substitution.RequestedVersion.Should().Be(new Version(0, 5, 0, 0));
        substitution.HostVersion.Should().Be(new Version(1, 0, 0, 0));
    }

    [Test]
    public void It_records_nothing_when_the_host_serves_exactly_what_the_manifest_declared()
    {
        // The mirror case, and the one that pins which comparison is being made. The reference still
        // says 0.5.0.0, so a record keyed on the reference would appear here; the manifest says the host
        // served precisely what the plugin shipped, so nothing was substituted and nothing is reported.
        _root.SetDeclaredVersion(PluginFixtures.Substitution, "Acme.HostShared", "1.0.0.0");

        PluginLoaderRun run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.Substitution);
        run.Failure.Should().BeNull();

        LoadedPlugin plugin = run.Result!.Plugins[0];

        plugin
            .Instance.GetType()
            .Assembly.GetType("Acme.Substitution.SubstitutionPlugin")!
            .GetMethod("DescribeHostShared")!
            .Invoke(null, null);

        plugin
            .MaterializeSubstitutions()
            .Should()
            .NotContain(record => record.AssemblyName == "Acme.HostShared");
    }
}

/// <summary>
/// A reference that asks for exactly the version the host carries, over a manifest that declares
/// something else.
/// </summary>
/// <remarks>
/// The case a record keyed on the reference cannot see at all. The plugin was published against one
/// version of a shared assembly and compiled against another, which is ordinary: a reference records
/// the version the compiler bound to, and the manifest records what the publish actually shipped. What
/// the plugin's author tested against is the copy in the package, so a host serving something else has
/// substituted, whatever the reference happens to say.
/// </remarks>
[TestFixture]
public class Given_a_reference_asking_for_the_version_the_host_carries
{
    private TemporaryPluginRoot _root = null!;
    private HostFirstSubstitution _substitution = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Substitution);

        // The contract, which every plugin references at the host's own version and which the publish
        // declares at that same version. Moving the declaration alone leaves the reference asking for
        // precisely what the host serves.
        _root.SetDeclaredVersion(PluginFixtures.Substitution, "EdFi.Api.Plugins", "0.1.0.0");

        PluginLoaderRun run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.Substitution);
        run.Failure.Should().BeNull();

        _substitution = run.Result!.Plugins[0]
            .MaterializeSubstitutions()
            .Single(record => record.AssemblyName == "EdFi.Api.Plugins");
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_records_the_declaration_the_manifest_makes()
    {
        _substitution.DeclaredVersion.Should().Be(new Version(0, 1, 0, 0));
    }

    [Test]
    public void It_records_a_request_the_host_satisfied_exactly()
    {
        // The half that makes the case: nothing about this resolution differs from what the reference
        // asked for, so a record derived from the reference would not exist.
        _substitution.RequestedVersion.Should().Be(_substitution.HostVersion);
    }

    [Test]
    public void It_records_the_version_the_host_actually_served()
    {
        _substitution.HostVersion.Should().Be(new Version(1, 0, 0, 0));
    }
}
