// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Slice 6 (DMS-1142) integration coverage for profile guarded no-op against PostgreSQL.
// The fixtures in this file exercise unchanged profiled writes through the real
// relational write executor and assert that no DML-visible state changes — neither
// row contents, nor Document version/timestamp metadata, nor the DocumentChangeEvent
// audit log — when the post-merge effective rowset matches the stored rowset.
//
// The shared infrastructure (DI handlers, persisted-state records, read helper, and
// abstract test base) is intentionally reusable so the sibling profiled fixtures
// landing in subsequent tasks (POST-as-update, separate-table, top-level collection,
// stale-compare write conflict) can extend it without further wiring.

using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file sealed class ProfileGuardedNoOpHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class ProfileGuardedNoOpAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class ProfileGuardedNoOpUpdateCascadeHandler : IUpdateCascadeHandler
{
    public UpdateCascadeResult Cascade(
        JsonElement originalEdFiDoc,
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
/// Stale-compare freshness checker for the profiled guarded no-op suite. The first
/// invocation bumps <c>ContentVersion</c> on the target document before delegating
/// to the production checker, simulating a concurrent writer landing between the
/// candidate detection and freshness recheck. Reused by Task 9 (stale-compare).
/// </summary>
file sealed class ProfileGuardedNoOpConcurrentContentVersionBumpFreshnessChecker(
    NpgsqlDataSourceProvider dataSourceProvider
) : IRelationalWriteFreshnessChecker
{
    private readonly NpgsqlDataSourceProvider _dataSourceProvider =
        dataSourceProvider ?? throw new ArgumentNullException(nameof(dataSourceProvider));

    private readonly RelationalWriteFreshnessChecker _innerChecker = new();
    private bool _hasBumpedContentVersion;

    public async Task<bool> IsCurrentAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        if (!_hasBumpedContentVersion)
        {
            _hasBumpedContentVersion = true;

            await BumpContentVersionAsync(targetContext.DocumentId, cancellationToken);
        }

        return await _innerChecker.IsCurrentAsync(request, targetContext, writeSession, cancellationToken);
    }

    private async Task BumpContentVersionAsync(long documentId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync(
            cancellationToken
        );

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "dms"."Document"
            SET "ContentVersion" = "ContentVersion" + 1
            WHERE "DocumentId" = @documentId;
            """;
        command.Parameters.Add(new NpgsqlParameter("documentId", documentId));

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

        if (rowsAffected != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one document content-version bump for document id '{documentId}', but affected {rowsAffected} rows."
            );
        }
    }
}

internal sealed record ProfileGuardedNoOpDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion,
    DateTime ContentLastModifiedAt,
    long IdentityVersion,
    DateTime IdentityLastModifiedAt
);

internal sealed record ProfileGuardedNoOpPersistedState(
    ProfileGuardedNoOpDocumentRow Document,
    IReadOnlyDictionary<string, object?> RootRow,
    long DocumentChangeEventCount
);

file static class ProfileGuardedNoOpIntegrationTestSupport
{
    public static async Task<ProfileGuardedNoOpPersistedState> ReadPersistedStateAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        Func<
            PostgresqlGeneratedDdlTestDatabase,
            long,
            Task<IReadOnlyDictionary<string, object?>>
        > readRootRowByDocumentId
    )
    {
        var documentRows = await database.QueryRowsAsync(
            """
            SELECT "DocumentId", "DocumentUuid", "ResourceKeyId",
                   "ContentVersion", "ContentLastModifiedAt",
                   "IdentityVersion", "IdentityLastModifiedAt"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        if (documentRows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {documentRows.Count}."
            );
        }

        var documentRow = new ProfileGuardedNoOpDocumentRow(
            DocumentId: Convert.ToInt64(documentRows[0]["DocumentId"], CultureInfo.InvariantCulture),
            DocumentUuid: (Guid)documentRows[0]["DocumentUuid"]!,
            ResourceKeyId: Convert.ToInt16(documentRows[0]["ResourceKeyId"], CultureInfo.InvariantCulture),
            ContentVersion: Convert.ToInt64(documentRows[0]["ContentVersion"], CultureInfo.InvariantCulture),
            ContentLastModifiedAt: Convert.ToDateTime(
                documentRows[0]["ContentLastModifiedAt"],
                CultureInfo.InvariantCulture
            ),
            IdentityVersion: Convert.ToInt64(
                documentRows[0]["IdentityVersion"],
                CultureInfo.InvariantCulture
            ),
            IdentityLastModifiedAt: Convert.ToDateTime(
                documentRows[0]["IdentityLastModifiedAt"],
                CultureInfo.InvariantCulture
            )
        );

        var rootRow = await readRootRowByDocumentId(database, documentRow.DocumentId);

        var changeEventRows = await database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "RowCount"
            FROM "dms"."DocumentChangeEvent"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentRow.DocumentId)
        );
        var changeEventCount = Convert.ToInt64(changeEventRows[0]["RowCount"], CultureInfo.InvariantCulture);

        return new ProfileGuardedNoOpPersistedState(documentRow, rootRow, changeEventCount);
    }
}

/// <summary>
/// Shared base for the profiled guarded no-op fixtures in this file. Owns the database
/// provisioning lifecycle and the per-test service-provider lifecycle, but delegates
/// the shape-specific scaffolding (DDL fixture path, DI service provider, seed CREATE,
/// identical PUT, identical POST-as-update, and root-row reader) to a per-shape
/// intermediate base class. The shape-specific bases — for example
/// <see cref="RootOnlyShapeProfileGuardedNoOpFixtureBase"/> and
/// <see cref="SeparateTableShapeProfileGuardedNoOpFixtureBase"/> — implement these
/// abstracts using their respective profile-merge support classes. Each concrete fixture
/// then inherits its shape's intermediate base and supplies the assertion-specific
/// orchestration in <see cref="SetUpTestAsync"/>.
/// </summary>
internal abstract class ProfileGuardedNoOpGeneratedDdlFixtureTestBase
{
    protected MappingSet _mappingSet = null!;
    protected PostgresqlGeneratedDdlTestDatabase _database = null!;
    protected ServiceProvider _serviceProvider = null!;

    /// <summary>
    /// Repository-relative path to the generated-DDL fixture for this shape. Loaded
    /// once in <see cref="OneTimeSetUp"/> and provisioned into <see cref="_database"/>.
    /// </summary>
    protected abstract string FixtureRelativePath { get; }

    /// <summary>
    /// Builds the per-test service provider for this shape. The shape's intermediate
    /// base owns the registrations that match its support class's DI surface; this
    /// keeps the executor wiring shape-symmetric while letting each shape configure
    /// the freshness checker / cascade handler / authorization stubs it needs.
    /// </summary>
    protected abstract ServiceProvider CreateServiceProvider();

    /// <summary>
    /// Issues a non-profiled CREATE for the shape's synthetic target resource. The
    /// CREATE intentionally omits a profile context so the stored document is in a
    /// known canonical shape; the subsequent profiled PUT carries the profile context
    /// that activates the guarded no-op path. Implementations must assert the seed
    /// returned <see cref="UpsertResult.InsertSuccess"/>.
    /// </summary>
    protected abstract Task ExecuteProfiledShapeCreateAsync(DocumentUuid documentUuid);

    /// <summary>
    /// Issues a profiled PUT against the previously-seeded document with an
    /// identical body. The profile context must declare every shape scope fully
    /// VisiblePresent with no hidden member paths so the merged effective rowset
    /// equals the stored rowset and the guarded no-op short-circuit fires.
    /// </summary>
    protected abstract Task<UpdateResult> ExecuteProfiledShapeIdenticalPutAsync(DocumentUuid documentUuid);

    /// <summary>
    /// Issues a profiled POST against the previously-seeded document with an
    /// identical body and a DIFFERENT incoming <see cref="DocumentUuid"/>. The
    /// executor must classify the request as POST-as-update by semantic identity
    /// rather than inserting a new document, and the same VisiblePresent profile
    /// context as the identical-PUT case must trigger the guarded no-op short-circuit.
    /// </summary>
    protected abstract Task<UpsertResult> ExecuteProfiledShapePostAsUpdateAsync(
        DocumentUuid incomingDocumentUuid
    );

    /// <summary>
    /// Reads the single root-table row for this shape keyed by the supplied
    /// <paramref name="documentId"/>. Used by
    /// <see cref="ProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync"/> to
    /// snapshot the root rowset before/after the profiled write.
    /// </summary>
    protected abstract Task<IReadOnlyDictionary<string, object?>> ReadShapeRootRowByDocumentIdAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    );

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);

        _mappingSet = fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = CreateServiceProvider();
        await SetUpTestAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    protected abstract Task SetUpTestAsync();

    /// <summary>
    /// Builds the standard service provider used by the profiled guarded no-op suite.
    /// Mirrors the support classes' <c>CreateServiceProvider</c> but with the local
    /// profiled DI handler stubs so this file owns its DI surface. Shared by every
    /// shape-specific intermediate base.
    /// </summary>
    protected static ServiceProvider CreateDefaultServiceProvider(
        Action<IServiceCollection>? configureServices = null
    )
    {
        ServiceCollection services = [];
        services.AddSingleton<IHostApplicationLifetime, ProfileGuardedNoOpHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddPostgresqlReferenceResolver();
        configureServices?.Invoke(services);

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    /// <summary>
    /// Service provider variant that swaps in
    /// <see cref="ProfileGuardedNoOpConcurrentContentVersionBumpFreshnessChecker"/> for the
    /// production freshness checker so the first revalidation observes a stale
    /// <c>ContentVersion</c>. Reserved for the Task 9 stale-compare fixtures.
    /// </summary>
    protected static ServiceProvider CreateStaleCompareServiceProvider() =>
        CreateDefaultServiceProvider(static services =>
        {
            services.RemoveAll<IRelationalWriteFreshnessChecker>();
            services.AddScoped<
                IRelationalWriteFreshnessChecker,
                ProfileGuardedNoOpConcurrentContentVersionBumpFreshnessChecker
            >();
        });

    /// <summary>
    /// Counts rows in <c>dms.Document</c> matching the supplied
    /// <paramref name="documentUuid"/>. Used to assert that a profiled
    /// POST-as-update did NOT insert a new document under the incoming UUID.
    /// </summary>
    protected async Task<long> CountDocumentRowsByUuidAsync(Guid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "RowCount"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );
        return Convert.ToInt64(rows[0]["RowCount"], CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Intermediate base for fixtures whose target is the synthetic
/// <c>ProfileRootOnlyMergeItem</c> resource. Wires the abstract shape hooks of
/// <see cref="ProfileGuardedNoOpGeneratedDdlFixtureTestBase"/> through to
/// <see cref="PostgresqlProfileRootOnlyFixtureSupport"/> with the
/// fully-VisiblePresent profile context the guarded-no-op invariants require.
/// </summary>
internal abstract class RootOnlyShapeProfileGuardedNoOpFixtureBase
    : ProfileGuardedNoOpGeneratedDdlFixtureTestBase
{
    protected const int DefaultProfileRootOnlyMergeItemId = 9101;

    protected static readonly JsonNode IdenticalRequestBody = new JsonObject
    {
        ["profileRootOnlyMergeItemId"] = DefaultProfileRootOnlyMergeItemId,
        ["displayName"] = "OriginalDisplay",
        ["profileScope"] = new JsonObject
        {
            ["clearableText"] = "OriginalClearable",
            ["preservedText"] = "OriginalPreserved",
        },
    };

    protected override string FixtureRelativePath =>
        PostgresqlProfileRootOnlyFixtureSupport.FixtureRelativePath;

    protected override ServiceProvider CreateServiceProvider() => CreateDefaultServiceProvider();

    protected override async Task ExecuteProfiledShapeCreateAsync(DocumentUuid documentUuid)
    {
        var seedResult = await PostgresqlProfileRootOnlyFixtureSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            DefaultProfileRootOnlyMergeItemId,
            IdenticalRequestBody.DeepClone(),
            documentUuid,
            "pg-profile-guarded-no-op-put-create"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    protected override async Task<UpdateResult> ExecuteProfiledShapeIdenticalPutAsync(
        DocumentUuid documentUuid
    )
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileGuardedNoOp",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writeBody = IdenticalRequestBody.DeepClone();
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = PostgresqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: []
        );
        var updateRequest = new UpdateRequest(
            ResourceInfo: PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: PostgresqlProfileRootOnlyFixtureSupport.CreateDocumentInfo(
                DefaultProfileRootOnlyMergeItemId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("pg-profile-guarded-no-op-put-update"),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileGuardedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileGuardedNoOpAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }

    protected override async Task<UpsertResult> ExecuteProfiledShapePostAsUpdateAsync(
        DocumentUuid incomingDocumentUuid
    )
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileGuardedNoOp",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writeBody = IdenticalRequestBody.DeepClone();
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = PostgresqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: []
        );
        var upsertRequest = new UpsertRequest(
            ResourceInfo: PostgresqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: PostgresqlProfileRootOnlyFixtureSupport.CreateDocumentInfo(
                DefaultProfileRootOnlyMergeItemId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("pg-profile-guarded-no-op-post-as-update"),
            DocumentUuid: incomingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileGuardedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileGuardedNoOpAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    protected override Task<IReadOnlyDictionary<string, object?>> ReadShapeRootRowByDocumentIdAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    ) => ReadRootOnlyShapeRootRowByDocumentIdAsync(database, documentId);

    /// <summary>
    /// Reads the single <c>edfi.ProfileRootOnlyMergeItem</c> row keyed by the supplied
    /// <paramref name="documentId"/>. Returns the root rowset that the no-op invariants
    /// compare before and after the profiled write.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, object?>> ReadRootOnlyShapeRootRowByDocumentIdAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
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
            FROM "edfi"."ProfileRootOnlyMergeItem"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        if (rows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one ProfileRootOnlyMergeItem row for document id '{documentId}', but found {rows.Count}."
            );
        }

        return rows[0];
    }
}

/// <summary>
/// Intermediate base for fixtures whose target is the synthetic
/// <c>ProfileSeparateTableMergeItem</c> resource (root row +
/// <c>sample.ProfileSeparateTableMergeItemExtension</c> separate-table row at
/// <c>$._ext.sample</c>). Wires the abstract shape hooks of
/// <see cref="ProfileGuardedNoOpGeneratedDdlFixtureTestBase"/> through to
/// <see cref="PostgresqlProfileSeparateTableMergeSupport"/> with both the root and
/// the separate-table scope declared fully VisiblePresent on both the request and
/// stored sides — the guarded no-op invariant the fixtures in this file assert.
/// </summary>
internal abstract class SeparateTableShapeProfileGuardedNoOpFixtureBase
    : ProfileGuardedNoOpGeneratedDdlFixtureTestBase
{
    protected const int DefaultProfileSeparateTableMergeItemId = 9201;

    protected static readonly JsonNode IdenticalRequestBody = new JsonObject
    {
        ["profileSeparateTableMergeItemId"] = DefaultProfileSeparateTableMergeItemId,
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

    protected override string FixtureRelativePath =>
        PostgresqlProfileSeparateTableMergeSupport.FixtureRelativePath;

    protected override ServiceProvider CreateServiceProvider() => CreateDefaultServiceProvider();

    protected override async Task ExecuteProfiledShapeCreateAsync(DocumentUuid documentUuid)
    {
        var seedResult = await PostgresqlProfileSeparateTableMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            DefaultProfileSeparateTableMergeItemId,
            IdenticalRequestBody.DeepClone(),
            documentUuid,
            "pg-profile-guarded-no-op-separate-table-create"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    protected override Task<UpdateResult> ExecuteProfiledShapeIdenticalPutAsync(DocumentUuid documentUuid)
    {
        var writeBody = IdenticalRequestBody.DeepClone();
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
            extStoredHiddenMemberPaths: []
        );
        return PostgresqlProfileSeparateTableMergeSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            DefaultProfileSeparateTableMergeItemId,
            writeBody,
            documentUuid,
            profileContext,
            "pg-profile-guarded-no-op-separate-table-put"
        );
    }

    protected override Task<UpsertResult> ExecuteProfiledShapePostAsUpdateAsync(
        DocumentUuid incomingDocumentUuid
    )
    {
        var writeBody = IdenticalRequestBody.DeepClone();
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
            extStoredHiddenMemberPaths: []
        );
        return PostgresqlProfileSeparateTableMergeSupport.ExecuteProfiledPostAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            DefaultProfileSeparateTableMergeItemId,
            writeBody,
            incomingDocumentUuid,
            profileContext,
            "pg-profile-guarded-no-op-separate-table-post-as-update"
        );
    }

    protected override async Task<IReadOnlyDictionary<string, object?>> ReadShapeRootRowByDocumentIdAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                "DocumentId",
                "ProfileSeparateTableMergeItemId",
                "DisplayName"
            FROM "edfi"."ProfileSeparateTableMergeItem"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        if (rows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one ProfileSeparateTableMergeItem row for document id '{documentId}', but found {rows.Count}."
            );
        }

        return rows[0];
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
internal class Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Root_Only_Shape
    : RootOnlyShapeProfileGuardedNoOpFixtureBase
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000001")
    );

    private ProfileGuardedNoOpPersistedState _stateBeforeUpdate = null!;
    private ProfileGuardedNoOpPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    protected override async Task SetUpTestAsync()
    {
        await ExecuteProfiledShapeCreateAsync(DocumentUuid);
        _stateBeforeUpdate = await ProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            DocumentUuid.Value,
            ReadShapeRootRowByDocumentIdAsync
        );

        _updateResult = await ExecuteProfiledShapeIdenticalPutAsync(DocumentUuid);
        _stateAfterUpdate = await ProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            DocumentUuid.Value,
            ReadShapeRootRowByDocumentIdAsync
        );
    }

    [Test]
    public void It_returns_update_success_for_an_unchanged_profiled_put()
    {
        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(DocumentUuid);
    }

    [Test]
    public void It_does_not_change_rowsets()
    {
        _stateAfterUpdate.RootRow.Should().BeEquivalentTo(_stateBeforeUpdate.RootRow);
    }

    [Test]
    public void It_does_not_change_content_version()
    {
        _stateAfterUpdate.Document.ContentVersion.Should().Be(_stateBeforeUpdate.Document.ContentVersion);
    }

    [Test]
    public void It_does_not_change_content_last_modified_at()
    {
        _stateAfterUpdate
            .Document.ContentLastModifiedAt.Should()
            .Be(_stateBeforeUpdate.Document.ContentLastModifiedAt);
    }

    [Test]
    public void It_does_not_change_identity_version()
    {
        _stateAfterUpdate.Document.IdentityVersion.Should().Be(_stateBeforeUpdate.Document.IdentityVersion);
    }

    [Test]
    public void It_does_not_change_identity_last_modified_at()
    {
        _stateAfterUpdate
            .Document.IdentityLastModifiedAt.Should()
            .Be(_stateBeforeUpdate.Document.IdentityLastModifiedAt);
    }

    [Test]
    public void It_does_not_emit_a_document_change_event_row()
    {
        _stateAfterUpdate.DocumentChangeEventCount.Should().Be(_stateBeforeUpdate.DocumentChangeEventCount);
    }
}

/// <summary>
/// Slice 6 (DMS-1142) Task 6 — profiled POST-as-update guarded no-op. Seeds a
/// non-profiled CREATE for the synthetic <c>ProfileRootOnlyMergeItem</c> target,
/// then issues a profiled <c>POST</c> with the SAME natural-identity body but a
/// DIFFERENT incoming <see cref="DocumentUuid"/>. Per Slice 1's final-target
/// resolution, the executor must classify the second POST as POST-as-update by
/// matching the existing document's semantic identity rather than inserting a
/// new document. With the profile context declaring the root and inlined
/// <c>$.profileScope</c> fully VisiblePresent and no hidden members, the
/// merged effective rowset equals the stored rowset and the guarded no-op
/// short-circuit must fire — no row content / version / timestamp / change
/// event mutation is permitted, AND the incoming UUID must NOT be inserted.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
internal class Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Post_As_Update_With_Root_Only_Shape
    : RootOnlyShapeProfileGuardedNoOpFixtureBase
{
    private static readonly DocumentUuid ExistingDocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000002")
    );
    private static readonly DocumentUuid IncomingDocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000003")
    );

    private ProfileGuardedNoOpPersistedState _stateBeforePostAsUpdate = null!;
    private ProfileGuardedNoOpPersistedState _stateAfterPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private long _incomingDocumentUuidRowCount;

    protected override async Task SetUpTestAsync()
    {
        await ExecuteProfiledShapeCreateAsync(ExistingDocumentUuid);
        _stateBeforePostAsUpdate = await ProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingDocumentUuid.Value,
            ReadShapeRootRowByDocumentIdAsync
        );

        _postAsUpdateResult = await ExecuteProfiledShapePostAsUpdateAsync(IncomingDocumentUuid);

        _stateAfterPostAsUpdate = await ProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingDocumentUuid.Value,
            ReadShapeRootRowByDocumentIdAsync
        );
        _incomingDocumentUuidRowCount = await CountDocumentRowsByUuidAsync(IncomingDocumentUuid.Value);
    }

    [Test]
    public void It_returns_update_success_with_the_existing_document_uuid()
    {
        _postAsUpdateResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _postAsUpdateResult
            .As<UpsertResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(ExistingDocumentUuid);
    }

    [Test]
    public void It_does_not_insert_the_incoming_document_uuid()
    {
        _incomingDocumentUuidRowCount.Should().Be(0);
    }

    [Test]
    public void It_does_not_change_rowsets()
    {
        _stateAfterPostAsUpdate.RootRow.Should().BeEquivalentTo(_stateBeforePostAsUpdate.RootRow);
    }

    [Test]
    public void It_does_not_change_content_version()
    {
        _stateAfterPostAsUpdate
            .Document.ContentVersion.Should()
            .Be(_stateBeforePostAsUpdate.Document.ContentVersion);
    }

    [Test]
    public void It_does_not_change_content_last_modified_at()
    {
        _stateAfterPostAsUpdate
            .Document.ContentLastModifiedAt.Should()
            .Be(_stateBeforePostAsUpdate.Document.ContentLastModifiedAt);
    }

    [Test]
    public void It_does_not_change_identity_version()
    {
        _stateAfterPostAsUpdate
            .Document.IdentityVersion.Should()
            .Be(_stateBeforePostAsUpdate.Document.IdentityVersion);
    }

    [Test]
    public void It_does_not_change_identity_last_modified_at()
    {
        _stateAfterPostAsUpdate
            .Document.IdentityLastModifiedAt.Should()
            .Be(_stateBeforePostAsUpdate.Document.IdentityLastModifiedAt);
    }

    [Test]
    public void It_does_not_emit_a_document_change_event_row()
    {
        _stateAfterPostAsUpdate
            .DocumentChangeEventCount.Should()
            .Be(_stateBeforePostAsUpdate.DocumentChangeEventCount);
    }
}

/// <summary>
/// Slice 6 (DMS-1142) Task 7 — profiled separate-table PUT guarded no-op. Seeds a
/// non-profiled CREATE for the synthetic <c>ProfileSeparateTableMergeItem</c> target
/// (root row plus a populated <c>sample.ProfileSeparateTableMergeItemExtension</c>
/// separate-table row at <c>$._ext.sample</c>), then issues a profiled PUT carrying
/// a byte-identical body. The profile context declares both the root and the
/// separate-table <c>$._ext.sample</c> scope fully VisiblePresent (and creatable)
/// on both the request and stored sides with no hidden member paths, so the
/// merged effective rowset across both tables equals the stored rowset and the
/// guarded no-op short-circuit must fire — neither root nor extension row content,
/// nor Document version/timestamp metadata, nor a DocumentChangeEvent row may be
/// written.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
internal class Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Separate_Table_Shape
    : SeparateTableShapeProfileGuardedNoOpFixtureBase
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000004")
    );

    private ProfileGuardedNoOpPersistedState _stateBeforeUpdate = null!;
    private ProfileGuardedNoOpPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;
    private int _extRowCountBefore;
    private int _extRowCountAfter;
    private IReadOnlyDictionary<string, object?> _extRowBefore = null!;
    private IReadOnlyDictionary<string, object?> _extRowAfter = null!;

    protected override async Task SetUpTestAsync()
    {
        await ExecuteProfiledShapeCreateAsync(DocumentUuid);
        _stateBeforeUpdate = await ProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            DocumentUuid.Value,
            ReadShapeRootRowByDocumentIdAsync
        );
        _extRowCountBefore = await PostgresqlProfileSeparateTableMergeSupport.CountExtRowsAsync(
            _database,
            DocumentUuid
        );
        _extRowBefore = await ReadExtRowAsync(DocumentUuid);

        _updateResult = await ExecuteProfiledShapeIdenticalPutAsync(DocumentUuid);

        _stateAfterUpdate = await ProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            DocumentUuid.Value,
            ReadShapeRootRowByDocumentIdAsync
        );
        _extRowCountAfter = await PostgresqlProfileSeparateTableMergeSupport.CountExtRowsAsync(
            _database,
            DocumentUuid
        );
        _extRowAfter = await ReadExtRowAsync(DocumentUuid);
    }

    [Test]
    public void It_returns_update_success()
    {
        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(DocumentUuid);
    }

    [Test]
    public void It_does_not_change_root_row()
    {
        _stateAfterUpdate.RootRow.Should().BeEquivalentTo(_stateBeforeUpdate.RootRow);
    }

    [Test]
    public void It_does_not_change_ext_row_count()
    {
        _extRowCountAfter.Should().Be(_extRowCountBefore);
    }

    [Test]
    public void It_does_not_change_ext_row_contents()
    {
        _extRowAfter.Should().BeEquivalentTo(_extRowBefore);
    }

    [Test]
    public void It_does_not_change_content_version()
    {
        _stateAfterUpdate.Document.ContentVersion.Should().Be(_stateBeforeUpdate.Document.ContentVersion);
    }

    [Test]
    public void It_does_not_change_content_last_modified_at()
    {
        _stateAfterUpdate
            .Document.ContentLastModifiedAt.Should()
            .Be(_stateBeforeUpdate.Document.ContentLastModifiedAt);
    }

    [Test]
    public void It_does_not_change_identity_version()
    {
        _stateAfterUpdate.Document.IdentityVersion.Should().Be(_stateBeforeUpdate.Document.IdentityVersion);
    }

    [Test]
    public void It_does_not_change_identity_last_modified_at()
    {
        _stateAfterUpdate
            .Document.IdentityLastModifiedAt.Should()
            .Be(_stateBeforeUpdate.Document.IdentityLastModifiedAt);
    }

    [Test]
    public void It_does_not_emit_a_document_change_event_row()
    {
        _stateAfterUpdate.DocumentChangeEventCount.Should().Be(_stateBeforeUpdate.DocumentChangeEventCount);
    }

    /// <summary>
    /// Reads the single <c>sample.ProfileSeparateTableMergeItemExtension</c> row for the
    /// supplied <paramref name="documentUuid"/>. Wraps
    /// <see cref="PostgresqlProfileSeparateTableMergeSupport.TryReadExtRowAsync"/> with
    /// a non-null assertion so the no-op invariants can compare the row contents
    /// directly.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, object?>> ReadExtRowAsync(DocumentUuid documentUuid)
    {
        var row = await PostgresqlProfileSeparateTableMergeSupport.TryReadExtRowAsync(
            _database,
            documentUuid
        );
        row.Should().NotBeNull("the seeded ProfileSeparateTableMergeItem must have an extension row");
        return row!;
    }
}
