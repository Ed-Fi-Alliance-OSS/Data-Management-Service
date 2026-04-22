// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Slice 3 separate-table profile merge integration fixtures. Each fixture below is one
// row of the spec's "Integration tests" section at
// reference/design/backend-redesign/epics/07-relational-write-path/03b-profile-aware-persist-executor/03-separate-table-profile-merge.md
//
// All six fixtures load the synthetic ProfileSeparateTableMergeItem fixture documented in
// the sibling PostgresqlProfileSeparateTableMergeTests.cs file, and assert on actual DB
// row state after the executor runs through RelationalDocumentStoreRepository.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 1: Visible-absent separate-table scope deletes its row.
//  Spec: Separate-table ProfileVisibleButAbsentNonCollectionScope.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Seed a ProfileSeparateTableMergeItem with displayName + an existing <c>$._ext.sample</c>
/// row populated. The profiled PUT classifies the <c>$._ext.sample</c> request scope as
/// VisibleAbsent (so the writable body omits the sub-object), the stored side still carries
/// a matched visible _ext row, and the root scope stays visible so displayName can update.
/// The separate-table decider returns Delete — the existing _ext row must be removed while
/// the root row keeps its new displayName.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_ProfiledUpdate_With_VisibleAbsent_SeparateTableScope_DeletesIt
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("cc000001-0000-0000-0000-000000000001")
    );
    private const int ItemId = 9101;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;
    private int _extRowCountAfterPut;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileSeparateTableMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileSeparateTableMergeSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "OriginalDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["extVisibleScalar"] = "OriginalVisible",
                    ["extHiddenScalar"] = "OriginalHidden",
                },
            },
        };
        var seedResult = await PostgresqlProfileSeparateTableMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            seedBody,
            DocumentUuid,
            "separate-table-visible-absent-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var writeBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
        };
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileSeparateTableMergeSupport.ItemResource
        ];
        var profileContext = PostgresqlProfileSeparateTableMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            emitExtRequestScope: true,
            extRequestVisibility: ProfileVisibilityKind.VisibleAbsent,
            extCreatable: true,
            emitExtStoredScope: true,
            extStoredVisibility: ProfileVisibilityKind.VisiblePresent,
            extStoredHiddenMemberPaths: []
        );
        _putResult = await PostgresqlProfileSeparateTableMergeSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            writeBody,
            DocumentUuid,
            profileContext,
            "separate-table-visible-absent-put"
        );
        _rowAfterPut = await PostgresqlProfileSeparateTableMergeSupport.ReadRootRowAsync(
            _database,
            DocumentUuid
        );
        _extRowCountAfterPut = await PostgresqlProfileSeparateTableMergeSupport.CountExtRowsAsync(
            _database,
            DocumentUuid
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_the_root_display_name() =>
        _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_deletes_the_separate_table_row() => _extRowCountAfterPut.Should().Be(0);
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 2: Hidden extension row is preserved.
//  Spec: ProfileHiddenExtensionRowPreservation.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Seed a ProfileSeparateTableMergeItem with displayName + an existing <c>$._ext.sample</c>
/// row populated. The profile hides the entire <c>$._ext.sample</c> scope (stored-side
/// <c>Hidden</c>) — no corresponding request scope is emitted because the writable profile
/// does not expose the sub-object. The profiled PUT updates only displayName. Asserts the
/// separate-table row is preserved untouched (both its scalars retain their seed values).
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_ProfiledUpdate_With_Hidden_Extension_Row_PreservesIt
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("cc000002-0000-0000-0000-000000000002")
    );
    private const int ItemId = 9102;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;
    private IReadOnlyDictionary<string, object?>? _extRowAfterPut;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileSeparateTableMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileSeparateTableMergeSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "OriginalDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["extVisibleScalar"] = "HiddenRowVisible",
                    ["extHiddenScalar"] = "HiddenRowHidden",
                },
            },
        };
        var seedResult = await PostgresqlProfileSeparateTableMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            seedBody,
            DocumentUuid,
            "separate-table-hidden-row-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var writeBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
        };
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileSeparateTableMergeSupport.ItemResource
        ];
        var profileContext = PostgresqlProfileSeparateTableMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            // Hidden stored scope means the writable profile does not expose the sub-object at all.
            // The request-side scope is omitted (emitExtRequestScope=false) because the profile
            // did not surface the extension scope on the writable side.
            emitExtRequestScope: false,
            extRequestVisibility: ProfileVisibilityKind.Hidden,
            extCreatable: false,
            emitExtStoredScope: true,
            extStoredVisibility: ProfileVisibilityKind.Hidden,
            extStoredHiddenMemberPaths: []
        );
        _putResult = await PostgresqlProfileSeparateTableMergeSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            writeBody,
            DocumentUuid,
            profileContext,
            "separate-table-hidden-row-put"
        );
        _rowAfterPut = await PostgresqlProfileSeparateTableMergeSupport.ReadRootRowAsync(
            _database,
            DocumentUuid
        );
        _extRowAfterPut = await PostgresqlProfileSeparateTableMergeSupport.TryReadExtRowAsync(
            _database,
            DocumentUuid
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_the_root_display_name() =>
        _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_preserves_the_hidden_extension_row()
    {
        _extRowAfterPut.Should().NotBeNull("hidden extension row must remain after PUT");
        _extRowAfterPut!["ExtVisibleScalar"].Should().Be("HiddenRowVisible");
        _extRowAfterPut["ExtHiddenScalar"].Should().Be("HiddenRowHidden");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 3 (pair with Fixture 4): Creatable=false for a NEW visible separate-table
//  scope rejects the create-new POST with a typed profile-data-policy failure.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A brand-new document is POSTed. The profile declares the root creatable, the separate-table
/// <c>$._ext.sample</c> scope visible-present in the request, but marks the separate-table
/// scope's Creatable=false. No stored state exists yet, so the decider returns
/// RejectCreateDenied and the executor must return
/// <see cref="UpsertResult.UpsertFailureProfileDataPolicy"/>.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_ProfiledUpsert_With_Creatable_False_ForNewSeparateTableScope_Rejects
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("cc000003-0000-0000-0000-000000000003")
    );
    private const int ItemId = 9103;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _postResult = null!;
    private int _rootRowCountAfterPost;
    private int _extRowCountAfterPost;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileSeparateTableMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileSeparateTableMergeSupport.CreateServiceProvider();

        var writeBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "NewDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["extVisibleScalar"] = "NewVisible",
                    ["extHiddenScalar"] = "NewHidden",
                },
            },
        };
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileSeparateTableMergeSupport.ItemResource
        ];
        // Create-new flow: no stored state, so emitExtStoredScope is irrelevant (no stored
        // projection happens). The decider sees request-visible-present + no stored row +
        // Creatable=false → RejectCreateDenied.
        var profileContext = PostgresqlProfileSeparateTableMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            emitExtRequestScope: true,
            extRequestVisibility: ProfileVisibilityKind.VisiblePresent,
            extCreatable: false,
            emitExtStoredScope: false,
            extStoredVisibility: ProfileVisibilityKind.VisiblePresent,
            extStoredHiddenMemberPaths: []
        );
        _postResult = await PostgresqlProfileSeparateTableMergeSupport.ExecuteProfiledPostAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            writeBody,
            DocumentUuid,
            profileContext,
            "separate-table-creatable-false-new-post"
        );

        _rootRowCountAfterPost = await CountRootRowsAsync();
        _extRowCountAfterPost = await PostgresqlProfileSeparateTableMergeSupport.CountExtRowsAsync(
            _database,
            DocumentUuid
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_profile_data_policy_failure() =>
        _postResult.Should().BeOfType<UpsertResult.UpsertFailureProfileDataPolicy>();

    [Test]
    public void It_does_not_persist_a_root_row() => _rootRowCountAfterPost.Should().Be(0);

    [Test]
    public void It_does_not_persist_a_separate_table_row() => _extRowCountAfterPost.Should().Be(0);

    private async Task<int> CountRootRowsAsync()
    {
        var scalar = await _database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "edfi"."ProfileSeparateTableMergeItem" i
            INNER JOIN "dms"."Document" d ON d."DocumentId" = i."DocumentId"
            WHERE d."DocumentUuid" = @documentUuid;
            """,
            new Npgsql.NpgsqlParameter("documentUuid", DocumentUuid.Value)
        );
        return (int)scalar;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 4 (pair with Fixture 3): Creatable=false on the separate-table scope
//  still allows updates of an existing matched visible scope row. The pair is the
//  cross-reference invariant: create is gated by Creatable; matched update is not.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Seed a ProfileSeparateTableMergeItem with an existing <c>$._ext.sample</c> row. Same
/// profile shape as Fixture 3 (separate-table scope visible-present, Creatable=false), but
/// the profiled PUT finds a matched stored visible row, so the decider returns Update. The
/// extension row is updated in place with the new visible scalar.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_ProfiledUpdate_WithExistingSeparateTableScope_And_Creatable_False_AllowsUpdate
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("cc000004-0000-0000-0000-000000000004")
    );
    private const int ItemId = 9104;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?>? _extRowAfterPut;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileSeparateTableMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileSeparateTableMergeSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "OriginalDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["extVisibleScalar"] = "OriginalVisible",
                    ["extHiddenScalar"] = "OriginalHidden",
                },
            },
        };
        var seedResult = await PostgresqlProfileSeparateTableMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            seedBody,
            DocumentUuid,
            "separate-table-creatable-false-matched-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var writeBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["extVisibleScalar"] = "UpdatedVisible",
                    ["extHiddenScalar"] = "UpdatedHiddenIgnored",
                },
            },
        };
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileSeparateTableMergeSupport.ItemResource
        ];
        var profileContext = PostgresqlProfileSeparateTableMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            emitExtRequestScope: true,
            extRequestVisibility: ProfileVisibilityKind.VisiblePresent,
            // Creatable=false on the separate-table scope: a matched existing visible scope
            // row is still allowed to update; only new creation would be rejected.
            extCreatable: false,
            emitExtStoredScope: true,
            extStoredVisibility: ProfileVisibilityKind.VisiblePresent,
            extStoredHiddenMemberPaths: []
        );
        _putResult = await PostgresqlProfileSeparateTableMergeSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            writeBody,
            DocumentUuid,
            profileContext,
            "separate-table-creatable-false-matched-put"
        );
        _extRowAfterPut = await PostgresqlProfileSeparateTableMergeSupport.TryReadExtRowAsync(
            _database,
            DocumentUuid
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_the_separate_table_visible_scalar()
    {
        _extRowAfterPut.Should().NotBeNull();
        _extRowAfterPut!["ExtVisibleScalar"].Should().Be("UpdatedVisible");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 5: Hidden descriptor FK on a visible separate-table scope is preserved.
//
//  Closes the spec's "One separate-table hidden-binding preservation case covering
//  FK/descriptor or key-unification/synthetic-presence behavior outside the root row"
//  (03-separate-table-profile-merge.md:119) with a real non-scalar binding. The Sample
//  extension schema contributes a SchoolTypeDescriptor FK at
//  <c>$._ext.sample.sampleCategoryDescriptor</c>, compiled into the
//  <c>SampleCategoryDescriptor_DescriptorId</c> column on the
//  sample.ProfileSeparateTableMergeItemExtension table. The profile lists
//  <c>sampleCategoryDescriptor</c> as a stored-side hidden member path; the profiled PUT
//  updates the unrelated visible scalar and must not disturb the descriptor FK column.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Seed a SchoolTypeDescriptor row, seed a ProfileSeparateTableMergeItem with both
/// <c>$._ext.sample</c> scalars populated (no descriptor URI in the seed body), then write the
/// descriptor FK column on the extension row directly. The profile keeps the separate-table
/// scope visible-present but declares <c>sampleCategoryDescriptor</c> as a hidden member path
/// on the stored side. The profiled PUT updates <c>extVisibleScalar</c> and carries no
/// descriptor URI. Asserts the visible scalar moves to its new value while the hidden
/// descriptor FK column is preserved unchanged from its pre-PUT value.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_ProfiledUpdate_WithHiddenDescriptorFKOn_SeparateTable_PreservesFK
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("cc000005-0000-0000-0000-000000000005")
    );
    private static readonly Guid DescriptorDocumentUuid = Guid.Parse("cc000005-1000-0000-0000-000000000005");
    private const int ItemId = 9105;
    private const string DescriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";
    private const string DescriptorCodeValue = "SeparateTableHiddenFK";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _descriptorDocumentId;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?>? _extRowAfterPut;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileSeparateTableMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileSeparateTableMergeSupport.CreateServiceProvider();

        _descriptorDocumentId =
            await PostgresqlProfileSeparateTableMergeSupport.SeedSchoolTypeDescriptorAsync(
                _database,
                DescriptorDocumentUuid,
                DescriptorNamespace,
                DescriptorCodeValue,
                DescriptorCodeValue
            );

        var seedBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "OriginalDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["extVisibleScalar"] = "OriginalVisible",
                    ["extHiddenScalar"] = "OriginalHidden",
                },
            },
        };
        var seedResult = await PostgresqlProfileSeparateTableMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            seedBody,
            DocumentUuid,
            "separate-table-hidden-descriptor-fk-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        // Populate the descriptor FK column on the extension row directly — the normal seed
        // path does not thread DescriptorReferences through the relational write, and the
        // preservation invariant we are testing concerns what happens on a subsequent
        // profiled PUT when the descriptor member is hidden.
        await PostgresqlProfileSeparateTableMergeSupport.SetExtRowSampleCategoryDescriptorAsync(
            _database,
            DocumentUuid,
            _descriptorDocumentId
        );

        // Writable body omits the hidden descriptor member — the profile middleware would
        // have removed it from the writable view; the request-side scope carries only
        // extVisibleScalar.
        var writeBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject { ["extVisibleScalar"] = "UpdatedVisible" },
            },
        };
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileSeparateTableMergeSupport.ItemResource
        ];
        var profileContext = PostgresqlProfileSeparateTableMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            emitExtRequestScope: true,
            extRequestVisibility: ProfileVisibilityKind.VisiblePresent,
            extCreatable: true,
            emitExtStoredScope: true,
            extStoredVisibility: ProfileVisibilityKind.VisiblePresent,
            extStoredHiddenMemberPaths: ["sampleCategoryDescriptor"]
        );
        _putResult = await PostgresqlProfileSeparateTableMergeSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            writeBody,
            DocumentUuid,
            profileContext,
            "separate-table-hidden-descriptor-fk-put"
        );
        _extRowAfterPut = await PostgresqlProfileSeparateTableMergeSupport.TryReadExtRowAsync(
            _database,
            DocumentUuid
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_the_visible_scalar()
    {
        _extRowAfterPut.Should().NotBeNull();
        _extRowAfterPut!["ExtVisibleScalar"].Should().Be("UpdatedVisible");
    }

    [Test]
    public void It_preserves_the_hidden_descriptor_fk()
    {
        _extRowAfterPut.Should().NotBeNull();
        _extRowAfterPut!["SampleCategoryDescriptor_DescriptorId"].Should().Be(_descriptorDocumentId);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 6: Hidden whole-scope preservation with a request-side Hidden entry —
//  distinct from Fixture 2 only in the request-scope catalog shape.
//
//  Fixture 2 omits the RequestScopeState entry entirely (tolerant back-compat for a
//  writable profile that did not surface the sub-object at all). Fixture 6 emits the
//  RequestScopeState with Visibility=Hidden, matching C3's shared visibility
//  classification for a scope hidden on both sides. Both must preserve the stored row
//  unchanged. Under a consistent writable profile Hidden is profile-level, so request
//  and stored classifications agree; a VisiblePresent request paired with a Hidden
//  stored row is an inconsistent tuple and fails closed in the decider (see unit tests
//  Given_SeparateTableDecider_for_visible_present_request_with_hidden_stored_fails_closed).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Seed a ProfileSeparateTableMergeItem with both <c>$._ext.sample</c> scalars populated.
/// The writable profile hides the sub-object, so both request-side and stored-side
/// classifications are Hidden (emitted in the context). The decider's Preserve rule
/// fires and the stored row stays untouched. The profiled PUT updates root displayName.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_ProfiledUpdate_WithHiddenWholeSeparateTableScope_PreservesRow
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("cc000006-0000-0000-0000-000000000006")
    );
    private const int ItemId = 9106;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;
    private IReadOnlyDictionary<string, object?>? _extRowAfterPut;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileSeparateTableMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileSeparateTableMergeSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "OriginalDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["extVisibleScalar"] = "WholeScopeVisible",
                    ["extHiddenScalar"] = "WholeScopeHidden",
                },
            },
        };
        var seedResult = await PostgresqlProfileSeparateTableMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            seedBody,
            DocumentUuid,
            "separate-table-hidden-whole-scope-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var writeBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
        };
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileSeparateTableMergeSupport.ItemResource
        ];
        // Hidden on both sides: the writable profile hides the sub-object, so C3/C6 emit
        // matching Hidden entries for the extension scope. The decider's Preserve rule
        // fires and the stored row is left untouched.
        var profileContext = PostgresqlProfileSeparateTableMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            emitExtRequestScope: true,
            extRequestVisibility: ProfileVisibilityKind.Hidden,
            extCreatable: false,
            emitExtStoredScope: true,
            extStoredVisibility: ProfileVisibilityKind.Hidden,
            extStoredHiddenMemberPaths: []
        );
        _putResult = await PostgresqlProfileSeparateTableMergeSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            writeBody,
            DocumentUuid,
            profileContext,
            "separate-table-hidden-whole-scope-put"
        );
        _rowAfterPut = await PostgresqlProfileSeparateTableMergeSupport.ReadRootRowAsync(
            _database,
            DocumentUuid
        );
        _extRowAfterPut = await PostgresqlProfileSeparateTableMergeSupport.TryReadExtRowAsync(
            _database,
            DocumentUuid
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_the_root_display_name() =>
        _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_preserves_both_separate_table_scalars_untouched()
    {
        _extRowAfterPut.Should().NotBeNull("hidden-whole-scope must preserve the row");
        _extRowAfterPut!["ExtVisibleScalar"].Should().Be("WholeScopeVisible");
        _extRowAfterPut["ExtHiddenScalar"].Should().Be("WholeScopeHidden");
    }
}
