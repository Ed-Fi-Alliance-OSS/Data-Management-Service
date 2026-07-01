// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_A_Mssql_Generated_Ddl_Apply_Harness_With_The_Authoritative_Ds52_Tpdm_Fixture_For_Smoke_Coverage
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2-tpdm";

    private static readonly string[] _nullableSurveyResponderChoiceColumns =
    [
        "SurveyResponderChoiceContact_DocumentId",
        "SurveyResponderChoiceContact_ContactUniqueId",
        "SurveyResponderChoiceStaff_DocumentId",
        "SurveyResponderChoiceStaff_StaffUniqueId",
        "SurveyResponderChoiceStudent_DocumentId",
        "SurveyResponderChoiceStudent_StudentUniqueId",
    ];

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineLease _databaseLease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private IReadOnlyDictionary<string, bool> _surveyResponseColumnNullability = null!;
    private IReadOnlyList<string> _surveyResponseCheckConstraintNames = null!;
    private IReadOnlyList<MssqlForeignKeyMetadata> _surveyResponseForeignKeys = null!;
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _schemaComponentRows = null!;
    private long _effectiveSchemaCount;
    private long _resourceKeyCount;
    private bool _tpdmSurveyResponsePersonTargetAssociationResourceKeyExists;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _databaseLease = await MssqlBackendBaselineCache.AcquireLeaseAsync(
            FixtureRelativePath,
            strict: true,
            _fixture.GeneratedDdl
        );
        _database = _databaseLease.Database;

        await _database.ApplyGeneratedDdlAsync(_fixture.GeneratedDdl);

        _effectiveSchemaCount = await _database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM [dms].[EffectiveSchema];"
        );
        _resourceKeyCount = await _database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM [dms].[ResourceKey];"
        );
        _schemaComponentRows = await _database.QueryRowsAsync(
            """
            SELECT [ProjectEndpointName], [ProjectName], [ProjectVersion], [IsExtensionProject]
            FROM [dms].[SchemaComponent]
            ORDER BY [ProjectEndpointName];
            """
        );
        _tpdmSurveyResponsePersonTargetAssociationResourceKeyExists =
            await _database.ExecuteScalarAsync<bool>(
                """
                SELECT CAST(
                    CASE
                        WHEN EXISTS (
                            SELECT 1
                            FROM [dms].[ResourceKey]
                            WHERE [ProjectName] = N'TPDM'
                              AND [ResourceName] = N'SurveyResponsePersonTargetAssociation'
                        )
                        THEN 1
                        ELSE 0
                    END AS bit
                );
                """
            );
        _surveyResponseColumnNullability = await GetSurveyResponseColumnNullabilityAsync();
        _surveyResponseCheckConstraintNames = await GetSurveyResponseCheckConstraintNamesAsync();
        _surveyResponseForeignKeys = await _database.GetForeignKeyMetadataAsync("edfi", "SurveyResponse");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_databaseLease is not null)
        {
            await _databaseLease.DisposeAsync();
        }
    }

    [Test]
    public void It_can_reapply_the_authoritative_ds52_tpdm_ddl_without_duplicate_effective_schema_seed_rows()
    {
        _effectiveSchemaCount.Should().Be(1);
        _resourceKeyCount.Should().Be((long)_fixture.EffectiveSchemaSet.EffectiveSchema.ResourceKeyCount);
    }

    [Test]
    public void It_records_the_ds52_tpdm_effective_schema_components()
    {
        _schemaComponentRows.Should().HaveCount(2);

        var componentByEndpointName = _schemaComponentRows.ToDictionary(
            row => GetRequiredString(row, "ProjectEndpointName"),
            StringComparer.Ordinal
        );

        componentByEndpointName.Keys.Should().BeEquivalentTo("ed-fi", "tpdm");
        GetRequiredString(componentByEndpointName["ed-fi"], "ProjectName").Should().Be("Ed-Fi");
        GetRequiredString(componentByEndpointName["ed-fi"], "ProjectVersion").Should().Be("5.2.0");
        GetRequiredBool(componentByEndpointName["ed-fi"], "IsExtensionProject").Should().BeFalse();
        GetRequiredString(componentByEndpointName["tpdm"], "ProjectName").Should().Be("TPDM");
        GetRequiredString(componentByEndpointName["tpdm"], "ProjectVersion").Should().Be("1.1.0");
        GetRequiredBool(componentByEndpointName["tpdm"], "IsExtensionProject").Should().BeTrue();
        _tpdmSurveyResponsePersonTargetAssociationResourceKeyExists.Should().BeTrue();
    }

    [Test]
    public void It_keeps_survey_response_responder_choice_columns_nullable()
    {
        foreach (var columnName in _nullableSurveyResponderChoiceColumns)
        {
            _surveyResponseColumnNullability.Should().ContainKey(columnName);
            _surveyResponseColumnNullability[columnName]
                .Should()
                .BeTrue($"{columnName} must remain nullable in the DS52+TPDM DDL");
        }
    }

    [Test]
    public void It_keeps_survey_response_responder_choice_all_or_none_checks()
    {
        var responderChoiceChecks = _surveyResponseCheckConstraintNames
            .Where(name =>
                name.StartsWith("CK_SurveyResponse_SurveyResponderChoice", StringComparison.Ordinal)
            )
            .ToArray();

        responderChoiceChecks
            .Should()
            .BeEquivalentTo(
                "CK_SurveyResponse_SurveyResponderChoiceContact_AllNone",
                "CK_SurveyResponse_SurveyResponderChoiceStaff_AllNone",
                "CK_SurveyResponse_SurveyResponderChoiceStudent_AllNone"
            );
    }

    [Test]
    public void It_keeps_survey_response_responder_choice_composite_foreign_keys()
    {
        AssertResponderChoiceForeignKey(
            "FK_SurveyResponse_SurveyResponderChoiceContact_RefKey",
            ["SurveyResponderChoiceContact_ContactUniqueId", "SurveyResponderChoiceContact_DocumentId"],
            "Contact",
            ["ContactUniqueId", "DocumentId"]
        );
        AssertResponderChoiceForeignKey(
            "FK_SurveyResponse_SurveyResponderChoiceStaff_RefKey",
            ["SurveyResponderChoiceStaff_StaffUniqueId", "SurveyResponderChoiceStaff_DocumentId"],
            "Staff",
            ["StaffUniqueId", "DocumentId"]
        );
        AssertResponderChoiceForeignKey(
            "FK_SurveyResponse_SurveyResponderChoiceStudent_RefKey",
            ["SurveyResponderChoiceStudent_StudentUniqueId", "SurveyResponderChoiceStudent_DocumentId"],
            "Student",
            ["StudentUniqueId", "DocumentId"]
        );
    }

    private async Task<IReadOnlyDictionary<string, bool>> GetSurveyResponseColumnNullabilityAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT columns.name AS [ColumnName], CAST(columns.is_nullable AS bit) AS [IsNullable]
            FROM sys.columns columns
            INNER JOIN sys.tables tables
                ON tables.object_id = columns.object_id
            INNER JOIN sys.schemas schemas
                ON schemas.schema_id = tables.schema_id
            WHERE schemas.name = N'edfi'
              AND tables.name = N'SurveyResponse'
              AND columns.name IN (
                  N'SurveyResponderChoiceContact_DocumentId',
                  N'SurveyResponderChoiceContact_ContactUniqueId',
                  N'SurveyResponderChoiceStaff_DocumentId',
                  N'SurveyResponderChoiceStaff_StaffUniqueId',
                  N'SurveyResponderChoiceStudent_DocumentId',
                  N'SurveyResponderChoiceStudent_StudentUniqueId'
              )
            ORDER BY columns.name;
            """
        );

        return rows.ToDictionary(
            row => GetRequiredString(row, "ColumnName"),
            row => GetRequiredBool(row, "IsNullable"),
            StringComparer.Ordinal
        );
    }

    private async Task<IReadOnlyList<string>> GetSurveyResponseCheckConstraintNamesAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT check_constraints.name AS [ConstraintName]
            FROM sys.check_constraints check_constraints
            INNER JOIN sys.tables tables
                ON tables.object_id = check_constraints.parent_object_id
            INNER JOIN sys.schemas schemas
                ON schemas.schema_id = tables.schema_id
            WHERE schemas.name = N'edfi'
              AND tables.name = N'SurveyResponse'
            ORDER BY check_constraints.name;
            """
        );

        return rows.Select(row => GetRequiredString(row, "ConstraintName")).ToArray();
    }

    private void AssertResponderChoiceForeignKey(
        string constraintName,
        string[] columns,
        string referencedTable,
        string[] referencedColumns
    )
    {
        var foreignKey = _surveyResponseForeignKeys.Single(foreignKey =>
            foreignKey.ConstraintName == constraintName
        );

        foreignKey.Columns.Should().Equal(columns);
        foreignKey.ReferencedSchema.Should().Be("edfi");
        foreignKey.ReferencedTable.Should().Be(referencedTable);
        foreignKey.ReferencedColumns.Should().Equal(referencedColumns);
        foreignKey.DeleteAction.Should().Be("NO ACTION");
        foreignKey.UpdateAction.Should().Be("NO ACTION");
    }

    private static string GetRequiredString(IReadOnlyDictionary<string, object?> row, string key)
    {
        return row[key]?.ToString()
            ?? throw new InvalidOperationException($"Expected non-null string value for {key}.");
    }

    private static bool GetRequiredBool(IReadOnlyDictionary<string, object?> row, string key)
    {
        return row[key] is bool value
            ? value
            : throw new InvalidOperationException($"Expected non-null bool value for {key}.");
    }
}
