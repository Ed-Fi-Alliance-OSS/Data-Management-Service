// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Be.Vlaanderen.Basisregisters.Generators.Guid;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Npgsql;
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

internal sealed record TrackedChangeKeyChangeAssociationSeedData(
    long AssociationDocumentId,
    Guid AssociationDocumentUuid,
    long StudentDocumentId,
    string StudentUniqueId,
    long OriginalEducationOrganizationId,
    long ReplacementSchoolDocumentId,
    long ReplacementEducationOrganizationId
);

[TestFixture]
public class Given_A_Postgresql_Generated_Ddl_Apply_Harness_With_The_Authoritative_DS_Sample_Fixture_For_Smoke_Coverage
{
    private static readonly string FixtureRelativePath = Path.Combine(
        "src",
        "dms",
        "backend",
        "Fixtures",
        "authoritative",
        "sample"
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private AuthoritativeSampleSmokeSeedData _seedData = null!;
    private DbTableModel _schoolTable = null!;
    private DbTableModel _studentTable = null!;
    private DbTableModel _busTable = null!;
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
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromFixtureDirectory(
            ResolveFixtureDirectory(),
            strict: true
        );
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        await InstallReferentialIdentityAuditAsync();

        _schoolTable = PostgresqlGeneratedDdlModelLookup.RequireTable(_fixture.ModelSet, "edfi", "School");
        _studentTable = PostgresqlGeneratedDdlModelLookup.RequireTable(_fixture.ModelSet, "edfi", "Student");
        _busTable = PostgresqlGeneratedDdlModelLookup.RequireTable(_fixture.ModelSet, "sample", "Bus");
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
        var beforeMirror = await GetRootMirrorStampStateAsync(
            _studentEducationOrganizationAssociationTable,
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );
        AssertMirrorContentMatchesDocument(beforeMirror, before);

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
        var afterMirror = await GetRootMirrorStampStateAsync(
            _studentEducationOrganizationAssociationTable,
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        after.Should().Be(before);
        afterMirror.Should().Be(beforeMirror);
        AssertMirrorContentMatchesDocument(afterMirror, after);
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
        var beforeMirror = await GetRootMirrorStampStateAsync(
            _studentEducationOrganizationAssociationTable,
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );
        AssertMirrorContentMatchesDocument(beforeMirror, before);

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
        var afterMirror = await GetRootMirrorStampStateAsync(
            _studentEducationOrganizationAssociationTable,
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        after.Should().Be(before);
        afterMirror.Should().Be(beforeMirror);
        AssertMirrorContentMatchesDocument(afterMirror, after);
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
        var beforeMirror = await GetRootMirrorStampStateAsync(_studentTable, _seedData.StudentDocumentId);
        AssertMirrorContentMatchesDocument(beforeMirror, before);

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
        var afterMirror = await GetRootMirrorStampStateAsync(_studentTable, _seedData.StudentDocumentId);

        after.Should().Be(before);
        afterMirror.Should().Be(beforeMirror);
        AssertMirrorContentMatchesDocument(afterMirror, after);
    }

    [Test]
    public async Task It_should_not_stamp_same_value_identity_column_root_updates()
    {
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var educationOrganizationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "EducationOrganization"
        );
        var expectedRiRows = SortReferentialIdentityRows(
            new[]
            {
                new ReferentialIdentityRow(
                    ComputeReferentialId("Ed-Fi", "School", ("$.schoolId", "100")),
                    _seedData.SchoolDocumentId,
                    schoolResourceKeyId
                ),
                new ReferentialIdentityRow(
                    ComputeReferentialId(
                        "Ed-Fi",
                        "EducationOrganization",
                        ("$.educationOrganizationId", "100")
                    ),
                    _seedData.SchoolDocumentId,
                    educationOrganizationResourceKeyId
                ),
            }
        );

        var beforeStamps = await GetDocumentStampStateAsync(_seedData.SchoolDocumentId);
        var beforeRiRows = await GetReferentialIdentityRowsForDocumentAsync(_seedData.SchoolDocumentId);
        beforeRiRows.Should().Equal(expectedRiRows);

        await TruncateReferentialIdentityAuditAsync();
        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."School"
            SET "SchoolId" = "SchoolId"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
        );

        var afterStamps = await GetDocumentStampStateAsync(_seedData.SchoolDocumentId);
        var afterRiRows = await GetReferentialIdentityRowsForDocumentAsync(_seedData.SchoolDocumentId);
        var auditOps = await CountReferentialIdentityAuditOpsForDocumentAsync(_seedData.SchoolDocumentId);

        afterStamps.Should().Be(beforeStamps);
        afterRiRows.Should().Equal(beforeRiRows);
        auditOps.Should().Be(0);
    }

    [Test]
    public async Task It_should_not_stamp_identity_when_scalar_identity_column_is_self_assigned_alongside_content_change()
    {
        // The pure same-value test above is filtered out by the trigger's outer no-op
        // short-circuit before the inner identity-only gate runs. This test sends a
        // content change AND a same-value self-assignment of the identity column in
        // one UPDATE — the content change defeats the outer guard, so the inner
        // IS DISTINCT FROM gate over identity-projection columns is what must keep
        // IdentityVersion and dms.ReferentialIdentity untouched.
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var educationOrganizationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "EducationOrganization"
        );
        var expectedRiRows = SortReferentialIdentityRows(
            new[]
            {
                new ReferentialIdentityRow(
                    ComputeReferentialId("Ed-Fi", "School", ("$.schoolId", "100")),
                    _seedData.SchoolDocumentId,
                    schoolResourceKeyId
                ),
                new ReferentialIdentityRow(
                    ComputeReferentialId(
                        "Ed-Fi",
                        "EducationOrganization",
                        ("$.educationOrganizationId", "100")
                    ),
                    _seedData.SchoolDocumentId,
                    educationOrganizationResourceKeyId
                ),
            }
        );

        var beforeStamps = await GetDocumentStampStateAsync(_seedData.SchoolDocumentId);
        var beforeRiRows = await GetReferentialIdentityRowsForDocumentAsync(_seedData.SchoolDocumentId);
        beforeRiRows.Should().Equal(expectedRiRows);

        await TruncateReferentialIdentityAuditAsync();
        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."School"
            SET "NameOfInstitution" = @nameOfInstitution,
                "SchoolId" = "SchoolId"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("nameOfInstitution", "Alpha Academy Renamed"),
            new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
        );

        var afterStamps = await GetDocumentStampStateAsync(_seedData.SchoolDocumentId);
        var afterRiRows = await GetReferentialIdentityRowsForDocumentAsync(_seedData.SchoolDocumentId);
        var auditOps = await CountReferentialIdentityAuditOpsForDocumentAsync(_seedData.SchoolDocumentId);

        afterStamps.ContentVersion.Should().BeGreaterThan(beforeStamps.ContentVersion);
        afterStamps.ContentLastModifiedAt.Should().BeAfter(beforeStamps.ContentLastModifiedAt);
        afterStamps.IdentityVersion.Should().Be(beforeStamps.IdentityVersion);
        afterStamps.IdentityLastModifiedAt.Should().Be(beforeStamps.IdentityLastModifiedAt);
        afterRiRows.Should().Equal(beforeRiRows);
        auditOps.Should().Be(0);
    }

    [Test]
    public async Task It_should_stamp_indirect_identity_propagation_changes_via_postgresql_cascade()
    {
        var before = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        var schoolDocumentUuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var associationDocumentUuid = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var schoolTrackedBefore = await CountTrackedChangeRowsAsync(
            "tracked_changes_edfi",
            "School",
            schoolDocumentUuid
        );
        var associationTrackedBefore = await CountTrackedChangeRowsAsync(
            "tracked_changes_edfi",
            "StudentEducationOrganizationAssociation",
            associationDocumentUuid
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
        await AssertRootMirrorMatchesDocumentAsync(_schoolTable, _seedData.SchoolDocumentId);
        await AssertRootMirrorMatchesDocumentAsync(
            _studentEducationOrganizationAssociationTable,
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        // School is concrete-abstract: its identity change bumps stamps but must not
        // insert a key-change row (tombstones only). The FK-cascaded SEOA identity
        // change inserts exactly one key-change row with three-way linkage.
        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "School", schoolDocumentUuid))
            .Should()
            .Be(schoolTrackedBefore);
        (
            await CountTrackedChangeRowsAsync(
                "tracked_changes_edfi",
                "StudentEducationOrganizationAssociation",
                associationDocumentUuid
            )
        )
            .Should()
            .Be(associationTrackedBefore + 1);

        var associationTrackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "StudentEducationOrganizationAssociation",
            associationDocumentUuid
        );
        Convert
            .ToInt64(
                associationTrackedRow["OldEducationOrganization_EducationOrganizationId"],
                CultureInfo.InvariantCulture
            )
            .Should()
            .Be(100);
        Convert
            .ToInt64(
                associationTrackedRow["NewEducationOrganization_EducationOrganizationId"],
                CultureInfo.InvariantCulture
            )
            .Should()
            .Be(101);
        associationTrackedRow["OldStudent_StudentUniqueId"].Should().Be("10001");
        associationTrackedRow["NewStudent_StudentUniqueId"].Should().Be("10001");
        Convert
            .ToInt64(associationTrackedRow["ChangeVersion"], CultureInfo.InvariantCulture)
            .Should()
            .Be(after.ContentVersion);
    }

    [Test]
    public async Task It_should_not_stamp_same_value_propagated_identity_reference_updates()
    {
        // A same-value parent UPDATE cannot reach the dependent: TF_TR_School_AbstractIdentity
        // gates the EducationOrganizationIdentity write on OLD."SchoolId" IS DISTINCT FROM NEW,
        // so the FK ON UPDATE CASCADE never fires. Drive the dependent's stamp trigger directly
        // with a self-assignment on its propagated identity-source reference column to exercise
        // the trigger's outer no-op short-circuit (IS DISTINCT FROM over identity-projection
        // columns) against both stamp bumps and redundant RI rewrites. The inner identity-only
        // gate is covered by It_should_stamp_indirect_identity_propagation_changes_via_postgresql_cascade.
        var seoaResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "StudentEducationOrganizationAssociation"
        );
        var expectedRiRows = SortReferentialIdentityRows(
            new[]
            {
                new ReferentialIdentityRow(
                    ComputeReferentialId(
                        "Ed-Fi",
                        "StudentEducationOrganizationAssociation",
                        ("$.educationOrganizationReference.educationOrganizationId", "100"),
                        ("$.studentReference.studentUniqueId", "10001")
                    ),
                    _seedData.StudentEducationOrganizationAssociationDocumentId,
                    seoaResourceKeyId
                ),
            }
        );

        var beforeStamps = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );
        var beforeRiRows = await GetReferentialIdentityRowsForDocumentAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );
        beforeRiRows.Should().Equal(expectedRiRows);

        await TruncateReferentialIdentityAuditAsync();
        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."StudentEducationOrganizationAssociation"
            SET "EducationOrganization_EducationOrganizationId" = "EducationOrganization_EducationOrganizationId"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", _seedData.StudentEducationOrganizationAssociationDocumentId)
        );

        var afterStamps = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );
        var afterRiRows = await GetReferentialIdentityRowsForDocumentAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );
        var auditOps = await CountReferentialIdentityAuditOpsForDocumentAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        afterStamps.Should().Be(beforeStamps);
        afterRiRows.Should().Equal(beforeRiRows);
        auditOps.Should().Be(0);
    }

    [Test]
    public async Task It_should_not_stamp_identity_when_propagated_identity_reference_is_self_assigned_alongside_content_change()
    {
        // Mixed-write counterpart for the propagated identity-source reference column:
        // a non-identity content column changes while the propagated identity column is
        // self-assigned to the same value. The content change defeats the outer no-op
        // guard, so the IS DISTINCT FROM gate over identity-projection columns is the
        // sole protection against false IdentityVersion bumps and redundant RI rewrites.
        var seoaResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "StudentEducationOrganizationAssociation"
        );
        var expectedRiRows = SortReferentialIdentityRows(
            new[]
            {
                new ReferentialIdentityRow(
                    ComputeReferentialId(
                        "Ed-Fi",
                        "StudentEducationOrganizationAssociation",
                        ("$.educationOrganizationReference.educationOrganizationId", "100"),
                        ("$.studentReference.studentUniqueId", "10001")
                    ),
                    _seedData.StudentEducationOrganizationAssociationDocumentId,
                    seoaResourceKeyId
                ),
            }
        );

        var beforeStamps = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );
        var beforeRiRows = await GetReferentialIdentityRowsForDocumentAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );
        beforeRiRows.Should().Equal(expectedRiRows);

        await TruncateReferentialIdentityAuditAsync();
        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."StudentEducationOrganizationAssociation"
            SET "LoginId" = @loginId,
                "EducationOrganization_EducationOrganizationId" = "EducationOrganization_EducationOrganizationId"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("loginId", "student-login-001"),
            new NpgsqlParameter("documentId", _seedData.StudentEducationOrganizationAssociationDocumentId)
        );

        var afterStamps = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );
        var afterRiRows = await GetReferentialIdentityRowsForDocumentAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );
        var auditOps = await CountReferentialIdentityAuditOpsForDocumentAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        afterStamps.ContentVersion.Should().BeGreaterThan(beforeStamps.ContentVersion);
        afterStamps.ContentLastModifiedAt.Should().BeAfter(beforeStamps.ContentLastModifiedAt);
        afterStamps.IdentityVersion.Should().Be(beforeStamps.IdentityVersion);
        afterStamps.IdentityLastModifiedAt.Should().Be(beforeStamps.IdentityLastModifiedAt);
        afterRiRows.Should().Equal(beforeRiRows);
        auditOps.Should().Be(0);
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

    [Test]
    public async Task It_should_keep_resource_mirrors_in_lock_step_for_representative_write_paths()
    {
        await AssertRootMirrorMatchesDocumentAsync(_studentTable, _seedData.StudentDocumentId);
        await AssertRootMirrorMatchesDocumentAsync(
            _studentEducationOrganizationAssociationTable,
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        var studentBefore = await GetDocumentStampStateAsync(_seedData.StudentDocumentId);

        await DelayForDistinctTimestampsAsync();
        var rootRowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."Student"
            SET "FirstName" = @firstName
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("firstName", "Jordan-Mirror"),
            new NpgsqlParameter("documentId", _seedData.StudentDocumentId)
        );
        rootRowsAffected.Should().Be(1);

        var studentAfter = await GetDocumentStampStateAsync(_seedData.StudentDocumentId);
        studentAfter.ContentVersion.Should().BeGreaterThan(studentBefore.ContentVersion);
        await AssertRootMirrorMatchesDocumentAsync(_studentTable, _seedData.StudentDocumentId);

        var associationBefore = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        await DelayForDistinctTimestampsAsync();
        var childRowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."StudentEducationOrganizationAssociationAddress"
            SET "StreetNumberName" = @streetNumberName
            WHERE "CollectionItemId" = @collectionItemId
              AND "StudentEducationOrganizationAssociation_DocumentId" = @documentId;
            """,
            new NpgsqlParameter("streetNumberName", "300 Mirror Lane"),
            new NpgsqlParameter(
                "collectionItemId",
                _seedData.StudentEducationOrganizationAssociationAddressCollectionItemId
            ),
            new NpgsqlParameter("documentId", _seedData.StudentEducationOrganizationAssociationDocumentId)
        );
        childRowsAffected.Should().Be(1);

        var associationAfter = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );
        associationAfter.ContentVersion.Should().BeGreaterThan(associationBefore.ContentVersion);
        await AssertRootMirrorMatchesDocumentAsync(
            _studentEducationOrganizationAssociationTable,
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        associationBefore = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        await DelayForDistinctTimestampsAsync();
        var extensionRowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "sample"."StudentEducationOrganizationAssociationExtensionAddress"
            SET "Complex" = @complex
            WHERE "BaseCollectionItemId" = @baseCollectionItemId
              AND "StudentEducationOrganizationAssociation_DocumentId" = @documentId;
            """,
            new NpgsqlParameter("complex", "Mirror-Complex"),
            new NpgsqlParameter(
                "baseCollectionItemId",
                _seedData.StudentEducationOrganizationAssociationAddressCollectionItemId
            ),
            new NpgsqlParameter("documentId", _seedData.StudentEducationOrganizationAssociationDocumentId)
        );
        extensionRowsAffected.Should().Be(1);

        associationAfter = await GetDocumentStampStateAsync(
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );
        associationAfter.ContentVersion.Should().BeGreaterThan(associationBefore.ContentVersion);
        await AssertRootMirrorMatchesDocumentAsync(
            _studentEducationOrganizationAssociationTable,
            _seedData.StudentEducationOrganizationAssociationDocumentId
        );

        studentBefore = await GetDocumentStampStateAsync(_seedData.StudentDocumentId);
        var otherStudentBefore = await GetDocumentStampStateAsync(_seedData.OtherStudentDocumentId);

        await DelayForDistinctTimestampsAsync();
        var multiRowRowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."Student"
            SET "LastSurname" = 'Mirror-Multi'
            WHERE "DocumentId" IN (@studentDocumentId, @otherStudentDocumentId);
            """,
            new NpgsqlParameter("studentDocumentId", _seedData.StudentDocumentId),
            new NpgsqlParameter("otherStudentDocumentId", _seedData.OtherStudentDocumentId)
        );
        multiRowRowsAffected.Should().Be(2);

        studentAfter = await GetDocumentStampStateAsync(_seedData.StudentDocumentId);
        var otherStudentAfter = await GetDocumentStampStateAsync(_seedData.OtherStudentDocumentId);
        studentAfter.ContentVersion.Should().BeGreaterThan(studentBefore.ContentVersion);
        otherStudentAfter.ContentVersion.Should().BeGreaterThan(otherStudentBefore.ContentVersion);
        studentAfter.ContentVersion.Should().NotBe(otherStudentAfter.ContentVersion);
        await AssertRootMirrorMatchesDocumentAsync(_studentTable, _seedData.StudentDocumentId);
        await AssertRootMirrorMatchesDocumentAsync(_studentTable, _seedData.OtherStudentDocumentId);
    }

    [Test]
    public async Task It_should_keep_extension_project_root_resource_mirrors_in_lock_step()
    {
        var busResourceKeyId = await GetResourceKeyIdAsync("Sample", "Bus");
        var busDocumentId = await InsertDocumentAsync(
            Guid.Parse("abababab-abab-abab-abab-abababababab"),
            busResourceKeyId
        );
        var beforeInsert = await GetDocumentStampStateAsync(busDocumentId);
        var beforeInsertMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var initialBusId = $"BUS-{busDocumentId}-001";
        var updatedBusId = $"BUS-{busDocumentId}-002";

        await DelayForDistinctTimestampsAsync();
        await InsertBusAsync(busDocumentId, initialBusId);

        var afterInsert = await GetDocumentStampStateAsync(busDocumentId);
        var afterInsertMaxChangeVersion = await ReadMaxChangeVersionAsync();
        afterInsert.Should().Be(beforeInsert);
        afterInsertMaxChangeVersion.Should().Be(beforeInsertMaxChangeVersion);
        await AssertRootMirrorMatchesDocumentAsync(_busTable, busDocumentId);

        await DelayForDistinctTimestampsAsync();
        var updateRowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "sample"."Bus"
            SET "BusId" = @busId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("busId", updatedBusId),
            new NpgsqlParameter("documentId", busDocumentId)
        );
        updateRowsAffected.Should().Be(1);

        var afterUpdate = await GetDocumentStampStateAsync(busDocumentId);
        afterUpdate.ContentVersion.Should().BeGreaterThan(afterInsert.ContentVersion);
        afterUpdate.IdentityVersion.Should().BeGreaterThan(afterInsert.IdentityVersion);
        await AssertRootMirrorMatchesDocumentAsync(_busTable, busDocumentId);

        var beforeNoOpMirror = await GetRootMirrorStampStateAsync(_busTable, busDocumentId);

        await DelayForDistinctTimestampsAsync();
        var noOpRowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "sample"."Bus"
            SET "BusId" = "BusId"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", busDocumentId)
        );
        noOpRowsAffected.Should().Be(1);

        var afterNoOp = await GetDocumentStampStateAsync(busDocumentId);
        var afterNoOpMirror = await GetRootMirrorStampStateAsync(_busTable, busDocumentId);
        afterNoOp.Should().Be(afterUpdate);
        afterNoOpMirror.Should().Be(beforeNoOpMirror);
        AssertMirrorContentMatchesDocument(afterNoOpMirror, afterNoOp);
    }

    [Test]
    public async Task It_should_not_stamp_document_or_track_changes_for_direct_root_stamp_only_updates()
    {
        var busResourceKeyId = await GetResourceKeyIdAsync("Sample", "Bus");
        var busDocumentId = await InsertDocumentAsync(
            Guid.Parse("bcbcbcbc-bcbc-bcbc-bcbc-bcbcbcbcbcbc"),
            busResourceKeyId
        );

        await InsertBusAsync(busDocumentId, $"BUS-{busDocumentId}-STAMP-ONLY");

        var beforeDocument = await GetDocumentStampStateAsync(busDocumentId);
        var beforeMirror = await GetRootMirrorStampStateAsync(_busTable, busDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var trackedChangeRowsBefore = await CountRowsAsync(
            """SELECT COUNT(*) FROM "tracked_changes_sample"."Bus";"""
        );
        AssertMirrorContentMatchesDocument(beforeMirror, beforeDocument);

        await DelayForDistinctTimestampsAsync();
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "sample"."Bus"
            SET "ContentVersion" = "ContentVersion" + 1
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", busDocumentId)
        );
        rowsAffected.Should().Be(1);

        var afterDocument = await GetDocumentStampStateAsync(busDocumentId);
        var afterMirror = await GetRootMirrorStampStateAsync(_busTable, busDocumentId);
        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var trackedChangeRowsAfter = await CountRowsAsync(
            """SELECT COUNT(*) FROM "tracked_changes_sample"."Bus";"""
        );

        afterDocument.Should().Be(beforeDocument);
        afterMaxChangeVersion.Should().Be(beforeMaxChangeVersion);
        trackedChangeRowsAfter.Should().Be(trackedChangeRowsBefore);
        afterMirror.ContentVersion.Should().Be(beforeMirror.ContentVersion + 1);
        afterMirror.ContentLastModifiedAt.Should().Be(beforeMirror.ContentLastModifiedAt);
    }

    // GUID allocation convention for tracked-change tests:
    //   c* seeds  → SeedKeyChangeStudentEducationOrganizationAssociationAsync (key-change)
    //   d1-d4 seeds → It_should_insert_one_key_change_row_per_row_in_a_multi_row_update
    //   d5-d8 seeds → It_should_insert_key_change_rows_only_for_identity_changed_rows_in_a_mixed_workset_update
    //   e1-e3 seeds → It_should_use_canonical_columns_for_key_unified_paths
    //   e4 seed   → It_should_project_old_and_new_descriptor_values_from_their_own_images_on_key_change (SchoolYearType)
    //   e5 seed   → It_should_project_old_and_new_descriptor_values_from_their_own_images_on_key_change (original descriptor)
    //   e6 seed   → It_should_project_old_and_new_descriptor_values_from_their_own_images_on_key_change (replacement descriptor)
    //   e7 seed   → It_should_project_old_and_new_descriptor_values_from_their_own_images_on_key_change (GradingPeriod)
    //   b5 seed   → It_should_project_a_null_to_value_transition_in_the_key_change_row (Assessment)
    //   b6 seed   → It_should_project_a_null_to_value_transition_in_the_key_change_row (Student)
    //   b7 seed   → It_should_project_a_null_to_value_transition_in_the_key_change_row (StudentAssessment)
    //   f* seeds  → tombstone tests (see #region Tracked-change tombstones)
    // Per-test ResetAsync wipes all transient rows before each test, so collisions with
    // the permanent seeds planted in SeedSmokeRowsAsync (1*/2*/…/9* and the full-byte
    // a*–f* patterns such as cccccccc-…) are impossible.
    #region Tracked-change key-change rows

    [Test]
    public async Task It_should_insert_a_key_change_row_with_three_way_linkage_on_identity_update()
    {
        var seed = await SeedKeyChangeStudentEducationOrganizationAssociationAsync();

        await DelayForDistinctTimestampsAsync();
        await RepointAssociationEducationOrganizationAsync(seed);

        var afterDocument = await GetDocumentStampStateAsync(seed.AssociationDocumentId);
        var afterMirror = await GetRootMirrorStampStateAsync(
            _studentEducationOrganizationAssociationTable,
            seed.AssociationDocumentId
        );

        (
            await CountTrackedChangeRowsAsync(
                "tracked_changes_edfi",
                "StudentEducationOrganizationAssociation",
                seed.AssociationDocumentUuid
            )
        )
            .Should()
            .Be(1);

        var trackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "StudentEducationOrganizationAssociation",
            seed.AssociationDocumentUuid
        );

        Convert
            .ToInt64(trackedRow["ChangeVersion"], CultureInfo.InvariantCulture)
            .Should()
            .Be(afterDocument.ContentVersion);
        afterMirror.ContentVersion.Should().Be(afterDocument.ContentVersion);
        trackedRow["Id"].Should().Be(seed.AssociationDocumentUuid);
        Convert
            .ToInt64(
                trackedRow["OldEducationOrganization_EducationOrganizationId"],
                CultureInfo.InvariantCulture
            )
            .Should()
            .Be(seed.OriginalEducationOrganizationId);
        Convert
            .ToInt64(
                trackedRow["NewEducationOrganization_EducationOrganizationId"],
                CultureInfo.InvariantCulture
            )
            .Should()
            .Be(seed.ReplacementEducationOrganizationId);
        trackedRow["OldStudent_StudentUniqueId"].Should().Be(seed.StudentUniqueId);
        trackedRow["NewStudent_StudentUniqueId"].Should().Be(seed.StudentUniqueId);
    }

    [Test]
    public async Task It_should_store_the_person_document_id_on_key_change_rows()
    {
        var seed = await SeedKeyChangeStudentEducationOrganizationAssociationAsync();

        await DelayForDistinctTimestampsAsync();
        await RepointAssociationEducationOrganizationAsync(seed);

        (
            await CountTrackedChangeRowsAsync(
                "tracked_changes_edfi",
                "StudentEducationOrganizationAssociation",
                seed.AssociationDocumentUuid
            )
        )
            .Should()
            .Be(1);

        var trackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "StudentEducationOrganizationAssociation",
            seed.AssociationDocumentUuid
        );

        Convert
            .ToInt64(trackedRow["OldStudent_DocumentId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(seed.StudentDocumentId);
        Convert
            .ToInt64(trackedRow["NewStudent_DocumentId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(seed.StudentDocumentId);
    }

    [Test]
    public async Task It_should_not_insert_tracked_rows_for_non_identity_updates()
    {
        var seed = await SeedKeyChangeStudentEducationOrganizationAssociationAsync();
        var before = await GetDocumentStampStateAsync(seed.AssociationDocumentId);

        await DelayForDistinctTimestampsAsync();
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."StudentEducationOrganizationAssociation"
            SET "LoginId" = @loginId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("loginId", "key-change-login-001"),
            new NpgsqlParameter("documentId", seed.AssociationDocumentId)
        );
        rowsAffected.Should().Be(1);

        var after = await GetDocumentStampStateAsync(seed.AssociationDocumentId);

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.IdentityVersion.Should().Be(before.IdentityVersion);
        (
            await CountTrackedChangeRowsAsync(
                "tracked_changes_edfi",
                "StudentEducationOrganizationAssociation",
                seed.AssociationDocumentUuid
            )
        )
            .Should()
            .Be(0);
    }

    [Test]
    public async Task It_should_insert_one_key_change_row_per_row_in_a_multi_row_update()
    {
        const int SchoolYear = 2025;
        const string GradingPeriodDescriptorNamespace = "uri://ed-fi.org/GradingPeriodDescriptor";
        const string GradingPeriodDescriptorCodeValue = "FirstSixWeeks";

        var schoolYearTypeResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var gradingPeriodResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "GradingPeriod");
        var gradingPeriodDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "GradingPeriodDescriptor"
        );

        var schoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("d1d1d1d1-d1d1-d1d1-d1d1-d1d1d1d1d1d1"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(schoolYearTypeDocumentId, SchoolYear, "2024-2025");

        // GradingPeriod pairs School_SchoolId with School_DocumentId in its FK to the
        // EducationOrganizationIdentity projection, so read the seeded school's id back
        // instead of hard-coding the seed literal.
        var seededSchoolId = await _database.ExecuteScalarAsync<long>(
            """SELECT "SchoolId" FROM "edfi"."School" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
        );

        var gradingPeriodDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("d2d2d2d2-d2d2-d2d2-d2d2-d2d2d2d2d2d2"),
            gradingPeriodDescriptorResourceKeyId,
            "Ed-Fi:GradingPeriodDescriptor",
            $"{GradingPeriodDescriptorNamespace}#{GradingPeriodDescriptorCodeValue}",
            GradingPeriodDescriptorNamespace,
            GradingPeriodDescriptorCodeValue,
            "First Six Weeks"
        );

        var firstGradingPeriodDocumentUuid = Guid.Parse("d3d3d3d3-d3d3-d3d3-d3d3-d3d3d3d3d3d3");
        var firstGradingPeriodDocumentId = await InsertDocumentAsync(
            firstGradingPeriodDocumentUuid,
            gradingPeriodResourceKeyId
        );
        await InsertGradingPeriodAsync(
            firstGradingPeriodDocumentId,
            schoolYearTypeDocumentId,
            SchoolYear,
            _seedData.SchoolDocumentId,
            seededSchoolId,
            gradingPeriodDescriptorDocumentId,
            "First Grading Period"
        );

        var secondGradingPeriodDocumentUuid = Guid.Parse("d4d4d4d4-d4d4-d4d4-d4d4-d4d4d4d4d4d4");
        var secondGradingPeriodDocumentId = await InsertDocumentAsync(
            secondGradingPeriodDocumentUuid,
            gradingPeriodResourceKeyId
        );
        await InsertGradingPeriodAsync(
            secondGradingPeriodDocumentId,
            schoolYearTypeDocumentId,
            SchoolYear,
            _seedData.SchoolDocumentId,
            seededSchoolId,
            gradingPeriodDescriptorDocumentId,
            "Second Grading Period"
        );

        await DelayForDistinctTimestampsAsync();
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."GradingPeriod"
            SET "GradingPeriodName" = "GradingPeriodName" || '-renamed'
            WHERE "DocumentId" IN (@firstDocumentId, @secondDocumentId);
            """,
            new NpgsqlParameter("firstDocumentId", firstGradingPeriodDocumentId),
            new NpgsqlParameter("secondDocumentId", secondGradingPeriodDocumentId)
        );
        rowsAffected.Should().Be(2);

        var firstAfter = await GetDocumentStampStateAsync(firstGradingPeriodDocumentId);
        var secondAfter = await GetDocumentStampStateAsync(secondGradingPeriodDocumentId);
        firstAfter.ContentVersion.Should().NotBe(secondAfter.ContentVersion);

        (
            await CountTrackedChangeRowsAsync(
                "tracked_changes_edfi",
                "GradingPeriod",
                firstGradingPeriodDocumentUuid
            )
        )
            .Should()
            .Be(1);
        (
            await CountTrackedChangeRowsAsync(
                "tracked_changes_edfi",
                "GradingPeriod",
                secondGradingPeriodDocumentUuid
            )
        )
            .Should()
            .Be(1);

        var firstTrackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "GradingPeriod",
            firstGradingPeriodDocumentUuid
        );
        var secondTrackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "GradingPeriod",
            secondGradingPeriodDocumentUuid
        );

        Convert
            .ToInt64(firstTrackedRow["ChangeVersion"], CultureInfo.InvariantCulture)
            .Should()
            .Be(firstAfter.ContentVersion);
        Convert
            .ToInt64(secondTrackedRow["ChangeVersion"], CultureInfo.InvariantCulture)
            .Should()
            .Be(secondAfter.ContentVersion);

        firstTrackedRow["OldGradingPeriodName"].Should().Be("First Grading Period");
        firstTrackedRow["NewGradingPeriodName"].Should().Be("First Grading Period-renamed");
        secondTrackedRow["OldGradingPeriodName"].Should().Be("Second Grading Period");
        secondTrackedRow["NewGradingPeriodName"].Should().Be("Second Grading Period-renamed");

        foreach (var trackedRow in new[] { firstTrackedRow, secondTrackedRow })
        {
            trackedRow["OldGradingPeriodDescriptor_Namespace"].Should().Be(GradingPeriodDescriptorNamespace);
            trackedRow["NewGradingPeriodDescriptor_Namespace"].Should().Be(GradingPeriodDescriptorNamespace);
            trackedRow["OldGradingPeriodDescriptor_CodeValue"].Should().Be(GradingPeriodDescriptorCodeValue);
            trackedRow["NewGradingPeriodDescriptor_CodeValue"].Should().Be(GradingPeriodDescriptorCodeValue);
        }
    }

    [Test]
    public async Task It_should_insert_key_change_rows_only_for_identity_changed_rows_in_a_mixed_workset_update()
    {
        // The core statement-level-trigger risk: one UPDATE whose workset contains
        // multiple rows where only SOME change identity. The identity-changed gate
        // must admit exactly the changed row — no key-change row and no stamps at
        // all for the row whose values were self-assigned. PG triggers are row-level
        // today, so this pins the contract ahead of any statement-level migration.
        const int SchoolYear = 2026;
        const string GradingPeriodDescriptorNamespace = "uri://ed-fi.org/GradingPeriodDescriptor";
        const string GradingPeriodDescriptorCodeValue = "SecondSixWeeks";

        var schoolYearTypeResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var gradingPeriodResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "GradingPeriod");
        var gradingPeriodDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "GradingPeriodDescriptor"
        );

        var schoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("d5d5d5d5-d5d5-d5d5-d5d5-d5d5d5d5d5d5"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(schoolYearTypeDocumentId, SchoolYear, "2026-2027");

        // GradingPeriod pairs School_SchoolId with School_DocumentId in its FK to the
        // EducationOrganizationIdentity projection, so read the seeded school's id back
        // instead of hard-coding the seed literal.
        var seededSchoolId = await _database.ExecuteScalarAsync<long>(
            """SELECT "SchoolId" FROM "edfi"."School" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
        );

        var gradingPeriodDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("d6d6d6d6-d6d6-d6d6-d6d6-d6d6d6d6d6d6"),
            gradingPeriodDescriptorResourceKeyId,
            "Ed-Fi:GradingPeriodDescriptor",
            $"{GradingPeriodDescriptorNamespace}#{GradingPeriodDescriptorCodeValue}",
            GradingPeriodDescriptorNamespace,
            GradingPeriodDescriptorCodeValue,
            "Second Six Weeks"
        );

        var changedDocumentUuid = Guid.Parse("d7d7d7d7-d7d7-d7d7-d7d7-d7d7d7d7d7d7");
        var changedDocumentId = await InsertDocumentAsync(changedDocumentUuid, gradingPeriodResourceKeyId);
        await InsertGradingPeriodAsync(
            changedDocumentId,
            schoolYearTypeDocumentId,
            SchoolYear,
            _seedData.SchoolDocumentId,
            seededSchoolId,
            gradingPeriodDescriptorDocumentId,
            "Mixed Changed Period"
        );

        var unchangedDocumentUuid = Guid.Parse("d8d8d8d8-d8d8-d8d8-d8d8-d8d8d8d8d8d8");
        var unchangedDocumentId = await InsertDocumentAsync(
            unchangedDocumentUuid,
            gradingPeriodResourceKeyId
        );
        await InsertGradingPeriodAsync(
            unchangedDocumentId,
            schoolYearTypeDocumentId,
            SchoolYear,
            _seedData.SchoolDocumentId,
            seededSchoolId,
            gradingPeriodDescriptorDocumentId,
            "Mixed Unchanged Period"
        );

        var changedBefore = await GetDocumentStampStateAsync(changedDocumentId);
        var unchangedBefore = await GetDocumentStampStateAsync(unchangedDocumentId);

        await DelayForDistinctTimestampsAsync();
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."GradingPeriod"
            SET "GradingPeriodName" = CASE
                WHEN "DocumentId" = @changedDocumentId THEN "GradingPeriodName" || '-renamed'
                ELSE "GradingPeriodName" END
            WHERE "DocumentId" IN (@changedDocumentId, @unchangedDocumentId);
            """,
            new NpgsqlParameter("changedDocumentId", changedDocumentId),
            new NpgsqlParameter("unchangedDocumentId", unchangedDocumentId)
        );
        rowsAffected.Should().Be(2);

        var changedAfter = await GetDocumentStampStateAsync(changedDocumentId);
        var unchangedAfter = await GetDocumentStampStateAsync(unchangedDocumentId);

        changedAfter.ContentVersion.Should().BeGreaterThan(changedBefore.ContentVersion);
        changedAfter.IdentityVersion.Should().BeGreaterThan(changedBefore.IdentityVersion);
        // The self-assigned row is a stored-value no-op: no stamps at all.
        unchangedAfter.Should().Be(unchangedBefore);

        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "GradingPeriod", changedDocumentUuid))
            .Should()
            .Be(1);
        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "GradingPeriod", unchangedDocumentUuid))
            .Should()
            .Be(0);

        var trackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "GradingPeriod",
            changedDocumentUuid
        );
        trackedRow["OldGradingPeriodName"].Should().Be("Mixed Changed Period");
        trackedRow["NewGradingPeriodName"].Should().Be("Mixed Changed Period-renamed");
        Convert
            .ToInt64(trackedRow["ChangeVersion"], CultureInfo.InvariantCulture)
            .Should()
            .Be(changedAfter.ContentVersion);
    }

    [Test]
    public async Task It_should_project_old_and_new_descriptor_values_from_their_own_images_on_key_change()
    {
        // The descriptor element of the identity actually CHANGES here, so the
        // key-change row's OldX descriptor projection must come from the deleted
        // image and NewX from the inserted image — equal-value tests cannot tell.
        const int SchoolYear = 2027;
        const string DescriptorNamespace = "uri://ed-fi.org/GradingPeriodDescriptor";

        var schoolYearTypeResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var gradingPeriodResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "GradingPeriod");
        var gradingPeriodDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "GradingPeriodDescriptor"
        );

        var schoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("e4e4e4e4-e4e4-e4e4-e4e4-e4e4e4e4e4e4"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(schoolYearTypeDocumentId, SchoolYear, "2027-2028");

        // GradingPeriod pairs School_SchoolId with School_DocumentId in its FK to the
        // EducationOrganizationIdentity projection, so read the seeded school's id back
        // instead of hard-coding the seed literal.
        var seededSchoolId = await _database.ExecuteScalarAsync<long>(
            """SELECT "SchoolId" FROM "edfi"."School" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
        );

        var originalDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("e5e5e5e5-e5e5-e5e5-e5e5-e5e5e5e5e5e5"),
            gradingPeriodDescriptorResourceKeyId,
            "Ed-Fi:GradingPeriodDescriptor",
            $"{DescriptorNamespace}#FourthSixWeeks",
            DescriptorNamespace,
            "FourthSixWeeks",
            "Fourth Six Weeks"
        );
        var replacementDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("e6e6e6e6-e6e6-e6e6-e6e6-e6e6e6e6e6e6"),
            gradingPeriodDescriptorResourceKeyId,
            "Ed-Fi:GradingPeriodDescriptor",
            $"{DescriptorNamespace}#FifthSixWeeks",
            DescriptorNamespace,
            "FifthSixWeeks",
            "Fifth Six Weeks"
        );

        var gradingPeriodDocumentUuid = Guid.Parse("e7e7e7e7-e7e7-e7e7-e7e7-e7e7e7e7e7e7");
        var gradingPeriodDocumentId = await InsertDocumentAsync(
            gradingPeriodDocumentUuid,
            gradingPeriodResourceKeyId
        );
        await InsertGradingPeriodAsync(
            gradingPeriodDocumentId,
            schoolYearTypeDocumentId,
            SchoolYear,
            _seedData.SchoolDocumentId,
            seededSchoolId,
            originalDescriptorDocumentId,
            "Descriptor Swap Period"
        );

        var before = await GetDocumentStampStateAsync(gradingPeriodDocumentId);

        await DelayForDistinctTimestampsAsync();
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."GradingPeriod"
            SET "GradingPeriodDescriptor_DescriptorId" = @replacementDescriptorDocumentId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("replacementDescriptorDocumentId", replacementDescriptorDocumentId),
            new NpgsqlParameter("documentId", gradingPeriodDocumentId)
        );
        rowsAffected.Should().Be(1);

        var after = await GetDocumentStampStateAsync(gradingPeriodDocumentId);
        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.IdentityVersion.Should().BeGreaterThan(before.IdentityVersion);

        (
            await CountTrackedChangeRowsAsync(
                "tracked_changes_edfi",
                "GradingPeriod",
                gradingPeriodDocumentUuid
            )
        )
            .Should()
            .Be(1);

        var trackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "GradingPeriod",
            gradingPeriodDocumentUuid
        );
        trackedRow["OldGradingPeriodDescriptor_Namespace"].Should().Be(DescriptorNamespace);
        trackedRow["NewGradingPeriodDescriptor_Namespace"].Should().Be(DescriptorNamespace);
        trackedRow["OldGradingPeriodDescriptor_CodeValue"].Should().Be("FourthSixWeeks");
        trackedRow["NewGradingPeriodDescriptor_CodeValue"].Should().Be("FifthSixWeeks");
        trackedRow["OldGradingPeriodName"].Should().Be("Descriptor Swap Period");
        trackedRow["NewGradingPeriodName"].Should().Be("Descriptor Swap Period");
        Convert
            .ToInt64(trackedRow["ChangeVersion"], CultureInfo.InvariantCulture)
            .Should()
            .Be(after.ContentVersion);
    }

    [Test]
    public async Task It_should_project_a_null_to_value_transition_in_the_key_change_row()
    {
        // ReportedSchool is a nullable securable projection on StudentAssessment.
        // Change the identity (StudentAssessmentIdentifier) and set ReportedSchool
        // from NULL to a value in the same UPDATE: the key-change row must read the
        // old image as NULL and the new image as the value — a bug reading both
        // images from the post-update row projects the value on both sides.
        const string AssessmentNamespace = "uri://ed-fi.org/Assessment";
        const string AssessmentIdentifier = "ASMT-NULLT-001";
        const string StudentUniqueId = "20001";

        var assessmentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Assessment");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var studentAssessmentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "StudentAssessment");

        var assessmentDocumentId = await InsertDocumentAsync(
            Guid.Parse("b5b5b5b5-b5b5-b5b5-b5b5-b5b5b5b5b5b5"),
            assessmentResourceKeyId
        );
        await InsertAssessmentAsync(
            assessmentDocumentId,
            AssessmentIdentifier,
            "Null Transition Assessment",
            AssessmentNamespace
        );

        var studentDocumentId = await InsertDocumentAsync(
            Guid.Parse("b6b6b6b6-b6b6-b6b6-b6b6-b6b6b6b6b6b6"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, StudentUniqueId, "Nola", "Vale");

        var studentAssessmentDocumentUuid = Guid.Parse("b7b7b7b7-b7b7-b7b7-b7b7-b7b7b7b7b7b7");
        var studentAssessmentDocumentId = await InsertDocumentAsync(
            studentAssessmentDocumentUuid,
            studentAssessmentResourceKeyId
        );
        await InsertStudentAssessmentAsync(
            studentAssessmentDocumentId,
            assessmentDocumentId,
            AssessmentIdentifier,
            AssessmentNamespace,
            studentDocumentId,
            StudentUniqueId,
            "SA-001"
        );

        var seededSchoolId = await _database.ExecuteScalarAsync<long>(
            """SELECT "SchoolId" FROM "edfi"."School" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
        );
        var before = await GetDocumentStampStateAsync(studentAssessmentDocumentId);

        await DelayForDistinctTimestampsAsync();
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."StudentAssessment"
            SET "StudentAssessmentIdentifier" = @replacementIdentifier,
                "ReportedSchool_DocumentId" = @schoolDocumentId,
                "ReportedSchool_SchoolId" = @schoolId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("replacementIdentifier", "SA-001-renamed"),
            new NpgsqlParameter("schoolDocumentId", _seedData.SchoolDocumentId),
            new NpgsqlParameter("schoolId", seededSchoolId),
            new NpgsqlParameter("documentId", studentAssessmentDocumentId)
        );
        rowsAffected.Should().Be(1);

        var after = await GetDocumentStampStateAsync(studentAssessmentDocumentId);
        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.IdentityVersion.Should().BeGreaterThan(before.IdentityVersion);

        (
            await CountTrackedChangeRowsAsync(
                "tracked_changes_edfi",
                "StudentAssessment",
                studentAssessmentDocumentUuid
            )
        )
            .Should()
            .Be(1);

        var trackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "StudentAssessment",
            studentAssessmentDocumentUuid
        );
        trackedRow["OldStudentAssessmentIdentifier"].Should().Be("SA-001");
        trackedRow["NewStudentAssessmentIdentifier"].Should().Be("SA-001-renamed");
        trackedRow["OldReportedSchool_SchoolId"].Should().BeNull();
        Convert
            .ToInt64(trackedRow["NewReportedSchool_SchoolId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(seededSchoolId);
        trackedRow["OldStudent_StudentUniqueId"].Should().Be(StudentUniqueId);
        trackedRow["NewStudent_StudentUniqueId"].Should().Be(StudentUniqueId);
        Convert
            .ToInt64(trackedRow["ChangeVersion"], CultureInfo.InvariantCulture)
            .Should()
            .Be(after.ContentVersion);
    }

    [Test]
    public async Task It_should_use_canonical_columns_for_key_unified_paths()
    {
        const string AssessmentNamespace = "uri://ed-fi.org/Assessment";
        const string OriginalAssessmentIdentifier = "ASMT-001";
        const string ReplacementAssessmentIdentifier = "ASMT-002";
        const string ScoreRangeId = "SR-001";

        var assessmentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Assessment");
        var scoreRangeResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "AssessmentScoreRangeLearningStandard"
        );

        var originalAssessmentDocumentId = await InsertDocumentAsync(
            Guid.Parse("e1e1e1e1-e1e1-e1e1-e1e1-e1e1e1e1e1e1"),
            assessmentResourceKeyId
        );
        await InsertAssessmentAsync(
            originalAssessmentDocumentId,
            OriginalAssessmentIdentifier,
            "Original Assessment",
            AssessmentNamespace
        );

        var replacementAssessmentDocumentId = await InsertDocumentAsync(
            Guid.Parse("e2e2e2e2-e2e2-e2e2-e2e2-e2e2e2e2e2e2"),
            assessmentResourceKeyId
        );
        await InsertAssessmentAsync(
            replacementAssessmentDocumentId,
            ReplacementAssessmentIdentifier,
            "Replacement Assessment",
            AssessmentNamespace
        );

        var scoreRangeDocumentUuid = Guid.Parse("e3e3e3e3-e3e3-e3e3-e3e3-e3e3e3e3e3e3");
        var scoreRangeDocumentId = await InsertDocumentAsync(scoreRangeDocumentUuid, scoreRangeResourceKeyId);
        await InsertAssessmentScoreRangeLearningStandardAsync(
            scoreRangeDocumentId,
            OriginalAssessmentIdentifier,
            AssessmentNamespace,
            originalAssessmentDocumentId,
            ScoreRangeId
        );

        // The Assessment_AssessmentIdentifier/Assessment_Namespace alias columns are
        // GENERATED from the canonical *_Unified columns, and the FK to edfi.Assessment
        // pairs the canonical columns with Assessment_DocumentId (ON UPDATE NO ACTION).
        // Re-pointing the reference and the canonical identifier in a single UPDATE is
        // the direct-SQL simulation of an upstream assessment key change reaching this
        // key-unified resource; the trigger's identity gate keys off the canonical column.
        await DelayForDistinctTimestampsAsync();
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."AssessmentScoreRangeLearningStandard"
            SET "AssessmentIdentifier_Unified" = @replacementAssessmentIdentifier,
                "Assessment_DocumentId" = @replacementAssessmentDocumentId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("replacementAssessmentIdentifier", ReplacementAssessmentIdentifier),
            new NpgsqlParameter("replacementAssessmentDocumentId", replacementAssessmentDocumentId),
            new NpgsqlParameter("documentId", scoreRangeDocumentId)
        );
        rowsAffected.Should().Be(1);

        var afterDocument = await GetDocumentStampStateAsync(scoreRangeDocumentId);

        (
            await CountTrackedChangeRowsAsync(
                "tracked_changes_edfi",
                "AssessmentScoreRangeLearningStandard",
                scoreRangeDocumentUuid
            )
        )
            .Should()
            .Be(1);

        var trackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "AssessmentScoreRangeLearningStandard",
            scoreRangeDocumentUuid
        );

        Convert
            .ToInt64(trackedRow["ChangeVersion"], CultureInfo.InvariantCulture)
            .Should()
            .Be(afterDocument.ContentVersion);
        trackedRow["Id"].Should().Be(scoreRangeDocumentUuid);
        trackedRow["OldAssessmentIdentifier_Unified"].Should().Be(OriginalAssessmentIdentifier);
        trackedRow["NewAssessmentIdentifier_Unified"].Should().Be(ReplacementAssessmentIdentifier);
        trackedRow["OldNamespace_Unified"].Should().Be(AssessmentNamespace);
        trackedRow["NewNamespace_Unified"].Should().Be(AssessmentNamespace);
        trackedRow["OldScoreRangeId"].Should().Be(ScoreRangeId);
        trackedRow["NewScoreRangeId"].Should().Be(ScoreRangeId);
    }

    #endregion

    // f* GUID seeds used here — see the GUID allocation comment above #region Tracked-change key-change rows.
    #region Tracked-change tombstones

    [Test]
    public async Task It_should_insert_a_tombstone_with_the_bumped_content_version_on_delete()
    {
        var seed = await SeedKeyChangeStudentEducationOrganizationAssociationAsync();
        var before = await GetDocumentStampStateAsync(seed.AssociationDocumentId);

        // DMS-1180 two-statement order, statement 1: delete the resource row while the
        // dms.Document row still exists, so the stamping trigger can read the doc-stamp
        // linkage for the tombstone.
        await DelayForDistinctTimestampsAsync();
        var resourceRowsAffected = await _database.ExecuteNonQueryAsync(
            """DELETE FROM "edfi"."StudentEducationOrganizationAssociation" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", seed.AssociationDocumentId)
        );
        resourceRowsAffected.Should().Be(1);

        (await CountDocumentRowsAsync(seed.AssociationDocumentId)).Should().Be(1);
        var afterResourceDelete = await GetDocumentStampStateAsync(seed.AssociationDocumentId);
        afterResourceDelete.ContentVersion.Should().BeGreaterThan(before.ContentVersion);

        (
            await CountTrackedChangeRowsAsync(
                "tracked_changes_edfi",
                "StudentEducationOrganizationAssociation",
                seed.AssociationDocumentUuid
            )
        )
            .Should()
            .Be(1);

        var trackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "StudentEducationOrganizationAssociation",
            seed.AssociationDocumentUuid
        );
        var tombstoneChangeVersion = Convert.ToInt64(
            trackedRow["ChangeVersion"],
            CultureInfo.InvariantCulture
        );

        tombstoneChangeVersion.Should().Be(afterResourceDelete.ContentVersion);
        trackedRow["Id"].Should().Be(seed.AssociationDocumentUuid);
        Convert
            .ToInt64(
                trackedRow["OldEducationOrganization_EducationOrganizationId"],
                CultureInfo.InvariantCulture
            )
            .Should()
            .Be(seed.OriginalEducationOrganizationId);
        trackedRow["OldStudent_StudentUniqueId"].Should().Be(seed.StudentUniqueId);
        Convert
            .ToInt64(trackedRow["OldStudent_DocumentId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(seed.StudentDocumentId);
        AssertAllNewColumnsAreNull(trackedRow);

        // Statement 2: delete the dms.Document row.
        var documentRowsAffected = await _database.ExecuteNonQueryAsync(
            """DELETE FROM "dms"."Document" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", seed.AssociationDocumentId)
        );
        documentRowsAffected.Should().Be(1);

        (await CountDocumentRowsAsync(seed.AssociationDocumentId)).Should().Be(0);
        (
            await CountTrackedChangeRowsAsync(
                "tracked_changes_edfi",
                "StudentEducationOrganizationAssociation",
                seed.AssociationDocumentUuid
            )
        )
            .Should()
            .Be(1);
        await AssertMaxTrackedChangeVersionAsync(
            "tracked_changes_edfi",
            "StudentEducationOrganizationAssociation",
            seed.AssociationDocumentUuid,
            tombstoneChangeVersion
        );
    }

    [Test]
    public async Task It_should_insert_a_descriptor_tombstone_with_discriminator_on_delete()
    {
        const string DescriptorDiscriminator = "Ed-Fi:TermDescriptor";
        const string DescriptorNamespace = "uri://ed-fi.org/TermDescriptor";
        const string DescriptorCodeValue = "Summer";

        var termDescriptorResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "TermDescriptor");
        var descriptorDocumentUuid = Guid.Parse("f1f1f1f1-f1f1-f1f1-f1f1-f1f1f1f1f1f1");
        var descriptorDocumentId = await InsertDescriptorAsync(
            descriptorDocumentUuid,
            termDescriptorResourceKeyId,
            DescriptorDiscriminator,
            $"{DescriptorNamespace}#{DescriptorCodeValue}",
            DescriptorNamespace,
            DescriptorCodeValue,
            "Summer"
        );
        var before = await GetDocumentStampStateAsync(descriptorDocumentId);

        // Statement 1: delete the shared descriptor row while the dms.Document row
        // still exists.
        await DelayForDistinctTimestampsAsync();
        var resourceRowsAffected = await _database.ExecuteNonQueryAsync(
            """DELETE FROM "dms"."Descriptor" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", descriptorDocumentId)
        );
        resourceRowsAffected.Should().Be(1);

        (await CountDocumentRowsAsync(descriptorDocumentId)).Should().Be(1);
        var afterResourceDelete = await GetDocumentStampStateAsync(descriptorDocumentId);
        afterResourceDelete.ContentVersion.Should().BeGreaterThan(before.ContentVersion);

        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "Descriptor", descriptorDocumentUuid))
            .Should()
            .Be(1);

        var trackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "Descriptor",
            descriptorDocumentUuid
        );
        var tombstoneChangeVersion = Convert.ToInt64(
            trackedRow["ChangeVersion"],
            CultureInfo.InvariantCulture
        );

        tombstoneChangeVersion.Should().Be(afterResourceDelete.ContentVersion);
        trackedRow["Id"].Should().Be(descriptorDocumentUuid);
        trackedRow["Discriminator"].Should().Be(DescriptorDiscriminator);
        trackedRow["OldNamespace"].Should().Be(DescriptorNamespace);
        trackedRow["OldCodeValue"].Should().Be(DescriptorCodeValue);
        AssertAllNewColumnsAreNull(trackedRow);

        // Statement 2: delete the dms.Document row.
        var documentRowsAffected = await _database.ExecuteNonQueryAsync(
            """DELETE FROM "dms"."Document" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", descriptorDocumentId)
        );
        documentRowsAffected.Should().Be(1);

        (await CountDocumentRowsAsync(descriptorDocumentId)).Should().Be(0);
        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "Descriptor", descriptorDocumentUuid))
            .Should()
            .Be(1);
        await AssertMaxTrackedChangeVersionAsync(
            "tracked_changes_edfi",
            "Descriptor",
            descriptorDocumentUuid,
            tombstoneChangeVersion
        );
    }

    [Test]
    public async Task It_should_tombstone_concrete_abstract_resources_in_their_own_table()
    {
        // 400 chosen distinct from the seeded SchoolId=100 to avoid PK collisions.
        const int FreshSchoolId = 400;

        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var schoolDocumentUuid = Guid.Parse("f2f2f2f2-f2f2-f2f2-f2f2-f2f2f2f2f2f2");
        var schoolDocumentId = await InsertDocumentAsync(schoolDocumentUuid, schoolResourceKeyId);
        await InsertSchoolAsync(schoolDocumentId, FreshSchoolId, "Delta Academy");
        var before = await GetDocumentStampStateAsync(schoolDocumentId);

        // Statement 1: delete the concrete-abstract resource row while the dms.Document
        // row still exists. School tombstones into its OWN tracked-change table.
        await DelayForDistinctTimestampsAsync();
        var resourceRowsAffected = await _database.ExecuteNonQueryAsync(
            """DELETE FROM "edfi"."School" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", schoolDocumentId)
        );
        resourceRowsAffected.Should().Be(1);

        (await CountDocumentRowsAsync(schoolDocumentId)).Should().Be(1);
        var afterResourceDelete = await GetDocumentStampStateAsync(schoolDocumentId);
        afterResourceDelete.ContentVersion.Should().BeGreaterThan(before.ContentVersion);

        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "School", schoolDocumentUuid))
            .Should()
            .Be(1);

        var trackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "School",
            schoolDocumentUuid
        );
        var tombstoneChangeVersion = Convert.ToInt64(
            trackedRow["ChangeVersion"],
            CultureInfo.InvariantCulture
        );

        tombstoneChangeVersion.Should().Be(afterResourceDelete.ContentVersion);
        trackedRow["Id"].Should().Be(schoolDocumentUuid);
        Convert.ToInt64(trackedRow["OldSchoolId"], CultureInfo.InvariantCulture).Should().Be(FreshSchoolId);
        AssertAllNewColumnsAreNull(trackedRow);

        // Statement 2: delete the dms.Document row.
        var documentRowsAffected = await _database.ExecuteNonQueryAsync(
            """DELETE FROM "dms"."Document" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", schoolDocumentId)
        );
        documentRowsAffected.Should().Be(1);

        (await CountDocumentRowsAsync(schoolDocumentId)).Should().Be(0);
        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "School", schoolDocumentUuid))
            .Should()
            .Be(1);
        await AssertMaxTrackedChangeVersionAsync(
            "tracked_changes_edfi",
            "School",
            schoolDocumentUuid,
            tombstoneChangeVersion
        );
    }

    [Test]
    public async Task It_should_not_insert_key_change_rows_for_concrete_abstract_identity_updates()
    {
        // 500/501 chosen distinct from the seeded SchoolId=100 and the tombstone test's 400.
        const int OriginalSchoolId = 500;
        const int ReplacementSchoolId = 501;

        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var schoolDocumentUuid = Guid.Parse("f3f3f3f3-f3f3-f3f3-f3f3-f3f3f3f3f3f3");
        var schoolDocumentId = await InsertDocumentAsync(schoolDocumentUuid, schoolResourceKeyId);
        await InsertSchoolAsync(schoolDocumentId, OriginalSchoolId, "Epsilon Academy");

        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "School", schoolDocumentUuid))
            .Should()
            .Be(0);
        var before = await GetDocumentStampStateAsync(schoolDocumentId);

        // A successful identity-value change on a concrete-abstract resource must bump
        // IdentityVersion but never insert a key-change row: deletes are the only writes
        // that land in tracked_changes for concrete-abstract resources.
        await DelayForDistinctTimestampsAsync();
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."School"
            SET "SchoolId" = @replacementSchoolId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("replacementSchoolId", ReplacementSchoolId),
            new NpgsqlParameter("documentId", schoolDocumentId)
        );
        rowsAffected.Should().Be(1);

        var after = await GetDocumentStampStateAsync(schoolDocumentId);

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.IdentityVersion.Should().BeGreaterThan(before.IdentityVersion);
        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "School", schoolDocumentUuid))
            .Should()
            .Be(0);
    }

    [Test]
    public async Task It_should_emit_exactly_one_root_tombstone_for_cascaded_deletes()
    {
        // The per-test seed builds a fresh SEOA with an edfi child-collection row
        // (StudentEducationOrganizationAssociationAddress), a sample _ext row
        // (StudentEducationOrganizationAssociationExtensionAddress), and _ext
        // grandchildren (SchoolDistrict/Term) — all FK-cascaded from the root row.
        var associationDocumentId = _seedData.StudentEducationOrganizationAssociationDocumentId;
        var associationDocumentUuid = await GetDocumentUuidAsync(associationDocumentId);

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "edfi"."StudentEducationOrganizationAssociationAddress" WHERE "StudentEducationOrganizationAssociation_DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", associationDocumentId)
            )
        )
            .Should()
            .Be(1);
        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "sample"."StudentEducationOrganizationAssociationExtensionAddress" WHERE "StudentEducationOrganizationAssociation_DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", associationDocumentId)
            )
        )
            .Should()
            .Be(1);

        var before = await GetDocumentStampStateAsync(associationDocumentId);

        // Statement 1: delete the root resource row; FK cascades remove the child and
        // _ext rows and fire their stamping triggers.
        await DelayForDistinctTimestampsAsync();
        var resourceRowsAffected = await _database.ExecuteNonQueryAsync(
            """DELETE FROM "edfi"."StudentEducationOrganizationAssociation" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", associationDocumentId)
        );
        resourceRowsAffected.Should().Be(1);

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "edfi"."StudentEducationOrganizationAssociationAddress" WHERE "StudentEducationOrganizationAssociation_DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", associationDocumentId)
            )
        )
            .Should()
            .Be(0);
        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "sample"."StudentEducationOrganizationAssociationExtensionAddress" WHERE "StudentEducationOrganizationAssociation_DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", associationDocumentId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountTrackedChangeRowsAsync(
                "tracked_changes_edfi",
                "StudentEducationOrganizationAssociation",
                associationDocumentUuid
            )
        )
            .Should()
            .Be(1);

        var trackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "StudentEducationOrganizationAssociation",
            associationDocumentUuid
        );
        var tombstoneChangeVersion = Convert.ToInt64(
            trackedRow["ChangeVersion"],
            CultureInfo.InvariantCulture
        );

        trackedRow["Id"].Should().Be(associationDocumentUuid);
        tombstoneChangeVersion.Should().BeGreaterThan(before.ContentVersion);
        AssertAllNewColumnsAreNull(trackedRow);

        // The tombstone must be the final visible root delete stamp. Cascaded child trigger
        // activity must not advance the document extraction watermark past the tombstone.
        (await CountDocumentRowsAsync(associationDocumentId))
            .Should()
            .Be(1);
        var afterResourceDelete = await GetDocumentStampStateAsync(associationDocumentId);
        afterResourceDelete.ContentVersion.Should().Be(tombstoneChangeVersion);
        await AssertMaxTrackedChangeVersionAsync(
            "tracked_changes_edfi",
            "StudentEducationOrganizationAssociation",
            associationDocumentUuid,
            tombstoneChangeVersion
        );

        // Statement 2: delete the dms.Document row.
        var documentRowsAffected = await _database.ExecuteNonQueryAsync(
            """DELETE FROM "dms"."Document" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", associationDocumentId)
        );
        documentRowsAffected.Should().Be(1);

        (await CountDocumentRowsAsync(associationDocumentId)).Should().Be(0);
        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "edfi"."StudentEducationOrganizationAssociation" WHERE "DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", associationDocumentId)
            )
        )
            .Should()
            .Be(0);
        (
            await CountTrackedChangeRowsAsync(
                "tracked_changes_edfi",
                "StudentEducationOrganizationAssociation",
                associationDocumentUuid
            )
        )
            .Should()
            .Be(1);

        // No later visible tracked row may advance an extraction watermark past the
        // tombstone.
        await AssertMaxTrackedChangeVersionAsync(
            "tracked_changes_edfi",
            "StudentEducationOrganizationAssociation",
            associationDocumentUuid,
            tombstoneChangeVersion
        );
    }

    [Test]
    public async Task It_should_emit_exactly_one_root_tombstone_for_cascaded_abstract_family_deletes()
    {
        // Abstract-resource-family cascade (AC): a concrete-abstract root (School)
        // carrying a cascaded _ext row and _ext grandchild. The root delete must
        // produce exactly one School tombstone and no key-change rows, regardless
        // of cascaded extension-trigger activity.
        const int FreshSchoolId = 401;

        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var busResourceKeyId = await GetResourceKeyIdAsync("Sample", "Bus");

        var schoolDocumentUuid = Guid.Parse("f7f7f7f7-f7f7-f7f7-f7f7-f7f7f7f7f7f7");
        var schoolDocumentId = await InsertDocumentAsync(schoolDocumentUuid, schoolResourceKeyId);
        await InsertSchoolAsync(schoolDocumentId, FreshSchoolId, "Zeta Academy");
        await InsertSchoolExtensionAsync(schoolDocumentId);

        var busDocumentId = await InsertDocumentAsync(
            Guid.Parse("f8f8f8f8-f8f8-f8f8-f8f8-f8f8f8f8f8f8"),
            busResourceKeyId
        );
        await InsertBusAsync(busDocumentId, "BUS-401");
        _ = await InsertSchoolExtensionDirectlyOwnedBusAsync(schoolDocumentId, 1, busDocumentId, "BUS-401");

        var before = await GetDocumentStampStateAsync(schoolDocumentId);

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "sample"."SchoolExtension" WHERE "DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", schoolDocumentId)
            )
        )
            .Should()
            .Be(1);
        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "sample"."SchoolExtensionDirectlyOwnedBus" WHERE "School_DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", schoolDocumentId)
            )
        )
            .Should()
            .Be(1);
        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "edfi"."EducationOrganizationIdentity" WHERE "DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", schoolDocumentId)
            )
        )
            .Should()
            .Be(1);

        // Statement 1: delete the root School row; FK cascades remove the _ext row
        // and its grandchild and fire their stamping triggers.
        await DelayForDistinctTimestampsAsync();
        var resourceRowsAffected = await _database.ExecuteNonQueryAsync(
            """DELETE FROM "edfi"."School" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", schoolDocumentId)
        );
        resourceRowsAffected.Should().Be(1);

        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "sample"."SchoolExtension" WHERE "DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", schoolDocumentId)
            )
        )
            .Should()
            .Be(0);
        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "sample"."SchoolExtensionDirectlyOwnedBus" WHERE "School_DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", schoolDocumentId)
            )
        )
            .Should()
            .Be(0);

        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "School", schoolDocumentUuid))
            .Should()
            .Be(1);

        var trackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "School",
            schoolDocumentUuid
        );
        var tombstoneChangeVersion = Convert.ToInt64(
            trackedRow["ChangeVersion"],
            CultureInfo.InvariantCulture
        );

        trackedRow["Id"].Should().Be(schoolDocumentUuid);
        Convert.ToInt64(trackedRow["OldSchoolId"], CultureInfo.InvariantCulture).Should().Be(FreshSchoolId);
        AssertAllNewColumnsAreNull(trackedRow);
        tombstoneChangeVersion.Should().BeGreaterThan(before.ContentVersion);

        // The tombstone must be the final visible root delete stamp. Cascaded extension trigger
        // activity must not advance the document extraction watermark past the tombstone.
        (await CountDocumentRowsAsync(schoolDocumentId))
            .Should()
            .Be(1);
        var afterResourceDelete = await GetDocumentStampStateAsync(schoolDocumentId);
        afterResourceDelete.ContentVersion.Should().Be(tombstoneChangeVersion);
        await AssertMaxTrackedChangeVersionAsync(
            "tracked_changes_edfi",
            "School",
            schoolDocumentUuid,
            tombstoneChangeVersion
        );

        // Statement 2: delete the dms.Document row.
        var documentRowsAffected = await _database.ExecuteNonQueryAsync(
            """DELETE FROM "dms"."Document" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", schoolDocumentId)
        );
        documentRowsAffected.Should().Be(1);

        (await CountDocumentRowsAsync(schoolDocumentId)).Should().Be(0);
        (
            await CountRowsAsync(
                """SELECT COUNT(*) FROM "edfi"."EducationOrganizationIdentity" WHERE "DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", schoolDocumentId)
            )
        )
            .Should()
            .Be(0);
        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "School", schoolDocumentUuid))
            .Should()
            .Be(1);
        await AssertMaxTrackedChangeVersionAsync(
            "tracked_changes_edfi",
            "School",
            schoolDocumentUuid,
            tombstoneChangeVersion
        );
    }

    #endregion

    private async Task<AuthoritativeSampleSmokeSeedData> SeedSmokeRowsAsync()
    {
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var busResourceKeyId = await GetResourceKeyIdAsync("Sample", "Bus");
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

        var directlyOwnedBusDocumentId = await InsertDocumentAsync(
            Guid.Parse("10101010-1010-1010-1010-101010101010"),
            busResourceKeyId
        );
        await InsertBusAsync(directlyOwnedBusDocumentId, "BUS-001");

        var schoolExtensionDirectlyOwnedBusCollectionItemId =
            await InsertSchoolExtensionDirectlyOwnedBusAsync(
                schoolDocumentId,
                1,
                directlyOwnedBusDocumentId,
                "BUS-001"
            );

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
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentId,
                @resourceKeyId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId),
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

    private async Task InsertBusAsync(long documentId, string busId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "sample"."Bus" ("DocumentId", "BusId")
            VALUES (@documentId, @busId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("busId", busId)
        );
    }

    private async Task<long> InsertSchoolExtensionDirectlyOwnedBusAsync(
        long schoolDocumentId,
        int ordinal,
        long directlyOwnedBusDocumentId,
        string directlyOwnedBusId
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "sample"."SchoolExtensionDirectlyOwnedBus" ("Ordinal", "School_DocumentId", "DirectlyOwnedBus_DocumentId", "DirectlyOwnedBus_BusId")
            VALUES (@ordinal, @schoolDocumentId, @directlyOwnedBusDocumentId, @directlyOwnedBusId)
            RETURNING "CollectionItemId";
            """,
            new NpgsqlParameter("ordinal", ordinal),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("directlyOwnedBusDocumentId", directlyOwnedBusDocumentId),
            new NpgsqlParameter("directlyOwnedBusId", directlyOwnedBusId)
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

    private async Task<TrackedChangeKeyChangeAssociationSeedData> SeedKeyChangeStudentEducationOrganizationAssociationAsync()
    {
        const string StudentUniqueId = "20001";
        const long OriginalEducationOrganizationId = 100;
        const long ReplacementEducationOrganizationId = 200;

        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var associationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "StudentEducationOrganizationAssociation"
        );

        var studentDocumentId = await InsertDocumentAsync(
            Guid.Parse("c1c1c1c1-c1c1-c1c1-c1c1-c1c1c1c1c1c1"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, StudentUniqueId, "Riley", "Quinn");

        var replacementSchoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("c2c2c2c2-c2c2-c2c2-c2c2-c2c2c2c2c2c2"),
            schoolResourceKeyId
        );
        await InsertSchoolAsync(
            replacementSchoolDocumentId,
            (int)ReplacementEducationOrganizationId,
            "Gamma Academy"
        );

        var associationDocumentUuid = Guid.Parse("c3c3c3c3-c3c3-c3c3-c3c3-c3c3c3c3c3c3");
        var associationDocumentId = await InsertDocumentAsync(
            associationDocumentUuid,
            associationResourceKeyId
        );
        await InsertStudentEducationOrganizationAssociationAsync(
            associationDocumentId,
            _seedData.SchoolDocumentId,
            (int)OriginalEducationOrganizationId,
            studentDocumentId,
            StudentUniqueId
        );

        return new(
            associationDocumentId,
            associationDocumentUuid,
            studentDocumentId,
            StudentUniqueId,
            OriginalEducationOrganizationId,
            replacementSchoolDocumentId,
            ReplacementEducationOrganizationId
        );
    }

    private async Task RepointAssociationEducationOrganizationAsync(
        TrackedChangeKeyChangeAssociationSeedData seed
    )
    {
        // The stored EdOrg identity value is FK-paired with its DocumentId against
        // edfi.EducationOrganizationIdentity, so the direct-SQL simulation of a cascaded
        // identity key change must re-point both columns in a single UPDATE.
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."StudentEducationOrganizationAssociation"
            SET "EducationOrganization_DocumentId" = @replacementSchoolDocumentId,
                "EducationOrganization_EducationOrganizationId" = @replacementEducationOrganizationId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("replacementSchoolDocumentId", seed.ReplacementSchoolDocumentId),
            new NpgsqlParameter(
                "replacementEducationOrganizationId",
                seed.ReplacementEducationOrganizationId
            ),
            new NpgsqlParameter("documentId", seed.AssociationDocumentId)
        );
        rowsAffected.Should().Be(1);
    }

    private async Task InsertSchoolYearTypeAsync(
        long documentId,
        int schoolYear,
        string schoolYearDescription
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."SchoolYearType" ("DocumentId", "CurrentSchoolYear", "SchoolYear", "SchoolYearDescription")
            VALUES (@documentId, @currentSchoolYear, @schoolYear, @schoolYearDescription);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("currentSchoolYear", true),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolYearDescription", schoolYearDescription)
        );
    }

    private async Task InsertGradingPeriodAsync(
        long documentId,
        long schoolYearTypeDocumentId,
        int schoolYear,
        long schoolDocumentId,
        long schoolId,
        long gradingPeriodDescriptorDocumentId,
        string gradingPeriodName
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."GradingPeriod" (
                "DocumentId",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "School_DocumentId",
                "School_SchoolId",
                "GradingPeriodDescriptor_DescriptorId",
                "BeginDate",
                "EndDate",
                "GradingPeriodName",
                "TotalInstructionalDays"
            )
            VALUES (
                @documentId,
                @schoolYearTypeDocumentId,
                @schoolYear,
                @schoolDocumentId,
                @schoolId,
                @gradingPeriodDescriptorDocumentId,
                @beginDate,
                @endDate,
                @gradingPeriodName,
                @totalInstructionalDays
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolYearTypeDocumentId", schoolYearTypeDocumentId),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("schoolId", schoolId),
            new NpgsqlParameter("gradingPeriodDescriptorDocumentId", gradingPeriodDescriptorDocumentId),
            new NpgsqlParameter("beginDate", new DateOnly(2025, 8, 1)),
            new NpgsqlParameter("endDate", new DateOnly(2025, 9, 15)),
            new NpgsqlParameter("gradingPeriodName", gradingPeriodName),
            new NpgsqlParameter("totalInstructionalDays", 30)
        );
    }

    private async Task InsertAssessmentAsync(
        long documentId,
        string assessmentIdentifier,
        string assessmentTitle,
        string @namespace
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Assessment" ("DocumentId", "AssessmentIdentifier", "AssessmentTitle", "Namespace")
            VALUES (@documentId, @assessmentIdentifier, @assessmentTitle, @namespace);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("assessmentIdentifier", assessmentIdentifier),
            new NpgsqlParameter("assessmentTitle", assessmentTitle),
            new NpgsqlParameter("namespace", @namespace)
        );
    }

    private async Task InsertStudentAssessmentAsync(
        long documentId,
        long assessmentDocumentId,
        string assessmentIdentifier,
        string assessmentNamespace,
        long studentDocumentId,
        string studentUniqueId,
        string studentAssessmentIdentifier
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."StudentAssessment" (
                "DocumentId",
                "Assessment_DocumentId",
                "Assessment_AssessmentIdentifier",
                "Assessment_Namespace",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "StudentAssessmentIdentifier"
            )
            VALUES (
                @documentId,
                @assessmentDocumentId,
                @assessmentIdentifier,
                @assessmentNamespace,
                @studentDocumentId,
                @studentUniqueId,
                @studentAssessmentIdentifier
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("assessmentDocumentId", assessmentDocumentId),
            new NpgsqlParameter("assessmentIdentifier", assessmentIdentifier),
            new NpgsqlParameter("assessmentNamespace", assessmentNamespace),
            new NpgsqlParameter("studentDocumentId", studentDocumentId),
            new NpgsqlParameter("studentUniqueId", studentUniqueId),
            new NpgsqlParameter("studentAssessmentIdentifier", studentAssessmentIdentifier)
        );
    }

    private async Task InsertAssessmentScoreRangeLearningStandardAsync(
        long documentId,
        string assessmentIdentifier,
        string @namespace,
        long assessmentDocumentId,
        string scoreRangeId
    )
    {
        // The Assessment_AssessmentIdentifier/Assessment_Namespace alias columns are
        // GENERATED ALWAYS from the canonical *_Unified columns and must not be inserted.
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."AssessmentScoreRangeLearningStandard" (
                "DocumentId",
                "AssessmentIdentifier_Unified",
                "Namespace_Unified",
                "Assessment_DocumentId",
                "MaximumScore",
                "MinimumScore",
                "ScoreRangeId"
            )
            VALUES (
                @documentId,
                @assessmentIdentifier,
                @namespace,
                @assessmentDocumentId,
                @maximumScore,
                @minimumScore,
                @scoreRangeId
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("assessmentIdentifier", assessmentIdentifier),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("assessmentDocumentId", assessmentDocumentId),
            new NpgsqlParameter("maximumScore", "100"),
            new NpgsqlParameter("minimumScore", "0"),
            new NpgsqlParameter("scoreRangeId", scoreRangeId)
        );
    }

    private async Task<long> CountTrackedChangeRowsAsync(
        string schemaName,
        string tableName,
        Guid documentUuid
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            $"""
            SELECT COUNT(*)
            FROM "{schemaName}"."{tableName}"
            WHERE "Id" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );
    }

    private async Task<IReadOnlyDictionary<string, object?>> GetLatestTrackedChangeRowAsync(
        string schemaName,
        string tableName,
        Guid documentUuid
    )
    {
        var rows = await _database.QueryRowsAsync(
            $"""
            SELECT *
            FROM "{schemaName}"."{tableName}"
            WHERE "Id" = @documentUuid
            ORDER BY "ChangeVersion" DESC
            LIMIT 1;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Single();
    }

    // No tracked row may advance an extraction watermark past the tombstone
    // (extraction-watermark contract). This helper makes that invariant explicit at
    // every call site so it cannot be mistaken for a duplicate of GetLatestTrackedChangeRowAsync.
    private async Task AssertMaxTrackedChangeVersionAsync(
        string schemaName,
        string tableName,
        Guid documentUuid,
        long expectedChangeVersion
    )
    {
        var maxChangeVersion = await _database.ExecuteScalarAsync<long>(
            $"""
            SELECT max("ChangeVersion")
            FROM "{schemaName}"."{tableName}"
            WHERE "Id" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );
        maxChangeVersion
            .Should()
            .Be(
                expectedChangeVersion,
                "no tracked row may advance an extraction watermark past the tombstone"
            );
    }

    private static void AssertAllNewColumnsAreNull(IReadOnlyDictionary<string, object?> trackedRow)
    {
        var newColumns = trackedRow
            .Keys.Where(columnName => columnName.StartsWith("New", StringComparison.Ordinal))
            .ToList();

        newColumns.Should().NotBeEmpty();
        foreach (var columnName in newColumns)
        {
            trackedRow[columnName].Should().BeNull($"tombstone column \"{columnName}\" must be NULL");
        }
    }

    private async Task<long> CountDocumentRowsAsync(long documentId)
    {
        return await CountRowsAsync(
            """SELECT COUNT(*) FROM "dms"."Document" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", documentId)
        );
    }

    private async Task<Guid> GetDocumentUuidAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<Guid>(
            """SELECT "DocumentUuid" FROM "dms"."Document" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", documentId)
        );
    }

    private async Task<long> CountRowsAsync(string sql, params NpgsqlParameter[] parameters)
    {
        return await _database.ExecuteScalarAsync<long>(sql, parameters);
    }

    private async Task<long> ReadMaxChangeVersionAsync()
    {
        return await _database.ExecuteScalarAsync<long>("""SELECT "dms"."GetMaxChangeVersion"();""");
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

    private async Task<DocumentStampState> GetRootMirrorStampStateAsync(
        DbTableModel rootTable,
        long documentId
    )
    {
        var row = (
            await _database.QueryRowsAsync(
                $"""
                SELECT
                    "ContentVersion",
                    "ContentLastModifiedAt"
                FROM "{rootTable.Table.Schema.Value}"."{rootTable.Table.Name}"
                WHERE "DocumentId" = @documentId;
                """,
                new NpgsqlParameter("documentId", documentId)
            )
        ).Single();

        return new(
            Convert.ToInt64(row["ContentVersion"], CultureInfo.InvariantCulture),
            IdentityVersion: 0,
            ReadDateTimeOffset(row["ContentLastModifiedAt"]),
            IdentityLastModifiedAt: DateTimeOffset.UnixEpoch
        );
    }

    private async Task AssertRootMirrorMatchesDocumentAsync(DbTableModel rootTable, long documentId)
    {
        var document = await GetDocumentStampStateAsync(documentId);
        var mirror = await GetRootMirrorStampStateAsync(rootTable, documentId);

        AssertMirrorContentMatchesDocument(mirror, document);
    }

    private static void AssertMirrorContentMatchesDocument(
        DocumentStampState mirror,
        DocumentStampState document
    )
    {
        mirror.ContentVersion.Should().Be(document.ContentVersion);
        mirror.ContentLastModifiedAt.Should().Be(document.ContentLastModifiedAt);
    }

    private async Task<IReadOnlyList<ReferentialIdentityRow>> GetReferentialIdentityRowsForDocumentAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "ReferentialId", "DocumentId", "ResourceKeyId"
            FROM "dms"."ReferentialIdentity"
            WHERE "DocumentId" = @documentId
            ORDER BY "ResourceKeyId", "ReferentialId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(r => new ReferentialIdentityRow(
                (Guid)r["ReferentialId"]!,
                Convert.ToInt64(r["DocumentId"], CultureInfo.InvariantCulture),
                Convert.ToInt16(r["ResourceKeyId"], CultureInfo.InvariantCulture)
            ))
            .ToList();
    }

    // Test-only audit installed in OneTimeSetUp captures every INSERT/DELETE on
    // dms.ReferentialIdentity. The same-value identity tests truncate the audit
    // immediately before the UPDATE under test and assert zero ops afterwards,
    // so a regression that ran a redundant DELETE+INSERT cycle (which produces
    // identical deterministic UUIDv5 rows) would still be caught.
    private async Task InstallReferentialIdentityAuditAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            CREATE SCHEMA IF NOT EXISTS "dms_test";

            CREATE TABLE IF NOT EXISTS "dms_test"."ReferentialIdentityAudit"
            (
                "Op" char(1) NOT NULL,
                "DocumentId" bigint NOT NULL,
                "ResourceKeyId" smallint NOT NULL,
                "ReferentialId" uuid NOT NULL
            );

            CREATE OR REPLACE FUNCTION "dms_test"."ReferentialIdentityAuditFn"() RETURNS trigger AS $audit$
            BEGIN
                IF TG_OP = 'INSERT' THEN
                    INSERT INTO "dms_test"."ReferentialIdentityAudit" ("Op", "DocumentId", "ResourceKeyId", "ReferentialId")
                    VALUES ('I', NEW."DocumentId", NEW."ResourceKeyId", NEW."ReferentialId");
                    RETURN NEW;
                ELSIF TG_OP = 'DELETE' THEN
                    INSERT INTO "dms_test"."ReferentialIdentityAudit" ("Op", "DocumentId", "ResourceKeyId", "ReferentialId")
                    VALUES ('D', OLD."DocumentId", OLD."ResourceKeyId", OLD."ReferentialId");
                    RETURN OLD;
                END IF;
                RETURN NULL;
            END;
            $audit$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS "TR_ReferentialIdentity_Audit" ON "dms"."ReferentialIdentity";
            CREATE TRIGGER "TR_ReferentialIdentity_Audit"
            AFTER INSERT OR DELETE ON "dms"."ReferentialIdentity"
            FOR EACH ROW EXECUTE FUNCTION "dms_test"."ReferentialIdentityAuditFn"();
            """
        );
    }

    private async Task<long> CountReferentialIdentityAuditOpsForDocumentAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)::bigint
            FROM "dms_test"."ReferentialIdentityAudit"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );
    }

    private async Task TruncateReferentialIdentityAuditAsync()
    {
        await _database.ExecuteNonQueryAsync("""TRUNCATE TABLE "dms_test"."ReferentialIdentityAudit";""");
    }

    // Mirrors ReferentialIdFactory in EdFi.DataManagementService.Core.External: the same
    // UUIDv5 namespace + "{ProjectName}{ResourceName}{path1=value1#path2=value2}" hashing
    // used by the generated dms.uuidv5() trigger calls.
    private static readonly Guid s_edFiUuidv5Namespace = new("edf1edf1-3df1-3df1-3df1-3df1edf1edf1");

    private static Guid ComputeReferentialId(
        string projectName,
        string resourceName,
        params (string Path, string Value)[] identityElements
    )
    {
        var identityHash = string.Join("#", identityElements.Select(e => $"{e.Path}={e.Value}"));
        return Deterministic.Create(s_edFiUuidv5Namespace, $"{projectName}{resourceName}{identityHash}");
    }

    private static IReadOnlyList<ReferentialIdentityRow> SortReferentialIdentityRows(
        IEnumerable<ReferentialIdentityRow> rows
    )
    {
        return rows.OrderBy(r => r.ResourceKeyId).ThenBy(r => r.ReferentialId).ToList();
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

    private static string ResolveFixtureDirectory()
    {
        var fixtureDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            TestContext.CurrentContext.TestDirectory,
            FixtureRelativePath
        );

        return RemoveWindowsLongPathPrefix(fixtureDirectory);
    }

    private static string RemoveWindowsLongPathPrefix(string path)
    {
        const string windowsLongPathPrefix = @"\\?\";

        return path.StartsWith(windowsLongPathPrefix, StringComparison.Ordinal)
            ? path[windowsLongPathPrefix.Length..]
            : path;
    }
}
