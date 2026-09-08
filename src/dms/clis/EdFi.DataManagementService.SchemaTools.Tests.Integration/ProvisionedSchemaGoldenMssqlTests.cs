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
    private ProvisionedSchemaManifest? _manifest;

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
            Assert.Fail(
                $"ddl emit failed (exit code {emitExitCode}).\nstdout: {emitOutput}\nstderr: {emitError}"
            );
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
        _manifest = introspector.Introspect(connectionString, schemaAllowlist);

        var manifestJson = ProvisionedSchemaManifestEmitter.Emit(_manifest);

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
            Assert.Ignore(
                "MSSQL is not configured. Set MssqlAdmin connection string in appsettings.Test.json."
            );
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

    [Test]
    public void Manifest_reports_e18_fixed_inventory()
    {
        if (!_isConfigured)
        {
            Assert.Ignore(
                "MSSQL is not configured. Set MssqlAdmin connection string in appsettings.Test.json."
            );
        }

        _manifest!.ManifestVersion.Should().Be("1");

        _manifest
            .Tables.Should()
            .ContainEquivalentOf(new TableEntry("dms", "DataStoreIdentity"))
            .And.ContainEquivalentOf(new TableEntry("dms", "DocumentCache"))
            .And.ContainEquivalentOf(new TableEntry("dms", "DocumentCacheState"))
            .And.ContainEquivalentOf(new TableEntry("dms", "DocumentProjectionWork"));

        _manifest
            .Constraints.Should()
            .Contain(c =>
                c.SchemaName == "dms"
                && c.TableName == "DocumentCacheState"
                && c.ConstraintName == "CK_DocumentCacheState_Lifecycle"
            )
            .And.Contain(c =>
                c.SchemaName == "dms"
                && c.TableName == "DocumentProjectionWork"
                && c.ConstraintName == "FK_DocumentProjectionWork_Document"
            );

        _manifest
            .Indexes.Should()
            .Contain(i =>
                i.SchemaName == "dms"
                && i.TableName == "DocumentProjectionWork"
                && i.IndexName == "IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId"
                && i.Columns.Count == 2
                && i.Columns[0] == "FirstEnqueuedAt"
                && i.Columns[1] == "DocumentId"
            );

        _manifest
            .Triggers.Should()
            .Contain(t => t.SchemaName == "dms" && t.TriggerName == "TR_Document_EnqueueProjectionWork")
            .And.Contain(t =>
                t.SchemaName == "dms" && t.TriggerName == "TR_DocumentCache_ValidateDocumentUuid"
            );
    }

    [Test]
    public void Manifest_omits_legacy_and_deferred_document_cache_artifacts()
    {
        if (!_isConfigured)
        {
            Assert.Ignore(
                "MSSQL is not configured. Set MssqlAdmin connection string in appsettings.Test.json."
            );
        }

        _manifest!
            .Columns.Should()
            .NotContain(c =>
                c.SchemaName == "dms" && c.TableName == "DocumentCache" && c.ColumnName == "Etag"
            );
        _manifest
            .Constraints.Should()
            .NotContain(c => c.SchemaName == "dms" && c.ConstraintName == "UX_DocumentCache_DocumentUuid");
        _manifest
            .Indexes.Should()
            .NotContain(i =>
                i.SchemaName == "dms"
                && i.IndexName == "IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt"
            );
        _manifest
            .Indexes.Should()
            .NotContain(i =>
                i.SchemaName == "dms"
                && i.TableName == "Document"
                && i.Columns.Count == 2
                && i.Columns[0] == "ContentVersion"
                && i.Columns[1] == "DocumentId"
            );
        _manifest.Schemas.Should().NotContain(s => s.SchemaName == "cdc");
        _manifest
            .Tables.Should()
            .NotContain(t => t.TableName.Contains("capture", StringComparison.OrdinalIgnoreCase));
        _manifest.Triggers.Should().NotContain(t => t.Definition.Contains("EXECUTE AS"));
    }
}
