// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed record AuthoritativeSampleSmokeSeedData(
    long ContactDocumentId,
    long OtherContactDocumentId,
    long EducationOrganizationDocumentId,
    long AccountabilityRatingDocumentId,
    long SessionDocumentId,
    long SurveyDocumentId,
    long CourseOfferingDocumentId,
    long AlternateTermDescriptorDocumentId,
    long ContactExtensionAuthorCollectionItemId,
    long ContactAddressCollectionItemId,
    long ContactExtensionAddressSchoolDistrictCollectionItemId,
    long ContactExtensionAddressTermCollectionItemId
);

internal sealed record DocumentStampState(
    long ContentVersion,
    long IdentityVersion,
    DateTimeOffset ContentLastModifiedAt,
    DateTimeOffset IdentityLastModifiedAt
);

internal sealed record CourseOfferingSessionReferenceState(
    long SessionDocumentId,
    int SchoolIdUnified,
    int SessionSchoolYear,
    string SessionSessionName
);

internal sealed record SurveySessionReferenceState(
    long SessionDocumentId,
    int SessionSchoolId,
    int SchoolYearUnified,
    string SessionSessionName
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
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
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
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
    public async Task It_should_reapply_the_same_generated_ddl_without_duplicate_generated_seed_rows()
    {
        await _database.ResetAsync();

        var effectiveSchemaCountBefore = await CountRowsAsync(
            "SELECT COUNT(*) FROM [dms].[EffectiveSchema];"
        );
        var resourceKeyCountBefore = await CountRowsAsync("SELECT COUNT(*) FROM [dms].[ResourceKey];");
        var schemaComponentCountBefore = await CountRowsAsync(
            "SELECT COUNT(*) FROM [dms].[SchemaComponent];"
        );

        await _database.ApplyGeneratedDdlAsync(_fixture.GeneratedDdl);

        var effectiveSchemaCountAfter = await CountRowsAsync("SELECT COUNT(*) FROM [dms].[EffectiveSchema];");
        var resourceKeyCountAfter = await CountRowsAsync("SELECT COUNT(*) FROM [dms].[ResourceKey];");
        var schemaComponentCountAfter = await CountRowsAsync("SELECT COUNT(*) FROM [dms].[SchemaComponent];");

        effectiveSchemaCountAfter.Should().Be(effectiveSchemaCountBefore);
        resourceKeyCountAfter.Should().Be(resourceKeyCountBefore);
        schemaComponentCountAfter.Should().Be(schemaComponentCountBefore);
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

    [Test]
    public async Task It_should_stamp_content_only_root_changes_without_touching_identity_stamps()
    {
        var before = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[Contact]
            SET [FirstName] = @firstName
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@firstName", "Jordan"),
            new SqlParameter("@documentId", _seedData.ContactDocumentId)
        );

        var after = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().Be(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().Be(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_stamp_child_representation_changes_without_touching_identity_stamps()
    {
        var before = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[ContactAddress]
            SET [StreetNumberName] = @streetNumberName
            WHERE [CollectionItemId] = @collectionItemId
              AND [Contact_DocumentId] = @documentId;
            """,
            new SqlParameter("@streetNumberName", "101 Congress Ave"),
            new SqlParameter("@collectionItemId", _seedData.ContactAddressCollectionItemId),
            new SqlParameter("@documentId", _seedData.ContactDocumentId)
        );

        var after = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().Be(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().Be(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_stamp_child_inserts_without_touching_identity_stamps()
    {
        var before = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);
        var addressTypeDescriptorDocumentId = await GetDescriptorDocumentIdAsync(
            "Ed-Fi:AddressTypeDescriptor",
            "Home"
        );
        var stateAbbreviationDescriptorDocumentId = await GetDescriptorDocumentIdAsync(
            "Ed-Fi:StateAbbreviationDescriptor",
            "TX"
        );

        await DelayForDistinctTimestampsAsync();
        await InsertContactAddressAsync(
            _seedData.ContactDocumentId,
            2,
            addressTypeDescriptorDocumentId,
            stateAbbreviationDescriptorDocumentId,
            "Austin",
            "78702",
            "200 Congress Ave"
        );

        var after = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().Be(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().Be(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_not_stamp_successful_no_op_child_updates()
    {
        var before = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[ContactAddress]
            SET [StreetNumberName] = [StreetNumberName]
            WHERE [CollectionItemId] = @collectionItemId
              AND [Contact_DocumentId] = @documentId;
            """,
            new SqlParameter("@collectionItemId", _seedData.ContactAddressCollectionItemId),
            new SqlParameter("@documentId", _seedData.ContactDocumentId)
        );

        var after = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        after.Should().Be(before);
    }

    [Test]
    public async Task It_should_stamp_extension_scope_representation_changes_without_touching_identity_stamps()
    {
        var before = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [sample].[ContactExtensionAddress]
            SET [Complex] = @complex
            WHERE [BaseCollectionItemId] = @baseCollectionItemId
              AND [Contact_DocumentId] = @documentId;
            """,
            new SqlParameter("@complex", "Complex-Updated"),
            new SqlParameter("@baseCollectionItemId", _seedData.ContactAddressCollectionItemId),
            new SqlParameter("@documentId", _seedData.ContactDocumentId)
        );

        var after = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().Be(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().Be(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_not_stamp_successful_no_op_extension_scope_updates()
    {
        var before = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [sample].[ContactExtensionAddress]
            SET [Complex] = [Complex]
            WHERE [BaseCollectionItemId] = @baseCollectionItemId
              AND [Contact_DocumentId] = @documentId;
            """,
            new SqlParameter("@baseCollectionItemId", _seedData.ContactAddressCollectionItemId),
            new SqlParameter("@documentId", _seedData.ContactDocumentId)
        );

        var after = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        after.Should().Be(before);
    }

    [Test]
    public async Task It_should_stamp_root_extension_inserts_without_touching_identity_stamps()
    {
        var contactResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Contact");
        var documentId = await InsertDocumentAsync(
            Guid.Parse("12121212-1212-1212-1212-121212121212"),
            contactResourceKeyId
        );
        await InsertContactAsync(documentId, "10003", "Taylor", "Reed");

        var before = await GetDocumentStampStateAsync(documentId);

        await DelayForDistinctTimestampsAsync();
        await InsertContactExtensionAsync(documentId);

        var after = await GetDocumentStampStateAsync(documentId);

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().Be(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().Be(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_stamp_root_identity_changes_as_both_content_and_identity_updates()
    {
        var before = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[Contact]
            SET [ContactUniqueId] = @contactUniqueId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@contactUniqueId", "10001X"),
            new SqlParameter("@documentId", _seedData.ContactDocumentId)
        );

        var after = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().BeGreaterThan(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().BeAfter(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_not_stamp_successful_no_op_root_updates()
    {
        var before = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[Contact]
            SET [FirstName] = [FirstName]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", _seedData.ContactDocumentId)
        );

        var after = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);

        after.Should().Be(before);
    }

    [Test]
    public async Task It_should_not_stamp_same_value_identity_column_root_updates()
    {
        var beforeStamps = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);
        var beforeRiRows = await GetReferentialIdentityRowsForDocumentAsync(_seedData.ContactDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[Contact]
            SET [ContactUniqueId] = [ContactUniqueId]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", _seedData.ContactDocumentId)
        );

        var afterStamps = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);
        var afterRiRows = await GetReferentialIdentityRowsForDocumentAsync(_seedData.ContactDocumentId);

        afterStamps.Should().Be(beforeStamps);
        afterRiRows.Should().Equal(beforeRiRows);
    }

    [Test]
    public async Task It_should_not_stamp_identity_for_content_only_updates_on_identity_propagation_tables()
    {
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("23232323-2323-2323-2323-232323232323"),
            schoolResourceKeyId
        );
        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "edfi",
            "School",
            async () => await InsertSchoolAsync(schoolDocumentId, 101, "North Ridge High")
        );

        var before = await GetDocumentStampStateAsync(schoolDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[School]
            SET [NameOfInstitution] = @nameOfInstitution
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@nameOfInstitution", "North Ridge High Updated"),
            new SqlParameter("@documentId", schoolDocumentId)
        );

        var after = await GetDocumentStampStateAsync(schoolDocumentId);

        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
        after.ContentLastModifiedAt.Should().BeAfter(before.ContentLastModifiedAt);
        after.IdentityVersion.Should().Be(before.IdentityVersion);
        after.IdentityLastModifiedAt.Should().Be(before.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_stamp_indirect_Identity_propagation_changes_via_mssql_trigger_fallback_without_disabling_constraints()
    {
        const string updatedSessionName = "Fall Updated";

        var beforeCourseOfferingSessionReferenceState = await GetCourseOfferingSessionReferenceStateAsync(
            _seedData.CourseOfferingDocumentId
        );
        var beforeSurveySessionReferenceState = await GetSurveySessionReferenceStateAsync(
            _seedData.SurveyDocumentId
        );
        var beforeSession = await GetDocumentStampStateAsync(_seedData.SessionDocumentId);
        var beforeCourseOffering = await GetDocumentStampStateAsync(_seedData.CourseOfferingDocumentId);
        var beforeSurvey = await GetDocumentStampStateAsync(_seedData.SurveyDocumentId);

        beforeCourseOfferingSessionReferenceState
            .Should()
            .Be(new CourseOfferingSessionReferenceState(_seedData.SessionDocumentId, 100, 2025, "Fall"));
        beforeSurveySessionReferenceState
            .Should()
            .Be(new SurveySessionReferenceState(_seedData.SessionDocumentId, 100, 2025, "Fall"));

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[Session]
            SET [SessionName] = @sessionName
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@sessionName", updatedSessionName),
            new SqlParameter("@documentId", _seedData.SessionDocumentId)
        );

        var afterCourseOfferingSessionReferenceState = await GetCourseOfferingSessionReferenceStateAsync(
            _seedData.CourseOfferingDocumentId
        );
        var afterSurveySessionReferenceState = await GetSurveySessionReferenceStateAsync(
            _seedData.SurveyDocumentId
        );
        var afterSession = await GetDocumentStampStateAsync(_seedData.SessionDocumentId);
        var afterCourseOffering = await GetDocumentStampStateAsync(_seedData.CourseOfferingDocumentId);
        var afterSurvey = await GetDocumentStampStateAsync(_seedData.SurveyDocumentId);

        afterCourseOfferingSessionReferenceState
            .Should()
            .Be(beforeCourseOfferingSessionReferenceState with { SessionSessionName = updatedSessionName });
        afterSurveySessionReferenceState
            .Should()
            .Be(beforeSurveySessionReferenceState with { SessionSessionName = updatedSessionName });

        afterSession.ContentVersion.Should().BeGreaterThan(beforeSession.ContentVersion);
        afterSession.ContentLastModifiedAt.Should().BeAfter(beforeSession.ContentLastModifiedAt);
        afterSession.IdentityVersion.Should().BeGreaterThan(beforeSession.IdentityVersion);
        afterSession.IdentityLastModifiedAt.Should().BeAfter(beforeSession.IdentityLastModifiedAt);

        afterCourseOffering.ContentVersion.Should().BeGreaterThan(beforeCourseOffering.ContentVersion);
        afterCourseOffering
            .ContentLastModifiedAt.Should()
            .BeAfter(beforeCourseOffering.ContentLastModifiedAt);
        afterCourseOffering.IdentityVersion.Should().BeGreaterThan(beforeCourseOffering.IdentityVersion);
        afterCourseOffering
            .IdentityLastModifiedAt.Should()
            .BeAfter(beforeCourseOffering.IdentityLastModifiedAt);

        afterSurvey.ContentVersion.Should().BeGreaterThan(beforeSurvey.ContentVersion);
        afterSurvey.ContentLastModifiedAt.Should().BeAfter(beforeSurvey.ContentLastModifiedAt);
        // Survey's identity is Namespace + SurveyIdentifier only; Session_SessionName is a reference
        // field, not part of Survey's identity, so IdentityVersion must not change here.
        afterSurvey.IdentityVersion.Should().Be(beforeSurvey.IdentityVersion);
        afterSurvey.IdentityLastModifiedAt.Should().Be(beforeSurvey.IdentityLastModifiedAt);
    }

    [Test]
    public async Task It_should_not_stamp_same_value_propagated_identity_reference_updates()
    {
        // CourseOffering's INSTEAD OF UPDATE rewrites every column unconditionally, so the
        // AFTER stamp/RI triggers see UPDATE([Session_SessionName]) = true even though the
        // user-issued UPDATE was a same-value self-assignment. Only the null-safe value diff
        // in the trigger body should prevent a false content/identity stamp bump and a
        // redundant RI row rewrite.
        var beforeStamps = await GetDocumentStampStateAsync(_seedData.CourseOfferingDocumentId);
        var beforeRiRows = await GetReferentialIdentityRowsForDocumentAsync(
            _seedData.CourseOfferingDocumentId
        );

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[CourseOffering]
            SET [Session_SessionName] = [Session_SessionName]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", _seedData.CourseOfferingDocumentId)
        );

        var afterStamps = await GetDocumentStampStateAsync(_seedData.CourseOfferingDocumentId);
        var afterRiRows = await GetReferentialIdentityRowsForDocumentAsync(
            _seedData.CourseOfferingDocumentId
        );

        afterStamps.Should().Be(beforeStamps);
        afterRiRows.Should().Equal(beforeRiRows);
    }

    [Test]
    public async Task It_should_allocate_distinct_content_versions_for_multi_row_root_updates()
    {
        var beforeContact = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);
        var beforeOtherContact = await GetDocumentStampStateAsync(_seedData.OtherContactDocumentId);

        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[Contact]
            SET [FirstName] = [FirstName] + N' Updated'
            WHERE [DocumentId] IN (@firstDocumentId, @secondDocumentId);
            """,
            new SqlParameter("@firstDocumentId", _seedData.ContactDocumentId),
            new SqlParameter("@secondDocumentId", _seedData.OtherContactDocumentId)
        );

        var afterContact = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);
        var afterOtherContact = await GetDocumentStampStateAsync(_seedData.OtherContactDocumentId);

        afterContact.ContentVersion.Should().BeGreaterThan(beforeContact.ContentVersion);
        afterOtherContact.ContentVersion.Should().BeGreaterThan(beforeOtherContact.ContentVersion);
        afterContact.ContentVersion.Should().NotBe(afterOtherContact.ContentVersion);
        afterContact.IdentityVersion.Should().Be(beforeContact.IdentityVersion);
        afterOtherContact.IdentityVersion.Should().Be(beforeOtherContact.IdentityVersion);
    }

    private async Task<AuthoritativeSampleSmokeSeedData> SeedSmokeRowsAsync()
    {
        var accountabilityRatingResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "AccountabilityRating");
        var contactResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Contact");
        var courseOfferingResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "CourseOffering");
        var courseResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Course");
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var schoolYearTypeResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var sessionResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Session");
        var surveyResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Survey");
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

        var educationOrganizationDocumentId = await InsertDocumentAsync(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            schoolResourceKeyId
        );
        await InsertEducationOrganizationIdentityAsync(educationOrganizationDocumentId, 100, "Ed-Fi:School");

        var schoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(schoolYearTypeDocumentId, 2025);

        var accountabilityRatingDocumentId = await InsertDocumentAsync(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            accountabilityRatingResourceKeyId
        );
        await InsertAccountabilityRatingAsync(
            accountabilityRatingDocumentId,
            educationOrganizationDocumentId,
            100,
            schoolYearTypeDocumentId,
            2025,
            "Exceeds Standard",
            "Accountability"
        );

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

        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            schoolResourceKeyId
        );
        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "edfi",
            "School",
            async () => await InsertSchoolAsync(schoolDocumentId, 100, "Sample High School")
        );

        var sessionDocumentId = await InsertDocumentAsync(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            sessionResourceKeyId
        );
        await InsertSessionAsync(
            sessionDocumentId,
            schoolYearTypeDocumentId,
            2025,
            schoolDocumentId,
            100,
            termDescriptorDocumentId,
            new DateOnly(2025, 8, 1),
            new DateOnly(2025, 12, 31),
            "Fall",
            90
        );

        var surveyDocumentId = await InsertDocumentAsync(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            surveyResourceKeyId
        );
        await InsertSurveyAsync(
            surveyDocumentId,
            schoolYearTypeDocumentId,
            2025,
            sessionDocumentId,
            100,
            "Fall",
            "uri://ed-fi.org/Survey",
            "student-climate",
            "Student Climate Survey"
        );

        var courseDocumentId = await InsertDocumentAsync(
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            courseResourceKeyId
        );
        await InsertCourseAsync(
            courseDocumentId,
            educationOrganizationDocumentId,
            100,
            "ALG-1",
            "Algebra I",
            1
        );

        var courseOfferingDocumentId = await InsertDocumentAsync(
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            courseOfferingResourceKeyId
        );
        await InsertCourseOfferingAsync(
            courseOfferingDocumentId,
            100,
            courseDocumentId,
            "ALG-1",
            100,
            schoolDocumentId,
            sessionDocumentId,
            2025,
            "Fall",
            "ALG-1-01"
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
            educationOrganizationDocumentId,
            accountabilityRatingDocumentId,
            sessionDocumentId,
            surveyDocumentId,
            courseOfferingDocumentId,
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

    private async Task<long> GetDescriptorDocumentIdAsync(string discriminator, string codeValue)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT [DocumentId]
            FROM [dms].[Descriptor]
            WHERE [Discriminator] = @discriminator
              AND [CodeValue] = @codeValue;
            """,
            new SqlParameter("@discriminator", discriminator),
            new SqlParameter("@codeValue", codeValue)
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

    private async Task InsertEducationOrganizationIdentityAsync(
        long documentId,
        int educationOrganizationId,
        string discriminator
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[EducationOrganizationIdentity] ([DocumentId], [EducationOrganizationId], [Discriminator])
            VALUES (@documentId, @educationOrganizationId, @discriminator);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@educationOrganizationId", educationOrganizationId),
            new SqlParameter("@discriminator", discriminator)
        );
    }

    private async Task InsertSchoolYearTypeAsync(long documentId, int schoolYear)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[SchoolYearType] (
                [DocumentId],
                [CurrentSchoolYear],
                [SchoolYear],
                [SchoolYearDescription]
            )
            VALUES (
                @documentId,
                @currentSchoolYear,
                @schoolYear,
                @schoolYearDescription
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@currentSchoolYear", true),
            new SqlParameter("@schoolYear", schoolYear),
            new SqlParameter("@schoolYearDescription", $"{schoolYear}-{schoolYear + 1}")
        );
    }

    private async Task InsertSchoolAsync(long documentId, int schoolId, string nameOfInstitution)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[School] ([DocumentId], [NameOfInstitution], [SchoolId])
            VALUES (@documentId, @nameOfInstitution, @schoolId);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@nameOfInstitution", nameOfInstitution),
            new SqlParameter("@schoolId", schoolId)
        );
    }

    private async Task InsertSessionAsync(
        long documentId,
        long schoolYearDocumentId,
        int schoolYear,
        long schoolDocumentId,
        int schoolId,
        long termDescriptorDocumentId,
        DateOnly beginDate,
        DateOnly endDate,
        string sessionName,
        int totalInstructionalDays
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Session] (
                [DocumentId],
                [SchoolYear_DocumentId],
                [SchoolYear_SchoolYear],
                [School_DocumentId],
                [School_SchoolId],
                [TermDescriptor_DescriptorId],
                [BeginDate],
                [EndDate],
                [SessionName],
                [TotalInstructionalDays]
            )
            VALUES (
                @documentId,
                @schoolYearDocumentId,
                @schoolYear,
                @schoolDocumentId,
                @schoolId,
                @termDescriptorDocumentId,
                @beginDate,
                @endDate,
                @sessionName,
                @totalInstructionalDays
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolYearDocumentId", schoolYearDocumentId),
            new SqlParameter("@schoolYear", schoolYear),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@termDescriptorDocumentId", termDescriptorDocumentId),
            new SqlParameter("@beginDate", beginDate),
            new SqlParameter("@endDate", endDate),
            new SqlParameter("@sessionName", sessionName),
            new SqlParameter("@totalInstructionalDays", totalInstructionalDays)
        );
    }

    private async Task InsertCourseAsync(
        long documentId,
        long educationOrganizationDocumentId,
        int educationOrganizationId,
        string courseCode,
        string courseTitle,
        int numberOfParts
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Course] (
                [DocumentId],
                [EducationOrganization_DocumentId],
                [EducationOrganization_EducationOrganizationId],
                [CourseCode],
                [CourseTitle],
                [NumberOfParts]
            )
            VALUES (
                @documentId,
                @educationOrganizationDocumentId,
                @educationOrganizationId,
                @courseCode,
                @courseTitle,
                @numberOfParts
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@educationOrganizationDocumentId", educationOrganizationDocumentId),
            new SqlParameter("@educationOrganizationId", educationOrganizationId),
            new SqlParameter("@courseCode", courseCode),
            new SqlParameter("@courseTitle", courseTitle),
            new SqlParameter("@numberOfParts", numberOfParts)
        );
    }

    private async Task InsertCourseOfferingAsync(
        long documentId,
        int schoolId,
        long courseDocumentId,
        string courseCode,
        int courseEducationOrganizationId,
        long schoolDocumentId,
        long sessionDocumentId,
        int sessionSchoolYear,
        string sessionName,
        string localCourseCode
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[CourseOffering] (
                [DocumentId],
                [SchoolId_Unified],
                [Course_DocumentId],
                [Course_CourseCode],
                [Course_EducationOrganizationId],
                [School_DocumentId],
                [Session_DocumentId],
                [Session_SchoolYear],
                [Session_SessionName],
                [LocalCourseCode]
            )
            VALUES (
                @documentId,
                @schoolId,
                @courseDocumentId,
                @courseCode,
                @courseEducationOrganizationId,
                @schoolDocumentId,
                @sessionDocumentId,
                @sessionSchoolYear,
                @sessionName,
                @localCourseCode
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@courseDocumentId", courseDocumentId),
            new SqlParameter("@courseCode", courseCode),
            new SqlParameter("@courseEducationOrganizationId", courseEducationOrganizationId),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@sessionDocumentId", sessionDocumentId),
            new SqlParameter("@sessionSchoolYear", sessionSchoolYear),
            new SqlParameter("@sessionName", sessionName),
            new SqlParameter("@localCourseCode", localCourseCode)
        );
    }

    private async Task InsertSurveyAsync(
        long documentId,
        long schoolYearDocumentId,
        int schoolYear,
        long sessionDocumentId,
        int sessionSchoolId,
        string sessionName,
        string @namespace,
        string surveyIdentifier,
        string surveyTitle
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Survey] (
                [DocumentId],
                [SchoolYear_Unified],
                [SchoolYear_DocumentId],
                [Session_DocumentId],
                [Session_SchoolId],
                [Session_SessionName],
                [Namespace],
                [SurveyIdentifier],
                [SurveyTitle]
            )
            VALUES (
                @documentId,
                @schoolYear,
                @schoolYearDocumentId,
                @sessionDocumentId,
                @sessionSchoolId,
                @sessionName,
                @namespace,
                @surveyIdentifier,
                @surveyTitle
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolYear", schoolYear),
            new SqlParameter("@schoolYearDocumentId", schoolYearDocumentId),
            new SqlParameter("@sessionDocumentId", sessionDocumentId),
            new SqlParameter("@sessionSchoolId", sessionSchoolId),
            new SqlParameter("@sessionName", sessionName),
            new SqlParameter("@namespace", @namespace),
            new SqlParameter("@surveyIdentifier", surveyIdentifier),
            new SqlParameter("@surveyTitle", surveyTitle)
        );
    }

    private async Task InsertAccountabilityRatingAsync(
        long documentId,
        long educationOrganizationDocumentId,
        int educationOrganizationId,
        long schoolYearDocumentId,
        int schoolYear,
        string rating,
        string ratingTitle
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[AccountabilityRating] (
                [DocumentId],
                [EducationOrganization_DocumentId],
                [EducationOrganization_EducationOrganizationId],
                [SchoolYear_DocumentId],
                [SchoolYear_SchoolYear],
                [Rating],
                [RatingTitle]
            )
            VALUES (
                @documentId,
                @educationOrganizationDocumentId,
                @educationOrganizationId,
                @schoolYearDocumentId,
                @schoolYear,
                @rating,
                @ratingTitle
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@educationOrganizationDocumentId", educationOrganizationDocumentId),
            new SqlParameter("@educationOrganizationId", educationOrganizationId),
            new SqlParameter("@schoolYearDocumentId", schoolYearDocumentId),
            new SqlParameter("@schoolYear", schoolYear),
            new SqlParameter("@rating", rating),
            new SqlParameter("@ratingTitle", ratingTitle)
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

    private async Task<DocumentStampState> GetDocumentStampStateAsync(long documentId)
    {
        var row = (
            await _database.QueryRowsAsync(
                """
                SELECT
                    [ContentVersion],
                    [IdentityVersion],
                    [ContentLastModifiedAt],
                    [IdentityLastModifiedAt]
                FROM [dms].[Document]
                WHERE [DocumentId] = @documentId;
                """,
                new SqlParameter("@documentId", documentId)
            )
        ).Single();

        return new(
            Convert.ToInt64(row["ContentVersion"], CultureInfo.InvariantCulture),
            Convert.ToInt64(row["IdentityVersion"], CultureInfo.InvariantCulture),
            ReadDateTimeOffset(row["ContentLastModifiedAt"]),
            ReadDateTimeOffset(row["IdentityLastModifiedAt"])
        );
    }

    private async Task<IReadOnlyList<ReferentialIdentityRow>> GetReferentialIdentityRowsForDocumentAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT [ReferentialId], [DocumentId], [ResourceKeyId]
            FROM [dms].[ReferentialIdentity]
            WHERE [DocumentId] = @documentId
            ORDER BY [ResourceKeyId], [ReferentialId];
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Select(r => new ReferentialIdentityRow(
                (Guid)r["ReferentialId"]!,
                Convert.ToInt64(r["DocumentId"], CultureInfo.InvariantCulture),
                Convert.ToInt16(r["ResourceKeyId"], CultureInfo.InvariantCulture)
            ))
            .ToList();
    }

    private async Task<CourseOfferingSessionReferenceState> GetCourseOfferingSessionReferenceStateAsync(
        long documentId
    )
    {
        var row = (
            await _database.QueryRowsAsync(
                """
                SELECT
                    [Session_DocumentId],
                    [SchoolId_Unified],
                    [Session_SchoolYear],
                    [Session_SessionName]
                FROM [edfi].[CourseOffering]
                WHERE [DocumentId] = @documentId;
                """,
                new SqlParameter("@documentId", documentId)
            )
        ).Single();

        return new(
            Convert.ToInt64(row["Session_DocumentId"], CultureInfo.InvariantCulture),
            Convert.ToInt32(row["SchoolId_Unified"], CultureInfo.InvariantCulture),
            Convert.ToInt32(row["Session_SchoolYear"], CultureInfo.InvariantCulture),
            ReadRequiredString(row["Session_SessionName"])
        );
    }

    private async Task<SurveySessionReferenceState> GetSurveySessionReferenceStateAsync(long documentId)
    {
        var row = (
            await _database.QueryRowsAsync(
                """
                SELECT
                    [Session_DocumentId],
                    [Session_SchoolId],
                    [SchoolYear_Unified],
                    [Session_SessionName]
                FROM [edfi].[Survey]
                WHERE [DocumentId] = @documentId;
                """,
                new SqlParameter("@documentId", documentId)
            )
        ).Single();

        return new(
            Convert.ToInt64(row["Session_DocumentId"], CultureInfo.InvariantCulture),
            Convert.ToInt32(row["Session_SchoolId"], CultureInfo.InvariantCulture),
            Convert.ToInt32(row["SchoolYear_Unified"], CultureInfo.InvariantCulture),
            ReadRequiredString(row["Session_SessionName"])
        );
    }

    private async Task ExecuteWithTriggersTemporarilyDisabledAsync(
        string schema,
        string table,
        Func<Task> action
    )
    {
        await _database.ExecuteNonQueryAsync(
            $"""
            DISABLE TRIGGER ALL ON [{schema}].[{table}];
            """
        );

        try
        {
            await action();
        }
        finally
        {
            await _database.ExecuteNonQueryAsync(
                $"""
                ENABLE TRIGGER ALL ON [{schema}].[{table}];
                """
            );
        }
    }

    private async Task DelayForDistinctTimestampsAsync()
    {
        await _database.ExecuteNonQueryAsync("""WAITFOR DELAY '00:00:00.050';""");
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

    private static string ReadRequiredString(object? value)
    {
        return value as string
            ?? throw new InvalidOperationException(
                $"Unsupported string value type '{value?.GetType().FullName ?? "<null>"}'."
            );
    }

    private static async Task AssertForeignKeyViolationAsync(Func<Task> act)
    {
        var exception = (await act.Should().ThrowAsync<SqlException>()).Which;
        exception.Number.Should().Be(547);
    }
}
