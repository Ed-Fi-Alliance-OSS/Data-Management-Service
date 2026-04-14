// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
public class Given_A_Postgresql_Generated_Ddl_Apply_Harness_With_A_Focused_Stable_Key_Fixture
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private IReadOnlyList<PostgresqlForeignKeyMetadata> _interventionVisitForeignKeys = null!;
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _schemaComponentRows = null!;
    private long _effectiveSchemaCount;
    private string? _schoolAddressCollectionItemDefault;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);

        // Prove reruns use the same full emitter output safely.
        await _database.ApplyGeneratedDdlAsync(_fixture.GeneratedDdl);

        _effectiveSchemaCount = await _database.ExecuteScalarAsync<long>(
            """SELECT COUNT(*) FROM dms."EffectiveSchema";"""
        );

        _schoolAddressCollectionItemDefault = await _database.GetColumnDefaultAsync(
            "edfi",
            "SchoolAddress",
            "CollectionItemId"
        );

        _interventionVisitForeignKeys = await _database.GetForeignKeyMetadataAsync(
            "sample",
            "SchoolExtensionInterventionVisit"
        );

        _schemaComponentRows = await _database.QueryRowsAsync(
            """
            SELECT "ProjectName", "IsExtensionProject"
            FROM dms."SchemaComponent"
            ORDER BY "ProjectEndpointName";
            """
        );
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_can_reapply_the_same_generated_ddl_without_duplicate_effective_schema_seed_rows()
    {
        _effectiveSchemaCount.Should().Be(1);
    }

    [Test]
    public async Task It_can_assert_sequence_existence()
    {
        (await _database.SequenceExistsAsync("dms", "CollectionItemIdSequence")).Should().BeTrue();
    }

    [Test]
    public void It_can_read_engine_specific_column_defaults()
    {
        _schoolAddressCollectionItemDefault.Should().NotBeNull();
        _schoolAddressCollectionItemDefault.Should().Contain("CollectionItemIdSequence");
        _schoolAddressCollectionItemDefault.Should().Contain("nextval");
    }

    [Test]
    public void It_can_read_foreign_key_metadata_for_targeted_schema_assertions()
    {
        var foreignKey = _interventionVisitForeignKeys.Single(foreignKey =>
            foreignKey.ConstraintName == "FK_SchoolExtensionInterventionVisit_SchoolExtensionIntervention"
        );

        foreignKey.Columns.Should().Equal("ParentCollectionItemId", "School_DocumentId");
        foreignKey.ReferencedSchema.Should().Be("sample");
        foreignKey.ReferencedTable.Should().Be("SchoolExtensionIntervention");
        foreignKey.ReferencedColumns.Should().Equal("CollectionItemId", "School_DocumentId");
        foreignKey.DeleteAction.Should().Be("CASCADE");
        foreignKey.UpdateAction.Should().Be("NO ACTION");
    }

    [Test]
    public void It_can_query_representative_rows_inserted_by_generated_seed_dml()
    {
        _schemaComponentRows.Should().HaveCount(2);

        var projectNames = _schemaComponentRows
            .Select(row => row["ProjectName"]?.ToString())
            .Where(projectName => projectName is not null)
            .ToArray();
        var extensionFlags = _schemaComponentRows.Select(row => row["IsExtensionProject"]).ToArray();

        projectNames.Should().BeEquivalentTo("Ed-Fi", "Sample");
        extensionFlags.Should().Contain(true);
    }
}
