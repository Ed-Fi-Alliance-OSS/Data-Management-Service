// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Slice 6 (DMS-1142) Task 10 — MSSQL parity coverage for profile guarded no-op.
// Mirrors the root-only-shape happy-path and stale-compare fixtures from the
// pgsql sibling (PostgresqlProfileGuardedNoOpTests.cs) onto SQL Server. The
// dialect-sensitive bit being exercised here is the freshness checker's mssql
// branch in RelationalWriteGuardedNoOp, which uses
// WITH (UPDLOCK, HOLDLOCK, ROWLOCK) instead of pgsql's FOR UPDATE; the
// stale-compare fixtures drive that lock semantics implicitly through the
// production RelationalWriteFreshnessChecker.
//
// The mssql DocumentChangeEvent trigger (TR_Document_Journal in the generated
// DDL) fires on UPDATE([ContentVersion]) and inserts one DocumentChangeEvent
// row, exactly mirroring the pgsql trigger. The stale-compare deep-equivalence
// pattern from the pgsql fixtures therefore carries over verbatim:
// substitute ContentVersion AND DocumentChangeEventCount back to the
// before-state, then assert deep equivalence.
//
// Slice 6 mssql parity is intentionally root-only (4 fixtures); separate-table
// and top-level collection mssql parity coverage is Slice 7 hardening if
// scoped.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

// Mssql parity: no IHostApplicationLifetime registration is needed because the
// mssql DI graph does not depend on it (unlike pgsql's NpgsqlDataSourceCache).
// The mssql connection lifecycle is owned by MssqlGeneratedDdlTestDatabase via
// raw SqlConnection; the test fixtures hand the connection string to the
// reference resolver through IDmsInstanceSelection.

file sealed class MssqlProfileGuardedNoOpAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlProfileGuardedNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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
/// Stale-compare freshness checker for the mssql profiled guarded no-op suite. The
/// first invocation bumps <c>ContentVersion</c> on the target document before
/// delegating to the production checker, simulating a concurrent writer landing
/// between the candidate detection and freshness recheck. Reused by the stale
/// PUT and POST-as-update fixtures.
/// </summary>
file sealed class MssqlProfileGuardedNoOpConcurrentContentVersionBumpFreshnessChecker(
    MssqlGeneratedDdlTestDatabase database
) : IRelationalWriteFreshnessChecker
{
    private readonly MssqlGeneratedDdlTestDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

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

            await BumpContentVersionAsync(targetContext.DocumentId, cancellationToken).ConfigureAwait(false);
        }

        return await _innerChecker
            .IsCurrentAsync(request, targetContext, writeSession, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task BumpContentVersionAsync(long documentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Document]
            SET [ContentVersion] = [ContentVersion] + 1
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        if (rowsAffected != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one document content-version bump for document id '{documentId}', but affected {rowsAffected} rows."
            );
        }
    }
}

internal sealed record MssqlProfileGuardedNoOpDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion,
    DateTime ContentLastModifiedAt,
    long IdentityVersion,
    DateTime IdentityLastModifiedAt
);

internal sealed record MssqlProfileGuardedNoOpPersistedState(
    MssqlProfileGuardedNoOpDocumentRow Document,
    IReadOnlyDictionary<string, object?> RootRow,
    long DocumentChangeEventCount
);

file static class MssqlProfileGuardedNoOpIntegrationTestSupport
{
    public static async Task<MssqlProfileGuardedNoOpPersistedState> ReadPersistedStateAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        Func<
            MssqlGeneratedDdlTestDatabase,
            long,
            Task<IReadOnlyDictionary<string, object?>>
        > readRootRowByDocumentId
    )
    {
        var documentRows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [DocumentUuid], [ResourceKeyId],
                   [ContentVersion], [ContentLastModifiedAt],
                   [IdentityVersion], [IdentityLastModifiedAt]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );

        if (documentRows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {documentRows.Count}."
            );
        }

        var documentRow = new MssqlProfileGuardedNoOpDocumentRow(
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
            SELECT COUNT_BIG(*) AS [RowCount]
            FROM [dms].[DocumentChangeEvent]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentRow.DocumentId)
        );
        var changeEventCount = Convert.ToInt64(changeEventRows[0]["RowCount"], CultureInfo.InvariantCulture);

        return new MssqlProfileGuardedNoOpPersistedState(documentRow, rootRow, changeEventCount);
    }
}

/// <summary>
/// Shared base for the mssql profiled guarded no-op fixtures in this file. Owns the
/// database provisioning lifecycle and the per-test service-provider lifecycle, but
/// delegates the shape-specific scaffolding (DDL fixture path, DI service provider,
/// seed CREATE, identical PUT, identical POST-as-update, and root-row reader) to a
/// per-shape intermediate base class. Mirrors the pgsql sibling
/// <c>ProfileGuardedNoOpGeneratedDdlFixtureTestBase</c>.
/// </summary>
internal abstract class MssqlProfileGuardedNoOpGeneratedDdlFixtureTestBase
{
    protected MssqlGeneratedDdlFixture _fixture = null!;
    protected MappingSet _mappingSet = null!;
    protected MssqlGeneratedDdlTestDatabase _database = null!;
    protected ServiceProvider _serviceProvider = null!;

    /// <summary>
    /// Repository-relative path to the generated-DDL fixture for this shape.
    /// </summary>
    protected abstract string FixtureRelativePath { get; }

    /// <summary>
    /// Builds the per-test service provider for this shape. The shape's intermediate
    /// base owns the registrations that match its support class's DI surface.
    /// </summary>
    protected abstract ServiceProvider CreateServiceProvider();

    /// <summary>
    /// Issues a non-profiled CREATE for the shape's synthetic target resource. The
    /// CREATE intentionally omits a profile context so the stored document is in a
    /// known canonical shape; the subsequent profiled write carries the profile
    /// context that activates the guarded no-op path.
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
    /// identical body and a DIFFERENT incoming <see cref="DocumentUuid"/>.
    /// </summary>
    protected abstract Task<UpsertResult> ExecuteProfiledShapePostAsUpdateAsync(
        DocumentUuid incomingDocumentUuid
    );

    /// <summary>
    /// Reads the single root-table row for this shape keyed by the supplied
    /// <paramref name="documentId"/>.
    /// </summary>
    protected abstract Task<IReadOnlyDictionary<string, object?>> ReadShapeRootRowByDocumentIdAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    );

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
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
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
    /// Builds the standard service provider used by the profiled guarded no-op
    /// suite. Registers the local profiled DI handler stubs so this file owns its
    /// DI surface.
    /// </summary>
    protected static ServiceProvider CreateDefaultServiceProvider(
        Action<IServiceCollection>? configureServices = null
    )
    {
        ServiceCollection services = [];
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlReferenceResolver();
        configureServices?.Invoke(services);

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    /// <summary>
    /// Service provider variant that swaps in
    /// <see cref="MssqlProfileGuardedNoOpConcurrentContentVersionBumpFreshnessChecker"/>
    /// for the production freshness checker so the first revalidation observes a
    /// stale <c>ContentVersion</c>. Used by the stale-compare fixtures.
    /// </summary>
    protected ServiceProvider CreateStaleCompareServiceProvider() =>
        CreateDefaultServiceProvider(services =>
        {
            services.RemoveAll<IRelationalWriteFreshnessChecker>();
            services.AddScoped<IRelationalWriteFreshnessChecker>(
                _ => new MssqlProfileGuardedNoOpConcurrentContentVersionBumpFreshnessChecker(_database)
            );
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
            SELECT COUNT_BIG(*) AS [RowCount]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );
        return Convert.ToInt64(rows[0]["RowCount"], CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Intermediate base for fixtures whose target is the synthetic
/// <c>ProfileRootOnlyMergeItem</c> resource. Wires the abstract shape hooks of
/// <see cref="MssqlProfileGuardedNoOpGeneratedDdlFixtureTestBase"/> through to
/// <see cref="MssqlProfileRootOnlyFixtureSupport"/> with the fully-VisiblePresent
/// profile context the guarded-no-op invariants require. Inlines the profiled
/// PUT / POST executors because the shared support class does not expose them.
/// </summary>
internal abstract class MssqlRootOnlyShapeProfileGuardedNoOpFixtureBase
    : MssqlProfileGuardedNoOpGeneratedDdlFixtureTestBase
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

    protected override string FixtureRelativePath => MssqlProfileRootOnlyFixtureSupport.FixtureRelativePath;

    protected override ServiceProvider CreateServiceProvider() => CreateDefaultServiceProvider();

    protected override async Task ExecuteProfiledShapeCreateAsync(DocumentUuid documentUuid)
    {
        var seedResult = await MssqlProfileRootOnlyFixtureSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            DefaultProfileRootOnlyMergeItemId,
            IdenticalRequestBody.DeepClone(),
            documentUuid,
            "mssql-profile-guarded-no-op-create"
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
                    InstanceName: "MssqlProfileGuardedNoOp",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writeBody = IdenticalRequestBody.DeepClone();
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = MssqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: []
        );
        var updateRequest = new UpdateRequest(
            ResourceInfo: MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: MssqlProfileRootOnlyFixtureSupport.CreateDocumentInfo(
                DefaultProfileRootOnlyMergeItemId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-guarded-no-op-put-update"),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileGuardedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileGuardedNoOpAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "MssqlProfileGuardedNoOp",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var writeBody = IdenticalRequestBody.DeepClone();
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = MssqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: []
        );
        var upsertRequest = new UpsertRequest(
            ResourceInfo: MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: MssqlProfileRootOnlyFixtureSupport.CreateDocumentInfo(
                DefaultProfileRootOnlyMergeItemId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-guarded-no-op-post-as-update"),
            DocumentUuid: incomingDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileGuardedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileGuardedNoOpAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    protected override Task<IReadOnlyDictionary<string, object?>> ReadShapeRootRowByDocumentIdAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    ) => ReadRootOnlyShapeRootRowByDocumentIdAsync(database, documentId);

    /// <summary>
    /// Reads the single <c>edfi.ProfileRootOnlyMergeItem</c> row keyed by the supplied
    /// <paramref name="documentId"/>. Returns the root rowset that the no-op invariants
    /// compare before and after the profiled write.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, object?>> ReadRootOnlyShapeRootRowByDocumentIdAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                [DocumentId],
                [ProfileRootOnlyMergeItemId],
                [DisplayName],
                [ProfileScopeClearableText],
                [ProfileScopePreservedText],
                [StudentReference_DocumentId],
                [StudentReference_StudentUniqueId],
                [PrimarySchoolTypeDescriptor_DescriptorId_Present],
                [SecondarySchoolTypeDescriptor_DescriptorId_Present],
                [PrimarySchoolTypeDescriptor_Unified_DescriptorId]
            FROM [edfi].[ProfileRootOnlyMergeItem]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
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
/// Slice 6 (DMS-1142) Task 10 — mssql parity for profiled root-only PUT guarded
/// no-op. Mirrors the pgsql happy-path PUT fixture verbatim with mssql
/// connectivity. Asserts that an unchanged profiled PUT yields
/// <see cref="UpdateResult.UpdateSuccess"/> and persists no DML-visible state
/// changes — neither root row contents, nor Document version/timestamp metadata,
/// nor a DocumentChangeEvent audit log row.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_A_Mssql_Relational_Profile_Guarded_No_Op_Put_With_Root_Only_Shape
    : MssqlRootOnlyShapeProfileGuardedNoOpFixtureBase
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("ffffffff-0000-0000-0000-000000000001")
    );

    private MssqlProfileGuardedNoOpPersistedState _stateBeforeUpdate = null!;
    private MssqlProfileGuardedNoOpPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    protected override async Task SetUpTestAsync()
    {
        await ExecuteProfiledShapeCreateAsync(DocumentUuid);
        _stateBeforeUpdate = await MssqlProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            DocumentUuid.Value,
            ReadShapeRootRowByDocumentIdAsync
        );

        _updateResult = await ExecuteProfiledShapeIdenticalPutAsync(DocumentUuid);
        _stateAfterUpdate = await MssqlProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
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
/// Slice 6 (DMS-1142) Task 10 — mssql parity for profiled root-only POST-as-update
/// guarded no-op. Seeds a non-profiled CREATE for the synthetic
/// <c>ProfileRootOnlyMergeItem</c> target, then issues a profiled <c>POST</c>
/// with the SAME natural-identity body but a DIFFERENT incoming
/// <see cref="DocumentUuid"/>. The executor must classify the second POST as
/// POST-as-update by matching the existing document's semantic identity rather
/// than inserting a new document; with the profile context fully VisiblePresent
/// the merged effective rowset equals the stored rowset and the guarded no-op
/// short-circuit must fire — no row content / version / timestamp / change
/// event mutation is permitted, AND the incoming UUID must NOT be inserted.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_A_Mssql_Relational_Profile_Guarded_No_Op_Post_As_Update_With_Root_Only_Shape
    : MssqlRootOnlyShapeProfileGuardedNoOpFixtureBase
{
    private static readonly DocumentUuid ExistingDocumentUuid = new(
        Guid.Parse("ffffffff-0000-0000-0000-000000000002")
    );
    private static readonly DocumentUuid IncomingDocumentUuid = new(
        Guid.Parse("ffffffff-0000-0000-0000-000000000003")
    );

    private MssqlProfileGuardedNoOpPersistedState _stateBeforePostAsUpdate = null!;
    private MssqlProfileGuardedNoOpPersistedState _stateAfterPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private long _incomingDocumentUuidRowCount;

    protected override async Task SetUpTestAsync()
    {
        await ExecuteProfiledShapeCreateAsync(ExistingDocumentUuid);
        _stateBeforePostAsUpdate =
            await MssqlProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
                _database,
                ExistingDocumentUuid.Value,
                ReadShapeRootRowByDocumentIdAsync
            );

        _postAsUpdateResult = await ExecuteProfiledShapePostAsUpdateAsync(IncomingDocumentUuid);

        _stateAfterPostAsUpdate = await MssqlProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
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
/// Slice 6 (DMS-1142) Task 10 — mssql parity for profiled stale-compare retry on
/// PUT. Mirrors the pgsql sibling stale-compare PUT fixture; the freshness
/// checker bumps the stored <c>ContentVersion</c> on its first invocation only,
/// causing the executor's first attempt to observe a stale row and emit
/// <see cref="RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare"/>. The
/// repository's two-attempt loop swallows that and retries within the same
/// scope; the second attempt observes the bumped row, succeeds, and returns
/// <see cref="UpdateResult.UpdateSuccess"/>. This fixture pins the dialect-
/// sensitive locking semantics on mssql — the production
/// <see cref="RelationalWriteFreshnessChecker"/> issues
/// <c>WITH (UPDLOCK, HOLDLOCK, ROWLOCK)</c> on its compare query, exercising
/// the lock implicitly through the stale-then-bumped flow.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_A_Mssql_Relational_Profile_Stale_Guarded_No_Op_Put
    : MssqlRootOnlyShapeProfileGuardedNoOpFixtureBase
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("ffffffff-0000-0000-0000-000000000010")
    );

    private MssqlProfileGuardedNoOpPersistedState _stateBeforeUpdate = null!;
    private MssqlProfileGuardedNoOpPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    protected override ServiceProvider CreateServiceProvider() => CreateStaleCompareServiceProvider();

    protected override async Task SetUpTestAsync()
    {
        await ExecuteProfiledShapeCreateAsync(DocumentUuid);
        _stateBeforeUpdate = await MssqlProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            DocumentUuid.Value,
            ReadShapeRootRowByDocumentIdAsync
        );

        _updateResult = await ExecuteProfiledShapeIdenticalPutAsync(DocumentUuid);

        _stateAfterUpdate = await MssqlProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            DocumentUuid.Value,
            ReadShapeRootRowByDocumentIdAsync
        );
    }

    [Test]
    public void It_retries_and_returns_update_success_after_the_profiled_no_op_compare_goes_stale()
    {
        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(DocumentUuid);
    }

    [Test]
    public void It_preserves_the_persisted_state_aside_from_the_concurrent_content_version_bump()
    {
        // The freshness-checker bumper raises the stored Document.ContentVersion by one,
        // which transitively fires the dms.TR_Document_Journal trigger (mssql
        // emitter generates an AFTER UPDATE trigger that checks UPDATE([ContentVersion]))
        // and inserts a single DocumentChangeEvent row. Both moves are caused by the
        // simulated concurrent writer — not by the executor's stale-retry no-op success
        // path, which neither persists rowset content nor mutates Document metadata. We
        // substitute the bumper's side-effects back to the before-state so the
        // deep-equivalence assertion proves the no-op retry preserves every other
        // field (ContentLastModifiedAt, IdentityVersion, IdentityLastModifiedAt,
        // RootRow, ResourceKeyId, DocumentUuid, DocumentId).
        var adjustedAfterState = _stateAfterUpdate with
        {
            Document = _stateAfterUpdate.Document with
            {
                ContentVersion = _stateBeforeUpdate.Document.ContentVersion,
            },
            DocumentChangeEventCount = _stateBeforeUpdate.DocumentChangeEventCount,
        };

        adjustedAfterState.Should().BeEquivalentTo(_stateBeforeUpdate);
    }

    [Test]
    public void It_bumps_the_content_version_by_exactly_one()
    {
        _stateAfterUpdate.Document.ContentVersion.Should().Be(_stateBeforeUpdate.Document.ContentVersion + 1);
    }

    [Test]
    public void It_emits_only_the_concurrent_writer_journal_row_and_no_executor_journal_row()
    {
        // The Document.ContentVersion bump from the freshness-checker simulated
        // concurrent writer fires TR_Document_Journal exactly once. The executor's
        // stale-retry no-op success branch must NOT persist any additional row,
        // so the post-write journal row count is exactly before + 1.
        _stateAfterUpdate
            .DocumentChangeEventCount.Should()
            .Be(_stateBeforeUpdate.DocumentChangeEventCount + 1);
    }
}

/// <summary>
/// Slice 6 (DMS-1142) Task 10 — mssql parity for profiled stale-compare retry on
/// POST-as-update. Mirrors the pgsql sibling stale-compare POST-as-update fixture;
/// seeds a CREATE then issues a profiled POST with a DIFFERENT incoming
/// <see cref="DocumentUuid"/> against a byte-identical body. The single-shot
/// bumping freshness checker fires once, the executor retries, and the no-op
/// succeeds against the bumped stored rowset — yielding
/// <see cref="UpsertResult.UpdateSuccess"/> with the EXISTING document's UUID
/// (the incoming UUID must NOT be inserted).
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_A_Mssql_Relational_Profile_Stale_Guarded_No_Op_Post_As_Update
    : MssqlRootOnlyShapeProfileGuardedNoOpFixtureBase
{
    private static readonly DocumentUuid ExistingDocumentUuid = new(
        Guid.Parse("ffffffff-0000-0000-0000-000000000011")
    );
    private static readonly DocumentUuid IncomingDocumentUuid = new(
        Guid.Parse("ffffffff-0000-0000-0000-000000000012")
    );

    private MssqlProfileGuardedNoOpPersistedState _stateBeforePostAsUpdate = null!;
    private MssqlProfileGuardedNoOpPersistedState _stateAfterPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private long _incomingDocumentUuidRowCount;

    protected override ServiceProvider CreateServiceProvider() => CreateStaleCompareServiceProvider();

    protected override async Task SetUpTestAsync()
    {
        await ExecuteProfiledShapeCreateAsync(ExistingDocumentUuid);
        _stateBeforePostAsUpdate =
            await MssqlProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
                _database,
                ExistingDocumentUuid.Value,
                ReadShapeRootRowByDocumentIdAsync
            );

        _postAsUpdateResult = await ExecuteProfiledShapePostAsUpdateAsync(IncomingDocumentUuid);

        _stateAfterPostAsUpdate = await MssqlProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingDocumentUuid.Value,
            ReadShapeRootRowByDocumentIdAsync
        );
        _incomingDocumentUuidRowCount = await CountDocumentRowsByUuidAsync(IncomingDocumentUuid.Value);
    }

    [Test]
    public void It_retries_and_returns_update_success_after_the_profiled_no_op_compare_goes_stale()
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
    public void It_preserves_the_persisted_state_aside_from_the_concurrent_content_version_bump()
    {
        // See the PUT stale fixture sibling for the substitution rationale; the mssql
        // TR_Document_Journal trigger fires identically on UPDATE([ContentVersion]).
        var adjustedAfterState = _stateAfterPostAsUpdate with
        {
            Document = _stateAfterPostAsUpdate.Document with
            {
                ContentVersion = _stateBeforePostAsUpdate.Document.ContentVersion,
            },
            DocumentChangeEventCount = _stateBeforePostAsUpdate.DocumentChangeEventCount,
        };

        adjustedAfterState.Should().BeEquivalentTo(_stateBeforePostAsUpdate);
    }

    [Test]
    public void It_bumps_the_content_version_by_exactly_one()
    {
        _stateAfterPostAsUpdate
            .Document.ContentVersion.Should()
            .Be(_stateBeforePostAsUpdate.Document.ContentVersion + 1);
    }

    [Test]
    public void It_emits_only_the_concurrent_writer_journal_row_and_no_executor_journal_row()
    {
        // See the PUT stale fixture sibling.
        _stateAfterPostAsUpdate
            .DocumentChangeEventCount.Should()
            .Be(_stateBeforePostAsUpdate.DocumentChangeEventCount + 1);
    }
}
