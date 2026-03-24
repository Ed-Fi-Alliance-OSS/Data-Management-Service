// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed record AuthoritativeSampleSmokeSeedData(
    long ContactDocumentId,
    long OtherContactDocumentId,
    long AlternateTermDescriptorDocumentId,
    long ContactExtensionAuthorCollectionItemId,
    long ContactAddressCollectionItemId,
    long ContactExtensionAddressSchoolDistrictCollectionItemId,
    long ContactExtensionAddressTermCollectionItemId
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_A_Mssql_Generated_Ddl_Apply_Harness_With_The_Authoritative_DS_Sample_Fixture_For_Smoke_Coverage
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";
    private const string ContactExtensionAuthorConstraintName = "FK_ContactExtensionAuthor_ContactExtension";
    private const string ContactExtensionAddressConstraintName = "FK_ContactExtensionAddress_ContactAddress";
    private const string ContactExtensionAddressSchoolDistrictConstraintName =
        "FK_ContactExtensionAddressSchoolDistrict_ContactExtensionAddress";
    private const string ContactExtensionAddressTermConstraintName =
        "FK_ContactExtensionAddressTerm_ContactExtensionAddress";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private AuthoritativeSampleSmokeSeedData _seedData = null!;
    private IReadOnlyList<MssqlForeignKeyMetadata> _contactExtensionAuthorForeignKeys = null!;
    private IReadOnlyList<MssqlForeignKeyMetadata> _contactExtensionAddressForeignKeys = null!;
    private IReadOnlyList<MssqlForeignKeyMetadata> _contactExtensionAddressSchoolDistrictForeignKeys = null!;
    private IReadOnlyList<MssqlForeignKeyMetadata> _contactExtensionAddressTermForeignKeys = null!;
    private string? _contactExtensionAuthorCollectionItemDefault;
    private string? _contactAddressCollectionItemDefault;
    private string? _contactExtensionAddressSchoolDistrictCollectionItemDefault;
    private string? _contactExtensionAddressTermCollectionItemDefault;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);

        // Reapply the emitted DDL so each smoke test also covers idempotent apply.
        await _database.ApplyGeneratedDdlAsync(_fixture.GeneratedDdl);

        _seedData = await SeedSmokeRowsAsync();
        _contactExtensionAuthorForeignKeys = await _database.GetForeignKeyMetadataAsync(
            "sample",
            "ContactExtensionAuthor"
        );
        _contactExtensionAddressForeignKeys = await _database.GetForeignKeyMetadataAsync(
            "sample",
            "ContactExtensionAddress"
        );
        _contactExtensionAddressSchoolDistrictForeignKeys = await _database.GetForeignKeyMetadataAsync(
            "sample",
            "ContactExtensionAddressSchoolDistrict"
        );
        _contactExtensionAddressTermForeignKeys = await _database.GetForeignKeyMetadataAsync(
            "sample",
            "ContactExtensionAddressTerm"
        );

        _contactExtensionAuthorCollectionItemDefault = await _database.GetColumnDefaultAsync(
            "sample",
            "ContactExtensionAuthor",
            "CollectionItemId"
        );
        _contactAddressCollectionItemDefault = await _database.GetColumnDefaultAsync(
            "edfi",
            "ContactAddress",
            "CollectionItemId"
        );
        _contactExtensionAddressSchoolDistrictCollectionItemDefault = await _database.GetColumnDefaultAsync(
            "sample",
            "ContactExtensionAddressSchoolDistrict",
            "CollectionItemId"
        );
        _contactExtensionAddressTermCollectionItemDefault = await _database.GetColumnDefaultAsync(
            "sample",
            "ContactExtensionAddressTerm",
            "CollectionItemId"
        );
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public async Task It_should_allocate_collection_item_ids_via_defaults_for_representative_real_ds_sample_collection_tables()
    {
        (await _database.SequenceExistsAsync("dms", "CollectionItemIdSequence")).Should().BeTrue();

        _contactExtensionAuthorCollectionItemDefault.Should().NotBeNull();
        _contactExtensionAuthorCollectionItemDefault.Should().Contain("CollectionItemIdSequence");
        _contactExtensionAuthorCollectionItemDefault.Should().Contain("NEXT VALUE FOR");

        _contactAddressCollectionItemDefault.Should().NotBeNull();
        _contactAddressCollectionItemDefault.Should().Contain("CollectionItemIdSequence");
        _contactAddressCollectionItemDefault.Should().Contain("NEXT VALUE FOR");

        _contactExtensionAddressSchoolDistrictCollectionItemDefault.Should().NotBeNull();
        _contactExtensionAddressSchoolDistrictCollectionItemDefault
            .Should()
            .Contain("CollectionItemIdSequence");
        _contactExtensionAddressSchoolDistrictCollectionItemDefault.Should().Contain("NEXT VALUE FOR");

        _contactExtensionAddressTermCollectionItemDefault.Should().NotBeNull();
        _contactExtensionAddressTermCollectionItemDefault.Should().Contain("CollectionItemIdSequence");
        _contactExtensionAddressTermCollectionItemDefault.Should().Contain("NEXT VALUE FOR");

        var generatedCollectionItemIds = new[]
        {
            _seedData.ContactExtensionAuthorCollectionItemId,
            _seedData.ContactAddressCollectionItemId,
            _seedData.ContactExtensionAddressSchoolDistrictCollectionItemId,
            _seedData.ContactExtensionAddressTermCollectionItemId,
        };

        generatedCollectionItemIds.Should().OnlyContain(collectionItemId => collectionItemId > 0);
        generatedCollectionItemIds.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task It_should_enforce_immediate_parent_fk_shapes_for_root_and_collection_aligned_extension_relationships()
    {
        var contactExtensionAuthorForeignKey = _contactExtensionAuthorForeignKeys.Single(foreignKey =>
            foreignKey.ConstraintName == ContactExtensionAuthorConstraintName
        );

        contactExtensionAuthorForeignKey.Columns.Should().Equal("Contact_DocumentId");
        contactExtensionAuthorForeignKey.ReferencedSchema.Should().Be("sample");
        contactExtensionAuthorForeignKey.ReferencedTable.Should().Be("ContactExtension");
        contactExtensionAuthorForeignKey.ReferencedColumns.Should().Equal("DocumentId");
        contactExtensionAuthorForeignKey.DeleteAction.Should().Be("CASCADE");
        contactExtensionAuthorForeignKey.UpdateAction.Should().Be("NO ACTION");
        _contactExtensionAuthorForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema == "edfi" && foreignKey.ReferencedTable == "Contact"
            );

        var contactExtensionAddressForeignKey = _contactExtensionAddressForeignKeys.Single(foreignKey =>
            foreignKey.ConstraintName == ContactExtensionAddressConstraintName
        );

        contactExtensionAddressForeignKey
            .Columns.Should()
            .Equal("BaseCollectionItemId", "Contact_DocumentId");
        contactExtensionAddressForeignKey.ReferencedSchema.Should().Be("edfi");
        contactExtensionAddressForeignKey.ReferencedTable.Should().Be("ContactAddress");
        contactExtensionAddressForeignKey
            .ReferencedColumns.Should()
            .Equal("CollectionItemId", "Contact_DocumentId");
        contactExtensionAddressForeignKey.DeleteAction.Should().Be("CASCADE");
        contactExtensionAddressForeignKey.UpdateAction.Should().Be("NO ACTION");
        _contactExtensionAddressForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema == "edfi" && foreignKey.ReferencedTable == "Contact"
            );

        var contactExtensionAddressSchoolDistrictForeignKey =
            _contactExtensionAddressSchoolDistrictForeignKeys.Single(foreignKey =>
                foreignKey.ConstraintName == ContactExtensionAddressSchoolDistrictConstraintName
            );

        contactExtensionAddressSchoolDistrictForeignKey
            .Columns.Should()
            .Equal("BaseCollectionItemId", "Contact_DocumentId");
        contactExtensionAddressSchoolDistrictForeignKey.ReferencedSchema.Should().Be("sample");
        contactExtensionAddressSchoolDistrictForeignKey
            .ReferencedTable.Should()
            .Be("ContactExtensionAddress");
        contactExtensionAddressSchoolDistrictForeignKey
            .ReferencedColumns.Should()
            .Equal("BaseCollectionItemId", "Contact_DocumentId");
        contactExtensionAddressSchoolDistrictForeignKey.DeleteAction.Should().Be("CASCADE");
        contactExtensionAddressSchoolDistrictForeignKey.UpdateAction.Should().Be("NO ACTION");
        _contactExtensionAddressSchoolDistrictForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema == "edfi" && foreignKey.ReferencedTable == "ContactAddress"
            );

        var contactExtensionAddressTermForeignKey = _contactExtensionAddressTermForeignKeys.Single(
            foreignKey => foreignKey.ConstraintName == ContactExtensionAddressTermConstraintName
        );

        contactExtensionAddressTermForeignKey
            .Columns.Should()
            .Equal("BaseCollectionItemId", "Contact_DocumentId");
        contactExtensionAddressTermForeignKey.ReferencedSchema.Should().Be("sample");
        contactExtensionAddressTermForeignKey.ReferencedTable.Should().Be("ContactExtensionAddress");
        contactExtensionAddressTermForeignKey
            .ReferencedColumns.Should()
            .Equal("BaseCollectionItemId", "Contact_DocumentId");
        contactExtensionAddressTermForeignKey.DeleteAction.Should().Be("CASCADE");
        contactExtensionAddressTermForeignKey.UpdateAction.Should().Be("NO ACTION");
        _contactExtensionAddressTermForeignKeys
            .Should()
            .NotContain(foreignKey =>
                foreignKey.ReferencedSchema == "edfi" && foreignKey.ReferencedTable == "ContactAddress"
            );

        await AssertForeignKeyViolationAsync(async () =>
            await InsertContactExtensionAddressSchoolDistrictAsync(
                _seedData.ContactAddressCollectionItemId,
                _seedData.OtherContactDocumentId,
                2,
                "Wrong-Root-District"
            )
        );

        await AssertForeignKeyViolationAsync(async () =>
            await InsertContactExtensionAddressTermAsync(
                _seedData.ContactAddressCollectionItemId,
                _seedData.OtherContactDocumentId,
                2,
                _seedData.AlternateTermDescriptorDocumentId
            )
        );
    }

    [Test]
    public async Task It_should_delete_descendants_through_the_intended_immediate_parent_chain()
    {
        await _database.ExecuteNonQueryAsync(
            "DELETE FROM [sample].[ContactExtension] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", _seedData.ContactDocumentId)
        );

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [sample].[ContactExtensionAuthor] WHERE [Contact_DocumentId] = @documentId;",
                new SqlParameter("@documentId", _seedData.ContactDocumentId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [edfi].[Contact] WHERE [DocumentId] = @documentId;",
                new SqlParameter("@documentId", _seedData.ContactDocumentId)
            )
        )
            .Should()
            .Be(1);

        await _database.ExecuteNonQueryAsync(
            "DELETE FROM [edfi].[ContactAddress] WHERE [CollectionItemId] = @collectionItemId;",
            new SqlParameter("@collectionItemId", _seedData.ContactAddressCollectionItemId)
        );

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [sample].[ContactExtensionAddress] WHERE [BaseCollectionItemId] = @baseCollectionItemId;",
                new SqlParameter("@baseCollectionItemId", _seedData.ContactAddressCollectionItemId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [sample].[ContactExtensionAddressSchoolDistrict] WHERE [BaseCollectionItemId] = @baseCollectionItemId;",
                new SqlParameter("@baseCollectionItemId", _seedData.ContactAddressCollectionItemId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [sample].[ContactExtensionAddressTerm] WHERE [BaseCollectionItemId] = @baseCollectionItemId;",
                new SqlParameter("@baseCollectionItemId", _seedData.ContactAddressCollectionItemId)
            )
        )
            .Should()
            .Be(0);

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [edfi].[Contact] WHERE [DocumentId] = @documentId;",
                new SqlParameter("@documentId", _seedData.ContactDocumentId)
            )
        )
            .Should()
            .Be(1);
    }

    private async Task<AuthoritativeSampleSmokeSeedData> SeedSmokeRowsAsync()
    {
        var contactResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Contact");
        var addressTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "AddressTypeDescriptor"
        );
        var stateAbbreviationDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "StateAbbreviationDescriptor"
        );
        var termDescriptorResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "TermDescriptor");

        var contactDocumentId = await InsertDocumentAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            contactResourceKeyId
        );
        await InsertContactAsync(contactDocumentId, "10001", "Casey", "Cole");
        await InsertContactExtensionAsync(contactDocumentId);

        var contactExtensionAuthorCollectionItemId = await InsertContactExtensionAuthorAsync(
            contactDocumentId,
            1,
            "Octavia Butler"
        );

        var otherContactDocumentId = await InsertDocumentAsync(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            contactResourceKeyId
        );
        await InsertContactAsync(otherContactDocumentId, "10002", "Morgan", "Lane");

        var addressTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            addressTypeDescriptorResourceKeyId,
            "Ed-Fi:AddressTypeDescriptor",
            "uri://ed-fi.org/AddressTypeDescriptor#Home",
            "uri://ed-fi.org/AddressTypeDescriptor",
            "Home",
            "Home"
        );
        var stateAbbreviationDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            stateAbbreviationDescriptorResourceKeyId,
            "Ed-Fi:StateAbbreviationDescriptor",
            "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
            "uri://ed-fi.org/StateAbbreviationDescriptor",
            "TX",
            "Texas"
        );
        var termDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            termDescriptorResourceKeyId,
            "Ed-Fi:TermDescriptor",
            "uri://ed-fi.org/TermDescriptor#Fall",
            "uri://ed-fi.org/TermDescriptor",
            "Fall",
            "Fall"
        );
        var alternateTermDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            termDescriptorResourceKeyId,
            "Ed-Fi:TermDescriptor",
            "uri://ed-fi.org/TermDescriptor#Spring",
            "uri://ed-fi.org/TermDescriptor",
            "Spring",
            "Spring"
        );

        var contactAddressCollectionItemId = await InsertContactAddressAsync(
            contactDocumentId,
            1,
            addressTypeDescriptorDocumentId,
            stateAbbreviationDescriptorDocumentId,
            "Austin",
            "78701",
            "100 Congress Ave"
        );

        await InsertContactExtensionAddressAsync(
            contactAddressCollectionItemId,
            contactDocumentId,
            "Tower A"
        );

        var contactExtensionAddressSchoolDistrictCollectionItemId =
            await InsertContactExtensionAddressSchoolDistrictAsync(
                contactAddressCollectionItemId,
                contactDocumentId,
                1,
                "District Nine"
            );

        var contactExtensionAddressTermCollectionItemId = await InsertContactExtensionAddressTermAsync(
            contactAddressCollectionItemId,
            contactDocumentId,
            1,
            termDescriptorDocumentId
        );

        return new(
            contactDocumentId,
            otherContactDocumentId,
            alternateTermDescriptorDocumentId,
            contactExtensionAuthorCollectionItemId,
            contactAddressCollectionItemId,
            contactExtensionAddressSchoolDistrictCollectionItemId,
            contactExtensionAddressTermCollectionItemId
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

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
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
            INSERT INTO [dms].[Descriptor] (
                [DocumentId],
                [Namespace],
                [CodeValue],
                [ShortDescription],
                [Description],
                [Discriminator],
                [Uri]
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
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@namespace", @namespace),
            new SqlParameter("@codeValue", codeValue),
            new SqlParameter("@shortDescription", shortDescription),
            new SqlParameter("@description", shortDescription),
            new SqlParameter("@discriminator", discriminator),
            new SqlParameter("@uri", uri)
        );

        return documentId;
    }

    private async Task InsertContactAsync(
        long documentId,
        string contactUniqueId,
        string firstName,
        string lastSurname
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Contact] ([DocumentId], [ContactUniqueId], [FirstName], [LastSurname])
            VALUES (@documentId, @contactUniqueId, @firstName, @lastSurname);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@contactUniqueId", contactUniqueId),
            new SqlParameter("@firstName", firstName),
            new SqlParameter("@lastSurname", lastSurname)
        );
    }

    private async Task InsertContactExtensionAsync(long documentId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [sample].[ContactExtension] ([DocumentId])
            VALUES (@documentId);
            """,
            new SqlParameter("@documentId", documentId)
        );
    }

    private async Task<long> InsertContactExtensionAuthorAsync(
        long contactDocumentId,
        int ordinal,
        string author
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([CollectionItemId] bigint);
            INSERT INTO [sample].[ContactExtensionAuthor] ([Contact_DocumentId], [Ordinal], [Author])
            OUTPUT INSERTED.[CollectionItemId] INTO @Inserted ([CollectionItemId])
            VALUES (@contactDocumentId, @ordinal, @author);
            SELECT TOP (1) [CollectionItemId] FROM @Inserted;
            """,
            new SqlParameter("@contactDocumentId", contactDocumentId),
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@author", author)
        );
    }

    private async Task<long> InsertContactAddressAsync(
        long contactDocumentId,
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
            DECLARE @Inserted TABLE ([CollectionItemId] bigint);
            INSERT INTO [edfi].[ContactAddress] (
                [Contact_DocumentId],
                [Ordinal],
                [AddressTypeDescriptor_DescriptorId],
                [StateAbbreviationDescriptor_DescriptorId],
                [City],
                [PostalCode],
                [StreetNumberName]
            )
            OUTPUT INSERTED.[CollectionItemId] INTO @Inserted ([CollectionItemId])
            VALUES (
                @contactDocumentId,
                @ordinal,
                @addressTypeDescriptorDocumentId,
                @stateAbbreviationDescriptorDocumentId,
                @city,
                @postalCode,
                @streetNumberName
            );
            SELECT TOP (1) [CollectionItemId] FROM @Inserted;
            """,
            new SqlParameter("@contactDocumentId", contactDocumentId),
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@addressTypeDescriptorDocumentId", addressTypeDescriptorDocumentId),
            new SqlParameter("@stateAbbreviationDescriptorDocumentId", stateAbbreviationDescriptorDocumentId),
            new SqlParameter("@city", city),
            new SqlParameter("@postalCode", postalCode),
            new SqlParameter("@streetNumberName", streetNumberName)
        );
    }

    private async Task InsertContactExtensionAddressAsync(
        long baseCollectionItemId,
        long contactDocumentId,
        string complex
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [sample].[ContactExtensionAddress] (
                [BaseCollectionItemId],
                [Contact_DocumentId],
                [Complex]
            )
            VALUES (@baseCollectionItemId, @contactDocumentId, @complex);
            """,
            new SqlParameter("@baseCollectionItemId", baseCollectionItemId),
            new SqlParameter("@contactDocumentId", contactDocumentId),
            new SqlParameter("@complex", complex)
        );
    }

    private async Task<long> InsertContactExtensionAddressSchoolDistrictAsync(
        long baseCollectionItemId,
        long contactDocumentId,
        int ordinal,
        string schoolDistrict
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([CollectionItemId] bigint);
            INSERT INTO [sample].[ContactExtensionAddressSchoolDistrict] (
                [BaseCollectionItemId],
                [Contact_DocumentId],
                [Ordinal],
                [SchoolDistrict]
            )
            OUTPUT INSERTED.[CollectionItemId] INTO @Inserted ([CollectionItemId])
            VALUES (@baseCollectionItemId, @contactDocumentId, @ordinal, @schoolDistrict);
            SELECT TOP (1) [CollectionItemId] FROM @Inserted;
            """,
            new SqlParameter("@baseCollectionItemId", baseCollectionItemId),
            new SqlParameter("@contactDocumentId", contactDocumentId),
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@schoolDistrict", schoolDistrict)
        );
    }

    private async Task<long> InsertContactExtensionAddressTermAsync(
        long baseCollectionItemId,
        long contactDocumentId,
        int ordinal,
        long termDescriptorDocumentId
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([CollectionItemId] bigint);
            INSERT INTO [sample].[ContactExtensionAddressTerm] (
                [BaseCollectionItemId],
                [Contact_DocumentId],
                [Ordinal],
                [TermDescriptor_DescriptorId]
            )
            OUTPUT INSERTED.[CollectionItemId] INTO @Inserted ([CollectionItemId])
            VALUES (@baseCollectionItemId, @contactDocumentId, @ordinal, @termDescriptorDocumentId);
            SELECT TOP (1) [CollectionItemId] FROM @Inserted;
            """,
            new SqlParameter("@baseCollectionItemId", baseCollectionItemId),
            new SqlParameter("@contactDocumentId", contactDocumentId),
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@termDescriptorDocumentId", termDescriptorDocumentId)
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
