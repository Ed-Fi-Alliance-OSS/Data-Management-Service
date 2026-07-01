// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Be.Vlaanderen.Basisregisters.Generators.Guid;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
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
    long ContactExtensionAddressTermCollectionItemId,
    long SchoolDocumentId,
    long SchoolYearTypeDocumentId
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
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
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
    private IMssqlGeneratedDdlBaselineLease _databaseLease = null!;
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
        _databaseLease = await MssqlBackendBaselineCache.AcquireLeaseAsync(
            FixtureRelativePath,
            strict: true,
            _fixture.GeneratedDdl
        );
        _database = _databaseLease.Database;
        await InstallReferentialIdentityAuditAsync();
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
        if (_databaseLease is not null)
        {
            await _databaseLease.DisposeAsync();
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
        var beforeMirror = await GetRootMirrorStampStateAsync("edfi", "Contact", _seedData.ContactDocumentId);
        AssertMirrorContentMatchesDocument(beforeMirror, before);

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
        var afterMirror = await GetRootMirrorStampStateAsync("edfi", "Contact", _seedData.ContactDocumentId);

        after.Should().Be(before);
        afterMirror.Should().Be(beforeMirror);
        AssertMirrorContentMatchesDocument(afterMirror, after);
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
        var beforeMirror = await GetRootMirrorStampStateAsync("edfi", "Contact", _seedData.ContactDocumentId);
        AssertMirrorContentMatchesDocument(beforeMirror, before);

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
        var afterMirror = await GetRootMirrorStampStateAsync("edfi", "Contact", _seedData.ContactDocumentId);

        after.Should().Be(before);
        afterMirror.Should().Be(beforeMirror);
        AssertMirrorContentMatchesDocument(afterMirror, after);
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
        var beforeMirror = await GetRootMirrorStampStateAsync("edfi", "Contact", _seedData.ContactDocumentId);
        AssertMirrorContentMatchesDocument(beforeMirror, before);

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
        var afterMirror = await GetRootMirrorStampStateAsync("edfi", "Contact", _seedData.ContactDocumentId);

        after.Should().Be(before);
        afterMirror.Should().Be(beforeMirror);
        AssertMirrorContentMatchesDocument(afterMirror, after);
    }

    [Test]
    public async Task It_should_not_stamp_same_value_identity_column_root_updates()
    {
        var contactResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Contact");
        var expectedRiRows = SortReferentialIdentityRows(
            new[]
            {
                new ReferentialIdentityRow(
                    ComputeReferentialId("Ed-Fi", "Contact", ("$.contactUniqueId", "10001")),
                    _seedData.ContactDocumentId,
                    contactResourceKeyId
                ),
            }
        );

        var beforeStamps = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);
        var beforeRiRows = await GetReferentialIdentityRowsForDocumentAsync(_seedData.ContactDocumentId);
        beforeRiRows.Should().Equal(expectedRiRows);

        await TruncateReferentialIdentityAuditAsync();
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
        var auditOps = await CountReferentialIdentityAuditOpsForDocumentAsync(_seedData.ContactDocumentId);

        afterStamps.Should().Be(beforeStamps);
        afterRiRows.Should().Equal(beforeRiRows);
        auditOps.Should().Be(0);
    }

    [Test]
    public async Task It_should_not_stamp_identity_when_scalar_identity_column_is_self_assigned_alongside_content_change()
    {
        // The pure same-value test above is filtered out before the inner identity-only
        // gate runs. This test sends a content change AND a same-value self-assignment
        // of the identity column in one UPDATE — UPDATE([ContactUniqueId]) returns true
        // (the column appeared in SET), so the inner null-safe value-diff predicate is
        // what must keep IdentityVersion and dms.ReferentialIdentity untouched.
        var contactResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Contact");
        var expectedRiRows = SortReferentialIdentityRows(
            new[]
            {
                new ReferentialIdentityRow(
                    ComputeReferentialId("Ed-Fi", "Contact", ("$.contactUniqueId", "10001")),
                    _seedData.ContactDocumentId,
                    contactResourceKeyId
                ),
            }
        );

        var beforeStamps = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);
        var beforeRiRows = await GetReferentialIdentityRowsForDocumentAsync(_seedData.ContactDocumentId);
        beforeRiRows.Should().Equal(expectedRiRows);

        await TruncateReferentialIdentityAuditAsync();
        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[Contact]
            SET [FirstName] = @firstName,
                [ContactUniqueId] = [ContactUniqueId]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@firstName", "Casey Renamed"),
            new SqlParameter("@documentId", _seedData.ContactDocumentId)
        );

        var afterStamps = await GetDocumentStampStateAsync(_seedData.ContactDocumentId);
        var afterRiRows = await GetReferentialIdentityRowsForDocumentAsync(_seedData.ContactDocumentId);
        var auditOps = await CountReferentialIdentityAuditOpsForDocumentAsync(_seedData.ContactDocumentId);

        afterStamps.ContentVersion.Should().BeGreaterThan(beforeStamps.ContentVersion);
        afterStamps.ContentLastModifiedAt.Should().BeAfter(beforeStamps.ContentLastModifiedAt);
        afterStamps.IdentityVersion.Should().Be(beforeStamps.IdentityVersion);
        afterStamps.IdentityLastModifiedAt.Should().Be(beforeStamps.IdentityLastModifiedAt);
        afterRiRows.Should().Equal(beforeRiRows);
        auditOps.Should().Be(0);
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

        var sessionDocumentUuid = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var courseOfferingDocumentUuid = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var surveyDocumentUuid = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var sessionTrackedBefore = await CountTrackedChangeRowsAsync(
            "tracked_changes_edfi",
            "Session",
            sessionDocumentUuid
        );
        var courseOfferingTrackedBefore = await CountTrackedChangeRowsAsync(
            "tracked_changes_edfi",
            "CourseOffering",
            courseOfferingDocumentUuid
        );
        var surveyTrackedBefore = await CountTrackedChangeRowsAsync(
            "tracked_changes_edfi",
            "Survey",
            surveyDocumentUuid
        );

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
        var afterSessionMirror = await GetRootMirrorStampStateAsync(
            "edfi",
            "Session",
            _seedData.SessionDocumentId
        );
        var afterCourseOfferingMirror = await GetRootMirrorStampStateAsync(
            "edfi",
            "CourseOffering",
            _seedData.CourseOfferingDocumentId
        );
        var afterSurveyMirror = await GetRootMirrorStampStateAsync(
            "edfi",
            "Survey",
            _seedData.SurveyDocumentId
        );

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

        AssertMirrorContentMatchesDocument(afterSessionMirror, afterSession);
        AssertMirrorContentMatchesDocument(afterCourseOfferingMirror, afterCourseOffering);
        AssertMirrorContentMatchesDocument(afterSurveyMirror, afterSurvey);

        // The renamed Session and the cascade-updated CourseOffering each insert
        // exactly one key-change row. Survey's session reference is NOT part of its
        // identity projection (Namespace + SurveyIdentifier), so the trigger-fallback
        // rewrite must not produce a Survey key-change row.
        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "Session", sessionDocumentUuid))
            .Should()
            .Be(sessionTrackedBefore + 1);
        (
            await CountTrackedChangeRowsAsync(
                "tracked_changes_edfi",
                "CourseOffering",
                courseOfferingDocumentUuid
            )
        )
            .Should()
            .Be(courseOfferingTrackedBefore + 1);
        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "Survey", surveyDocumentUuid))
            .Should()
            .Be(surveyTrackedBefore);

        var sessionTrackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "Session",
            sessionDocumentUuid
        );
        sessionTrackedRow["Old_SessionName"].Should().Be("Fall");
        sessionTrackedRow["New_SessionName"].Should().Be(updatedSessionName);
        Convert
            .ToInt64(sessionTrackedRow["ChangeVersion"], CultureInfo.InvariantCulture)
            .Should()
            .Be(afterSession.ContentVersion);

        var courseOfferingTrackedRow = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "CourseOffering",
            courseOfferingDocumentUuid
        );
        courseOfferingTrackedRow["Old_Session_SessionName"].Should().Be("Fall");
        courseOfferingTrackedRow["New_Session_SessionName"].Should().Be(updatedSessionName);
        Convert
            .ToInt64(courseOfferingTrackedRow["ChangeVersion"], CultureInfo.InvariantCulture)
            .Should()
            .Be(afterCourseOffering.ContentVersion);
    }

    [Test]
    public async Task It_should_not_stamp_same_value_propagated_identity_reference_updates()
    {
        // CourseOffering's INSTEAD OF UPDATE rewrites every column unconditionally, so the
        // AFTER stamp/RI triggers see UPDATE([Session_SessionName]) = true even though the
        // user-issued UPDATE was a same-value self-assignment. Only the null-safe value diff
        // in the trigger body should prevent a false content/identity stamp bump and a
        // redundant RI row rewrite.
        var courseOfferingResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "CourseOffering");
        var expectedRiRows = SortReferentialIdentityRows(
            new[]
            {
                new ReferentialIdentityRow(
                    ComputeReferentialId(
                        "Ed-Fi",
                        "CourseOffering",
                        ("$.localCourseCode", "ALG-1-01"),
                        ("$.schoolReference.schoolId", "100"),
                        ("$.sessionReference.schoolId", "100"),
                        ("$.sessionReference.schoolYear", "2025"),
                        ("$.sessionReference.sessionName", "Fall")
                    ),
                    _seedData.CourseOfferingDocumentId,
                    courseOfferingResourceKeyId
                ),
            }
        );

        var beforeStamps = await GetDocumentStampStateAsync(_seedData.CourseOfferingDocumentId);
        var beforeMirror = await GetRootMirrorStampStateAsync(
            "edfi",
            "CourseOffering",
            _seedData.CourseOfferingDocumentId
        );
        AssertMirrorContentMatchesDocument(beforeMirror, beforeStamps);
        var beforeRiRows = await GetReferentialIdentityRowsForDocumentAsync(
            _seedData.CourseOfferingDocumentId
        );
        beforeRiRows.Should().Equal(expectedRiRows);

        await TruncateReferentialIdentityAuditAsync();
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
        var afterMirror = await GetRootMirrorStampStateAsync(
            "edfi",
            "CourseOffering",
            _seedData.CourseOfferingDocumentId
        );
        var afterRiRows = await GetReferentialIdentityRowsForDocumentAsync(
            _seedData.CourseOfferingDocumentId
        );
        var auditOps = await CountReferentialIdentityAuditOpsForDocumentAsync(
            _seedData.CourseOfferingDocumentId
        );

        afterStamps.Should().Be(beforeStamps);
        afterMirror.Should().Be(beforeMirror);
        AssertMirrorContentMatchesDocument(afterMirror, afterStamps);
        afterRiRows.Should().Equal(beforeRiRows);
        auditOps.Should().Be(0);
    }

    [Test]
    public async Task It_should_not_stamp_identity_when_propagated_identity_reference_is_self_assigned_alongside_content_change()
    {
        // Mixed-write counterpart for the propagated identity-source reference column:
        // a non-identity content column changes while the propagated identity column is
        // self-assigned to the same value. UPDATE([Session_SessionName]) returns true
        // (and CourseOffering's INSTEAD OF UPDATE rewrites every column anyway), so the
        // null-safe value-diff predicate is the sole protection against false
        // IdentityVersion bumps and redundant RI rewrites.
        var courseOfferingResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "CourseOffering");
        var expectedRiRows = SortReferentialIdentityRows(
            new[]
            {
                new ReferentialIdentityRow(
                    ComputeReferentialId(
                        "Ed-Fi",
                        "CourseOffering",
                        ("$.localCourseCode", "ALG-1-01"),
                        ("$.schoolReference.schoolId", "100"),
                        ("$.sessionReference.schoolId", "100"),
                        ("$.sessionReference.schoolYear", "2025"),
                        ("$.sessionReference.sessionName", "Fall")
                    ),
                    _seedData.CourseOfferingDocumentId,
                    courseOfferingResourceKeyId
                ),
            }
        );

        var beforeStamps = await GetDocumentStampStateAsync(_seedData.CourseOfferingDocumentId);
        var beforeRiRows = await GetReferentialIdentityRowsForDocumentAsync(
            _seedData.CourseOfferingDocumentId
        );
        beforeRiRows.Should().Equal(expectedRiRows);

        await TruncateReferentialIdentityAuditAsync();
        await DelayForDistinctTimestampsAsync();
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[CourseOffering]
            SET [LocalCourseTitle] = @localCourseTitle,
                [Session_SessionName] = [Session_SessionName]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@localCourseTitle", "Algebra Renamed"),
            new SqlParameter("@documentId", _seedData.CourseOfferingDocumentId)
        );

        var afterStamps = await GetDocumentStampStateAsync(_seedData.CourseOfferingDocumentId);
        var afterRiRows = await GetReferentialIdentityRowsForDocumentAsync(
            _seedData.CourseOfferingDocumentId
        );
        var auditOps = await CountReferentialIdentityAuditOpsForDocumentAsync(
            _seedData.CourseOfferingDocumentId
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
        await AssertRootMirrorMatchesDocumentAsync("sample", "Bus", busDocumentId);

        await DelayForDistinctTimestampsAsync();
        var updateRowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [sample].[Bus]
            SET [BusId] = @busId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@busId", updatedBusId),
            new SqlParameter("@documentId", busDocumentId)
        );
        updateRowsAffected.Should().Be(1);

        var afterUpdate = await GetDocumentStampStateAsync(busDocumentId);
        afterUpdate.ContentVersion.Should().BeGreaterThan(afterInsert.ContentVersion);
        afterUpdate.IdentityVersion.Should().BeGreaterThan(afterInsert.IdentityVersion);
        await AssertRootMirrorMatchesDocumentAsync("sample", "Bus", busDocumentId);

        var beforeNoOpMirror = await GetRootMirrorStampStateAsync("sample", "Bus", busDocumentId);

        await DelayForDistinctTimestampsAsync();
        var noOpRowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [sample].[Bus]
            SET [BusId] = [BusId]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", busDocumentId)
        );
        noOpRowsAffected.Should().Be(1);

        var afterNoOp = await GetDocumentStampStateAsync(busDocumentId);
        var afterNoOpMirror = await GetRootMirrorStampStateAsync("sample", "Bus", busDocumentId);
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
        var beforeMirror = await GetRootMirrorStampStateAsync("sample", "Bus", busDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var trackedChangeRowsBefore = await CountRowsAsync(
            "SELECT COUNT(*) FROM [tracked_changes_sample].[Bus];"
        );
        AssertMirrorContentMatchesDocument(beforeMirror, beforeDocument);

        await DelayForDistinctTimestampsAsync();
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [sample].[Bus]
            SET [ContentVersion] = [ContentVersion] + 1
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", busDocumentId)
        );
        rowsAffected.Should().Be(1);

        var afterDocument = await GetDocumentStampStateAsync(busDocumentId);
        var afterMirror = await GetRootMirrorStampStateAsync("sample", "Bus", busDocumentId);
        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var trackedChangeRowsAfter = await CountRowsAsync(
            "SELECT COUNT(*) FROM [tracked_changes_sample].[Bus];"
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
    //   d5-d7 seeds → It_should_insert_key_change_rows_only_for_identity_changed_rows_in_a_mixed_workset_update
    //   e1-e3 seeds → It_should_use_canonical_columns_for_key_unified_paths
    //   e5-e7 seeds → It_should_project_old_and_new_descriptor_values_from_their_own_images_on_key_change (e4 unused)
    //   b5-b7 seeds → It_should_project_a_null_to_value_transition_in_the_key_change_row
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
            "edfi",
            "StudentEducationOrganizationAssociation",
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
                trackedRow["Old_EducationOrganization_EducationOrganizationId"],
                CultureInfo.InvariantCulture
            )
            .Should()
            .Be(seed.OriginalEducationOrganizationId);
        Convert
            .ToInt64(
                trackedRow["New_EducationOrganization_EducationOrganizationId"],
                CultureInfo.InvariantCulture
            )
            .Should()
            .Be(seed.ReplacementEducationOrganizationId);
        trackedRow["Old_Student_StudentUniqueId"].Should().Be(seed.StudentUniqueId);
        trackedRow["New_Student_StudentUniqueId"].Should().Be(seed.StudentUniqueId);
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
            .ToInt64(trackedRow["Old_Student_DocumentId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(seed.StudentDocumentId);
        Convert
            .ToInt64(trackedRow["New_Student_DocumentId"], CultureInfo.InvariantCulture)
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
            UPDATE [edfi].[StudentEducationOrganizationAssociation]
            SET [LoginId] = @loginId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@loginId", "key-change-login-001"),
            new SqlParameter("@documentId", seed.AssociationDocumentId)
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

        var gradingPeriodResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "GradingPeriod");
        var gradingPeriodDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "GradingPeriodDescriptor"
        );

        // SchoolYear is unique on edfi.SchoolYearType, so reuse the seeded 2025 row
        // instead of inserting a second one.
        var schoolYearTypeDocumentId = _seedData.SchoolYearTypeDocumentId;

        // GradingPeriod pairs School_SchoolId with School_DocumentId in its FK to
        // edfi.School (SchoolId, DocumentId), so read the seeded school's id back
        // instead of hard-coding the seed literal.
        var seededSchoolId = await _database.ExecuteScalarAsync<long>(
            "SELECT [SchoolId] FROM [edfi].[School] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", _seedData.SchoolDocumentId)
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
            UPDATE [edfi].[GradingPeriod]
            SET [GradingPeriodName] = [GradingPeriodName] + N'-renamed'
            WHERE [DocumentId] IN (@firstDocumentId, @secondDocumentId);
            """,
            new SqlParameter("@firstDocumentId", firstGradingPeriodDocumentId),
            new SqlParameter("@secondDocumentId", secondGradingPeriodDocumentId)
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

        firstTrackedRow["Old_GradingPeriodName"].Should().Be("First Grading Period");
        firstTrackedRow["New_GradingPeriodName"].Should().Be("First Grading Period-renamed");
        secondTrackedRow["Old_GradingPeriodName"].Should().Be("Second Grading Period");
        secondTrackedRow["New_GradingPeriodName"].Should().Be("Second Grading Period-renamed");

        foreach (var trackedRow in new[] { firstTrackedRow, secondTrackedRow })
        {
            trackedRow["Old_GradingPeriodDescriptor_Namespace"].Should().Be(GradingPeriodDescriptorNamespace);
            trackedRow["New_GradingPeriodDescriptor_Namespace"].Should().Be(GradingPeriodDescriptorNamespace);
            trackedRow["Old_GradingPeriodDescriptor_CodeValue"].Should().Be(GradingPeriodDescriptorCodeValue);
            trackedRow["New_GradingPeriodDescriptor_CodeValue"].Should().Be(GradingPeriodDescriptorCodeValue);
        }
    }

    [Test]
    public async Task It_should_insert_key_change_rows_only_for_identity_changed_rows_in_a_mixed_workset_update()
    {
        // The core statement-level-trigger risk: one UPDATE whose workset contains
        // multiple rows where only SOME change identity. @identityChangedDocs must
        // admit exactly the changed row — no key-change row and no stamps at all
        // for the row whose values were self-assigned.
        const int SchoolYear = 2025;
        const string GradingPeriodDescriptorNamespace = "uri://ed-fi.org/GradingPeriodDescriptor";
        const string GradingPeriodDescriptorCodeValue = "SecondSixWeeks";

        var gradingPeriodResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "GradingPeriod");
        var gradingPeriodDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "GradingPeriodDescriptor"
        );

        var schoolYearTypeDocumentId = _seedData.SchoolYearTypeDocumentId;
        var seededSchoolId = await _database.ExecuteScalarAsync<long>(
            "SELECT [SchoolId] FROM [edfi].[School] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", _seedData.SchoolDocumentId)
        );

        var gradingPeriodDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("d5d5d5d5-d5d5-d5d5-d5d5-d5d5d5d5d5d5"),
            gradingPeriodDescriptorResourceKeyId,
            "Ed-Fi:GradingPeriodDescriptor",
            $"{GradingPeriodDescriptorNamespace}#{GradingPeriodDescriptorCodeValue}",
            GradingPeriodDescriptorNamespace,
            GradingPeriodDescriptorCodeValue,
            "Second Six Weeks"
        );

        var changedDocumentUuid = Guid.Parse("d6d6d6d6-d6d6-d6d6-d6d6-d6d6d6d6d6d6");
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

        var unchangedDocumentUuid = Guid.Parse("d7d7d7d7-d7d7-d7d7-d7d7-d7d7d7d7d7d7");
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
            UPDATE [edfi].[GradingPeriod]
            SET [GradingPeriodName] = CASE
                WHEN [DocumentId] = @changedDocumentId THEN [GradingPeriodName] + N'-renamed'
                ELSE [GradingPeriodName] END
            WHERE [DocumentId] IN (@changedDocumentId, @unchangedDocumentId);
            """,
            new SqlParameter("@changedDocumentId", changedDocumentId),
            new SqlParameter("@unchangedDocumentId", unchangedDocumentId)
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
        trackedRow["Old_GradingPeriodName"].Should().Be("Mixed Changed Period");
        trackedRow["New_GradingPeriodName"].Should().Be("Mixed Changed Period-renamed");
        Convert
            .ToInt64(trackedRow["ChangeVersion"], CultureInfo.InvariantCulture)
            .Should()
            .Be(changedAfter.ContentVersion);
    }

    [Test]
    public async Task It_should_project_old_and_new_descriptor_values_from_their_own_images_on_key_change()
    {
        // The descriptor element of the identity actually CHANGES here, so the
        // key-change row's Old_* descriptor projection must come from the deleted
        // image and New_* from the inserted image — equal-value tests cannot tell.
        const int SchoolYear = 2025;
        const string DescriptorNamespace = "uri://ed-fi.org/GradingPeriodDescriptor";

        var gradingPeriodResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "GradingPeriod");
        var gradingPeriodDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "GradingPeriodDescriptor"
        );

        var schoolYearTypeDocumentId = _seedData.SchoolYearTypeDocumentId;
        var seededSchoolId = await _database.ExecuteScalarAsync<long>(
            "SELECT [SchoolId] FROM [edfi].[School] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", _seedData.SchoolDocumentId)
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
            UPDATE [edfi].[GradingPeriod]
            SET [GradingPeriodDescriptor_DescriptorId] = @replacementDescriptorDocumentId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@replacementDescriptorDocumentId", replacementDescriptorDocumentId),
            new SqlParameter("@documentId", gradingPeriodDocumentId)
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
        trackedRow["Old_GradingPeriodDescriptor_Namespace"].Should().Be(DescriptorNamespace);
        trackedRow["New_GradingPeriodDescriptor_Namespace"].Should().Be(DescriptorNamespace);
        trackedRow["Old_GradingPeriodDescriptor_CodeValue"].Should().Be("FourthSixWeeks");
        trackedRow["New_GradingPeriodDescriptor_CodeValue"].Should().Be("FifthSixWeeks");
        trackedRow["Old_GradingPeriodName"].Should().Be("Descriptor Swap Period");
        trackedRow["New_GradingPeriodName"].Should().Be("Descriptor Swap Period");
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
        // old image as NULL and the new image as the value — a both-images-from-
        // inserted bug projects the value on both sides.
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
            "SELECT [SchoolId] FROM [edfi].[School] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", _seedData.SchoolDocumentId)
        );
        var before = await GetDocumentStampStateAsync(studentAssessmentDocumentId);

        await DelayForDistinctTimestampsAsync();
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[StudentAssessment]
            SET [StudentAssessmentIdentifier] = @replacementIdentifier,
                [ReportedSchool_DocumentId] = @schoolDocumentId,
                [ReportedSchool_SchoolId] = @schoolId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@replacementIdentifier", "SA-001-renamed"),
            new SqlParameter("@schoolDocumentId", _seedData.SchoolDocumentId),
            new SqlParameter("@schoolId", seededSchoolId),
            new SqlParameter("@documentId", studentAssessmentDocumentId)
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
        trackedRow["Old_StudentAssessmentIdentifier"].Should().Be("SA-001");
        trackedRow["New_StudentAssessmentIdentifier"].Should().Be("SA-001-renamed");
        trackedRow["Old_ReportedSchool_SchoolId"].Should().BeNull();
        Convert
            .ToInt64(trackedRow["New_ReportedSchool_SchoolId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(seededSchoolId);
        trackedRow["Old_Student_StudentUniqueId"].Should().Be(StudentUniqueId);
        trackedRow["New_Student_StudentUniqueId"].Should().Be(StudentUniqueId);
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
        // computed (PERSISTED) from the canonical *_Unified columns, and the FK to
        // edfi.Assessment pairs the canonical columns with Assessment_DocumentId
        // (ON UPDATE NO ACTION). Re-pointing the reference and the canonical identifier
        // in a single UPDATE is the direct-SQL simulation of an upstream assessment key
        // change reaching this key-unified resource; the trigger's identity gate keys
        // off the canonical column.
        await DelayForDistinctTimestampsAsync();
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[AssessmentScoreRangeLearningStandard]
            SET [AssessmentIdentifier_Unified] = @replacementAssessmentIdentifier,
                [Assessment_DocumentId] = @replacementAssessmentDocumentId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@replacementAssessmentIdentifier", ReplacementAssessmentIdentifier),
            new SqlParameter("@replacementAssessmentDocumentId", replacementAssessmentDocumentId),
            new SqlParameter("@documentId", scoreRangeDocumentId)
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
        trackedRow["Old_AssessmentIdentifier_Unified"].Should().Be(OriginalAssessmentIdentifier);
        trackedRow["New_AssessmentIdentifier_Unified"].Should().Be(ReplacementAssessmentIdentifier);
        trackedRow["Old_Namespace_Unified"].Should().Be(AssessmentNamespace);
        trackedRow["New_Namespace_Unified"].Should().Be(AssessmentNamespace);
        trackedRow["Old_ScoreRangeId"].Should().Be(ScoreRangeId);
        trackedRow["New_ScoreRangeId"].Should().Be(ScoreRangeId);
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
            "DELETE FROM [edfi].[StudentEducationOrganizationAssociation] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", seed.AssociationDocumentId)
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
                trackedRow["Old_EducationOrganization_EducationOrganizationId"],
                CultureInfo.InvariantCulture
            )
            .Should()
            .Be(seed.OriginalEducationOrganizationId);
        trackedRow["Old_Student_StudentUniqueId"].Should().Be(seed.StudentUniqueId);
        Convert
            .ToInt64(trackedRow["Old_Student_DocumentId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(seed.StudentDocumentId);
        AssertAllNewColumnsAreNull(trackedRow);

        // Statement 2: delete the dms.Document row.
        var documentRowsAffected = await _database.ExecuteNonQueryAsync(
            "DELETE FROM [dms].[Document] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", seed.AssociationDocumentId)
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
            "DELETE FROM [dms].[Descriptor] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", descriptorDocumentId)
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
        trackedRow["Old_Namespace"].Should().Be(DescriptorNamespace);
        trackedRow["Old_CodeValue"].Should().Be(DescriptorCodeValue);
        AssertAllNewColumnsAreNull(trackedRow);

        // Statement 2: delete the dms.Document row.
        var documentRowsAffected = await _database.ExecuteNonQueryAsync(
            "DELETE FROM [dms].[Document] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", descriptorDocumentId)
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
        // 400 chosen distinct from the seeded SchoolId=100 to avoid PK collisions. The
        // fresh school keeps its triggers enabled (only the seeded SchoolId=100 school
        // must dodge the manually planted EducationOrganizationIdentity row for that id).
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
            "DELETE FROM [edfi].[School] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", schoolDocumentId)
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
        Convert.ToInt64(trackedRow["Old_SchoolId"], CultureInfo.InvariantCulture).Should().Be(FreshSchoolId);
        AssertAllNewColumnsAreNull(trackedRow);

        // Statement 2: delete the dms.Document row.
        var documentRowsAffected = await _database.ExecuteNonQueryAsync(
            "DELETE FROM [dms].[Document] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", schoolDocumentId)
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
        // 500/501 chosen distinct from the seeded SchoolId=100 and the tombstone test's
        // 400. Triggers stay enabled so TR_School_AbstractIdentity re-projects the
        // EducationOrganizationIdentity row alongside the identity update.
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
            UPDATE [edfi].[School]
            SET [SchoolId] = @replacementSchoolId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@replacementSchoolId", ReplacementSchoolId),
            new SqlParameter("@documentId", schoolDocumentId)
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
        // Build a fresh SEOA with an edfi child-collection row
        // (StudentEducationOrganizationAssociationAddress), a sample _ext row
        // (StudentEducationOrganizationAssociationExtensionAddress), and _ext
        // grandchildren (SchoolDistrict/Term) — all FK-cascaded from the root row.
        const string StudentUniqueId = "20002";

        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var associationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "StudentEducationOrganizationAssociation"
        );

        var studentDocumentId = await InsertDocumentAsync(
            Guid.Parse("f4f4f4f4-f4f4-f4f4-f4f4-f4f4f4f4f4f4"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, StudentUniqueId, "Avery", "Sloan");

        var associationDocumentUuid = Guid.Parse("f5f5f5f5-f5f5-f5f5-f5f5-f5f5f5f5f5f5");
        var associationDocumentId = await InsertDocumentAsync(
            associationDocumentUuid,
            associationResourceKeyId
        );
        await InsertStudentEducationOrganizationAssociationAsync(
            associationDocumentId,
            _seedData.EducationOrganizationDocumentId,
            100,
            studentDocumentId,
            StudentUniqueId
        );

        var addressTypeDescriptorDocumentId = await GetDescriptorDocumentIdAsync(
            "Ed-Fi:AddressTypeDescriptor",
            "Home"
        );
        var stateAbbreviationDescriptorDocumentId = await GetDescriptorDocumentIdAsync(
            "Ed-Fi:StateAbbreviationDescriptor",
            "TX"
        );
        var termDescriptorDocumentId = await GetDescriptorDocumentIdAsync("Ed-Fi:TermDescriptor", "Fall");

        var addressCollectionItemId = await InsertStudentEducationOrganizationAssociationAddressAsync(
            associationDocumentId,
            1,
            addressTypeDescriptorDocumentId,
            stateAbbreviationDescriptorDocumentId,
            "Austin",
            "78701",
            "100 Congress Ave"
        );
        await InsertStudentEducationOrganizationAssociationExtensionAddressAsync(
            addressCollectionItemId,
            associationDocumentId,
            "Tower A"
        );
        await InsertStudentEducationOrganizationAssociationExtensionAddressSchoolDistrictAsync(
            addressCollectionItemId,
            associationDocumentId,
            1,
            "District Nine"
        );
        await InsertStudentEducationOrganizationAssociationExtensionAddressTermAsync(
            addressCollectionItemId,
            associationDocumentId,
            1,
            termDescriptorDocumentId
        );

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [edfi].[StudentEducationOrganizationAssociationAddress] WHERE [StudentEducationOrganizationAssociation_DocumentId] = @documentId;",
                new SqlParameter("@documentId", associationDocumentId)
            )
        )
            .Should()
            .Be(1);
        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [sample].[StudentEducationOrganizationAssociationExtensionAddress] WHERE [StudentEducationOrganizationAssociation_DocumentId] = @documentId;",
                new SqlParameter("@documentId", associationDocumentId)
            )
        )
            .Should()
            .Be(1);

        var before = await GetDocumentStampStateAsync(associationDocumentId);

        // Statement 1: delete the root resource row; FK cascades remove the child and
        // _ext rows and fire their stamping triggers.
        await DelayForDistinctTimestampsAsync();
        var resourceRowsAffected = await _database.ExecuteNonQueryAsync(
            "DELETE FROM [edfi].[StudentEducationOrganizationAssociation] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", associationDocumentId)
        );
        resourceRowsAffected.Should().Be(1);

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [edfi].[StudentEducationOrganizationAssociationAddress] WHERE [StudentEducationOrganizationAssociation_DocumentId] = @documentId;",
                new SqlParameter("@documentId", associationDocumentId)
            )
        )
            .Should()
            .Be(0);
        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [sample].[StudentEducationOrganizationAssociationExtensionAddress] WHERE [StudentEducationOrganizationAssociation_DocumentId] = @documentId;",
                new SqlParameter("@documentId", associationDocumentId)
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
            "DELETE FROM [dms].[Document] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", associationDocumentId)
        );
        documentRowsAffected.Should().Be(1);

        (await CountDocumentRowsAsync(associationDocumentId)).Should().Be(0);
        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [edfi].[StudentEducationOrganizationAssociation] WHERE [DocumentId] = @documentId;",
                new SqlParameter("@documentId", associationDocumentId)
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
        await InsertSchoolExtensionDirectlyOwnedBusAsync(schoolDocumentId, 1, busDocumentId, "BUS-401");

        var before = await GetDocumentStampStateAsync(schoolDocumentId);

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [sample].[SchoolExtension] WHERE [DocumentId] = @documentId;",
                new SqlParameter("@documentId", schoolDocumentId)
            )
        )
            .Should()
            .Be(1);
        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [sample].[SchoolExtensionDirectlyOwnedBus] WHERE [School_DocumentId] = @documentId;",
                new SqlParameter("@documentId", schoolDocumentId)
            )
        )
            .Should()
            .Be(1);
        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [edfi].[EducationOrganizationIdentity] WHERE [DocumentId] = @documentId;",
                new SqlParameter("@documentId", schoolDocumentId)
            )
        )
            .Should()
            .Be(1);

        // Statement 1: delete the root School row; FK cascades remove the _ext row
        // and its grandchild and fire their stamping triggers.
        await DelayForDistinctTimestampsAsync();
        var resourceRowsAffected = await _database.ExecuteNonQueryAsync(
            "DELETE FROM [edfi].[School] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", schoolDocumentId)
        );
        resourceRowsAffected.Should().Be(1);

        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [sample].[SchoolExtension] WHERE [DocumentId] = @documentId;",
                new SqlParameter("@documentId", schoolDocumentId)
            )
        )
            .Should()
            .Be(0);
        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [sample].[SchoolExtensionDirectlyOwnedBus] WHERE [School_DocumentId] = @documentId;",
                new SqlParameter("@documentId", schoolDocumentId)
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
        Convert.ToInt64(trackedRow["Old_SchoolId"], CultureInfo.InvariantCulture).Should().Be(FreshSchoolId);
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
            "DELETE FROM [dms].[Document] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", schoolDocumentId)
        );
        documentRowsAffected.Should().Be(1);

        (await CountDocumentRowsAsync(schoolDocumentId)).Should().Be(0);
        (
            await CountRowsAsync(
                "SELECT COUNT(*) FROM [edfi].[EducationOrganizationIdentity] WHERE [DocumentId] = @documentId;",
                new SqlParameter("@documentId", schoolDocumentId)
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
            contactExtensionAddressTermCollectionItemId,
            schoolDocumentId,
            schoolYearTypeDocumentId
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

    private async Task InsertBusAsync(long documentId, string busId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [sample].[Bus] ([DocumentId], [BusId])
            VALUES (@documentId, @busId);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@busId", busId)
        );
    }

    private async Task InsertSchoolExtensionAsync(long schoolDocumentId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [sample].[SchoolExtension] ([DocumentId])
            VALUES (@documentId);
            """,
            new SqlParameter("@documentId", schoolDocumentId)
        );
    }

    private async Task InsertSchoolExtensionDirectlyOwnedBusAsync(
        long schoolDocumentId,
        int ordinal,
        long busDocumentId,
        string busId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [sample].[SchoolExtensionDirectlyOwnedBus] (
                [Ordinal],
                [School_DocumentId],
                [DirectlyOwnedBus_DocumentId],
                [DirectlyOwnedBus_BusId]
            )
            VALUES (@ordinal, @schoolDocumentId, @busDocumentId, @busId);
            """,
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@busDocumentId", busDocumentId),
            new SqlParameter("@busId", busId)
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

    private async Task InsertStudentAsync(
        long documentId,
        string studentUniqueId,
        string firstName,
        string lastSurname
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Student] ([DocumentId], [BirthDate], [FirstName], [LastSurname], [StudentUniqueId])
            VALUES (@documentId, @birthDate, @firstName, @lastSurname, @studentUniqueId);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@birthDate", new DateOnly(2010, 1, 1)),
            new SqlParameter("@firstName", firstName),
            new SqlParameter("@lastSurname", lastSurname),
            new SqlParameter("@studentUniqueId", studentUniqueId)
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
            INSERT INTO [edfi].[StudentEducationOrganizationAssociation] (
                [DocumentId],
                [EducationOrganization_DocumentId],
                [EducationOrganization_EducationOrganizationId],
                [Student_DocumentId],
                [Student_StudentUniqueId]
            )
            VALUES (
                @documentId,
                @educationOrganizationDocumentId,
                @educationOrganizationId,
                @studentDocumentId,
                @studentUniqueId
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@educationOrganizationDocumentId", educationOrganizationDocumentId),
            new SqlParameter("@educationOrganizationId", educationOrganizationId),
            new SqlParameter("@studentDocumentId", studentDocumentId),
            new SqlParameter("@studentUniqueId", studentUniqueId)
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
            DECLARE @Inserted TABLE ([CollectionItemId] bigint);
            INSERT INTO [edfi].[StudentEducationOrganizationAssociationAddress] (
                [Ordinal],
                [StudentEducationOrganizationAssociation_DocumentId],
                [AddressTypeDescriptor_DescriptorId],
                [StateAbbreviationDescriptor_DescriptorId],
                [City],
                [PostalCode],
                [StreetNumberName]
            )
            OUTPUT INSERTED.[CollectionItemId] INTO @Inserted ([CollectionItemId])
            VALUES (
                @ordinal,
                @studentEducationOrganizationAssociationDocumentId,
                @addressTypeDescriptorDocumentId,
                @stateAbbreviationDescriptorDocumentId,
                @city,
                @postalCode,
                @streetNumberName
            );
            SELECT TOP (1) [CollectionItemId] FROM @Inserted;
            """,
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter(
                "@studentEducationOrganizationAssociationDocumentId",
                studentEducationOrganizationAssociationDocumentId
            ),
            new SqlParameter("@addressTypeDescriptorDocumentId", addressTypeDescriptorDocumentId),
            new SqlParameter("@stateAbbreviationDescriptorDocumentId", stateAbbreviationDescriptorDocumentId),
            new SqlParameter("@city", city),
            new SqlParameter("@postalCode", postalCode),
            new SqlParameter("@streetNumberName", streetNumberName)
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
            INSERT INTO [sample].[StudentEducationOrganizationAssociationExtensionAddress] (
                [BaseCollectionItemId],
                [StudentEducationOrganizationAssociation_DocumentId],
                [Complex]
            )
            VALUES (
                @baseCollectionItemId,
                @studentEducationOrganizationAssociationDocumentId,
                @complex
            );
            """,
            new SqlParameter("@baseCollectionItemId", baseCollectionItemId),
            new SqlParameter(
                "@studentEducationOrganizationAssociationDocumentId",
                studentEducationOrganizationAssociationDocumentId
            ),
            new SqlParameter("@complex", complex)
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
            """
            DECLARE @Inserted TABLE ([CollectionItemId] bigint);
            INSERT INTO [sample].[StudentEducationOrganizationAssociationExtensionAddressSchoolDistrict] (
                [BaseCollectionItemId],
                [Ordinal],
                [StudentEducationOrganizationAssociation_DocumentId],
                [SchoolDistrict]
            )
            OUTPUT INSERTED.[CollectionItemId] INTO @Inserted ([CollectionItemId])
            VALUES (
                @baseCollectionItemId,
                @ordinal,
                @studentEducationOrganizationAssociationDocumentId,
                @schoolDistrict
            );
            SELECT TOP (1) [CollectionItemId] FROM @Inserted;
            """,
            new SqlParameter("@baseCollectionItemId", baseCollectionItemId),
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter(
                "@studentEducationOrganizationAssociationDocumentId",
                studentEducationOrganizationAssociationDocumentId
            ),
            new SqlParameter("@schoolDistrict", schoolDistrict)
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
            DECLARE @Inserted TABLE ([CollectionItemId] bigint);
            INSERT INTO [sample].[StudentEducationOrganizationAssociationExtensionAddressTerm] (
                [BaseCollectionItemId],
                [Ordinal],
                [StudentEducationOrganizationAssociation_DocumentId],
                [TermDescriptor_DescriptorId]
            )
            OUTPUT INSERTED.[CollectionItemId] INTO @Inserted ([CollectionItemId])
            VALUES (
                @baseCollectionItemId,
                @ordinal,
                @studentEducationOrganizationAssociationDocumentId,
                @termDescriptorDocumentId
            );
            SELECT TOP (1) [CollectionItemId] FROM @Inserted;
            """,
            new SqlParameter("@baseCollectionItemId", baseCollectionItemId),
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter(
                "@studentEducationOrganizationAssociationDocumentId",
                studentEducationOrganizationAssociationDocumentId
            ),
            new SqlParameter("@termDescriptorDocumentId", termDescriptorDocumentId)
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
            INSERT INTO [edfi].[GradingPeriod] (
                [DocumentId],
                [SchoolYear_DocumentId],
                [SchoolYear_SchoolYear],
                [School_DocumentId],
                [School_SchoolId],
                [GradingPeriodDescriptor_DescriptorId],
                [BeginDate],
                [EndDate],
                [GradingPeriodName],
                [TotalInstructionalDays]
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
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolYearTypeDocumentId", schoolYearTypeDocumentId),
            new SqlParameter("@schoolYear", schoolYear),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@gradingPeriodDescriptorDocumentId", gradingPeriodDescriptorDocumentId),
            new SqlParameter("@beginDate", new DateOnly(2025, 8, 1)),
            new SqlParameter("@endDate", new DateOnly(2025, 9, 15)),
            new SqlParameter("@gradingPeriodName", gradingPeriodName),
            new SqlParameter("@totalInstructionalDays", 30)
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
            INSERT INTO [edfi].[Assessment] ([DocumentId], [AssessmentIdentifier], [AssessmentTitle], [Namespace])
            VALUES (@documentId, @assessmentIdentifier, @assessmentTitle, @namespace);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@assessmentIdentifier", assessmentIdentifier),
            new SqlParameter("@assessmentTitle", assessmentTitle),
            new SqlParameter("@namespace", @namespace)
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
            INSERT INTO [edfi].[StudentAssessment] (
                [DocumentId],
                [Assessment_DocumentId],
                [Assessment_AssessmentIdentifier],
                [Assessment_Namespace],
                [Student_DocumentId],
                [Student_StudentUniqueId],
                [StudentAssessmentIdentifier]
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
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@assessmentDocumentId", assessmentDocumentId),
            new SqlParameter("@assessmentIdentifier", assessmentIdentifier),
            new SqlParameter("@assessmentNamespace", assessmentNamespace),
            new SqlParameter("@studentDocumentId", studentDocumentId),
            new SqlParameter("@studentUniqueId", studentUniqueId),
            new SqlParameter("@studentAssessmentIdentifier", studentAssessmentIdentifier)
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
        // computed (PERSISTED) from the canonical *_Unified columns and must not be
        // inserted.
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[AssessmentScoreRangeLearningStandard] (
                [DocumentId],
                [AssessmentIdentifier_Unified],
                [Namespace_Unified],
                [Assessment_DocumentId],
                [MaximumScore],
                [MinimumScore],
                [ScoreRangeId]
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
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@assessmentIdentifier", assessmentIdentifier),
            new SqlParameter("@namespace", @namespace),
            new SqlParameter("@assessmentDocumentId", assessmentDocumentId),
            new SqlParameter("@maximumScore", "100"),
            new SqlParameter("@minimumScore", "0"),
            new SqlParameter("@scoreRangeId", scoreRangeId)
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

        // Unlike the seeded SchoolId=100 school (whose triggers are disabled to dodge the
        // manually planted EducationOrganizationIdentity row for the same id), this fresh
        // school keeps its triggers enabled so TR_School_AbstractIdentity projects the
        // EducationOrganizationIdentity row the association FK re-points to.
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
        // The association's original EdOrg reference targets the seeded
        // EducationOrganizationIdentity row (EducationOrganizationId=100), which the
        // single-column FK on EducationOrganization_DocumentId requires.
        await InsertStudentEducationOrganizationAssociationAsync(
            associationDocumentId,
            _seedData.EducationOrganizationDocumentId,
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
        // The stored EdOrg identity value travels with its DocumentId: the FK targets
        // edfi.EducationOrganizationIdentity through EducationOrganization_DocumentId and
        // the AllNone CHECK pairs the two columns, so the direct-SQL simulation of a
        // cascaded identity key change re-points both in a single UPDATE (the trigger's
        // identity gate keys off the value column).
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[StudentEducationOrganizationAssociation]
            SET [EducationOrganization_DocumentId] = @replacementSchoolDocumentId,
                [EducationOrganization_EducationOrganizationId] = @replacementEducationOrganizationId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@replacementSchoolDocumentId", seed.ReplacementSchoolDocumentId),
            new SqlParameter("@replacementEducationOrganizationId", seed.ReplacementEducationOrganizationId),
            new SqlParameter("@documentId", seed.AssociationDocumentId)
        );
        rowsAffected.Should().Be(1);
    }

    private async Task<long> CountRowsAsync(string sql, params SqlParameter[] parameters)
    {
        return await _database.ExecuteScalarAsync<long>(sql, parameters);
    }

    private async Task<long> ReadMaxChangeVersionAsync()
    {
        return await _database.ExecuteScalarAsync<long>("SELECT dms.GetMaxChangeVersion();");
    }

    private async Task<long> CountDocumentRowsAsync(long documentId)
    {
        return await CountRowsAsync(
            "SELECT COUNT(*) FROM [dms].[Document] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", documentId)
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
            FROM [{schemaName}].[{tableName}]
            WHERE [Id] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
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
            SELECT TOP (1) *
            FROM [{schemaName}].[{tableName}]
            WHERE [Id] = @documentUuid
            ORDER BY [ChangeVersion] DESC;
            """,
            new SqlParameter("@documentUuid", documentUuid)
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
            SELECT max([ChangeVersion])
            FROM [{schemaName}].[{tableName}]
            WHERE [Id] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
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
            .Keys.Where(columnName => columnName.StartsWith("New_", StringComparison.Ordinal))
            .ToList();

        newColumns.Should().NotBeEmpty();
        foreach (var columnName in newColumns)
        {
            trackedRow[columnName].Should().BeNull($"tombstone column [{columnName}] must be NULL");
        }
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

    private async Task<DocumentStampState> GetRootMirrorStampStateAsync(
        string schemaName,
        string tableName,
        long documentId
    )
    {
        var row = (
            await _database.QueryRowsAsync(
                $"""
                SELECT
                    [ContentVersion],
                    [ContentLastModifiedAt]
                FROM [{schemaName}].[{tableName}]
                WHERE [DocumentId] = @documentId;
                """,
                new SqlParameter("@documentId", documentId)
            )
        ).Single();

        return new(
            Convert.ToInt64(row["ContentVersion"], CultureInfo.InvariantCulture),
            IdentityVersion: 0,
            ReadDateTimeOffset(row["ContentLastModifiedAt"]),
            IdentityLastModifiedAt: DateTimeOffset.UnixEpoch
        );
    }

    private async Task AssertRootMirrorMatchesDocumentAsync(
        string schemaName,
        string tableName,
        long documentId
    )
    {
        var document = await GetDocumentStampStateAsync(documentId);
        var mirror = await GetRootMirrorStampStateAsync(schemaName, tableName, documentId);

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

    // Test-only audit installed in OneTimeSetUp captures every INSERT/DELETE on
    // dms.ReferentialIdentity. The same-value identity tests truncate the audit
    // immediately before the UPDATE under test and assert zero ops afterwards,
    // so a regression that ran a redundant DELETE+INSERT cycle (which produces
    // identical deterministic UUIDv5 rows) would still be caught.
    private async Task InstallReferentialIdentityAuditAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            IF SCHEMA_ID(N'dms_test') IS NULL EXEC(N'CREATE SCHEMA [dms_test];');

            IF OBJECT_ID(N'[dms_test].[ReferentialIdentityAudit]', N'U') IS NULL
            CREATE TABLE [dms_test].[ReferentialIdentityAudit]
            (
                [Op] char(1) NOT NULL,
                [DocumentId] bigint NOT NULL,
                [ResourceKeyId] smallint NOT NULL,
                [ReferentialId] uniqueidentifier NOT NULL
            );

            IF OBJECT_ID(N'[dms].[TR_ReferentialIdentity_Audit]', N'TR') IS NOT NULL
            EXEC(N'DROP TRIGGER [dms].[TR_ReferentialIdentity_Audit];');

            EXEC(N'
                CREATE TRIGGER [dms].[TR_ReferentialIdentity_Audit] ON [dms].[ReferentialIdentity]
                AFTER INSERT, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    INSERT INTO [dms_test].[ReferentialIdentityAudit] ([Op], [DocumentId], [ResourceKeyId], [ReferentialId])
                    SELECT N''I'', [DocumentId], [ResourceKeyId], [ReferentialId] FROM inserted;
                    INSERT INTO [dms_test].[ReferentialIdentityAudit] ([Op], [DocumentId], [ResourceKeyId], [ReferentialId])
                    SELECT N''D'', [DocumentId], [ResourceKeyId], [ReferentialId] FROM deleted;
                END;
            ');
            """
        );
    }

    private async Task<long> CountReferentialIdentityAuditOpsForDocumentAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT_BIG(*)
            FROM [dms_test].[ReferentialIdentityAudit]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
    }

    private async Task TruncateReferentialIdentityAuditAsync()
    {
        await _database.ExecuteNonQueryAsync("""TRUNCATE TABLE [dms_test].[ReferentialIdentityAudit];""");
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
