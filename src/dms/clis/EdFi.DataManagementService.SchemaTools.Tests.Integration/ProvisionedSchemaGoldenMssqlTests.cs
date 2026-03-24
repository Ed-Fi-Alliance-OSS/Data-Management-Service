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

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        var (exitCode, output, error) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            CliTestHelper.GetAuthoritativeSchemaPaths(),
            createDatabase: true
        );

        if (exitCode != 0)
        {
            Assert.Fail($"Provisioning failed (exit code {exitCode}).\nstdout: {output}\nstderr: {error}");
        }

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
