// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

/// <summary>
/// Base class for golden-file fixture tests. Subclasses provide the fixture path;
/// this class runs the pipeline and asserts all standard artifacts are emitted and match expected/.
/// Assertions are driven by <see cref="FixtureConfig"/> so only declared artifacts are checked.
/// </summary>
public abstract class DdlGoldenFixtureTestBase
{
    private string _fixtureDirectory = default!;
    private string _actualDir = default!;
    private FixtureConfig _config = default!;
    private FixtureCompareResult _result = default!;

    protected abstract string ResolveFixtureDirectory(string projectRoot);

    /// <summary>
    /// Whether the fixture runs through the strict pass set (production-equivalent).
    /// Defaults to <see langword="true"/>; synthetic fixtures that omit collection semantic
    /// identity or PrimaryAssociation literal columns override to <see langword="false"/>.
    /// </summary>
    protected virtual bool Strict => true;

    /// <summary>
    /// Hook for subclasses to massage the FixtureRunner's actual/ output before comparison
    /// (e.g. trim trailing whitespace from generated SQL). Default is a no-op.
    /// </summary>
    protected virtual void NormalizeActualOutput(string actualDir) { }

    [OneTimeSetUp]
    public void Setup()
    {
        var projectRoot = FixtureTestHelper.FindProjectRoot();
        _fixtureDirectory = Path.GetFullPath(ResolveFixtureDirectory(projectRoot));

        _config = FixtureConfigReader.Read(_fixtureDirectory);
        _actualDir = FixtureRunner.Run(_fixtureDirectory, Strict);
        NormalizeActualOutput(_actualDir);
        _result = FixtureComparer.Compare(_fixtureDirectory);
    }

    [Test]
    public void It_should_produce_actual_output_files()
    {
        Directory.Exists(_actualDir).Should().BeTrue("FixtureRunner should create actual/ directory");
        Directory.GetFiles(_actualDir).Should().NotBeEmpty("FixtureRunner should emit artifacts");
    }

    [Test]
    public void It_should_emit_dialect_sql_for_each_declared_dialect()
    {
        foreach (var dialect in _config.Dialects)
        {
            File.Exists(Path.Combine(_actualDir, $"{dialect}.sql"))
                .Should()
                .BeTrue($"dialect '{dialect}' is declared in fixture.json");
        }
    }

    [Test]
    public void It_should_emit_effective_schema_manifest()
    {
        File.Exists(Path.Combine(_actualDir, "effective-schema.manifest.json")).Should().BeTrue();
    }

    [Test]
    public void It_should_emit_ddl_manifest_when_configured()
    {
        var exists = File.Exists(Path.Combine(_actualDir, "ddl.manifest.json"));

        if (_config.EmitDdlManifest)
        {
            exists.Should().BeTrue("emitDdlManifest is true in fixture.json");
        }
        else
        {
            exists.Should().BeFalse("emitDdlManifest is false in fixture.json");
        }
    }

    [Test]
    public void It_should_emit_relational_model_manifest_for_each_declared_dialect()
    {
        foreach (var dialect in _config.Dialects)
        {
            File.Exists(Path.Combine(_actualDir, $"relational-model.{dialect}.manifest.json"))
                .Should()
                .BeTrue($"dialect '{dialect}' is declared in fixture.json");
        }
    }

    [Test]
    public void It_should_match_expected_golden_files()
    {
        _result
            .Passed.Should()
            .BeTrue(
                $"expected/ and actual/ should match. Set UPDATE_GOLDENS=1 to regenerate.\n\n{_result.Message}"
            );
    }
}

/// <summary>
/// Base for golden-file fixture tests over synthetic small/focused fixtures that intentionally
/// omit collection semantic identity or PrimaryAssociation literal columns. Runs the permissive
/// (non-strict) pass set so the synthetic fixtures keep building.
/// </summary>
public abstract class SyntheticDdlGoldenFixtureTestBase : DdlGoldenFixtureTestBase
{
    protected sealed override bool Strict => false;
}
