// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.SchemaTools.Introspection;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
public class Given_Provisioned_Pgsql_Database_When_Introspecting_Schema
{
    private string _databaseName = null!;
    private string? _ddlOutputDir;
    private string _actualManifestPath = null!;
    private string _expectedManifestPath = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Emit DDL to a temp directory
        _ddlOutputDir = Path.Combine(Path.GetTempPath(), $"dms_emit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_ddlOutputDir);

        var (emitExitCode, emitOutput, emitError) = ProvisionTestHelper.RunEmit("pgsql", _ddlOutputDir);

        if (emitExitCode != 0)
        {
            Assert.Fail($"ddl emit failed (exit code {emitExitCode}).\nstdout: {emitOutput}\nstderr: {emitError}");
        }

        var sqlFilePath = Path.Combine(_ddlOutputDir, "pgsql.sql");
        Assert.That(File.Exists(sqlFilePath), Is.True, $"Expected emitted DDL file not found: {sqlFilePath}");

        // Create a fresh database and apply DDL via psql
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);
        PostgresTestDatabaseHelper.CreateDatabase(_databaseName);

        var (psqlExitCode, psqlOutput, psqlError) = ProvisionTestHelper.RunPsql(connectionString, sqlFilePath);

        if (psqlExitCode != 0)
        {
            Assert.Fail($"psql failed (exit code {psqlExitCode}).\nstdout: {psqlOutput}\nstderr: {psqlError}");
        }

        // Discover schemas created by the DDL
        var schemaAllowlist = ProvisionTestHelper.DiscoverProvisionedSchemasPgsql(connectionString);

        // Introspect
        var introspector = new PgsqlSchemaIntrospector();
        var manifest = introspector.Introspect(connectionString, schemaAllowlist);

        // Emit manifest JSON
        var manifestJson = ProvisionedSchemaManifestEmitter.Emit(manifest);

        // Write actual output
        var workDir = TestContext.CurrentContext.WorkDirectory;
        var actualDir = Path.Combine(workDir, "actual");
        Directory.CreateDirectory(actualDir);
        _actualManifestPath = Path.Combine(actualDir, "provisioned-schema.pgsql.manifest.json");
        File.WriteAllText(_actualManifestPath, manifestJson);

        // Resolve expected path (in project source, not bin output)
        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            "EdFi.DataManagementService.SchemaTools.Tests.Integration.csproj"
        );
        _expectedManifestPath = Path.Combine(
            projectRoot,
            "Fixtures",
            "expected",
            "provisioned-schema.pgsql.manifest.json"
        );

        // Update goldens if requested
        if (GoldenFixtureTestHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_expectedManifestPath)!);
            File.Copy(_actualManifestPath, _expectedManifestPath, overwrite: true);
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);

        if (_ddlOutputDir is not null && Directory.Exists(_ddlOutputDir))
        {
            Directory.Delete(_ddlOutputDir, recursive: true);
        }
    }

    [Test]
    public void Manifest_matches_golden_file()
    {
        if (!File.Exists(_expectedManifestPath))
        {
            Assert.Fail(
                $"Golden file not found: {_expectedManifestPath}\n"
                    + "Run with UPDATE_GOLDENS=1 to generate it."
            );
        }

        var diff = GoldenFixtureTestHelpers.RunGitDiff(_expectedManifestPath, _actualManifestPath);
        diff.Should().BeEmpty("the provisioned schema manifest should match the golden file.\n" + diff);
    }
}
