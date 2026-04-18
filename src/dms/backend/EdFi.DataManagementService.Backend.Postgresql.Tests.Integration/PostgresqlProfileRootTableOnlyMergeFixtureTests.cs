// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Supplemental Slice 2 profile merge integration coverage for ProfileRootOnlyMergeItem,
// a synthetic single-table resource from the
// src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/profile-root-only-merge
// fixture. The fixture exercises shapes that NamingStressItem (used by the sibling
// PostgresqlProfileRootTableOnlyMergeTests file for Task 7) cannot:
//
//   - An inlined sub-object scope `$.profileScope` carrying two scalar columns
//     (ProfileScopeClearableText, ProfileScopePreservedText).
//   - A nullable document reference StudentReference (FK + propagated identity column).
//   - Two descriptor references (PrimarySchoolTypeDescriptor, SecondarySchoolTypeDescriptor)
//     with an equality constraint that compiles into a single key-unification plan with a
//     unified canonical FK column and per-member synthetic presence columns.
//
// The single-table compilation yields the following columns on edfi.ProfileRootOnlyMergeItem:
//   DocumentId (bigint, PK),
//   PrimarySchoolTypeDescriptor_DescriptorId_Present (boolean, synthetic presence),
//   PrimarySchoolTypeDescriptor_Unified_DescriptorId (bigint, canonical FK),
//   SecondarySchoolTypeDescriptor_DescriptorId_Present (boolean, synthetic presence),
//   StudentReference_DocumentId (bigint, FK),
//   StudentReference_StudentUniqueId (varchar, propagated identity),
//   PrimarySchoolTypeDescriptor_DescriptorId (generated; CASE on presence + canonical),
//   SecondarySchoolTypeDescriptor_DescriptorId (generated; CASE on presence + canonical),
//   DisplayName (varchar),
//   ProfileRootOnlyMergeItemId (integer, natural key),
//   ProfileScopeClearableText (varchar),
//   ProfileScopePreservedText (varchar).
//
// The two scenarios landed in this file are the ones that do not require pre-seeding a
// referenced Student document or SchoolTypeDescriptor documents (those external-entity
// scenarios are called out as remaining gaps in the task report):
//   Fixture 1: Hidden inlined preservation on the root scope hides preservedText while
//              leaving clearableText visible. A PUT that updates displayName + clearableText
//              preserves preservedText.
//   Fixture 2: Visible-absent inlined scope $.profileScope clears its clearable members
//              (clearableText) while hidden-preserved preservedText is retained.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file sealed class ProfileRootOnlyFixtureNoOpHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class ProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class ProfileRootOnlyFixtureNoOpUpdateCascadeHandler : IUpdateCascadeHandler
{
    public UpdateCascadeResult Cascade(
        System.Text.Json.JsonElement originalEdFiDoc,
        ProjectName originalDocumentProjectName,
        ResourceName originalDocumentResourceName,
        JsonNode modifiedEdFiDoc,
        JsonNode referencingEdFiDoc,
        long referencingDocumentId,
        short referencingDocumentPartitionKey,
        Guid referencingDocumentUuid,
        ProjectName referencingProjectName,
        ResourceName referencingResourceName
    ) =>
        new(
            OriginalEdFiDoc: referencingEdFiDoc,
            ModifiedEdFiDoc: referencingEdFiDoc,
            Id: referencingDocumentId,
            DocumentPartitionKey: referencingDocumentPartitionKey,
            DocumentUuid: referencingDocumentUuid,
            ProjectName: referencingProjectName,
            ResourceName: referencingResourceName,
            isIdentityUpdate: false
        );
}

/// <summary>
/// Test-configurable <see cref="IStoredStateProjectionInvoker"/> that emits two scope states:
/// the root scope at <c>$</c> with caller-supplied visibility/hidden-member-paths, and an
/// inlined sub-scope at <c>$.profileScope</c> with caller-supplied visibility/hidden-member-paths.
/// Required for the ProfileRootOnlyMergeItem fixtures that exercise an inlined profileScope.
/// </summary>
internal sealed class ProfileRootOnlyFixtureProjectionInvoker : IStoredStateProjectionInvoker
{
    private readonly ProfileVisibilityKind _rootVisibility;
    private readonly System.Collections.Immutable.ImmutableArray<string> _rootHiddenMemberPaths;
    private readonly ProfileVisibilityKind _profileScopeVisibility;
    private readonly System.Collections.Immutable.ImmutableArray<string> _profileScopeHiddenMemberPaths;

    public ProfileRootOnlyFixtureProjectionInvoker(
        ProfileVisibilityKind rootVisibility,
        System.Collections.Immutable.ImmutableArray<string> rootHiddenMemberPaths,
        ProfileVisibilityKind profileScopeVisibility,
        System.Collections.Immutable.ImmutableArray<string> profileScopeHiddenMemberPaths
    )
    {
        _rootVisibility = rootVisibility;
        _rootHiddenMemberPaths = rootHiddenMemberPaths;
        _profileScopeVisibility = profileScopeVisibility;
        _profileScopeHiddenMemberPaths = profileScopeHiddenMemberPaths;
    }

    public ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        var rootAddress = new ScopeInstanceAddress("$", []);
        var profileScopeAddress = new ScopeInstanceAddress("$.profileScope", []);
        return new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: storedDocument,
            StoredScopeStates:
            [
                new StoredScopeState(
                    Address: rootAddress,
                    Visibility: _rootVisibility,
                    HiddenMemberPaths: _rootHiddenMemberPaths
                ),
                new StoredScopeState(
                    Address: profileScopeAddress,
                    Visibility: _profileScopeVisibility,
                    HiddenMemberPaths: _profileScopeHiddenMemberPaths
                ),
            ],
            VisibleStoredCollectionRows: []
        );
    }
}

/// <summary>
/// Shared helpers for the Slice 2 profile merge integration tests against the
/// synthetic ProfileRootOnlyMergeItem fixture.
/// </summary>
internal static class PostgresqlProfileRootOnlyFixtureSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/profile-root-only-merge";

    public static readonly QualifiedResourceName ProfileRootOnlyMergeItemResource = new(
        "Ed-Fi",
        "ProfileRootOnlyMergeItem"
    );

    public static readonly ResourceInfo ProfileRootOnlyMergeItemResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("ProfileRootOnlyMergeItem"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    public static readonly IReadOnlyList<(string JsonScope, ScopeKind Kind)> InlinedScopes =
    [
        ("$.profileScope", ScopeKind.NonCollection),
    ];

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];
        services.AddSingleton<IHostApplicationLifetime, ProfileRootOnlyFixtureNoOpHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddPostgresqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static DocumentInfo CreateDocumentInfo(int profileRootOnlyMergeItemId)
    {
        var identity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.profileRootOnlyMergeItemId"),
                profileRootOnlyMergeItemId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);
        return new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                ProfileRootOnlyMergeItemResourceInfo,
                identity
            ),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    public static BackendProfileWriteContext CreateProfileContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody,
        ProfileVisibilityKind rootVisibility,
        System.Collections.Immutable.ImmutableArray<string> rootHiddenMemberPaths,
        ProfileVisibilityKind profileScopeVisibility,
        System.Collections.Immutable.ImmutableArray<string> profileScopeHiddenMemberPaths,
        bool creatable = true,
        string profileName = "root-only-merge-profile"
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan, InlinedScopes);
        RequestScopeState[] scopeStates =
        [
            new RequestScopeState(
                Address: new ScopeInstanceAddress("$", []),
                Visibility: rootVisibility,
                Creatable: creatable
            ),
            new RequestScopeState(
                Address: new ScopeInstanceAddress("$.profileScope", []),
                Visibility: profileScopeVisibility,
                Creatable: creatable
            ),
        ];
        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: creatable,
                RequestScopeStates: [.. scopeStates],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: profileName,
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new ProfileRootOnlyFixtureProjectionInvoker(
                rootVisibility,
                rootHiddenMemberPaths,
                profileScopeVisibility,
                profileScopeHiddenMemberPaths
            )
        );
    }

    public static async Task<UpsertResult> SeedAsync(
        ServiceProvider serviceProvider,
        PostgresqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        int profileRootOnlyMergeItemId,
        JsonNode body,
        DocumentUuid documentUuid,
        string traceLabel
    )
    {
        using var scope = serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileRootOnlyFixture",
                    ConnectionString: database.ConnectionString,
                    RouteContext: []
                )
            );
        var upsertRequest = new UpsertRequest(
            ResourceInfo: ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: CreateDocumentInfo(profileRootOnlyMergeItemId),
            MappingSet: mappingSet,
            EdfiDoc: body,
            Headers: [],
            TraceId: new TraceId(traceLabel),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    public static async Task<IReadOnlyDictionary<string, object?>> ReadItemRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                i."ProfileRootOnlyMergeItemId",
                i."DisplayName",
                i."ProfileScopeClearableText",
                i."ProfileScopePreservedText"
            FROM "edfi"."ProfileRootOnlyMergeItem" i
            INNER JOIN "dms"."Document" d ON d."DocumentId" = i."DocumentId"
            WHERE d."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );
        rows.Should().HaveCount(1);
        return rows[0];
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Prerequisite-seed helpers. Used by the fixtures below that require
    //  pre-existing Student + SchoolTypeDescriptor documents so the synthetic
    //  ProfileRootOnlyMergeItem root row can carry FK + key-unification values
    //  into the profiled PUT. Helpers are kept local (per DMS-1124 supplement
    //  scope) and duplicated in the mssql sibling with dialect-specific SQL.
    //
    //  - Student inserts trigger TR_Student_ReferentialIdentity, which creates
    //    the dms.ReferentialIdentity row for ResourceKeyId=3 automatically.
    //  - Descriptors have no trigger, so we insert dms.ReferentialIdentity
    //    explicitly for ResourceKeyId=2 using the ReferentialId computed from
    //    the lowercase URI (identity path $.descriptor).
    // ─────────────────────────────────────────────────────────────────────────

    public static async Task<short> GetResourceKeyIdAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        string projectName,
        string resourceName
    )
    {
        return await database.ExecuteScalarAsync<short>(
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

    public static async Task<long> InsertDocumentRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        short resourceKeyId
    )
    {
        return await database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    public static async Task InsertReferentialIdentityRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid referentialId,
        long documentId,
        short resourceKeyId
    )
    {
        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId)
            ON CONFLICT ("ReferentialId") DO NOTHING;
            """,
            new NpgsqlParameter("referentialId", referentialId),
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    public static async Task<long> SeedStudentAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        string studentUniqueId,
        string firstName = "Seed"
    )
    {
        var resourceKeyId = await GetResourceKeyIdAsync(database, "Ed-Fi", "Student");
        var documentId = await InsertDocumentRowAsync(database, documentUuid, resourceKeyId);
        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Student" ("DocumentId", "StudentUniqueId", "FirstName")
            VALUES (@documentId, @studentUniqueId, @firstName);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("studentUniqueId", studentUniqueId),
            new NpgsqlParameter("firstName", firstName)
        );
        return documentId;
    }

    public static async Task<long> SeedSchoolTypeDescriptorAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        var resourceKeyId = await GetResourceKeyIdAsync(database, "Ed-Fi", "SchoolTypeDescriptor");
        var documentId = await InsertDocumentRowAsync(database, documentUuid, resourceKeyId);
        var uri = $"{@namespace}#{codeValue}";
        const string discriminator = "Ed-Fi:SchoolTypeDescriptor";
        await database.ExecuteNonQueryAsync(
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
                @shortDescription,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", shortDescription),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", uri)
        );

        var descriptorResourceInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("SchoolTypeDescriptor"),
            true
        );
        var descriptorIdentity = new DocumentIdentity([
            new DocumentIdentityElement(DocumentIdentity.DescriptorIdentityJsonPath, uri.ToLowerInvariant()),
        ]);
        var referentialId = ReferentialIdCalculator.ReferentialIdFrom(
            descriptorResourceInfo,
            descriptorIdentity
        );
        await InsertReferentialIdentityRowAsync(database, referentialId.Value, documentId, resourceKeyId);

        return documentId;
    }

    public static async Task<long> SeedProfileRootOnlyMergeItemRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        int profileRootOnlyMergeItemId,
        string displayName,
        string? clearableText,
        string? preservedText,
        long? studentDocumentId,
        string? studentUniqueId,
        long? unifiedDescriptorId,
        bool primaryPresent,
        bool secondaryPresent
    )
    {
        var resourceKeyId = await GetResourceKeyIdAsync(database, "Ed-Fi", "ProfileRootOnlyMergeItem");
        var documentId = await InsertDocumentRowAsync(database, documentUuid, resourceKeyId);

        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."ProfileRootOnlyMergeItem" (
                "DocumentId",
                "ProfileRootOnlyMergeItemId",
                "DisplayName",
                "ProfileScopeClearableText",
                "ProfileScopePreservedText",
                "StudentReference_DocumentId",
                "StudentReference_StudentUniqueId",
                "PrimarySchoolTypeDescriptor_DescriptorId_Present",
                "SecondarySchoolTypeDescriptor_DescriptorId_Present",
                "PrimarySchoolTypeDescriptor_Unified_DescriptorId"
            )
            VALUES (
                @documentId,
                @profileRootOnlyMergeItemId,
                @displayName,
                @clearableText,
                @preservedText,
                @studentDocumentId,
                @studentUniqueId,
                @primaryPresent,
                @secondaryPresent,
                @unifiedDescriptorId
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("profileRootOnlyMergeItemId", profileRootOnlyMergeItemId),
            new NpgsqlParameter("displayName", (object?)displayName ?? DBNull.Value),
            new NpgsqlParameter("clearableText", (object?)clearableText ?? DBNull.Value),
            new NpgsqlParameter("preservedText", (object?)preservedText ?? DBNull.Value),
            new NpgsqlParameter("studentDocumentId", (object?)studentDocumentId ?? DBNull.Value),
            new NpgsqlParameter("studentUniqueId", (object?)studentUniqueId ?? DBNull.Value),
            new NpgsqlParameter("primaryPresent", primaryPresent ? (object)true : DBNull.Value),
            new NpgsqlParameter("secondaryPresent", secondaryPresent ? (object)true : DBNull.Value),
            new NpgsqlParameter("unifiedDescriptorId", (object?)unifiedDescriptorId ?? DBNull.Value)
        );

        return documentId;
    }

    public static async Task<IReadOnlyDictionary<string, object?>> ReadItemRowWithReferenceColumnsAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                i."ProfileRootOnlyMergeItemId",
                i."DisplayName",
                i."ProfileScopeClearableText",
                i."ProfileScopePreservedText",
                i."StudentReference_DocumentId",
                i."StudentReference_StudentUniqueId",
                i."PrimarySchoolTypeDescriptor_DescriptorId_Present",
                i."SecondarySchoolTypeDescriptor_DescriptorId_Present",
                i."PrimarySchoolTypeDescriptor_Unified_DescriptorId"
            FROM "edfi"."ProfileRootOnlyMergeItem" i
            INNER JOIN "dms"."Document" d ON d."DocumentId" = i."DocumentId"
            WHERE d."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );
        rows.Should().HaveCount(1);
        return rows[0];
    }

    public static DocumentInfo CreateDocumentInfoWithReferences(
        int profileRootOnlyMergeItemId,
        DocumentReference[] documentReferences,
        DescriptorReference[] descriptorReferences
    )
    {
        var identity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.profileRootOnlyMergeItemId"),
                profileRootOnlyMergeItemId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);
        return new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                ProfileRootOnlyMergeItemResourceInfo,
                identity
            ),
            DocumentReferences: documentReferences,
            DocumentReferenceArrays: [],
            DescriptorReferences: descriptorReferences,
            SuperclassIdentity: null
        );
    }

    public static DescriptorReference BuildSchoolTypeDescriptorReference(string uri, string referenceJsonPath)
    {
        var descriptorResourceInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("SchoolTypeDescriptor"),
            true
        );
        var descriptorIdentity = new DocumentIdentity([
            new DocumentIdentityElement(DocumentIdentity.DescriptorIdentityJsonPath, uri.ToLowerInvariant()),
        ]);
        return new DescriptorReference(
            ResourceInfo: descriptorResourceInfo,
            DocumentIdentity: descriptorIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                descriptorResourceInfo,
                descriptorIdentity
            ),
            Path: new JsonPath(referenceJsonPath)
        );
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture A: Hidden inlined preservation on the root scope.
//  Spec scenario 5 — hidden inlined column preservation, multi-column.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Seed a ProfileRootOnlyMergeItem with displayName + both inlined nested scalars. The
/// profile context declares the root scope visible-present with
/// <c>profileScope.preservedText</c> listed as a hidden member path. PUT a body that
/// updates only displayName and profileScope.clearableText (omits preservedText).
/// Assert: displayName + clearableText updated; preservedText preserved.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Profiled_Put_With_Hidden_Inlined_PreservedText_On_Root_Scope
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("bb000001-0000-0000-0000-000000000001")
    );
    private const int ItemId = 9001;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileRootOnlyFixtureSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileRootOnlyFixtureSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "OriginalDisplay",
            ["profileScope"] = new JsonObject
            {
                ["clearableText"] = "OriginalClearable",
                ["preservedText"] = "OriginalPreserved",
            },
        };
        var seedResult = await PostgresqlProfileRootOnlyFixtureSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            seedBody,
            DocumentUuid,
            "profile-root-only-hidden-inlined-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var writeBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
            ["profileScope"] = new JsonObject { ["clearableText"] = "UpdatedClearable" },
        };
        _putResult = await ExecuteProfiledPutAsync(writeBody);
        _rowAfterPut = await PostgresqlProfileRootOnlyFixtureSupport.ReadItemRowAsync(
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
    public void It_updates_display_name() => _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_updates_profile_scope_clearable_text() =>
        _rowAfterPut["ProfileScopeClearableText"].Should().Be("UpdatedClearable");

    [Test]
    public void It_preserves_profile_scope_preserved_text() =>
        _rowAfterPut["ProfileScopePreservedText"].Should().Be("OriginalPreserved");

    private async Task<UpdateResult> ExecuteProfiledPutAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileRootOnlyFixture",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = PostgresqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: ["preservedText"]
        );
        var updateRequest = new UpdateRequest(
            ResourceInfo: PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: PostgresqlProfileRootOnlyFixtureSupport.CreateDocumentInfo(ItemId),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("profile-root-only-hidden-inlined-put"),
            DocumentUuid: DocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture B: Visible-absent inlined scope clears clearable, preserves hidden.
//  Spec scenario 4 — visible-absent inlined scope clears clearable only, with
//  hidden-override precedence protecting preservedText.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Seed a ProfileRootOnlyMergeItem with both inlined nested scalars populated. The
/// request scope for <c>$.profileScope</c> is VisibleAbsent (the profile says the
/// sub-object is not present in the request); the stored scope state also classifies
/// <c>$.profileScope</c> as VisibleAbsent with <c>preservedText</c> listed as a hidden
/// member path. PUT a body that omits profileScope entirely and updates displayName.
/// Assert: displayName updated, profileScope.clearableText cleared to NULL,
/// profileScope.preservedText preserved from seed.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Profiled_Put_With_VisibleAbsent_Inlined_Scope_Clears_Clearable_And_Preserves_Hidden
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("bb000002-0000-0000-0000-000000000002")
    );
    private const int ItemId = 9002;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileRootOnlyFixtureSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileRootOnlyFixtureSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "OriginalDisplay",
            ["profileScope"] = new JsonObject
            {
                ["clearableText"] = "OriginalClearable",
                ["preservedText"] = "OriginalPreserved",
            },
        };
        var seedResult = await PostgresqlProfileRootOnlyFixtureSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            seedBody,
            DocumentUuid,
            "profile-root-only-visible-absent-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        // The profile declares $.profileScope absent, so the writable body omits the sub-object.
        var writeBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
        };
        _putResult = await ExecuteProfiledPutAsync(writeBody);
        _rowAfterPut = await PostgresqlProfileRootOnlyFixtureSupport.ReadItemRowAsync(
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
    public void It_updates_display_name() => _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_clears_profile_scope_clearable_text() =>
        _rowAfterPut["ProfileScopeClearableText"].Should().BeNull();

    [Test]
    public void It_preserves_profile_scope_preserved_text() =>
        _rowAfterPut["ProfileScopePreservedText"].Should().Be("OriginalPreserved");

    private async Task<UpdateResult> ExecuteProfiledPutAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileRootOnlyFixture",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = PostgresqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            profileScopeVisibility: ProfileVisibilityKind.VisibleAbsent,
            profileScopeHiddenMemberPaths: ["preservedText"]
        );
        var updateRequest = new UpdateRequest(
            ResourceInfo: PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: PostgresqlProfileRootOnlyFixtureSupport.CreateDocumentInfo(ItemId),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("profile-root-only-visible-absent-put"),
            DocumentUuid: DocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture C: Hidden StudentReference preservation (FK + propagated identity).
//  Spec scenario 6 (FK half) — hidden document reference preserves its FK and
//  propagated identity columns across a profiled PUT.
//
//  The "descriptor half" of spec scenario 6 (hiding one primary/secondary
//  descriptor member while the other agrees visibly) is logically equivalent
//  to scenario 7a (key-unification agreement with a hidden member) because the
//  two descriptor members share a single unified canonical via the equality
//  constraint — the direction of which member is hidden is symmetric. It is
//  therefore covered by the Fixture E (scenario 7a) assertions below rather
//  than as a separate fixture.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Seed a Student document and a ProfileRootOnlyMergeItem row whose
/// studentReference points at the Student. The profile hides
/// <c>studentReference</c> on the root scope. PUT a body that updates only
/// displayName (and supplies the visible descriptors with the stored canonical
/// URI so the unchanged key-unification plan agrees). Assert:
/// StudentReference_DocumentId and StudentReference_StudentUniqueId are
/// preserved from stored state.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_ProfiledRootOnly_HiddenStudentReference_PreservesFKAndPropagatedIdentity
{
    private static readonly Guid ItemDocumentUuid = Guid.Parse("bb000003-0000-0000-0000-000000000003");
    private static readonly Guid StudentDocumentUuid = Guid.Parse("bb000003-1000-0000-0000-000000000003");
    private static readonly Guid PublicDescriptorDocumentUuid = Guid.Parse(
        "bb000003-2000-0000-0000-000000000003"
    );
    private const int ItemId = 9003;
    private const string StudentUniqueId = "STU-003";
    private const string DescriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";
    private const string PublicCodeValue = "Public";
    private static readonly string PublicUri = $"{DescriptorNamespace}#{PublicCodeValue}";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _studentDocumentId;
    private long _publicDescriptorDocumentId;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileRootOnlyFixtureSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileRootOnlyFixtureSupport.CreateServiceProvider();

        _studentDocumentId = await PostgresqlProfileRootOnlyFixtureSupport.SeedStudentAsync(
            _database,
            StudentDocumentUuid,
            StudentUniqueId
        );
        _publicDescriptorDocumentId =
            await PostgresqlProfileRootOnlyFixtureSupport.SeedSchoolTypeDescriptorAsync(
                _database,
                PublicDescriptorDocumentUuid,
                DescriptorNamespace,
                PublicCodeValue,
                PublicCodeValue
            );
        await PostgresqlProfileRootOnlyFixtureSupport.SeedProfileRootOnlyMergeItemRowAsync(
            _database,
            ItemDocumentUuid,
            ItemId,
            displayName: "OriginalDisplay",
            clearableText: null,
            preservedText: null,
            studentDocumentId: _studentDocumentId,
            studentUniqueId: StudentUniqueId,
            unifiedDescriptorId: _publicDescriptorDocumentId,
            primaryPresent: true,
            secondaryPresent: true
        );

        // Writable body omits the hidden studentReference; visible primary +
        // secondary descriptors both resolve to "Public" so the k-u plan agrees.
        var writeBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
            ["primarySchoolTypeDescriptor"] = PublicUri,
            ["secondarySchoolTypeDescriptor"] = PublicUri,
        };
        _putResult = await ExecuteProfiledPutAsync(writeBody);
        _rowAfterPut = await PostgresqlProfileRootOnlyFixtureSupport.ReadItemRowWithReferenceColumnsAsync(
            _database,
            new DocumentUuid(ItemDocumentUuid)
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
    public void It_updates_display_name() => _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_preserves_student_reference_document_id() =>
        _rowAfterPut["StudentReference_DocumentId"].Should().Be(_studentDocumentId);

    [Test]
    public void It_preserves_student_reference_student_unique_id() =>
        _rowAfterPut["StudentReference_StudentUniqueId"].Should().Be(StudentUniqueId);

    [Test]
    public void It_preserves_unified_descriptor_canonical_fk() =>
        _rowAfterPut["PrimarySchoolTypeDescriptor_Unified_DescriptorId"]
            .Should()
            .Be(_publicDescriptorDocumentId);

    [Test]
    public void It_preserves_primary_descriptor_synthetic_presence() =>
        _rowAfterPut["PrimarySchoolTypeDescriptor_DescriptorId_Present"].Should().Be(true);

    [Test]
    public void It_preserves_secondary_descriptor_synthetic_presence() =>
        _rowAfterPut["SecondarySchoolTypeDescriptor_DescriptorId_Present"].Should().Be(true);

    private async Task<UpdateResult> ExecuteProfiledPutAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileRootOnlyFixture",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = PostgresqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: ["studentReference"],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: []
        );
        // Both descriptors flow through as visible references.
        var descriptorReferences = new[]
        {
            PostgresqlProfileRootOnlyFixtureSupport.BuildSchoolTypeDescriptorReference(
                PublicUri,
                "$.primarySchoolTypeDescriptor"
            ),
            PostgresqlProfileRootOnlyFixtureSupport.BuildSchoolTypeDescriptorReference(
                PublicUri,
                "$.secondarySchoolTypeDescriptor"
            ),
        };
        var updateRequest = new UpdateRequest(
            ResourceInfo: PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: PostgresqlProfileRootOnlyFixtureSupport.CreateDocumentInfoWithReferences(
                ItemId,
                documentReferences: [],
                descriptorReferences: descriptorReferences
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("profile-root-only-hidden-ref-put"),
            DocumentUuid: new DocumentUuid(ItemDocumentUuid),
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture D: Key-unification hidden member + visible agreement.
//  Spec scenario 7a — hiding one descriptor member while the other visibly
//  resolves to the same canonical URI succeeds; the canonical FK is preserved
//  and both synthetic presence columns stay TRUE. Covers the descriptor half
//  of scenario 6 via symmetry (see Fixture C header comment).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Seed both descriptors with canonical unified = Public. Profile hides
/// <c>secondarySchoolTypeDescriptor</c> on the root scope; the request
/// supplies <c>primarySchoolTypeDescriptor</c> pointing at the same "Public"
/// URI as stored, so the resolver's first-present-wins agreement check passes.
/// Assert: displayName updated, unified canonical + both presence columns
/// preserved.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_ProfiledRootOnly_KeyUnificationHiddenMember_AgreementSucceeds
{
    private static readonly Guid ItemDocumentUuid = Guid.Parse("bb000004-0000-0000-0000-000000000004");
    private static readonly Guid PublicDescriptorDocumentUuid = Guid.Parse(
        "bb000004-2000-0000-0000-000000000004"
    );
    private const int ItemId = 9004;
    private const string DescriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";
    private const string PublicCodeValue = "Public";
    private static readonly string PublicUri = $"{DescriptorNamespace}#{PublicCodeValue}";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _publicDescriptorDocumentId;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileRootOnlyFixtureSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileRootOnlyFixtureSupport.CreateServiceProvider();

        _publicDescriptorDocumentId =
            await PostgresqlProfileRootOnlyFixtureSupport.SeedSchoolTypeDescriptorAsync(
                _database,
                PublicDescriptorDocumentUuid,
                DescriptorNamespace,
                PublicCodeValue,
                PublicCodeValue
            );
        await PostgresqlProfileRootOnlyFixtureSupport.SeedProfileRootOnlyMergeItemRowAsync(
            _database,
            ItemDocumentUuid,
            ItemId,
            displayName: "OriginalDisplay",
            clearableText: null,
            preservedText: null,
            studentDocumentId: null,
            studentUniqueId: null,
            unifiedDescriptorId: _publicDescriptorDocumentId,
            primaryPresent: true,
            secondaryPresent: true
        );

        var writeBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
            ["primarySchoolTypeDescriptor"] = PublicUri,
        };
        _putResult = await ExecuteProfiledPutAsync(writeBody);
        _rowAfterPut = await PostgresqlProfileRootOnlyFixtureSupport.ReadItemRowWithReferenceColumnsAsync(
            _database,
            new DocumentUuid(ItemDocumentUuid)
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
    public void It_updates_display_name() => _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_preserves_unified_descriptor_canonical_fk() =>
        _rowAfterPut["PrimarySchoolTypeDescriptor_Unified_DescriptorId"]
            .Should()
            .Be(_publicDescriptorDocumentId);

    [Test]
    public void It_preserves_primary_descriptor_synthetic_presence() =>
        _rowAfterPut["PrimarySchoolTypeDescriptor_DescriptorId_Present"].Should().Be(true);

    [Test]
    public void It_preserves_secondary_descriptor_synthetic_presence() =>
        _rowAfterPut["SecondarySchoolTypeDescriptor_DescriptorId_Present"].Should().Be(true);

    private async Task<UpdateResult> ExecuteProfiledPutAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileRootOnlyFixture",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = PostgresqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: ["secondarySchoolTypeDescriptor"],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: []
        );
        var descriptorReferences = new[]
        {
            PostgresqlProfileRootOnlyFixtureSupport.BuildSchoolTypeDescriptorReference(
                PublicUri,
                "$.primarySchoolTypeDescriptor"
            ),
        };
        var updateRequest = new UpdateRequest(
            ResourceInfo: PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: PostgresqlProfileRootOnlyFixtureSupport.CreateDocumentInfoWithReferences(
                ItemId,
                documentReferences: [],
                descriptorReferences: descriptorReferences
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("profile-root-only-ku-agreement-put"),
            DocumentUuid: new DocumentUuid(ItemDocumentUuid),
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture E: Key-unification hidden member + visible DISagreement.
//  Spec scenario 7b — hiding one descriptor member while the visible member
//  resolves to a DIFFERENT canonical raises a validation failure and rolls
//  back the transaction; the stored row is unchanged.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Seed unified canonical = Public. Profile hides
/// <c>secondarySchoolTypeDescriptor</c>; request supplies
/// <c>primarySchoolTypeDescriptor</c> pointing at "Private" (a different
/// canonical). The resolver's first-present-wins equality check rejects the
/// disagreement, surfacing as UpdateFailureValidation. Assert: validation
/// failure is returned and the stored row is unchanged.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_ProfiledRootOnly_KeyUnificationHiddenMember_DisagreementFailsValidation
{
    private static readonly Guid ItemDocumentUuid = Guid.Parse("bb000005-0000-0000-0000-000000000005");
    private static readonly Guid PublicDescriptorDocumentUuid = Guid.Parse(
        "bb000005-1000-0000-0000-000000000005"
    );
    private static readonly Guid PrivateDescriptorDocumentUuid = Guid.Parse(
        "bb000005-2000-0000-0000-000000000005"
    );
    private const int ItemId = 9005;
    private const string DescriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";
    private const string PublicCodeValue = "Public";
    private const string PrivateCodeValue = "Private";
    private static readonly string PrivateUri = $"{DescriptorNamespace}#{PrivateCodeValue}";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _publicDescriptorDocumentId;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileRootOnlyFixtureSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileRootOnlyFixtureSupport.CreateServiceProvider();

        _publicDescriptorDocumentId =
            await PostgresqlProfileRootOnlyFixtureSupport.SeedSchoolTypeDescriptorAsync(
                _database,
                PublicDescriptorDocumentUuid,
                DescriptorNamespace,
                PublicCodeValue,
                PublicCodeValue
            );
        await PostgresqlProfileRootOnlyFixtureSupport.SeedSchoolTypeDescriptorAsync(
            _database,
            PrivateDescriptorDocumentUuid,
            DescriptorNamespace,
            PrivateCodeValue,
            PrivateCodeValue
        );
        await PostgresqlProfileRootOnlyFixtureSupport.SeedProfileRootOnlyMergeItemRowAsync(
            _database,
            ItemDocumentUuid,
            ItemId,
            displayName: "OriginalDisplay",
            clearableText: null,
            preservedText: null,
            studentDocumentId: null,
            studentUniqueId: null,
            unifiedDescriptorId: _publicDescriptorDocumentId,
            primaryPresent: true,
            secondaryPresent: true
        );

        var writeBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
            ["primarySchoolTypeDescriptor"] = PrivateUri,
        };
        _putResult = await ExecuteProfiledPutAsync(writeBody);
        _rowAfterPut = await PostgresqlProfileRootOnlyFixtureSupport.ReadItemRowWithReferenceColumnsAsync(
            _database,
            new DocumentUuid(ItemDocumentUuid)
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
    public void It_returns_update_validation_failure() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateFailureValidation>();

    [Test]
    public void It_surfaces_key_unification_conflict_message()
    {
        var failure = _putResult as UpdateResult.UpdateFailureValidation;
        failure.Should().NotBeNull();
        failure!
            .ValidationFailures.Should()
            .Contain(f => f.Message.Contains("Key-unification conflict", StringComparison.Ordinal));
    }

    [Test]
    public void It_leaves_display_name_unchanged() =>
        _rowAfterPut["DisplayName"].Should().Be("OriginalDisplay");

    [Test]
    public void It_leaves_unified_descriptor_canonical_fk_unchanged() =>
        _rowAfterPut["PrimarySchoolTypeDescriptor_Unified_DescriptorId"]
            .Should()
            .Be(_publicDescriptorDocumentId);

    private async Task<UpdateResult> ExecuteProfiledPutAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileRootOnlyFixture",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = PostgresqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: ["secondarySchoolTypeDescriptor"],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: []
        );
        var descriptorReferences = new[]
        {
            PostgresqlProfileRootOnlyFixtureSupport.BuildSchoolTypeDescriptorReference(
                PrivateUri,
                "$.primarySchoolTypeDescriptor"
            ),
        };
        var updateRequest = new UpdateRequest(
            ResourceInfo: PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: PostgresqlProfileRootOnlyFixtureSupport.CreateDocumentInfoWithReferences(
                ItemId,
                documentReferences: [],
                descriptorReferences: descriptorReferences
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("profile-root-only-ku-disagreement-put"),
            DocumentUuid: new DocumentUuid(ItemDocumentUuid),
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture F: Synthetic presence preservation when both k-u members are hidden.
//  Spec scenario 8 covers the case where all key-unification members are hidden
//  and the PUT carries neither descriptor. The synthetic presence columns and
//  unified canonical must stay at their stored values.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Seed unified canonical = Public, both presence = TRUE. Profile hides BOTH
/// descriptor members on the root scope. PUT omits both descriptors entirely.
/// Assert: displayName updated, both presence columns preserved TRUE, unified
/// canonical preserved.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_ProfiledRootOnly_SyntheticPresence_PreservedWhenGoverningMemberHidden
{
    private static readonly Guid ItemDocumentUuid = Guid.Parse("bb000006-0000-0000-0000-000000000006");
    private static readonly Guid PublicDescriptorDocumentUuid = Guid.Parse(
        "bb000006-2000-0000-0000-000000000006"
    );
    private const int ItemId = 9006;
    private const string DescriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";
    private const string PublicCodeValue = "Public";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _publicDescriptorDocumentId;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileRootOnlyFixtureSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostgresqlProfileRootOnlyFixtureSupport.CreateServiceProvider();

        _publicDescriptorDocumentId =
            await PostgresqlProfileRootOnlyFixtureSupport.SeedSchoolTypeDescriptorAsync(
                _database,
                PublicDescriptorDocumentUuid,
                DescriptorNamespace,
                PublicCodeValue,
                PublicCodeValue
            );
        await PostgresqlProfileRootOnlyFixtureSupport.SeedProfileRootOnlyMergeItemRowAsync(
            _database,
            ItemDocumentUuid,
            ItemId,
            displayName: "OriginalDisplay",
            clearableText: null,
            preservedText: null,
            studentDocumentId: null,
            studentUniqueId: null,
            unifiedDescriptorId: _publicDescriptorDocumentId,
            primaryPresent: true,
            secondaryPresent: true
        );

        var writeBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
        };
        _putResult = await ExecuteProfiledPutAsync(writeBody);
        _rowAfterPut = await PostgresqlProfileRootOnlyFixtureSupport.ReadItemRowWithReferenceColumnsAsync(
            _database,
            new DocumentUuid(ItemDocumentUuid)
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
    public void It_updates_display_name() => _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_preserves_unified_descriptor_canonical_fk() =>
        _rowAfterPut["PrimarySchoolTypeDescriptor_Unified_DescriptorId"]
            .Should()
            .Be(_publicDescriptorDocumentId);

    [Test]
    public void It_preserves_primary_descriptor_synthetic_presence() =>
        _rowAfterPut["PrimarySchoolTypeDescriptor_DescriptorId_Present"].Should().Be(true);

    [Test]
    public void It_preserves_secondary_descriptor_synthetic_presence() =>
        _rowAfterPut["SecondarySchoolTypeDescriptor_DescriptorId_Present"].Should().Be(true);

    private async Task<UpdateResult> ExecuteProfiledPutAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileRootOnlyFixture",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = PostgresqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: ["primarySchoolTypeDescriptor", "secondarySchoolTypeDescriptor"],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: []
        );
        var updateRequest = new UpdateRequest(
            ResourceInfo: PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: PostgresqlProfileRootOnlyFixtureSupport.CreateDocumentInfoWithReferences(
                ItemId,
                documentReferences: [],
                descriptorReferences: []
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("profile-root-only-ku-both-hidden-put"),
            DocumentUuid: new DocumentUuid(ItemDocumentUuid),
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}
