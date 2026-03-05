// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_FixtureRunner_With_Minimal_Fixture
{
    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory is not null)
        {
            if (
                File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "EdFi.DataManagementService.Backend.Ddl.Tests.Unit.csproj"
                    )
                )
            )
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Unable to locate EdFi.DataManagementService.Backend.Ddl.Tests.Unit.csproj in parent directories."
        );
    }

    private string _fixtureDirectory = default!;
    private FixtureCompareResult _result = default!;

    [OneTimeSetUp]
    public void Setup()
    {
        var projectRoot = FindProjectRoot();
        _fixtureDirectory = Path.Combine(projectRoot, "Fixtures", "small", "minimal");

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
