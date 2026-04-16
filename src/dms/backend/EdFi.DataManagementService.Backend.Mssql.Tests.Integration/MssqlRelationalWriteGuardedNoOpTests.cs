// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

file sealed class MssqlGuardedNoOpAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlGuardedNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

internal sealed class MssqlGuardedNoOpCommitWindowProbe
{
    public int IsCurrentCallCount { get; private set; }

    public List<bool> Results { get; } = [];

    public void Record(bool result)
    {
        IsCurrentCallCount++;
        Results.Add(result);
    }
}

internal sealed class MssqlGuardedNoOpCommitWindowCoordinator(IDmsInstanceSelection dmsInstanceSelection)
    : IAsyncDisposable
{
    private readonly IDmsInstanceSelection _dmsInstanceSelection =
        dmsInstanceSelection ?? throw new ArgumentNullException(nameof(dmsInstanceSelection));

    private readonly TaskCompletionSource _writePending = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private readonly TaskCompletionSource _allowCommit = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private readonly TaskCompletionSource _committed = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private SqlConnection? _connection;
    private SqlTransaction? _transaction;
    private bool _commitCompleted;

    public int CommitCallCount { get; private set; }

    public async Task BeginPendingContentVersionBumpAsync(
        long documentId,
        CancellationToken cancellationToken = default
    )
    {
        _connection = new SqlConnection(_dmsInstanceSelection.GetSelectedDmsInstance().ConnectionString);
        await _connection.OpenAsync(cancellationToken);
        _transaction = (SqlTransaction)
            await _connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await using SqlCommand command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            UPDATE [dms].[Document]
            SET [ContentVersion] = [ContentVersion] + 1
            WHERE [DocumentId] = @documentId;
            """;
        command.Parameters.Add(new SqlParameter("@documentId", documentId));

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

        if (rowsAffected != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one pending document content-version bump for document id '{documentId}', but affected {rowsAffected} rows."
            );
        }

        _writePending.TrySetResult();
        await _allowCommit.Task.WaitAsync(cancellationToken);
        await _transaction.CommitAsync(cancellationToken);
        _commitCompleted = true;
        CommitCallCount++;
        _committed.TrySetResult();
    }

    public Task WaitUntilWriteIsPendingAsync(CancellationToken cancellationToken = default) =>
        _writePending.Task.WaitAsync(cancellationToken);

    public void ReleaseCommit() => _allowCommit.TrySetResult();

    public Task WaitUntilCommittedAsync(CancellationToken cancellationToken = default) =>
        _committed.Task.WaitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        ReleaseCommit();

        if (_transaction is not null && !_commitCompleted)
        {
            try
            {
                await _transaction.RollbackAsync();
            }
            catch
            {
                // Best-effort cleanup for test-owned pending transactions.
            }
        }

        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}

internal sealed class MssqlGuardedNoOpCommitWindowFreshnessChecker(
    MssqlGuardedNoOpCommitWindowCoordinator coordinator,
    MssqlGuardedNoOpCommitWindowProbe probe
) : IRelationalWriteFreshnessChecker
{
    private readonly MssqlGuardedNoOpCommitWindowCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    private readonly MssqlGuardedNoOpCommitWindowProbe _probe =
        probe ?? throw new ArgumentNullException(nameof(probe));

    private readonly RelationalWriteFreshnessChecker _innerChecker = new();

    public async Task<bool> IsCurrentAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        if (_probe.IsCurrentCallCount == 0)
        {
            var isCurrentTask = _innerChecker.IsCurrentAsync(
                request,
                targetContext,
                writeSession,
                cancellationToken
            );

            _coordinator.ReleaseCommit();

            var isCurrent = await isCurrentTask;
            _probe.Record(isCurrent);
            await _coordinator.WaitUntilCommittedAsync(cancellationToken);

            return isCurrent;
        }

        var retryResult = await _innerChecker.IsCurrentAsync(
            request,
            targetContext,
            writeSession,
            cancellationToken
        );

        _probe.Record(retryResult);
        return retryResult;
    }
}

internal sealed record MssqlGuardedNoOpDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record MssqlGuardedNoOpSchoolRow(long DocumentId, long SchoolId, string? ShortName);

internal sealed record MssqlGuardedNoOpSchoolAddressRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string City
);

internal sealed record MssqlGuardedNoOpSchoolExtensionAddressRow(
    long BaseCollectionItemId,
    long SchoolDocumentId,
    string Zone
);

internal sealed record MssqlGuardedNoOpPersistedState(
    MssqlGuardedNoOpDocumentRow Document,
    MssqlGuardedNoOpSchoolRow School,
    IReadOnlyList<MssqlGuardedNoOpSchoolAddressRow> Addresses,
    IReadOnlyList<MssqlGuardedNoOpSchoolExtensionAddressRow> ExtensionAddresses,
    long DocumentCount
);

internal sealed record MssqlGuardedNoOpReferentialIdentityRow(
    Guid ReferentialId,
    long DocumentId,
    short ResourceKeyId
);

file static class MssqlGuardedNoOpIntegrationTestSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    public const string RequestBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            {
              "city": "Austin"
            },
            {
              "city": "Dallas"
            }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-1"
                    }
                  }
                },
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-2"
                    }
                  }
                }
              ]
            }
          }
        }
        """;

    public static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    public static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    public static ServiceProvider CreateServiceProvider(Action<IServiceCollection>? configureServices = null)
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

    public static ServiceProvider CreateCommitWindowServiceProvider() =>
        CreateServiceProvider(static services =>
        {
            services.AddSingleton<MssqlGuardedNoOpCommitWindowProbe>();
            services.AddScoped<MssqlGuardedNoOpCommitWindowCoordinator>();
            services.RemoveAll<IRelationalWriteFreshnessChecker>();
            services.AddScoped<
                IRelationalWriteFreshnessChecker,
                MssqlGuardedNoOpCommitWindowFreshnessChecker
            >();
        });

    public static UpsertRequest CreateCreateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(RequestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlGuardedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlGuardedNoOpAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

    public static UpdateRequest CreateUpdateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(RequestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlGuardedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlGuardedNoOpAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

    public static UpsertRequest CreatePostAsUpdateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId,
        ReferentialId referentialId
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(referentialId),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(RequestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlGuardedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlGuardedNoOpAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

    public static async Task<MssqlGuardedNoOpPersistedState> ReadPersistedStateAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(database, documentUuid);
        var school = await ReadSchoolAsync(database, document.DocumentId);
        var addresses = await ReadSchoolAddressesAsync(database, document.DocumentId);
        var extensionAddresses = await ReadSchoolExtensionAddressesAsync(database, document.DocumentId);
        var documentCount = await ReadDocumentCountAsync(database);

        return new MssqlGuardedNoOpPersistedState(
            document,
            school,
            addresses,
            extensionAddresses,
            documentCount
        );
    }

    public static async Task<MssqlGuardedNoOpReferentialIdentityRow> ReadReferentialIdentityRowAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId,
        short resourceKeyId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [ReferentialId], [DocumentId], [ResourceKeyId]
            FROM [dms].[ReferentialIdentity]
            WHERE [DocumentId] = @documentId
                AND [ResourceKeyId] = @resourceKeyId;
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );

        return rows.Count == 1
            ? new MssqlGuardedNoOpReferentialIdentityRow(
                GetGuid(rows[0], "ReferentialId"),
                GetInt64(rows[0], "DocumentId"),
                GetInt16(rows[0], "ResourceKeyId")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one referential identity row for document id '{documentId}' and resource key '{resourceKeyId}', but found {rows.Count}."
            );
    }

    public static async Task<long> ReadDocumentCountAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS [Count]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }

    public static short GetInt16(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt16(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static int GetInt32(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt32(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static long GetInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static Guid GetGuid(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) is Guid value
            ? value
            : throw new InvalidOperationException($"Expected column '{columnName}' to contain a Guid value.");

    public static string GetString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) as string
        ?? throw new InvalidOperationException($"Expected column '{columnName}' to contain a string value.");

    public static string? GetNullableString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        row.TryGetValue(columnName, out var value)
            ? value as string
            : throw new InvalidOperationException(
                $"Expected persisted row to contain column '{columnName}'."
            );

    private static DocumentInfo CreateSchoolDocumentInfo(ReferentialId? referentialId = null)
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        return new DocumentInfo(
            DocumentIdentity: schoolIdentity,
            ReferentialId: referentialId
                ?? ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    private static async Task<MssqlGuardedNoOpDocumentRow> ReadDocumentAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [DocumentUuid], [ResourceKeyId], [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new MssqlGuardedNoOpDocumentRow(
                GetInt64(rows[0], "DocumentId"),
                GetGuid(rows[0], "DocumentUuid"),
                GetInt16(rows[0], "ResourceKeyId"),
                GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private static async Task<MssqlGuardedNoOpSchoolRow> ReadSchoolAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [SchoolId], [ShortName]
            FROM [edfi].[School]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Count == 1
            ? new MssqlGuardedNoOpSchoolRow(
                GetInt64(rows[0], "DocumentId"),
                GetInt64(rows[0], "SchoolId"),
                GetNullableString(rows[0], "ShortName")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private static async Task<IReadOnlyList<MssqlGuardedNoOpSchoolAddressRow>> ReadSchoolAddressesAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [CollectionItemId], [School_DocumentId], [Ordinal], [City]
            FROM [edfi].[SchoolAddress]
            WHERE [School_DocumentId] = @documentId
            ORDER BY [Ordinal], [CollectionItemId];
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Select(row => new MssqlGuardedNoOpSchoolAddressRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "City")
            ))
            .ToArray();
    }

    private static async Task<
        IReadOnlyList<MssqlGuardedNoOpSchoolExtensionAddressRow>
    > ReadSchoolExtensionAddressesAsync(MssqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [BaseCollectionItemId], [School_DocumentId], [Zone]
            FROM [sample].[SchoolExtensionAddress]
            WHERE [School_DocumentId] = @documentId
            ORDER BY [BaseCollectionItemId];
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Select(row => new MssqlGuardedNoOpSchoolExtensionAddressRow(
                GetInt64(row, "BaseCollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetString(row, "Zone")
            ))
            .ToArray();
    }

    private static async Task<long> ReadDocumentCountAsync(MssqlGeneratedDdlTestDatabase database)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS [Count]
            FROM [dms].[Document];
            """
        );

        return rows.Count == 1
            ? GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }

    private static object GetRequiredValue(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        if (!row.TryGetValue(columnName, out var value) || value is null)
        {
            throw new InvalidOperationException(
                $"Expected persisted row to contain non-null column '{columnName}'."
            );
        }

        return value;
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_A_Mssql_Relational_Guarded_No_Op_Put_With_A_Commit_Window_Race
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000007")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private MssqlGuardedNoOpCommitWindowProbe _freshnessProbe = null!;
    private MssqlGuardedNoOpPersistedState _stateBeforeUpdate = null!;
    private MssqlGuardedNoOpPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a configured connection string.");
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlGuardedNoOpIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlGuardedNoOpIntegrationTestSupport.CreateCommitWindowServiceProvider();

        _freshnessProbe = _serviceProvider.GetRequiredService<MssqlGuardedNoOpCommitWindowProbe>();

        await ExecuteCreateAsync();
        _stateBeforeUpdate = await MssqlGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );

        _updateResult = await ExecuteUpdateAsync(_stateBeforeUpdate.Document.DocumentId);
        _stateAfterUpdate = await MssqlGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );
    }

    [TearDown]
    public async Task TearDown()
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
    public void It_retries_the_no_op_after_the_commit_window_race_and_returns_update_success()
    {
        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
        _freshnessProbe.IsCurrentCallCount.Should().Be(2);
        _freshnessProbe.Results.Should().Equal(false, true);
    }

    [Test]
    public void It_preserves_rowsets_but_keeps_the_concurrent_content_version_bump()
    {
        var adjustedAfterState = _stateAfterUpdate with
        {
            Document = _stateAfterUpdate.Document with
            {
                ContentVersion = _stateBeforeUpdate.Document.ContentVersion,
            },
        };

        adjustedAfterState.Should().BeEquivalentTo(_stateBeforeUpdate);
        _stateAfterUpdate.Document.ContentVersion.Should().Be(_stateBeforeUpdate.Document.ContentVersion + 1);
        _stateAfterUpdate
            .Document.ResourceKeyId.Should()
            .Be(_mappingSet.ResourceKeyIdByResource[MssqlGuardedNoOpIntegrationTestSupport.SchoolResource]);
    }

    private async Task ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlRelationalWriteGuardedNoOpCommitWindowPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            MssqlGuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-guarded-no-op-commit-window-put-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpdateResult> ExecuteUpdateAsync(long documentId)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlRelationalWriteGuardedNoOpCommitWindowPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var coordinator = scope.ServiceProvider.GetRequiredService<MssqlGuardedNoOpCommitWindowCoordinator>();
        var pendingCommitTask = coordinator.BeginPendingContentVersionBumpAsync(documentId);

        await coordinator.WaitUntilWriteIsPendingAsync();

        try
        {
            return await repository.UpdateDocumentById(
                MssqlGuardedNoOpIntegrationTestSupport.CreateUpdateRequest(
                    _mappingSet,
                    SchoolDocumentUuid,
                    "mssql-guarded-no-op-commit-window-put-update"
                )
            );
        }
        finally
        {
            coordinator.ReleaseCommit();
            await pendingCommitTask;
        }
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_A_Mssql_Relational_Guarded_No_Op_Post_As_Update_With_A_Commit_Window_Race
{
    private static readonly DocumentUuid ExistingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000008")
    );
    private static readonly DocumentUuid IncomingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000009")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private MssqlGuardedNoOpCommitWindowProbe _freshnessProbe = null!;
    private MssqlGuardedNoOpPersistedState _stateBeforePostAsUpdate = null!;
    private MssqlGuardedNoOpPersistedState _stateAfterPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private long _incomingDocumentUuidCount;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a configured connection string.");
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlGuardedNoOpIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlGuardedNoOpIntegrationTestSupport.CreateCommitWindowServiceProvider();

        _freshnessProbe = _serviceProvider.GetRequiredService<MssqlGuardedNoOpCommitWindowProbe>();

        await ExecuteCreateAsync();
        _stateBeforePostAsUpdate = await MssqlGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingSchoolDocumentUuid.Value
        );

        var persistedReferentialIdentity =
            await MssqlGuardedNoOpIntegrationTestSupport.ReadReferentialIdentityRowAsync(
                _database,
                _stateBeforePostAsUpdate.Document.DocumentId,
                _mappingSet.ResourceKeyIdByResource[MssqlGuardedNoOpIntegrationTestSupport.SchoolResource]
            );

        _postAsUpdateResult = await ExecutePostAsUpdateAsync(
            _stateBeforePostAsUpdate.Document.DocumentId,
            new ReferentialId(persistedReferentialIdentity.ReferentialId)
        );

        _stateAfterPostAsUpdate = await MssqlGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingSchoolDocumentUuid.Value
        );
        _incomingDocumentUuidCount = await MssqlGuardedNoOpIntegrationTestSupport.ReadDocumentCountAsync(
            _database,
            IncomingSchoolDocumentUuid.Value
        );
    }

    [TearDown]
    public async Task TearDown()
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
    public void It_retries_the_no_op_after_the_commit_window_race_and_preserves_the_existing_document()
    {
        _postAsUpdateResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _postAsUpdateResult
            .As<UpsertResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(ExistingSchoolDocumentUuid);
        _incomingDocumentUuidCount.Should().Be(0);
        _freshnessProbe.IsCurrentCallCount.Should().Be(2);
        _freshnessProbe.Results.Should().Equal(false, true);
    }

    [Test]
    public void It_preserves_existing_rowsets_but_keeps_the_concurrent_content_version_bump()
    {
        var adjustedAfterState = _stateAfterPostAsUpdate with
        {
            Document = _stateAfterPostAsUpdate.Document with
            {
                ContentVersion = _stateBeforePostAsUpdate.Document.ContentVersion,
            },
        };

        adjustedAfterState.Should().BeEquivalentTo(_stateBeforePostAsUpdate);
        _stateAfterPostAsUpdate
            .Document.ContentVersion.Should()
            .Be(_stateBeforePostAsUpdate.Document.ContentVersion + 1);
        _stateAfterPostAsUpdate
            .Document.ResourceKeyId.Should()
            .Be(_mappingSet.ResourceKeyIdByResource[MssqlGuardedNoOpIntegrationTestSupport.SchoolResource]);
    }

    private async Task ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlRelationalWriteGuardedNoOpCommitWindowPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            MssqlGuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                ExistingSchoolDocumentUuid,
                "mssql-guarded-no-op-commit-window-post-as-update-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpsertResult> ExecutePostAsUpdateAsync(long documentId, ReferentialId referentialId)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlRelationalWriteGuardedNoOpCommitWindowPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var coordinator = scope.ServiceProvider.GetRequiredService<MssqlGuardedNoOpCommitWindowCoordinator>();
        var pendingCommitTask = coordinator.BeginPendingContentVersionBumpAsync(documentId);

        await coordinator.WaitUntilWriteIsPendingAsync();

        try
        {
            return await repository.UpsertDocument(
                MssqlGuardedNoOpIntegrationTestSupport.CreatePostAsUpdateRequest(
                    _mappingSet,
                    IncomingSchoolDocumentUuid,
                    "mssql-guarded-no-op-commit-window-post-as-update",
                    referentialId
                )
            );
        }
        finally
        {
            coordinator.ReleaseCommit();
            await pendingCommitTask;
        }
    }
}
