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
/// </summary>
public abstract class DdlGoldenFixtureTestBase
{
    private string _fixtureDirectory = default!;
    private FixtureCompareResult _result = default!;

    protected abstract string ResolveFixtureDirectory(string projectRoot);

    [OneTimeSetUp]
    public void Setup()
    {
        var projectRoot = FixtureTestHelper.FindProjectRoot();
        _fixtureDirectory = Path.GetFullPath(ResolveFixtureDirectory(projectRoot));

        FixtureRunner.Run(_fixtureDirectory);
        _result = FixtureComparer.Compare(_fixtureDirectory);
    }

    [Test]
    public void It_should_produce_actual_output_files()
    {
        var actualDir = Path.Combine(_fixtureDirectory, "actual");
        Directory.Exists(actualDir).Should().BeTrue("FixtureRunner should create actual/ directory");
        Directory.GetFiles(actualDir).Should().NotBeEmpty("FixtureRunner should emit artifacts");
    }

    [Test]
    public void It_should_emit_pgsql_sql()
    {
        File.Exists(Path.Combine(_fixtureDirectory, "actual", "pgsql.sql")).Should().BeTrue();
    }

    [Test]
    public void It_should_emit_mssql_sql()
    {
        File.Exists(Path.Combine(_fixtureDirectory, "actual", "mssql.sql")).Should().BeTrue();
    }

    [Test]
    public void It_should_emit_effective_schema_manifest()
    {
        File.Exists(Path.Combine(_fixtureDirectory, "actual", "effective-schema.manifest.json"))
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_should_emit_ddl_manifest()
    {
        File.Exists(Path.Combine(_fixtureDirectory, "actual", "ddl.manifest.json")).Should().BeTrue();
    }

    [Test]
    public void It_should_emit_pgsql_relational_model_manifest()
    {
        File.Exists(Path.Combine(_fixtureDirectory, "actual", "relational-model.pgsql.manifest.json"))
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_should_emit_mssql_relational_model_manifest()
    {
        File.Exists(Path.Combine(_fixtureDirectory, "actual", "relational-model.mssql.manifest.json"))
            .Should()
            .BeTrue();
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
