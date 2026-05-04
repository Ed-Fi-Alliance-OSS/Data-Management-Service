// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed record FocusedStableKeySmokeSeedData(
    long SchoolDocumentId,
    long OtherSchoolDocumentId,
    long AddressCollectionItemId,
    long PeriodCollectionItemId,
    long InterventionCollectionItemId,
    long VisitCollectionItemId,
    long SponsorReferenceCollectionItemId
);

[TestFixture]
public class Given_A_Postgresql_Generated_Ddl_Apply_Harness_With_A_Focused_Stable_Key_Fixture_For_Smoke_Coverage
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private FocusedStableKeySmokeSeedData _seedData = null!;
    private DbTableModel _schoolTable = null!;
    private DbTableModel _schoolAddressTable = null!;
    private DbTableModel _schoolExtensionTable = null!;
    private DbTableModel _schoolExtensionAddressTable = null!;
    private DbTableModel _addressPeriodTable = null!;
    private DbTableModel _interventionTable = null!;
    private DbTableModel _interventionVisitTable = null!;
    private DbTableModel _sponsorReferenceTable = null!;
    private TableConstraint.ForeignKey _addressPeriodForeignKeyDefinition = null!;
    private TableConstraint.ForeignKey _interventionForeignKeyDefinition = null!;
    private TableConstraint.ForeignKey _interventionVisitForeignKeyDefinition = null!;
    private TableConstraint.ForeignKey _sponsorReferenceParentForeignKeyDefinition = null!;
    private IReadOnlyList<PostgresqlForeignKeyMetadata> _addressPeriodForeignKeys = null!;
    private IReadOnlyList<PostgresqlForeignKeyMetadata> _interventionForeignKeys = null!;
    private IReadOnlyList<PostgresqlForeignKeyMetadata> _interventionVisitForeignKeys = null!;
    private IReadOnlyList<PostgresqlForeignKeyMetadata> _sponsorReferenceForeignKeys = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: false
        );
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);

        _schoolTable = PostgresqlGeneratedDdlModelLookup.RequireTable(_fixture.ModelSet, "edfi", "School");
        _schoolAddressTable = PostgresqlGeneratedDdlModelLookup.RequireTable(
            _fixture.ModelSet,
            "edfi",
            "SchoolAddress"
        );
        _schoolExtensionTable = PostgresqlGeneratedDdlModelLookup.RequireTable(
            _fixture.ModelSet,
            "sample",
            "SchoolExtension"
        );
        _schoolExtensionAddressTable = PostgresqlGeneratedDdlModelLookup.RequireTable(
            _fixture.ModelSet,
            "sample",
            "SchoolExtensionAddress"
        );
        _addressPeriodTable = PostgresqlGeneratedDdlModelLookup.RequireTableByScope(
            _fixture.ModelSet,
            "edfi",
            "$.addresses[*].periods[*]"
        );
        _interventionTable = PostgresqlGeneratedDdlModelLookup.RequireTableByScope(
            _fixture.ModelSet,
            "sample",
            "$._ext.sample.interventions[*]"
        );
        _interventionVisitTable = PostgresqlGeneratedDdlModelLookup.RequireTableByScope(
            _fixture.ModelSet,
            "sample",
            "$._ext.sample.interventions[*].visits[*]"
        );
        _sponsorReferenceTable = PostgresqlGeneratedDdlModelLookup.RequireTableByScope(
            _fixture.ModelSet,
            "sample",
            "$._ext.sample.addresses[*]._ext.sample.sponsorReferences[*]"
        );
        _addressPeriodForeignKeyDefinition = PostgresqlGeneratedDdlModelLookup.RequireForeignKey(
            _addressPeriodTable,
            _schoolAddressTable.Table,
            "ParentCollectionItemId",
            "School_DocumentId"
        );
        _interventionForeignKeyDefinition = PostgresqlGeneratedDdlModelLookup.RequireForeignKey(
            _interventionTable,
            _schoolExtensionTable.Table,
            "School_DocumentId"
        );
        _interventionVisitForeignKeyDefinition = PostgresqlGeneratedDdlModelLookup.RequireForeignKey(
            _interventionVisitTable,
            _interventionTable.Table,
            "ParentCollectionItemId",
            "School_DocumentId"
        );
        _sponsorReferenceParentForeignKeyDefinition = PostgresqlGeneratedDdlModelLookup.RequireForeignKey(
            _sponsorReferenceTable,
            _schoolExtensionAddressTable.Table,
            "BaseCollectionItemId",
            "School_DocumentId"
        );
        _addressPeriodForeignKeys = await _database.GetForeignKeyMetadataAsync(
            _addressPeriodTable.Table.Schema.Value,
            _addressPeriodTable.Table.Name
        );
        _interventionForeignKeys = await _database.GetForeignKeyMetadataAsync(
            _interventionTable.Table.Schema.Value,
            _interventionTable.Table.Name
        );
        _interventionVisitForeignKeys = await _database.GetForeignKeyMetadataAsync(
            _interventionVisitTable.Table.Schema.Value,
            _interventionVisitTable.Table.Name
        );
        _sponsorReferenceForeignKeys = await _database.GetForeignKeyMetadataAsync(
            _sponsorReferenceTable.Table.Schema.Value,
            _sponsorReferenceTable.Table.Name
        );
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
        _seedData = await SeedSmokeRowsAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_should_allocate_collection_item_ids_via_defaults_for_representative_collection_and_extension_child_tables()
    {
        (await _database.SequenceExistsAsync("dms", "CollectionItemIdSequence")).Should().BeTrue();

        var generatedCollectionItemIds = new[]
        {
            _seedData.AddressCollectionItemId,
            _seedData.PeriodCollectionItemId,
            _seedData.InterventionCollectionItemId,
            _seedData.VisitCollectionItemId,
            _seedData.SponsorReferenceCollectionItemId,
        };

        generatedCollectionItemIds.Should().OnlyContain(collectionItemId => collectionItemId > 0);
        generatedCollectionItemIds.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task It_should_enforce_immediate_parent_fk_shapes_without_redundant_root_cascade_paths()
    {
        var interventionForeignKey = _interventionForeignKeys.Single(foreignKey =>
            foreignKey.ConstraintName == _interventionForeignKeyDefinition.Name
        );

        _interventionForeignKeys.Should().ContainSingle();
        interventionForeignKey.ConstraintName.Should().Be(_interventionForeignKeyDefinition.Name);
        interventionForeignKey.Columns.Should().Equal("School_DocumentId");
        interventionForeignKey.ReferencedSchema.Should().Be(_schoolExtensionTable.Table.Schema.Value);
        interventionForeignKey.ReferencedTable.Should().Be(_schoolExtensionTable.Table.Name);
        interventionForeignKey.ReferencedColumns.Should().Equal("DocumentId");
        interventionForeignKey.DeleteAction.Should().Be("CASCADE");
        interventionForeignKey.UpdateAction.Should().Be("NO ACTION");
        _interventionForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema == _schoolTable.Table.Schema.Value
                && foreignKey.ReferencedTable == _schoolTable.Table.Name
            );

        var interventionVisitForeignKey = _interventionVisitForeignKeys.Single(foreignKey =>
            foreignKey.ConstraintName == _interventionVisitForeignKeyDefinition.Name
        );

        interventionVisitForeignKey.Columns.Should().Equal("ParentCollectionItemId", "School_DocumentId");
        interventionVisitForeignKey.ReferencedSchema.Should().Be(_interventionTable.Table.Schema.Value);
        interventionVisitForeignKey.ReferencedTable.Should().Be(_interventionTable.Table.Name);
        interventionVisitForeignKey.ReferencedColumns.Should().Equal("CollectionItemId", "School_DocumentId");
        interventionVisitForeignKey.DeleteAction.Should().Be("CASCADE");
        interventionVisitForeignKey.UpdateAction.Should().Be("NO ACTION");

        var addressPeriodForeignKey = _addressPeriodForeignKeys.Single(foreignKey =>
            foreignKey.ConstraintName == _addressPeriodForeignKeyDefinition.Name
        );

        addressPeriodForeignKey.Columns.Should().Equal("ParentCollectionItemId", "School_DocumentId");
        addressPeriodForeignKey.ReferencedSchema.Should().Be(_schoolAddressTable.Table.Schema.Value);
        addressPeriodForeignKey.ReferencedTable.Should().Be(_schoolAddressTable.Table.Name);
        addressPeriodForeignKey.ReferencedColumns.Should().Equal("CollectionItemId", "School_DocumentId");
        addressPeriodForeignKey.DeleteAction.Should().Be("CASCADE");
        addressPeriodForeignKey.UpdateAction.Should().Be("NO ACTION");

        var sponsorReferenceParentForeignKey = _sponsorReferenceForeignKeys.Single(foreignKey =>
            foreignKey.ConstraintName == _sponsorReferenceParentForeignKeyDefinition.Name
        );

        sponsorReferenceParentForeignKey.Columns.Should().Equal("BaseCollectionItemId", "School_DocumentId");
        sponsorReferenceParentForeignKey
            .ReferencedSchema.Should()
            .Be(_schoolExtensionAddressTable.Table.Schema.Value);
        sponsorReferenceParentForeignKey.ReferencedTable.Should().Be(_schoolExtensionAddressTable.Table.Name);
        sponsorReferenceParentForeignKey
            .ReferencedColumns.Should()
            .Equal("BaseCollectionItemId", "School_DocumentId");
        sponsorReferenceParentForeignKey.DeleteAction.Should().Be("CASCADE");
        sponsorReferenceParentForeignKey.UpdateAction.Should().Be("NO ACTION");
        _sponsorReferenceForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema == _schoolAddressTable.Table.Schema.Value
                && foreignKey.ReferencedTable == _schoolAddressTable.Table.Name
            );

        await AssertForeignKeyViolationAsync(async () =>
            await InsertSchoolAddressPeriodAsync(
                _seedData.AddressCollectionItemId,
                _seedData.OtherSchoolDocumentId,
                2,
                "Period-WrongRoot"
            )
        );

        await AssertForeignKeyViolationAsync(async () =>
            await InsertSchoolExtensionInterventionVisitAsync(
                _seedData.InterventionCollectionItemId,
                _seedData.OtherSchoolDocumentId,
                2,
                "Visit-WrongRoot"
            )
        );

        await AssertForeignKeyViolationAsync(async () =>
            await InsertSchoolExtensionAddressSponsorReferenceAsync(
                _seedData.AddressCollectionItemId,
                _seedData.OtherSchoolDocumentId,
                2,
                null,
                null
            )
        );
    }

    [Test]
    public async Task It_should_delete_descendants_through_the_immediate_parent_chain()
    {
        await _database.ExecuteNonQueryAsync(
            """DELETE FROM "sample"."SchoolExtension" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
        );

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "sample"."SchoolExtensionIntervention" WHERE "School_DocumentId" = @schoolDocumentId;""",
                new NpgsqlParameter("schoolDocumentId", _seedData.SchoolDocumentId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "sample"."SchoolExtensionInterventionVisit" WHERE "School_DocumentId" = @schoolDocumentId;""",
                new NpgsqlParameter("schoolDocumentId", _seedData.SchoolDocumentId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "edfi"."School" WHERE "DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
            )
        )
            .Should()
            .Be(1);

        await _database.ExecuteNonQueryAsync(
            """DELETE FROM "edfi"."SchoolAddress" WHERE "CollectionItemId" = @collectionItemId;""",
            new NpgsqlParameter("collectionItemId", _seedData.AddressCollectionItemId)
        );

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "sample"."SchoolExtensionAddress" WHERE "BaseCollectionItemId" = @baseCollectionItemId;""",
                new NpgsqlParameter("baseCollectionItemId", _seedData.AddressCollectionItemId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "sample"."SchoolExtensionAddressSponsorReference" WHERE "BaseCollectionItemId" = @baseCollectionItemId;""",
                new NpgsqlParameter("baseCollectionItemId", _seedData.AddressCollectionItemId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "edfi"."SchoolAddressPeriod" WHERE "ParentCollectionItemId" = @parentCollectionItemId;""",
                new NpgsqlParameter("parentCollectionItemId", _seedData.AddressCollectionItemId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "edfi"."School" WHERE "DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
            )
        )
            .Should()
            .Be(1);
    }

    [Test]
    public async Task It_should_preserve_immediate_parent_fk_enforcement_after_reapplying_the_same_generated_ddl()
    {
        await _database.ResetAsync();
        await _database.ApplyGeneratedDdlAsync(_fixture.GeneratedDdl);

        var seedData = await SeedSmokeRowsAsync();

        await AssertForeignKeyViolationAsync(async () =>
            await InsertSchoolExtensionInterventionVisitAsync(
                seedData.InterventionCollectionItemId,
                seedData.OtherSchoolDocumentId,
                2,
                "Visit-WrongRoot"
            )
        );
    }

    private async Task<FocusedStableKeySmokeSeedData> SeedSmokeRowsAsync()
    {
        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Ed-Fi",
            "School"
        );
        var otherSchoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "Ed-Fi",
            "School"
        );
        var programDocumentId = await InsertDocumentAsync(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "Ed-Fi",
            "Program"
        );

        await InsertSchoolAsync(schoolDocumentId, 100);
        await InsertSchoolAsync(otherSchoolDocumentId, 200);
        await InsertProgramAsync(programDocumentId, "Robotics");
        await InsertSchoolExtensionAsync(schoolDocumentId, "North");

        var addressCollectionItemId = await InsertSchoolAddressAsync(schoolDocumentId, 1, "Austin");
        await InsertSchoolExtensionAddressAsync(addressCollectionItemId, schoolDocumentId, "Zone-1");

        var interventionCollectionItemId = await InsertSchoolExtensionInterventionAsync(
            schoolDocumentId,
            1,
            "CheckIn"
        );
        var periodCollectionItemId = await InsertSchoolAddressPeriodAsync(
            addressCollectionItemId,
            schoolDocumentId,
            1,
            "Morning"
        );
        var visitCollectionItemId = await InsertSchoolExtensionInterventionVisitAsync(
            interventionCollectionItemId,
            schoolDocumentId,
            1,
            "Visit-1"
        );
        var sponsorReferenceCollectionItemId = await InsertSchoolExtensionAddressSponsorReferenceAsync(
            addressCollectionItemId,
            schoolDocumentId,
            1,
            programDocumentId,
            "Robotics"
        );

        return new(
            schoolDocumentId,
            otherSchoolDocumentId,
            addressCollectionItemId,
            periodCollectionItemId,
            interventionCollectionItemId,
            visitCollectionItemId,
            sponsorReferenceCollectionItemId
        );
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = @projectName
              AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("projectName", projectName),
            new NpgsqlParameter("resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, string projectName, string resourceName)
    {
        var resourceKeyId = await GetResourceKeyIdAsync(projectName, resourceName);

        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private async Task InsertSchoolAsync(long documentId, int schoolId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."School" ("DocumentId", "SchoolId")
            VALUES (@documentId, @schoolId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolId", schoolId)
        );
    }

    private async Task InsertProgramAsync(long documentId, string programName)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Program" ("DocumentId", "ProgramName")
            VALUES (@documentId, @programName);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("programName", programName)
        );
    }

    private async Task InsertSchoolExtensionAsync(long documentId, string campusCode)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "sample"."SchoolExtension" ("DocumentId", "CampusCode")
            VALUES (@documentId, @campusCode);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("campusCode", campusCode)
        );
    }

    private async Task<long> InsertSchoolAddressAsync(long schoolDocumentId, int ordinal, string city)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."SchoolAddress" ("Ordinal", "School_DocumentId", "City")
            VALUES (@ordinal, @schoolDocumentId, @city)
            RETURNING "CollectionItemId";
            """,
            new NpgsqlParameter("ordinal", ordinal),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("city", city)
        );
    }

    private async Task InsertSchoolExtensionAddressAsync(
        long baseCollectionItemId,
        long schoolDocumentId,
        string zone
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "sample"."SchoolExtensionAddress" ("BaseCollectionItemId", "School_DocumentId", "Zone")
            VALUES (@baseCollectionItemId, @schoolDocumentId, @zone);
            """,
            new NpgsqlParameter("baseCollectionItemId", baseCollectionItemId),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("zone", zone)
        );
    }

    private async Task<long> InsertSchoolExtensionInterventionAsync(
        long schoolDocumentId,
        int ordinal,
        string interventionCode
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "sample"."SchoolExtensionIntervention" ("Ordinal", "School_DocumentId", "InterventionCode")
            VALUES (@ordinal, @schoolDocumentId, @interventionCode)
            RETURNING "CollectionItemId";
            """,
            new NpgsqlParameter("ordinal", ordinal),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("interventionCode", interventionCode)
        );
    }

    private async Task<long> InsertSchoolAddressPeriodAsync(
        long parentCollectionItemId,
        long schoolDocumentId,
        int ordinal,
        string periodName
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."SchoolAddressPeriod" ("Ordinal", "ParentCollectionItemId", "School_DocumentId", "PeriodName")
            VALUES (@ordinal, @parentCollectionItemId, @schoolDocumentId, @periodName)
            RETURNING "CollectionItemId";
            """,
            new NpgsqlParameter("ordinal", ordinal),
            new NpgsqlParameter("parentCollectionItemId", parentCollectionItemId),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("periodName", periodName)
        );
    }

    private async Task<long> InsertSchoolExtensionInterventionVisitAsync(
        long parentCollectionItemId,
        long schoolDocumentId,
        int ordinal,
        string visitCode
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "sample"."SchoolExtensionInterventionVisit" ("Ordinal", "ParentCollectionItemId", "School_DocumentId", "VisitCode")
            VALUES (@ordinal, @parentCollectionItemId, @schoolDocumentId, @visitCode)
            RETURNING "CollectionItemId";
            """,
            new NpgsqlParameter("ordinal", ordinal),
            new NpgsqlParameter("parentCollectionItemId", parentCollectionItemId),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("visitCode", visitCode)
        );
    }

    private async Task<long> InsertSchoolExtensionAddressSponsorReferenceAsync(
        long baseCollectionItemId,
        long schoolDocumentId,
        int ordinal,
        long? programDocumentId,
        string? programName
    )
    {
        var programDocumentParameter = new NpgsqlParameter("programDocumentId", NpgsqlDbType.Bigint)
        {
            Value = programDocumentId is not null ? programDocumentId.Value : DBNull.Value,
        };
        var programNameParameter = new NpgsqlParameter("programName", NpgsqlDbType.Varchar)
        {
            Value = programName is not null ? programName : DBNull.Value,
        };

        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "sample"."SchoolExtensionAddressSponsorReference" ("BaseCollectionItemId", "Ordinal", "School_DocumentId", "Program_DocumentId", "Program_ProgramName")
            VALUES (@baseCollectionItemId, @ordinal, @schoolDocumentId, @programDocumentId, @programName)
            RETURNING "CollectionItemId";
            """,
            new NpgsqlParameter("baseCollectionItemId", baseCollectionItemId),
            new NpgsqlParameter("ordinal", ordinal),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            programDocumentParameter,
            programNameParameter
        );
    }

    private async Task<long> CountRowsAsync(string sql, params NpgsqlParameter[] parameters)
    {
        return await _database.ExecuteScalarAsync<long>(sql, parameters);
    }

    private static async Task AssertForeignKeyViolationAsync(Func<Task> act)
    {
        var exception = (await act.Should().ThrowAsync<PostgresException>()).Which;
        exception.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }
}
