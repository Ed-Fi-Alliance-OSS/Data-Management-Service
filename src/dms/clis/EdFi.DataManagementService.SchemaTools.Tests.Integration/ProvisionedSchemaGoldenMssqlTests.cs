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
[Category("MssqlIntegration")]
public class Given_Provisioned_Mssql_Database_When_Introspecting_Schema
{
    private string _databaseName = null!;
    private string? _ddlOutputDir;
    private string _actualManifestPath = null!;
    private string _expectedManifestPath = null!;
    private bool _isConfigured;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _isConfigured = MssqlTestDatabaseHelper.IsConfigured();
        if (!_isConfigured)
        {
            return;
        }

        // Emit DDL to a temp directory
        _ddlOutputDir = Path.Combine(Path.GetTempPath(), $"dms_emit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_ddlOutputDir);

        var (emitExitCode, emitOutput, emitError) = ProvisionTestHelper.RunEmit("mssql", _ddlOutputDir);

        if (emitExitCode != 0)
        {
            Assert.Fail($"ddl emit failed (exit code {emitExitCode}).\nstdout: {emitOutput}\nstderr: {emitError}");
        }

        var sqlFilePath = Path.Combine(_ddlOutputDir, "mssql.sql");
        Assert.That(File.Exists(sqlFilePath), Is.True, $"Expected emitted DDL file not found: {sqlFilePath}");

        // Create a fresh database and apply DDL via sqlcmd
        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);

        var (sqlcmdExitCode, sqlcmdOutput, sqlcmdError) = ProvisionTestHelper.RunSqlcmd(
            connectionString,
            sqlFilePath
        );

        if (sqlcmdExitCode != 0)
        {
            Assert.Fail(
                $"sqlcmd failed (exit code {sqlcmdExitCode}).\nstdout: {sqlcmdOutput}\nstderr: {sqlcmdError}"
            );
        }

        // Introspect
        var schemaAllowlist = ProvisionTestHelper.DiscoverProvisionedSchemasMssql(connectionString);

        var introspector = new MssqlSchemaIntrospector();
        var manifest = introspector.Introspect(connectionString, schemaAllowlist);

        var manifestJson = ProvisionedSchemaManifestEmitter.Emit(manifest);

        var workDir = TestContext.CurrentContext.WorkDirectory;
        var actualDir = Path.Combine(workDir, "actual");
        Directory.CreateDirectory(actualDir);
        _actualManifestPath = Path.Combine(actualDir, "provisioned-schema.mssql.manifest.json");
        File.WriteAllText(_actualManifestPath, manifestJson);

        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            "EdFi.DataManagementService.SchemaTools.Tests.Integration.csproj"
        );
        _expectedManifestPath = Path.Combine(
            projectRoot,
            "Fixtures",
            "expected",
            "provisioned-schema.mssql.manifest.json"
        );

        if (GoldenFixtureTestHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_expectedManifestPath)!);
            File.Copy(_actualManifestPath, _expectedManifestPath, overwrite: true);
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_isConfigured)
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }

        if (_ddlOutputDir is not null && Directory.Exists(_ddlOutputDir))
        {
            Directory.Delete(_ddlOutputDir, recursive: true);
        }
    }

    [Test]
    public void Manifest_matches_golden_file()
    {
        if (!_isConfigured)
        {
            Assert.Ignore("MSSQL is not configured. Set MssqlAdmin connection string in appsettings.Test.json.");
        }

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
