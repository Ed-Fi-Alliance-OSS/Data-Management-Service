// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed record AuthoritativeSampleSmokeSeedData(
    long SchoolDocumentId,
    long StudentEducationOrganizationAssociationDocumentId,
    long OtherStudentEducationOrganizationAssociationDocumentId,
    long TermDescriptorDocumentId,
    long AlternateTermDescriptorDocumentId,
    long SchoolExtensionDirectlyOwnedBusCollectionItemId,
    long StudentEducationOrganizationAssociationAddressCollectionItemId,
    long StudentEducationOrganizationAssociationExtensionAddressSchoolDistrictCollectionItemId,
    long StudentEducationOrganizationAssociationExtensionAddressTermCollectionItemId
);

[TestFixture]
[NonParallelizable]
public class Given_A_Postgresql_Generated_Ddl_Apply_Harness_With_The_Authoritative_DS_Sample_Fixture_For_Smoke_Coverage
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";
    private const string SchoolExtensionDirectlyOwnedBusConstraintName =
        "FK_SchoolExtensionDirectlyOwnedBus_SchoolExtension";
    private const string StudentEducationOrganizationAssociationExtensionAddressConstraintName =
        "FK_StudentEducationOrganizationAssociationExtensionA_9a76f5ea92";
    private const string StudentEducationOrganizationAssociationExtensionAddressSchoolDistrictTableName =
        "StudentEducationOrganizationAssociationExtensionAddr_5c87dfa8dc";
    private const string StudentEducationOrganizationAssociationExtensionAddressSchoolDistrictConstraintName =
        "FK_StudentEducationOrganizationAssociationExtensionA_de796ca8e1";
    private const string StudentEducationOrganizationAssociationExtensionAddressTermConstraintName =
        "FK_StudentEducationOrganizationAssociationExtensionA_7d01488b71";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private AuthoritativeSampleSmokeSeedData _seedData = null!;
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

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);

        // Reapply the emitted DDL so each smoke test also covers idempotent apply.
        await _database.ApplyGeneratedDdlAsync(_fixture.GeneratedDdl);

        _seedData = await SeedSmokeRowsAsync();
        _schoolExtensionDirectlyOwnedBusForeignKeys = await _database.GetForeignKeyMetadataAsync(
            "sample",
            "SchoolExtensionDirectlyOwnedBus"
        );
        _studentEducationOrganizationAssociationExtensionAddressForeignKeys =
            await _database.GetForeignKeyMetadataAsync(
                "sample",
                "StudentEducationOrganizationAssociationExtensionAddress"
            );
        _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictForeignKeys =
            await _database.GetForeignKeyMetadataAsync(
                "sample",
                StudentEducationOrganizationAssociationExtensionAddressSchoolDistrictTableName
            );
        _studentEducationOrganizationAssociationExtensionAddressTermForeignKeys =
            await _database.GetForeignKeyMetadataAsync(
                "sample",
                "StudentEducationOrganizationAssociationExtensionAddressTerm"
            );

        _schoolExtensionDirectlyOwnedBusCollectionItemDefault = await _database.GetColumnDefaultAsync(
            "sample",
            "SchoolExtensionDirectlyOwnedBus",
            "CollectionItemId"
        );
        _studentEducationOrganizationAssociationAddressCollectionItemDefault =
            await _database.GetColumnDefaultAsync(
                "edfi",
                "StudentEducationOrganizationAssociationAddress",
                "CollectionItemId"
            );
        _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictCollectionItemDefault =
            await _database.GetColumnDefaultAsync(
                "sample",
                StudentEducationOrganizationAssociationExtensionAddressSchoolDistrictTableName,
                "CollectionItemId"
            );
        _studentEducationOrganizationAssociationExtensionAddressTermCollectionItemDefault =
            await _database.GetColumnDefaultAsync(
                "sample",
                "StudentEducationOrganizationAssociationExtensionAddressTerm",
                "CollectionItemId"
            );
    }

    [TearDown]
    public async Task TearDown()
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
    public async Task It_should_enforce_immediate_parent_fk_shapes_for_root_and_collection_aligned_extension_relationships()
    {
        var schoolExtensionDirectlyOwnedBusForeignKey = _schoolExtensionDirectlyOwnedBusForeignKeys.Single(
            foreignKey => foreignKey.ConstraintName == SchoolExtensionDirectlyOwnedBusConstraintName
        );

        schoolExtensionDirectlyOwnedBusForeignKey.Columns.Should().Equal("School_DocumentId");
        schoolExtensionDirectlyOwnedBusForeignKey.ReferencedSchema.Should().Be("sample");
        schoolExtensionDirectlyOwnedBusForeignKey.ReferencedTable.Should().Be("SchoolExtension");
        schoolExtensionDirectlyOwnedBusForeignKey.ReferencedColumns.Should().Equal("DocumentId");
        schoolExtensionDirectlyOwnedBusForeignKey.DeleteAction.Should().Be("CASCADE");
        schoolExtensionDirectlyOwnedBusForeignKey.UpdateAction.Should().Be("NO ACTION");
        _schoolExtensionDirectlyOwnedBusForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema == "edfi" && foreignKey.ReferencedTable == "School"
            );

        var alignedExtensionAddressForeignKey =
            _studentEducationOrganizationAssociationExtensionAddressForeignKeys.Single(foreignKey =>
                foreignKey.ConstraintName
                == StudentEducationOrganizationAssociationExtensionAddressConstraintName
            );

        alignedExtensionAddressForeignKey
            .Columns.Should()
            .Equal("BaseCollectionItemId", "StudentEducationOrganizationAssociation_DocumentId");
        alignedExtensionAddressForeignKey.ReferencedSchema.Should().Be("edfi");
        alignedExtensionAddressForeignKey
            .ReferencedTable.Should()
            .Be("StudentEducationOrganizationAssociationAddress");
        alignedExtensionAddressForeignKey
            .ReferencedColumns.Should()
            .Equal("CollectionItemId", "StudentEducationOrganizationAssociation_DocumentId");
        alignedExtensionAddressForeignKey.DeleteAction.Should().Be("CASCADE");
        alignedExtensionAddressForeignKey.UpdateAction.Should().Be("NO ACTION");
        _studentEducationOrganizationAssociationExtensionAddressForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema == "edfi"
                && foreignKey.ReferencedTable == "StudentEducationOrganizationAssociation"
            );

        var extensionAddressSchoolDistrictForeignKey =
            _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictForeignKeys.Single(
                foreignKey =>
                    foreignKey.ConstraintName
                    == StudentEducationOrganizationAssociationExtensionAddressSchoolDistrictConstraintName
            );

        extensionAddressSchoolDistrictForeignKey
            .Columns.Should()
            .Equal("BaseCollectionItemId", "StudentEducationOrganizationAssociation_DocumentId");
        extensionAddressSchoolDistrictForeignKey.ReferencedSchema.Should().Be("sample");
        extensionAddressSchoolDistrictForeignKey
            .ReferencedTable.Should()
            .Be("StudentEducationOrganizationAssociationExtensionAddress");
        extensionAddressSchoolDistrictForeignKey
            .ReferencedColumns.Should()
            .Equal("BaseCollectionItemId", "StudentEducationOrganizationAssociation_DocumentId");
        extensionAddressSchoolDistrictForeignKey.DeleteAction.Should().Be("CASCADE");
        extensionAddressSchoolDistrictForeignKey.UpdateAction.Should().Be("NO ACTION");
        _studentEducationOrganizationAssociationExtensionAddressSchoolDistrictForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema == "edfi"
                && foreignKey.ReferencedTable == "StudentEducationOrganizationAssociationAddress"
            );

        var extensionAddressTermForeignKey =
            _studentEducationOrganizationAssociationExtensionAddressTermForeignKeys.Single(foreignKey =>
                foreignKey.ConstraintName
                == StudentEducationOrganizationAssociationExtensionAddressTermConstraintName
            );

        extensionAddressTermForeignKey
            .Columns.Should()
            .Equal("BaseCollectionItemId", "StudentEducationOrganizationAssociation_DocumentId");
        extensionAddressTermForeignKey.ReferencedSchema.Should().Be("sample");
        extensionAddressTermForeignKey
            .ReferencedTable.Should()
            .Be("StudentEducationOrganizationAssociationExtensionAddress");
        extensionAddressTermForeignKey
            .ReferencedColumns.Should()
            .Equal("BaseCollectionItemId", "StudentEducationOrganizationAssociation_DocumentId");
        extensionAddressTermForeignKey.DeleteAction.Should().Be("CASCADE");
        extensionAddressTermForeignKey.UpdateAction.Should().Be("NO ACTION");
        _studentEducationOrganizationAssociationExtensionAddressTermForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema == "edfi"
                && foreignKey.ReferencedTable == "StudentEducationOrganizationAssociationAddress"
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
                $"""SELECT COUNT(*) FROM "sample"."{StudentEducationOrganizationAssociationExtensionAddressSchoolDistrictTableName}" WHERE "BaseCollectionItemId" = @baseCollectionItemId;""",
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
            studentEducationOrganizationAssociationDocumentId,
            otherStudentEducationOrganizationAssociationDocumentId,
            termDescriptorDocumentId,
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
            INSERT INTO "sample"."{StudentEducationOrganizationAssociationExtensionAddressSchoolDistrictTableName}" (
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

    private static async Task AssertForeignKeyViolationAsync(Func<Task> act)
    {
        var exception = (await act.Should().ThrowAsync<PostgresException>()).Which;
        exception.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }
}
