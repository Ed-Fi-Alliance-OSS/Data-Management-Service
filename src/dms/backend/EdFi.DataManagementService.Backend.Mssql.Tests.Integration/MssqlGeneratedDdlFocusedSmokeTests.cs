// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

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
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_A_Mssql_Generated_Ddl_Apply_Harness_With_A_Focused_Stable_Key_Fixture_For_Smoke_Coverage
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private FocusedStableKeySmokeSeedData _seedData = null!;
    private IReadOnlyList<MssqlForeignKeyMetadata> _addressPeriodForeignKeys = null!;
    private IReadOnlyList<MssqlForeignKeyMetadata> _interventionForeignKeys = null!;
    private IReadOnlyList<MssqlForeignKeyMetadata> _interventionVisitForeignKeys = null!;
    private IReadOnlyList<MssqlForeignKeyMetadata> _sponsorReferenceForeignKeys = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _addressPeriodForeignKeys = await _database.GetForeignKeyMetadataAsync("edfi", "SchoolAddressPeriod");
        _interventionForeignKeys = await _database.GetForeignKeyMetadataAsync(
            "sample",
            "SchoolExtensionIntervention"
        );
        _interventionVisitForeignKeys = await _database.GetForeignKeyMetadataAsync(
            "sample",
            "SchoolExtensionInterventionVisit"
        );
        _sponsorReferenceForeignKeys = await _database.GetForeignKeyMetadataAsync(
            "sample",
            "SchoolExtensionAddressSponsorReference"
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
        _interventionForeignKeys.Should().ContainSingle();
        _interventionForeignKeys
            .Single()
            .ConstraintName.Should()
            .Be("FK_SchoolExtensionIntervention_SchoolExtension");
        _interventionForeignKeys.Single().Columns.Should().Equal("School_DocumentId");
        _interventionForeignKeys.Single().ReferencedSchema.Should().Be("sample");
        _interventionForeignKeys.Single().ReferencedTable.Should().Be("SchoolExtension");
        _interventionForeignKeys.Single().ReferencedColumns.Should().Equal("DocumentId");
        _interventionForeignKeys.Single().DeleteAction.Should().Be("CASCADE");
        _interventionForeignKeys.Single().UpdateAction.Should().Be("NO ACTION");
        _interventionForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema == "edfi" && foreignKey.ReferencedTable == "School"
            );

        var interventionVisitForeignKey = _interventionVisitForeignKeys.Single(foreignKey =>
            foreignKey.ConstraintName == "FK_SchoolExtensionInterventionVisit_SchoolExtensionIntervention"
        );

        interventionVisitForeignKey.Columns.Should().Equal("ParentCollectionItemId", "School_DocumentId");
        interventionVisitForeignKey.ReferencedSchema.Should().Be("sample");
        interventionVisitForeignKey.ReferencedTable.Should().Be("SchoolExtensionIntervention");
        interventionVisitForeignKey.ReferencedColumns.Should().Equal("CollectionItemId", "School_DocumentId");
        interventionVisitForeignKey.DeleteAction.Should().Be("CASCADE");
        interventionVisitForeignKey.UpdateAction.Should().Be("NO ACTION");

        var addressPeriodForeignKey = _addressPeriodForeignKeys.Single(foreignKey =>
            foreignKey.ConstraintName == "FK_SchoolAddressPeriod_SchoolAddress"
        );

        addressPeriodForeignKey.Columns.Should().Equal("ParentCollectionItemId", "School_DocumentId");
        addressPeriodForeignKey.ReferencedSchema.Should().Be("edfi");
        addressPeriodForeignKey.ReferencedTable.Should().Be("SchoolAddress");
        addressPeriodForeignKey.ReferencedColumns.Should().Equal("CollectionItemId", "School_DocumentId");
        addressPeriodForeignKey.DeleteAction.Should().Be("CASCADE");
        addressPeriodForeignKey.UpdateAction.Should().Be("NO ACTION");

        var sponsorReferenceParentForeignKey = _sponsorReferenceForeignKeys.Single(foreignKey =>
            foreignKey.ConstraintName == "FK_SchoolExtensionAddressSponsorReference_SchoolExtensionAddress"
        );

        sponsorReferenceParentForeignKey.Columns.Should().Equal("BaseCollectionItemId", "School_DocumentId");
        sponsorReferenceParentForeignKey.ReferencedSchema.Should().Be("sample");
        sponsorReferenceParentForeignKey.ReferencedTable.Should().Be("SchoolExtensionAddress");
        sponsorReferenceParentForeignKey
            .ReferencedColumns.Should()
            .Equal("BaseCollectionItemId", "School_DocumentId");
        sponsorReferenceParentForeignKey.DeleteAction.Should().Be("CASCADE");
        sponsorReferenceParentForeignKey.UpdateAction.Should().Be("NO ACTION");
        _sponsorReferenceForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema == "edfi" && foreignKey.ReferencedTable == "SchoolAddress"
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
            "DELETE FROM [sample].[SchoolExtension] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", _seedData.SchoolDocumentId)
        );

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [sample].[SchoolExtensionIntervention] WHERE [School_DocumentId] = @schoolDocumentId;",
                new SqlParameter("@schoolDocumentId", _seedData.SchoolDocumentId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [sample].[SchoolExtensionInterventionVisit] WHERE [School_DocumentId] = @schoolDocumentId;",
                new SqlParameter("@schoolDocumentId", _seedData.SchoolDocumentId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [edfi].[School] WHERE [DocumentId] = @documentId;",
                new SqlParameter("@documentId", _seedData.SchoolDocumentId)
            )
        )
            .Should()
            .Be(1);

        await _database.ExecuteNonQueryAsync(
            "DELETE FROM [edfi].[SchoolAddress] WHERE [CollectionItemId] = @collectionItemId;",
            new SqlParameter("@collectionItemId", _seedData.AddressCollectionItemId)
        );

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [sample].[SchoolExtensionAddress] WHERE [BaseCollectionItemId] = @baseCollectionItemId;",
                new SqlParameter("@baseCollectionItemId", _seedData.AddressCollectionItemId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [sample].[SchoolExtensionAddressSponsorReference] WHERE [BaseCollectionItemId] = @baseCollectionItemId;",
                new SqlParameter("@baseCollectionItemId", _seedData.AddressCollectionItemId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [edfi].[SchoolAddressPeriod] WHERE [ParentCollectionItemId] = @parentCollectionItemId;",
                new SqlParameter("@parentCollectionItemId", _seedData.AddressCollectionItemId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [edfi].[School] WHERE [DocumentId] = @documentId;",
                new SqlParameter("@documentId", _seedData.SchoolDocumentId)
            )
        )
            .Should()
            .Be(1);
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
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", projectName),
            new SqlParameter("@resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, string projectName, string resourceName)
    {
        var resourceKeyId = await GetResourceKeyIdAsync(projectName, resourceName);

        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );
    }

    private async Task InsertSchoolAsync(long documentId, int schoolId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[School] ([DocumentId], [SchoolId])
            VALUES (@documentId, @schoolId);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolId", schoolId)
        );
    }

    private async Task InsertProgramAsync(long documentId, string programName)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Program] ([DocumentId], [ProgramName])
            VALUES (@documentId, @programName);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@programName", programName)
        );
    }

    private async Task InsertSchoolExtensionAsync(long documentId, string campusCode)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [sample].[SchoolExtension] ([DocumentId], [CampusCode])
            VALUES (@documentId, @campusCode);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@campusCode", campusCode)
        );
    }

    private async Task<long> InsertSchoolAddressAsync(long schoolDocumentId, int ordinal, string city)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([CollectionItemId] bigint);
            INSERT INTO [edfi].[SchoolAddress] ([Ordinal], [School_DocumentId], [City])
            OUTPUT INSERTED.[CollectionItemId] INTO @Inserted ([CollectionItemId])
            VALUES (@ordinal, @schoolDocumentId, @city);
            SELECT TOP (1) [CollectionItemId] FROM @Inserted;
            """,
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@city", city)
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
            INSERT INTO [sample].[SchoolExtensionAddress] ([BaseCollectionItemId], [School_DocumentId], [Zone])
            VALUES (@baseCollectionItemId, @schoolDocumentId, @zone);
            """,
            new SqlParameter("@baseCollectionItemId", baseCollectionItemId),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@zone", zone)
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
            DECLARE @Inserted TABLE ([CollectionItemId] bigint);
            INSERT INTO [sample].[SchoolExtensionIntervention] ([Ordinal], [School_DocumentId], [InterventionCode])
            OUTPUT INSERTED.[CollectionItemId] INTO @Inserted ([CollectionItemId])
            VALUES (@ordinal, @schoolDocumentId, @interventionCode);
            SELECT TOP (1) [CollectionItemId] FROM @Inserted;
            """,
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@interventionCode", interventionCode)
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
            DECLARE @Inserted TABLE ([CollectionItemId] bigint);
            INSERT INTO [edfi].[SchoolAddressPeriod] ([Ordinal], [ParentCollectionItemId], [School_DocumentId], [PeriodName])
            OUTPUT INSERTED.[CollectionItemId] INTO @Inserted ([CollectionItemId])
            VALUES (@ordinal, @parentCollectionItemId, @schoolDocumentId, @periodName);
            SELECT TOP (1) [CollectionItemId] FROM @Inserted;
            """,
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@parentCollectionItemId", parentCollectionItemId),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@periodName", periodName)
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
            DECLARE @Inserted TABLE ([CollectionItemId] bigint);
            INSERT INTO [sample].[SchoolExtensionInterventionVisit] ([Ordinal], [ParentCollectionItemId], [School_DocumentId], [VisitCode])
            OUTPUT INSERTED.[CollectionItemId] INTO @Inserted ([CollectionItemId])
            VALUES (@ordinal, @parentCollectionItemId, @schoolDocumentId, @visitCode);
            SELECT TOP (1) [CollectionItemId] FROM @Inserted;
            """,
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@parentCollectionItemId", parentCollectionItemId),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@visitCode", visitCode)
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
        var programDocumentParameter = new SqlParameter("@programDocumentId", SqlDbType.BigInt)
        {
            Value = programDocumentId is not null ? programDocumentId.Value : DBNull.Value,
        };
        var programNameParameter = new SqlParameter("@programName", SqlDbType.NVarChar, 20)
        {
            Value = programName is not null ? programName : DBNull.Value,
        };

        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([CollectionItemId] bigint);
            INSERT INTO [sample].[SchoolExtensionAddressSponsorReference] ([BaseCollectionItemId], [Ordinal], [School_DocumentId], [Program_DocumentId], [Program_ProgramName])
            OUTPUT INSERTED.[CollectionItemId] INTO @Inserted ([CollectionItemId])
            VALUES (@baseCollectionItemId, @ordinal, @schoolDocumentId, @programDocumentId, @programName);
            SELECT TOP (1) [CollectionItemId] FROM @Inserted;
            """,
            new SqlParameter("@baseCollectionItemId", baseCollectionItemId),
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            programDocumentParameter,
            programNameParameter
        );
    }

    private async Task<long> CountRowsAsync(string sql, params SqlParameter[] parameters)
    {
        return await _database.ExecuteScalarAsync<long>(sql, parameters);
    }

    private static async Task AssertForeignKeyViolationAsync(Func<Task> act)
    {
        var exception = (await act.Should().ThrowAsync<SqlException>()).Which;
        exception.Number.Should().Be(547);
    }
}
