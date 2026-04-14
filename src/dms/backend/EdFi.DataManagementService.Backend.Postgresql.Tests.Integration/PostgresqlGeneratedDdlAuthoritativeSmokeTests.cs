// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed record AuthoritativeSampleSmokeSeedData(
    long SchoolDocumentId,
    long StudentDocumentId,
    long OtherStudentDocumentId,
    long StudentEducationOrganizationAssociationDocumentId,
    long OtherStudentEducationOrganizationAssociationDocumentId,
    long AlternateTermDescriptorDocumentId,
    long SchoolExtensionDirectlyOwnedBusCollectionItemId,
    long StudentEducationOrganizationAssociationAddressCollectionItemId,
    long StudentEducationOrganizationAssociationExtensionAddressSchoolDistrictCollectionItemId,
    long StudentEducationOrganizationAssociationExtensionAddressTermCollectionItemId
);

internal sealed record DocumentStampState(
    long ContentVersion,
    long IdentityVersion,
    DateTimeOffset ContentLastModifiedAt,
    DateTimeOffset IdentityLastModifiedAt
);

[TestFixture]
[NonParallelizable]
public class Given_A_Postgresql_Generated_Ddl_Apply_Harness_With_The_Authoritative_DS_Sample_Fixture_For_Smoke_Coverage
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private AuthoritativeSampleSmokeSeedData _seedData = null!;
    private DbTableModel _schoolTable = null!;
    private DbTableModel _schoolExtensionTable = null!;
    private DbTableModel _schoolExtensionDirectlyOwnedBusTable = null!;
    private DbTableModel _studentEducationOrganizationAssociationTable = null!;
    private DbTableModel _studentEducationOrganizationAssociationAddressTable = null!;
    private DbTableModel _studentEducationOrganizationAssociationExtensionAddressTable = null!;
    private DbTableModel _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictTable = null!;
    private DbTableModel _studentEducationOrganizationAssociationExtensionAddressTermTable = null!;
    private TableConstraint.ForeignKey _schoolExtensionDirectlyOwnedBusForeignKeyDefinition = null!;
    private TableConstraint.ForeignKey _studentEducationOrganizationAssociationExtensionAddressForeignKeyDefinition =
        null!;
    private TableConstraint.ForeignKey _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictForeignKeyDefinition =
        null!;
    private TableConstraint.ForeignKey _studentEducationOrganizationAssociationExtensionAddressTermForeignKeyDefinition =
        null!;
    private IReadOnlyList<PostgresqlForeignKeyMetadata> _schoolExtensionDirectlyOwnedBusForeignKeys = null!;
    private IReadOnlyList<PostgresqlForeignKeyMetadata> _studentEducationOrganizationAssociationExtensionAddressForeignKeys =
        null!;
    private IReadOnlyList<PostgresqlForeignKeyMetadata> _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictForeignKeys =
        null!;
    private IReadOnlyList<PostgresqlForeignKeyMetadata> _studentEducationOrganizationAssociationExtensionAddressTermForeignKeys =
        null!;
    private string? _schoolExtensionDirectlyOwnedBusCollectionItemDefault;
    private string? _studentEducationOrganizationAssociationAddressCollectionItemDefault;
    private string? _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictCollectionItemDefault;
    private string? _studentEducationOrganizationAssociationExtensionAddressTermCollectionItemDefault;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);

        _schoolTable = PostgresqlGeneratedDdlModelLookup.RequireTable(_fixture.ModelSet, "edfi", "School");
        _schoolExtensionTable = PostgresqlGeneratedDdlModelLookup.RequireTable(
            _fixture.ModelSet,
            "sample",
            "SchoolExtension"
        );
        _schoolExtensionDirectlyOwnedBusTable = PostgresqlGeneratedDdlModelLookup.RequireTableByScope(
            _fixture.ModelSet,
            "sample",
            "$._ext.sample.directlyOwnedBuses[*]"
        );
        _studentEducationOrganizationAssociationTable = PostgresqlGeneratedDdlModelLookup.RequireTable(
            _fixture.ModelSet,
            "edfi",
            "StudentEducationOrganizationAssociation"
        );
        _studentEducationOrganizationAssociationAddressTable = PostgresqlGeneratedDdlModelLookup.RequireTable(
            _fixture.ModelSet,
            "edfi",
            "StudentEducationOrganizationAssociationAddress"
        );
        _studentEducationOrganizationAssociationExtensionAddressTable =
            PostgresqlGeneratedDdlModelLookup.RequireTable(
                _fixture.ModelSet,
                "sample",
                "StudentEducationOrganizationAssociationExtensionAddress"
            );
        _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictTable =
            PostgresqlGeneratedDdlModelLookup.RequireTableByScopeAndColumns(
                _fixture.ModelSet,
                "sample",
                "$.addresses[*]._ext.sample.schoolDistricts[*]",
                "StudentEducationOrganizationAssociation_DocumentId",
                "SchoolDistrict"
            );
        _studentEducationOrganizationAssociationExtensionAddressTermTable =
            PostgresqlGeneratedDdlModelLookup.RequireTable(
                _fixture.ModelSet,
                "sample",
                "StudentEducationOrganizationAssociationExtensionAddressTerm"
            );
        _schoolExtensionDirectlyOwnedBusForeignKeyDefinition =
            PostgresqlGeneratedDdlModelLookup.RequireForeignKey(
                _schoolExtensionDirectlyOwnedBusTable,
                _schoolExtensionTable.Table,
                "School_DocumentId"
            );
        _studentEducationOrganizationAssociationExtensionAddressForeignKeyDefinition =
            PostgresqlGeneratedDdlModelLookup.RequireForeignKey(
                _studentEducationOrganizationAssociationExtensionAddressTable,
                _studentEducationOrganizationAssociationAddressTable.Table,
                "BaseCollectionItemId",
                "StudentEducationOrganizationAssociation_DocumentId"
            );
        _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictForeignKeyDefinition =
            PostgresqlGeneratedDdlModelLookup.RequireForeignKey(
                _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictTable,
                _studentEducationOrganizationAssociationExtensionAddressTable.Table,
                "BaseCollectionItemId",
                "StudentEducationOrganizationAssociation_DocumentId"
            );
        _studentEducationOrganizationAssociationExtensionAddressTermForeignKeyDefinition =
            PostgresqlGeneratedDdlModelLookup.RequireForeignKey(
                _studentEducationOrganizationAssociationExtensionAddressTermTable,
                _studentEducationOrganizationAssociationExtensionAddressTable.Table,
                "BaseCollectionItemId",
                "StudentEducationOrganizationAssociation_DocumentId"
            );

        _schoolExtensionDirectlyOwnedBusForeignKeys = await _database.GetForeignKeyMetadataAsync(
            _schoolExtensionDirectlyOwnedBusTable.Table.Schema.Value,
            _schoolExtensionDirectlyOwnedBusTable.Table.Name
        );
        _studentEducationOrganizationAssociationExtensionAddressForeignKeys =
            await _database.GetForeignKeyMetadataAsync(
                _studentEducationOrganizationAssociationExtensionAddressTable.Table.Schema.Value,
                _studentEducationOrganizationAssociationExtensionAddressTable.Table.Name
            );
        _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictForeignKeys =
            await _database.GetForeignKeyMetadataAsync(
                _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictTable
                    .Table
                    .Schema
                    .Value,
                _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictTable.Table.Name
            );
        _studentEducationOrganizationAssociationExtensionAddressTermForeignKeys =
            await _database.GetForeignKeyMetadataAsync(
                _studentEducationOrganizationAssociationExtensionAddressTermTable.Table.Schema.Value,
                _studentEducationOrganizationAssociationExtensionAddressTermTable.Table.Name
            );

        _schoolExtensionDirectlyOwnedBusCollectionItemDefault = await _database.GetColumnDefaultAsync(
            _schoolExtensionDirectlyOwnedBusTable.Table.Schema.Value,
            _schoolExtensionDirectlyOwnedBusTable.Table.Name,
            "CollectionItemId"
        );
        _studentEducationOrganizationAssociationAddressCollectionItemDefault =
            await _database.GetColumnDefaultAsync(
                _studentEducationOrganizationAssociationAddressTable.Table.Schema.Value,
                _studentEducationOrganizationAssociationAddressTable.Table.Name,
                "CollectionItemId"
            );
        _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictCollectionItemDefault =
            await _database.GetColumnDefaultAsync(
                _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictTable
                    .Table
                    .Schema
                    .Value,
                _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictTable.Table.Name,
                "CollectionItemId"
            );
        _studentEducationOrganizationAssociationExtensionAddressTermCollectionItemDefault =
            await _database.GetColumnDefaultAsync(
                _studentEducationOrganizationAssociationExtensionAddressTermTable.Table.Schema.Value,
                _studentEducationOrganizationAssociationExtensionAddressTermTable.Table.Name,
                "CollectionItemId"
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
    public async Task It_should_allocate_collection_item_ids_via_defaults_for_representative_real_ds_sample_collection_tables()
    {
        (await _database.SequenceExistsAsync("dms", "CollectionItemIdSequence")).Should().BeTrue();

        _schoolExtensionDirectlyOwnedBusCollectionItemDefault.Should().NotBeNull();
        _schoolExtensionDirectlyOwnedBusCollectionItemDefault.Should().Contain("CollectionItemIdSequence");
        _schoolExtensionDirectlyOwnedBusCollectionItemDefault.Should().Contain("nextval");

        _studentEducationOrganizationAssociationAddressCollectionItemDefault.Should().NotBeNull();
        _studentEducationOrganizationAssociationAddressCollectionItemDefault
            .Should()
            .Contain("CollectionItemIdSequence");
        _studentEducationOrganizationAssociationAddressCollectionItemDefault.Should().Contain("nextval");

        _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictCollectionItemDefault
            .Should()
            .NotBeNull();
        _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictCollectionItemDefault
            .Should()
            .Contain("CollectionItemIdSequence");
        _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictCollectionItemDefault
            .Should()
            .Contain("nextval");

        _studentEducationOrganizationAssociationExtensionAddressTermCollectionItemDefault
            .Should()
            .NotBeNull();
        _studentEducationOrganizationAssociationExtensionAddressTermCollectionItemDefault
            .Should()
            .Contain("CollectionItemIdSequence");
        _studentEducationOrganizationAssociationExtensionAddressTermCollectionItemDefault
            .Should()
            .Contain("nextval");

        var generatedCollectionItemIds = new[]
        {
            _seedData.SchoolExtensionDirectlyOwnedBusCollectionItemId,
            _seedData.StudentEducationOrganizationAssociationAddressCollectionItemId,
            _seedData.StudentEducationOrganizationAssociationExtensionAddressSchoolDistrictCollectionItemId,
            _seedData.StudentEducationOrganizationAssociationExtensionAddressTermCollectionItemId,
        };

        generatedCollectionItemIds.Should().OnlyContain(collectionItemId => collectionItemId > 0);
        generatedCollectionItemIds.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task It_should_reapply_the_same_generated_ddl_without_duplicate_generated_seed_rows()
    {
        await _database.ResetAsync();

        var effectiveSchemaCountBefore = await CountRowsAsync(
            """SELECT COUNT(*) FROM "dms"."EffectiveSchema";"""
        );
        var resourceKeyCountBefore = await CountRowsAsync("""SELECT COUNT(*) FROM "dms"."ResourceKey";""");
        var schemaComponentCountBefore = await CountRowsAsync(
            """SELECT COUNT(*) FROM "dms"."SchemaComponent";"""
        );

        await _database.ApplyGeneratedDdlAsync(_fixture.GeneratedDdl);

        var effectiveSchemaCountAfter = await CountRowsAsync(
            """SELECT COUNT(*) FROM "dms"."EffectiveSchema";"""
        );
        var resourceKeyCountAfter = await CountRowsAsync("""SELECT COUNT(*) FROM "dms"."ResourceKey";""");
        var schemaComponentCountAfter = await CountRowsAsync(
            """SELECT COUNT(*) FROM "dms"."SchemaComponent";"""
        );

        effectiveSchemaCountAfter.Should().Be(effectiveSchemaCountBefore);
        resourceKeyCountAfter.Should().Be(resourceKeyCountBefore);
        schemaComponentCountAfter.Should().Be(schemaComponentCountBefore);
    }

    [Test]
    public async Task It_should_enforce_immediate_parent_fk_shapes_for_root_and_collection_aligned_extension_relationships()
    {
        var schoolExtensionDirectlyOwnedBusForeignKey = _schoolExtensionDirectlyOwnedBusForeignKeys.Single(
            foreignKey =>
                foreignKey.ConstraintName == _schoolExtensionDirectlyOwnedBusForeignKeyDefinition.Name
        );

        schoolExtensionDirectlyOwnedBusForeignKey.Columns.Should().Equal("School_DocumentId");
        schoolExtensionDirectlyOwnedBusForeignKey
            .ReferencedSchema.Should()
            .Be(_schoolExtensionTable.Table.Schema.Value);
        schoolExtensionDirectlyOwnedBusForeignKey
            .ReferencedTable.Should()
            .Be(_schoolExtensionTable.Table.Name);
        schoolExtensionDirectlyOwnedBusForeignKey.ReferencedColumns.Should().Equal("DocumentId");
        schoolExtensionDirectlyOwnedBusForeignKey.DeleteAction.Should().Be("CASCADE");
        schoolExtensionDirectlyOwnedBusForeignKey.UpdateAction.Should().Be("NO ACTION");
        _schoolExtensionDirectlyOwnedBusForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema == _schoolTable.Table.Schema.Value
                && foreignKey.ReferencedTable == _schoolTable.Table.Name
            );

        var alignedExtensionAddressForeignKey =
            _studentEducationOrganizationAssociationExtensionAddressForeignKeys.Single(foreignKey =>
                foreignKey.ConstraintName
                == _studentEducationOrganizationAssociationExtensionAddressForeignKeyDefinition.Name
            );

        alignedExtensionAddressForeignKey
            .Columns.Should()
            .Equal("BaseCollectionItemId", "StudentEducationOrganizationAssociation_DocumentId");
        alignedExtensionAddressForeignKey
            .ReferencedSchema.Should()
            .Be(_studentEducationOrganizationAssociationAddressTable.Table.Schema.Value);
        alignedExtensionAddressForeignKey
            .ReferencedTable.Should()
            .Be(_studentEducationOrganizationAssociationAddressTable.Table.Name);
        alignedExtensionAddressForeignKey
            .ReferencedColumns.Should()
            .Equal("CollectionItemId", "StudentEducationOrganizationAssociation_DocumentId");
        alignedExtensionAddressForeignKey.DeleteAction.Should().Be("CASCADE");
        alignedExtensionAddressForeignKey.UpdateAction.Should().Be("NO ACTION");
        _studentEducationOrganizationAssociationExtensionAddressForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema
                    == _studentEducationOrganizationAssociationTable.Table.Schema.Value
                && foreignKey.ReferencedTable == _studentEducationOrganizationAssociationTable.Table.Name
            );

        var extensionAddressSchoolDistrictForeignKey =
            _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictForeignKeys.Single(
                foreignKey =>
                    foreignKey.ConstraintName
                    == _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictForeignKeyDefinition.Name
            );

        extensionAddressSchoolDistrictForeignKey
            .Columns.Should()
            .Equal("BaseCollectionItemId", "StudentEducationOrganizationAssociation_DocumentId");
        extensionAddressSchoolDistrictForeignKey
            .ReferencedSchema.Should()
            .Be(_studentEducationOrganizationAssociationExtensionAddressTable.Table.Schema.Value);
        extensionAddressSchoolDistrictForeignKey
            .ReferencedTable.Should()
            .Be(_studentEducationOrganizationAssociationExtensionAddressTable.Table.Name);
        extensionAddressSchoolDistrictForeignKey
            .ReferencedColumns.Should()
            .Equal("BaseCollectionItemId", "StudentEducationOrganizationAssociation_DocumentId");
        extensionAddressSchoolDistrictForeignKey.DeleteAction.Should().Be("CASCADE");
        extensionAddressSchoolDistrictForeignKey.UpdateAction.Should().Be("NO ACTION");
        _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema
                    == _studentEducationOrganizationAssociationAddressTable.Table.Schema.Value
                && foreignKey.ReferencedTable
                    == _studentEducationOrganizationAssociationAddressTable.Table.Name
            );

        var extensionAddressTermForeignKey =
            _studentEducationOrganizationAssociationExtensionAddressTermForeignKeys.Single(foreignKey =>
                foreignKey.ConstraintName
                == _studentEducationOrganizationAssociationExtensionAddressTermForeignKeyDefinition.Name
            );

        extensionAddressTermForeignKey
            .Columns.Should()
            .Equal("BaseCollectionItemId", "StudentEducationOrganizationAssociation_DocumentId");
        extensionAddressTermForeignKey
            .ReferencedSchema.Should()
            .Be(_studentEducationOrganizationAssociationExtensionAddressTable.Table.Schema.Value);
        extensionAddressTermForeignKey
            .ReferencedTable.Should()
            .Be(_studentEducationOrganizationAssociationExtensionAddressTable.Table.Name);
        extensionAddressTermForeignKey
            .ReferencedColumns.Should()
            .Equal("BaseCollectionItemId", "StudentEducationOrganizationAssociation_DocumentId");
        extensionAddressTermForeignKey.DeleteAction.Should().Be("CASCADE");
        extensionAddressTermForeignKey.UpdateAction.Should().Be("NO ACTION");
        _studentEducationOrganizationAssociationExtensionAddressTermForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema
                    == _studentEducationOrganizationAssociationAddressTable.Table.Schema.Value
                && foreignKey.ReferencedTable
                    == _studentEducationOrganizationAssociationAddressTable.Table.Name
            );

        await AssertForeignKeyViolationAsync(async () =>
            await InsertStudentEducationOrganizationAssociationExtensionAddressSchoolDistrictAsync(
                _seedData.StudentEducationOrganizationAssociationAddressCollectionItemId,
                _seedData.OtherStudentEducationOrganizationAssociationDocumentId,
                2,
                "Wrong-Root-District"
            )
        );

        await AssertForeignKeyViolationAsync(async () =>
            await InsertStudentEducationOrganizationAssociationExtensionAddressTermAsync(
                _seedData.StudentEducationOrganizationAssociationAddressCollectionItemId,
                _seedData.OtherStudentEducationOrganizationAssociationDocumentId,
                2,
                _seedData.AlternateTermDescriptorDocumentId
            )
        );
    }

    [Test]
    public async Task It_should_delete_descendants_through_the_intended_immediate_parent_chain()
    {
        await _database.ExecuteNonQueryAsync(
            """DELETE FROM "sample"."SchoolExtension" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
        );

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "sample"."SchoolExtensionDirectlyOwnedBus" WHERE "School_DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
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
            """DELETE FROM "edfi"."StudentEducationOrganizationAssociationAddress" WHERE "CollectionItemId" = @collectionItemId;""",
            new NpgsqlParameter(
                "collectionItemId",
                _seedData.StudentEducationOrganizationAssociationAddressCollectionItemId
            )
        );

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "sample"."StudentEducationOrganizationAssociationExtensionAddress" WHERE "BaseCollectionItemId" = @baseCollectionItemId;""",
                new NpgsqlParameter(
                    "baseCollectionItemId",
                    _seedData.StudentEducationOrganizationAssociationAddressCollectionItemId
                )
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                $"""SELECT COUNT(*) FROM "sample"."{_studentEducationOrganizationAssociationExtensionAddressSchoolDistrictTable.Table.Name}" WHERE "BaseCollectionItemId" = @baseCollectionItemId;""",
                new NpgsqlParameter(
                    "baseCollectionItemId",
                    _seedData.StudentEducationOrganizationAssociationAddressCollectionItemId
                )
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "sample"."StudentEducationOrganizationAssociationExtensionAddressTerm" WHERE "BaseCollectionItemId" = @baseCollectionItemId;""",
                new NpgsqlParameter(
                    "baseCollectionItemId",
                    _seedData.StudentEducationOrganizationAssociationAddressCollectionItemId
                )
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "edfi"."StudentEducationOrganizationAssociation" WHERE "DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", _seedData.StudentEducationOrganizationAssociationDocumentId)
            )
        )
            .Should()
            .Be(1);
    }

    [Test]
    public async Task It_should_stamp_content_only_root_changes_without_touching_identity_stamps()
    {
        var before = await GetDocumentStampStateAsync(_seedData.StudentDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."Student"
            SET "FirstName" = @firstName
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("firstName", "Jordan"),
            new NpgsqlParameter("documentId", _seedData.StudentDocumentId)
        );

        var after = await GetDocumentStampStateAsync(_seedData.StudentDocumentId);

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().Be(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().Be(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_stamp_child_representation_changes_without_touching_identity_stamps()
    {
        var before = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."StudentEducationOrganizationAssociationAddress"
            SET "StreetNumberName" = @streetNumberName
            WHERE "CollectionItemId" = @collectionItemId
              AND "StudentEducationOrganizationAssociation_DocumentId" = @documentId;
            """,
            new NpgsqlParameter("streetNumberName", "101 Congress Ave"),
            new NpgsqlParameter(
                "collectionItemId",
                _seedData.StudentEducationOrganizationAssociationAddressCollectionItemId
            ),
            new NpgsqlParameter("documentId", _seedData.StudentEducationOrganizationAssociationDocumentId)
        );

        var after = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().Be(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().Be(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_stamp_child_inserts_without_touching_identity_stamps()
    {
        var before = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );
        var addressTypeDescriptorDocumentId = await GetDescriptorDocumentIdAsync(
            "Ed-Fi:AddressTypeDescriptor",
            "Home"
        );
        var stateAbbreviationDescriptorDocumentId = await GetDescriptorDocumentIdAsync(
            "Ed-Fi:StateAbbreviationDescriptor",
            "TX"
        );

        await DelayForDistinctTimestampsAsync();
        await InsertStudentEducationOrganizationAssociationAddressAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId,
            2,
            addressTypeDescriptorDocumentId,
            stateAbbreviationDescriptorDocumentId,
            "Austin",
            "78702",
            "200 Congress Ave"
        );

        var after = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().Be(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().Be(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_not_stamp_successful_no_op_child_updates()
    {
        var before = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."StudentEducationOrganizationAssociationAddress"
            SET "StreetNumberName" = "StreetNumberName"
            WHERE "CollectionItemId" = @collectionItemId
              AND "StudentEducationOrganizationAssociation_DocumentId" = @documentId;
            """,
            new NpgsqlParameter(
                "collectionItemId",
                _seedData.StudentEducationOrganizationAssociationAddressCollectionItemId
            ),
            new NpgsqlParameter("documentId", _seedData.StudentEducationOrganizationAssociationDocumentId)
        );

        var after = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        after.Should().Be(before);
    }

    [Test]
    public async Task It_should_stamp_extension_scope_representation_changes_without_touching_identity_stamps()
    {
        var before = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "sample"."StudentEducationOrganizationAssociationExtensionAddress"
            SET "Complex" = @complex
            WHERE "BaseCollectionItemId" = @baseCollectionItemId
              AND "StudentEducationOrganizationAssociation_DocumentId" = @documentId;
            """,
            new NpgsqlParameter("complex", "Complex-Updated"),
            new NpgsqlParameter(
                "baseCollectionItemId",
                _seedData.StudentEducationOrganizationAssociationAddressCollectionItemId
            ),
            new NpgsqlParameter("documentId", _seedData.StudentEducationOrganizationAssociationDocumentId)
        );

        var after = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().Be(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().Be(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_not_stamp_successful_no_op_extension_scope_updates()
    {
        var before = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "sample"."StudentEducationOrganizationAssociationExtensionAddress"
            SET "Complex" = "Complex"
            WHERE "BaseCollectionItemId" = @baseCollectionItemId
              AND "StudentEducationOrganizationAssociation_DocumentId" = @documentId;
            """,
            new NpgsqlParameter(
                "baseCollectionItemId",
                _seedData.StudentEducationOrganizationAssociationAddressCollectionItemId
            ),
            new NpgsqlParameter("documentId", _seedData.StudentEducationOrganizationAssociationDocumentId)
        );

        var after = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        after.Should().Be(before);
    }

    [Test]
    public async Task It_should_stamp_root_extension_inserts_without_touching_identity_stamps()
    {
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var documentId = await InsertDocumentAsync(
            Guid.Parse("12121212-1212-1212-1212-121212121212"),
            schoolResourceKeyId
        );
        await InsertSchoolAsync(documentId, 101, "Beta Academy");

        var before = await GetDocumentStampStateAsync(documentId);

        await DelayForDistinctTimestampsAsync();
        await InsertSchoolExtensionAsync(documentId);

        var after = await GetDocumentStampStateAsync(documentId);

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().Be(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().Be(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_not_stamp_identity_for_content_only_updates_on_identity_propagation_tables()
    {
        var before = await GetDocumentStampStateAsync(_seedData.SchoolDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."School"
            SET "NameOfInstitution" = @nameOfInstitution
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("nameOfInstitution", "Alpha Academy Updated"),
            new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
        );

        var after = await GetDocumentStampStateAsync(_seedData.SchoolDocumentId);

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().Be(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().Be(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_stamp_root_identity_changes_as_both_content_and_identity_updates()
    {
        var before = await GetDocumentStampStateAsync(_seedData.SchoolDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."School"
            SET "SchoolId" = @schoolId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("schoolId", 101),
            new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
        );

        var after = await GetDocumentStampStateAsync(_seedData.SchoolDocumentId);

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().BeGreaterThan(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().BeAfter(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_not_stamp_successful_no_op_root_updates()
    {
        var before = await GetDocumentStampStateAsync(_seedData.StudentDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."Student"
            SET "FirstName" = "FirstName"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", _seedData.StudentDocumentId)
        );

        var after = await GetDocumentStampStateAsync(_seedData.StudentDocumentId);

        after.Should().Be(before);
    }

    [Test]
    public async Task It_should_stamp_indirect_identity_propagation_changes_via_postgresql_cascade()
    {
        var before = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."School"
            SET "SchoolId" = @schoolId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("schoolId", 101),
            new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
        );

        var after = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().BeGreaterThan(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().BeAfter(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_allocate_distinct_content_versions_for_multi_row_root_updates()
    {
        var beforeStudent = await GetDocumentStampStateAsync(_seedData.StudentDocumentId);
        var beforeOtherStudent = await GetDocumentStampStateAsync(_seedData.OtherStudentDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."Student"
            SET "FirstName" = "FirstName" || ' Updated'
            WHERE "DocumentId" IN (@firstDocumentId, @secondDocumentId);
            """,
            new NpgsqlParameter("firstDocumentId", _seedData.StudentDocumentId),
            new NpgsqlParameter("secondDocumentId", _seedData.OtherStudentDocumentId)
        );

        var afterStudent = await GetDocumentStampStateAsync(_seedData.StudentDocumentId);
        var afterOtherStudent = await GetDocumentStampStateAsync(_seedData.OtherStudentDocumentId);

        afterStudent.ContentVersion.Should().BeGreaterThan(beforeStudent.ContentVersion);
        afterOtherStudent.ContentVersion.Should().BeGreaterThan(beforeOtherStudent.ContentVersion);
        afterStudent.ContentVersion.Should().NotBe(afterOtherStudent.ContentVersion);
        afterStudent.IdentityVersion.Should().Be(beforeStudent.IdentityVersion);
        afterOtherStudent.IdentityVersion.Should().Be(beforeOtherStudent.IdentityVersion);
    }

    private async Task<AuthoritativeSampleSmokeSeedData> SeedSmokeRowsAsync()
    {
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var studentEducationOrganizationAssociationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "StudentEducationOrganizationAssociation"
        );
        var addressTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "AddressTypeDescriptor"
        );
        var stateAbbreviationDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "StateAbbreviationDescriptor"
        );
        var termDescriptorResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "TermDescriptor");

        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            schoolResourceKeyId
        );
        await InsertSchoolAsync(schoolDocumentId, 100, "Alpha Academy");
        await InsertSchoolExtensionAsync(schoolDocumentId);

        var schoolExtensionDirectlyOwnedBusCollectionItemId =
            await InsertSchoolExtensionDirectlyOwnedBusAsync(schoolDocumentId, 1);

        var studentDocumentId = await InsertDocumentAsync(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, "10001", "Casey", "Cole");

        var otherStudentDocumentId = await InsertDocumentAsync(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            studentResourceKeyId
        );
        await InsertStudentAsync(otherStudentDocumentId, "10002", "Morgan", "Lane");

        var studentEducationOrganizationAssociationDocumentId = await InsertDocumentAsync(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            studentEducationOrganizationAssociationResourceKeyId
        );
        await InsertStudentEducationOrganizationAssociationAsync(
            studentEducationOrganizationAssociationDocumentId,
            schoolDocumentId,
            100,
            studentDocumentId,
            "10001"
        );

        var otherStudentEducationOrganizationAssociationDocumentId = await InsertDocumentAsync(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            studentEducationOrganizationAssociationResourceKeyId
        );
        await InsertStudentEducationOrganizationAssociationAsync(
            otherStudentEducationOrganizationAssociationDocumentId,
            schoolDocumentId,
            100,
            otherStudentDocumentId,
            "10002"
        );

        var addressTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            addressTypeDescriptorResourceKeyId,
            "Ed-Fi:AddressTypeDescriptor",
            "uri://ed-fi.org/AddressTypeDescriptor#Home",
            "uri://ed-fi.org/AddressTypeDescriptor",
            "Home",
            "Home"
        );
        var stateAbbreviationDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            stateAbbreviationDescriptorResourceKeyId,
            "Ed-Fi:StateAbbreviationDescriptor",
            "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
            "uri://ed-fi.org/StateAbbreviationDescriptor",
            "TX",
            "Texas"
        );
        var termDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            termDescriptorResourceKeyId,
            "Ed-Fi:TermDescriptor",
            "uri://ed-fi.org/TermDescriptor#Fall",
            "uri://ed-fi.org/TermDescriptor",
            "Fall",
            "Fall"
        );
        var alternateTermDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            termDescriptorResourceKeyId,
            "Ed-Fi:TermDescriptor",
            "uri://ed-fi.org/TermDescriptor#Spring",
            "uri://ed-fi.org/TermDescriptor",
            "Spring",
            "Spring"
        );

        var studentEducationOrganizationAssociationAddressCollectionItemId =
            await InsertStudentEducationOrganizationAssociationAddressAsync(
                studentEducationOrganizationAssociationDocumentId,
                1,
                addressTypeDescriptorDocumentId,
                stateAbbreviationDescriptorDocumentId,
                "Austin",
                "78701",
                "100 Congress Ave"
            );

        await InsertStudentEducationOrganizationAssociationExtensionAddressAsync(
            studentEducationOrganizationAssociationAddressCollectionItemId,
            studentEducationOrganizationAssociationDocumentId,
            "Tower A"
        );

        var studentEducationOrganizationAssociationExtensionAddressSchoolDistrictCollectionItemId =
            await InsertStudentEducationOrganizationAssociationExtensionAddressSchoolDistrictAsync(
                studentEducationOrganizationAssociationAddressCollectionItemId,
                studentEducationOrganizationAssociationDocumentId,
                1,
                "District Nine"
            );

        var studentEducationOrganizationAssociationExtensionAddressTermCollectionItemId =
            await InsertStudentEducationOrganizationAssociationExtensionAddressTermAsync(
                studentEducationOrganizationAssociationAddressCollectionItemId,
                studentEducationOrganizationAssociationDocumentId,
                1,
                termDescriptorDocumentId
            );

        return new(
            schoolDocumentId,
            studentDocumentId,
            otherStudentDocumentId,
            studentEducationOrganizationAssociationDocumentId,
            otherStudentEducationOrganizationAssociationDocumentId,
            alternateTermDescriptorDocumentId,
            schoolExtensionDirectlyOwnedBusCollectionItemId,
            studentEducationOrganizationAssociationAddressCollectionItemId,
            studentEducationOrganizationAssociationExtensionAddressSchoolDistrictCollectionItemId,
            studentEducationOrganizationAssociationExtensionAddressTermCollectionItemId
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

    private async Task<long> GetDescriptorDocumentIdAsync(string discriminator, string codeValue)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT "DocumentId"
            FROM "dms"."Descriptor"
            WHERE "Discriminator" = @discriminator
              AND "CodeValue" = @codeValue;
            """,
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("codeValue", codeValue)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
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

    private async Task<long> InsertDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, resourceKeyId);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", shortDescription),
            new NpgsqlParameter("description", shortDescription),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", uri)
        );

        return documentId;
    }

    private async Task InsertSchoolAsync(long documentId, int schoolId, string nameOfInstitution)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."School" ("DocumentId", "NameOfInstitution", "SchoolId")
            VALUES (@documentId, @nameOfInstitution, @schoolId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter("schoolId", schoolId)
        );
    }

    private async Task InsertSchoolExtensionAsync(long documentId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "sample"."SchoolExtension" ("DocumentId")
            VALUES (@documentId);
            """,
            new NpgsqlParameter("documentId", documentId)
        );
    }

    private async Task<long> InsertSchoolExtensionDirectlyOwnedBusAsync(long schoolDocumentId, int ordinal)
    {
        var directlyOwnedBusDocumentIdParameter = new NpgsqlParameter(
            "directlyOwnedBusDocumentId",
            NpgsqlDbType.Bigint
        )
        {
            Value = DBNull.Value,
        };
        var directlyOwnedBusIdParameter = new NpgsqlParameter("directlyOwnedBusId", NpgsqlDbType.Varchar)
        {
            Value = DBNull.Value,
        };

        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "sample"."SchoolExtensionDirectlyOwnedBus" ("Ordinal", "School_DocumentId", "DirectlyOwnedBus_DocumentId", "DirectlyOwnedBus_BusId")
            VALUES (@ordinal, @schoolDocumentId, @directlyOwnedBusDocumentId, @directlyOwnedBusId)
            RETURNING "CollectionItemId";
            """,
            new NpgsqlParameter("ordinal", ordinal),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            directlyOwnedBusDocumentIdParameter,
            directlyOwnedBusIdParameter
        );
    }

    private async Task InsertStudentAsync(
        long documentId,
        string studentUniqueId,
        string firstName,
        string lastSurname
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Student" ("DocumentId", "BirthDate", "FirstName", "LastSurname", "StudentUniqueId")
            VALUES (@documentId, @birthDate, @firstName, @lastSurname, @studentUniqueId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("birthDate", new DateOnly(2010, 1, 1)),
            new NpgsqlParameter("firstName", firstName),
            new NpgsqlParameter("lastSurname", lastSurname),
            new NpgsqlParameter("studentUniqueId", studentUniqueId)
        );
    }

    private async Task InsertStudentEducationOrganizationAssociationAsync(
        long documentId,
        long educationOrganizationDocumentId,
        int educationOrganizationId,
        long studentDocumentId,
        string studentUniqueId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."StudentEducationOrganizationAssociation" (
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "Student_DocumentId",
                "Student_StudentUniqueId"
            )
            VALUES (
                @documentId,
                @educationOrganizationDocumentId,
                @educationOrganizationId,
                @studentDocumentId,
                @studentUniqueId
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("educationOrganizationDocumentId", educationOrganizationDocumentId),
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("studentDocumentId", studentDocumentId),
            new NpgsqlParameter("studentUniqueId", studentUniqueId)
        );
    }

    private async Task<long> InsertStudentEducationOrganizationAssociationAddressAsync(
        long studentEducationOrganizationAssociationDocumentId,
        int ordinal,
        long addressTypeDescriptorDocumentId,
        long stateAbbreviationDescriptorDocumentId,
        string city,
        string postalCode,
        string streetNumberName
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."StudentEducationOrganizationAssociationAddress" (
                "Ordinal",
                "StudentEducationOrganizationAssociation_DocumentId",
                "AddressTypeDescriptor_DescriptorId",
                "StateAbbreviationDescriptor_DescriptorId",
                "City",
                "PostalCode",
                "StreetNumberName"
            )
            VALUES (
                @ordinal,
                @studentEducationOrganizationAssociationDocumentId,
                @addressTypeDescriptorDocumentId,
                @stateAbbreviationDescriptorDocumentId,
                @city,
                @postalCode,
                @streetNumberName
            )
            RETURNING "CollectionItemId";
            """,
            new NpgsqlParameter("ordinal", ordinal),
            new NpgsqlParameter(
                "studentEducationOrganizationAssociationDocumentId",
                studentEducationOrganizationAssociationDocumentId
            ),
            new NpgsqlParameter("addressTypeDescriptorDocumentId", addressTypeDescriptorDocumentId),
            new NpgsqlParameter(
                "stateAbbreviationDescriptorDocumentId",
                stateAbbreviationDescriptorDocumentId
            ),
            new NpgsqlParameter("city", city),
            new NpgsqlParameter("postalCode", postalCode),
            new NpgsqlParameter("streetNumberName", streetNumberName)
        );
    }

    private async Task InsertStudentEducationOrganizationAssociationExtensionAddressAsync(
        long baseCollectionItemId,
        long studentEducationOrganizationAssociationDocumentId,
        string complex
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "sample"."StudentEducationOrganizationAssociationExtensionAddress" (
                "BaseCollectionItemId",
                "StudentEducationOrganizationAssociation_DocumentId",
                "Complex"
            )
            VALUES (
                @baseCollectionItemId,
                @studentEducationOrganizationAssociationDocumentId,
                @complex
            );
            """,
            new NpgsqlParameter("baseCollectionItemId", baseCollectionItemId),
            new NpgsqlParameter(
                "studentEducationOrganizationAssociationDocumentId",
                studentEducationOrganizationAssociationDocumentId
            ),
            new NpgsqlParameter("complex", complex)
        );
    }

    private async Task<long> InsertStudentEducationOrganizationAssociationExtensionAddressSchoolDistrictAsync(
        long baseCollectionItemId,
        long studentEducationOrganizationAssociationDocumentId,
        int ordinal,
        string schoolDistrict
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            $"""
            INSERT INTO "sample"."{_studentEducationOrganizationAssociationExtensionAddressSchoolDistrictTable.Table.Name}" (
                "BaseCollectionItemId",
                "Ordinal",
                "StudentEducationOrganizationAssociation_DocumentId",
                "SchoolDistrict"
            )
            VALUES (
                @baseCollectionItemId,
                @ordinal,
                @studentEducationOrganizationAssociationDocumentId,
                @schoolDistrict
            )
            RETURNING "CollectionItemId";
            """,
            new NpgsqlParameter("baseCollectionItemId", baseCollectionItemId),
            new NpgsqlParameter(
                "studentEducationOrganizationAssociationDocumentId",
                studentEducationOrganizationAssociationDocumentId
            ),
            new NpgsqlParameter("ordinal", ordinal),
            new NpgsqlParameter("schoolDistrict", schoolDistrict)
        );
    }

    private async Task<long> InsertStudentEducationOrganizationAssociationExtensionAddressTermAsync(
        long baseCollectionItemId,
        long studentEducationOrganizationAssociationDocumentId,
        int ordinal,
        long termDescriptorDocumentId
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "sample"."StudentEducationOrganizationAssociationExtensionAddressTerm" (
                "BaseCollectionItemId",
                "Ordinal",
                "StudentEducationOrganizationAssociation_DocumentId",
                "TermDescriptor_DescriptorId"
            )
            VALUES (
                @baseCollectionItemId,
                @ordinal,
                @studentEducationOrganizationAssociationDocumentId,
                @termDescriptorDocumentId
            )
            RETURNING "CollectionItemId";
            """,
            new NpgsqlParameter("baseCollectionItemId", baseCollectionItemId),
            new NpgsqlParameter(
                "studentEducationOrganizationAssociationDocumentId",
                studentEducationOrganizationAssociationDocumentId
            ),
            new NpgsqlParameter("ordinal", ordinal),
            new NpgsqlParameter("termDescriptorDocumentId", termDescriptorDocumentId)
        );
    }

    private async Task<long> CountRowsAsync(string sql, params NpgsqlParameter[] parameters)
    {
        return await _database.ExecuteScalarAsync<long>(sql, parameters);
    }

    private async Task<DocumentStampState> GetDocumentStampStateAsync(long documentId)
    {
        var row = (
            await _database.QueryRowsAsync(
                """
                SELECT
                    "ContentVersion",
                    "IdentityVersion",
                    "ContentLastModifiedAt",
                    "IdentityLastModifiedAt"
                FROM "dms"."Document"
                WHERE "DocumentId" = @documentId;
                """,
                new NpgsqlParameter("documentId", documentId)
            )
        ).Single();

        return new(
            Convert.ToInt64(row["ContentVersion"], CultureInfo.InvariantCulture),
            Convert.ToInt64(row["IdentityVersion"], CultureInfo.InvariantCulture),
            ReadDateTimeOffset(row["ContentLastModifiedAt"]),
            ReadDateTimeOffset(row["IdentityLastModifiedAt"])
        );
    }

    private async Task DelayForDistinctTimestampsAsync()
    {
        await _database.ExecuteNonQueryAsync("""SELECT pg_sleep(0.02);""");
    }

    private static DateTimeOffset ReadDateTimeOffset(object? value)
    {
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime
            ),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Unsupported timestamp value type '{value?.GetType().FullName ?? "<null>"}'."
            ),
        };
    }

    private static async Task AssertForeignKeyViolationAsync(Func<Task> act)
    {
        var exception = (await act.Should().ThrowAsync<PostgresException>()).Which;
        exception.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }
}
