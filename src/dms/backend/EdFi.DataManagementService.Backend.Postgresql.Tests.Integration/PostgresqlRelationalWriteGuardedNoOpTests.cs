// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
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

file sealed class GuardedNoOpHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class GuardedNoOpAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class GuardedNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file sealed class GuardedNoOpConcurrentContentVersionBumpFreshnessChecker(
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

internal sealed class GuardedNoOpCommitWindowProbe
{
    public int IsCurrentCallCount { get; private set; }

    public List<bool> Results { get; } = [];

    public void Record(bool result)
    {
        IsCurrentCallCount++;
        Results.Add(result);
    }
}

internal sealed class GuardedNoOpCommitWindowCoordinator(NpgsqlDataSourceProvider dataSourceProvider)
    : IAsyncDisposable
{
    private readonly NpgsqlDataSourceProvider _dataSourceProvider =
        dataSourceProvider ?? throw new ArgumentNullException(nameof(dataSourceProvider));

    private readonly TaskCompletionSource _writePending = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private readonly TaskCompletionSource _allowCommit = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private readonly TaskCompletionSource _committed = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private bool _commitCompleted;

    public int CommitCallCount { get; private set; }

    public async Task BeginPendingContentVersionBumpAsync(
        long documentId,
        CancellationToken cancellationToken = default
    )
    {
        _connection = await _dataSourceProvider.DataSource.OpenConnectionAsync(cancellationToken);
        _transaction = await _connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken
        );

        await using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
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

internal sealed class GuardedNoOpCommitWindowFreshnessChecker(
    GuardedNoOpCommitWindowCoordinator coordinator,
    GuardedNoOpCommitWindowProbe probe
) : IRelationalWriteFreshnessChecker
{
    private readonly GuardedNoOpCommitWindowCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    private readonly GuardedNoOpCommitWindowProbe _probe =
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

internal sealed class GuardedNoOpPreLoadContentVersionBumpProbe
{
    public int LoadCallCount { get; private set; }

    public int ContentVersionBumpCallCount { get; private set; }

    public List<long> LoadedContentVersions { get; } = [];

    public void RecordLoad(RelationalWriteCurrentState? currentState)
    {
        LoadCallCount++;

        if (currentState is not null)
        {
            LoadedContentVersions.Add(currentState.DocumentMetadata.ContentVersion);
        }
    }

    public void RecordContentVersionBump() => ContentVersionBumpCallCount++;
}

file sealed class GuardedNoOpPreLoadContentVersionBumpCurrentStateLoader(
    ISessionDocumentHydrator sessionDocumentHydrator,
    NpgsqlDataSourceProvider dataSourceProvider,
    GuardedNoOpPreLoadContentVersionBumpProbe probe
) : IRelationalWriteCurrentStateLoader
{
    private readonly RelationalWriteCurrentStateLoader _inner = new(sessionDocumentHydrator);
    private readonly NpgsqlDataSourceProvider _dataSourceProvider =
        dataSourceProvider ?? throw new ArgumentNullException(nameof(dataSourceProvider));
    private readonly GuardedNoOpPreLoadContentVersionBumpProbe _probe =
        probe ?? throw new ArgumentNullException(nameof(probe));
    private bool _hasBumpedContentVersion;

    public async Task<RelationalWriteCurrentState?> LoadAsync(
        RelationalWriteCurrentStateLoadRequest request,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        if (!_hasBumpedContentVersion)
        {
            _hasBumpedContentVersion = true;

            await BumpContentVersionAsync(request.TargetContext.DocumentId, cancellationToken);
            _probe.RecordContentVersionBump();
        }

        var currentState = await _inner.LoadAsync(request, writeSession, cancellationToken);
        _probe.RecordLoad(currentState);

        return currentState;
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
                $"Expected exactly one pre-load document content-version bump for document id '{documentId}', but affected {rowsAffected} rows."
            );
        }
    }
}

internal sealed record GuardedNoOpDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record GuardedNoOpSchoolRow(long DocumentId, long SchoolId, string? ShortName);

internal sealed record GuardedNoOpSchoolAddressRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string City
);

internal sealed record GuardedNoOpSchoolExtensionAddressRow(
    long BaseCollectionItemId,
    long SchoolDocumentId,
    string Zone
);

internal sealed record GuardedNoOpPersistedState(
    GuardedNoOpDocumentRow Document,
    GuardedNoOpSchoolRow School,
    IReadOnlyList<GuardedNoOpSchoolAddressRow> Addresses,
    IReadOnlyList<GuardedNoOpSchoolExtensionAddressRow> ExtensionAddresses,
    long DocumentCount
);

internal sealed record GuardedNoOpReferentialIdentityRow(
    Guid ReferentialId,
    long DocumentId,
    short ResourceKeyId
);

file static class GuardedNoOpIntegrationTestSupport
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

    public const string ReorderedRequestBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            {
              "city": "Dallas"
            },
            {
              "city": "Austin"
            }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-2"
                    }
                  }
                },
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-1"
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

        services.AddSingleton<IHostApplicationLifetime, GuardedNoOpHostApplicationLifetime>();
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

    public static ServiceProvider CreateStaleCompareServiceProvider() =>
        CreateServiceProvider(static services =>
        {
            services.RemoveAll<IRelationalWriteFreshnessChecker>();
            services.AddScoped<
                IRelationalWriteFreshnessChecker,
                GuardedNoOpConcurrentContentVersionBumpFreshnessChecker
            >();
        });

    public static ServiceProvider CreateCommitWindowServiceProvider() =>
        CreateServiceProvider(static services =>
        {
            services.AddSingleton<GuardedNoOpCommitWindowProbe>();
            services.AddScoped<GuardedNoOpCommitWindowCoordinator>();
            services.RemoveAll<IRelationalWriteFreshnessChecker>();
            services.AddScoped<IRelationalWriteFreshnessChecker, GuardedNoOpCommitWindowFreshnessChecker>();
        });

    public static ServiceProvider CreatePreLoadContentVersionBumpServiceProvider() =>
        CreateServiceProvider(static services =>
        {
            services.AddSingleton<GuardedNoOpPreLoadContentVersionBumpProbe>();
            services.RemoveAll<IRelationalWriteCurrentStateLoader>();
            services.AddScoped<
                IRelationalWriteCurrentStateLoader,
                GuardedNoOpPreLoadContentVersionBumpCurrentStateLoader
            >();
        });

    public static UpsertRequest CreateCreateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId
    ) => CreateCreateRequest(mappingSet, documentUuid, traceId, RequestBodyJson);

    public static UpsertRequest CreateCreateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId,
        string requestBodyJson
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new GuardedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new GuardedNoOpAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

    public static UpdateRequest CreateUpdateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId
    ) => CreateUpdateRequest(mappingSet, documentUuid, traceId, RequestBodyJson);

    public static UpdateRequest CreateUpdateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId,
        string requestBodyJson
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new GuardedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new GuardedNoOpAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

    public static UpsertRequest CreatePostAsUpdateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId,
        ReferentialId referentialId
    ) => CreatePostAsUpdateRequest(mappingSet, documentUuid, traceId, referentialId, RequestBodyJson);

    public static UpsertRequest CreatePostAsUpdateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId,
        ReferentialId referentialId,
        string requestBodyJson
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(referentialId),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new GuardedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new GuardedNoOpAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

    public static async Task<GuardedNoOpPersistedState> ReadPersistedStateAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(database, documentUuid);
        var school = await ReadSchoolAsync(database, document.DocumentId);
        var addresses = await ReadSchoolAddressesAsync(database, document.DocumentId);
        var extensionAddresses = await ReadSchoolExtensionAddressesAsync(database, document.DocumentId);
        var documentCount = await ReadDocumentCountAsync(database);

        return new GuardedNoOpPersistedState(document, school, addresses, extensionAddresses, documentCount);
    }

    public static async Task<GuardedNoOpReferentialIdentityRow> ReadReferentialIdentityRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId,
        short resourceKeyId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "ReferentialId", "DocumentId", "ResourceKeyId"
            FROM "dms"."ReferentialIdentity"
            WHERE "DocumentId" = @documentId
                AND "ResourceKeyId" = @resourceKeyId;
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        return rows.Count == 1
            ? new GuardedNoOpReferentialIdentityRow(
                GetGuid(rows[0], "ReferentialId"),
                GetInt64(rows[0], "DocumentId"),
                GetInt16(rows[0], "ResourceKeyId")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one referential identity row for document id '{documentId}' and resource key '{resourceKeyId}', but found {rows.Count}."
            );
    }

    public static async Task<long> ReadDocumentCountAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "Count"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
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

    private static async Task<GuardedNoOpDocumentRow> ReadDocumentAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new GuardedNoOpDocumentRow(
                GetInt64(rows[0], "DocumentId"),
                GetGuid(rows[0], "DocumentUuid"),
                GetInt16(rows[0], "ResourceKeyId"),
                GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private static async Task<GuardedNoOpSchoolRow> ReadSchoolAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "DocumentId", "SchoolId", "ShortName"
            FROM "edfi"."School"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new GuardedNoOpSchoolRow(
                GetInt64(rows[0], "DocumentId"),
                GetInt64(rows[0], "SchoolId"),
                GetNullableString(rows[0], "ShortName")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private static async Task<IReadOnlyList<GuardedNoOpSchoolAddressRow>> ReadSchoolAddressesAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "Ordinal", "City"
            FROM "edfi"."SchoolAddress"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new GuardedNoOpSchoolAddressRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "City")
            ))
            .ToArray();
    }

    private static async Task<
        IReadOnlyList<GuardedNoOpSchoolExtensionAddressRow>
    > ReadSchoolExtensionAddressesAsync(PostgresqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "BaseCollectionItemId", "School_DocumentId", "Zone"
            FROM "sample"."SchoolExtensionAddress"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "BaseCollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new GuardedNoOpSchoolExtensionAddressRow(
                GetInt64(row, "BaseCollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetString(row, "Zone")
            ))
            .ToArray();
    }

    private static async Task<long> ReadDocumentCountAsync(PostgresqlGeneratedDdlTestDatabase database)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "Count"
            FROM "dms"."Document";
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
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Guarded_No_Op_Put_With_A_Focused_Stable_Key_Fixture
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000001")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private GuardedNoOpPersistedState _stateBeforeUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            GuardedNoOpIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = GuardedNoOpIntegrationTestSupport.CreateServiceProvider();

        await ExecuteCreateAsync();
        _stateBeforeUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );

        _updateResult = await ExecuteUpdateAsync();
        _stateAfterUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
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
    public void It_returns_update_success_for_an_unchanged_put()
    {
        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
    }

    [Test]
    public void It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_put()
    {
        _stateAfterUpdate.Should().BeEquivalentTo(_stateBeforeUpdate);
        _stateAfterUpdate
            .Document.ResourceKeyId.Should()
            .Be(_mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]);
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
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-guarded-no-op-put-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpdateResult> ExecuteUpdateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            GuardedNoOpIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-guarded-no-op-put-update"
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_With_A_Focused_Stable_Key_Fixture
{
    private static readonly DocumentUuid ExistingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000002")
    );
    private static readonly DocumentUuid IncomingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000003")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private GuardedNoOpPersistedState _stateBeforePostAsUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private long _incomingDocumentUuidCount;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            GuardedNoOpIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = GuardedNoOpIntegrationTestSupport.CreateServiceProvider();

        await ExecuteCreateAsync();
        _stateBeforePostAsUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingSchoolDocumentUuid.Value
        );

        var persistedReferentialIdentity =
            await GuardedNoOpIntegrationTestSupport.ReadReferentialIdentityRowAsync(
                _database,
                _stateBeforePostAsUpdate.Document.DocumentId,
                _mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]
            );

        _postAsUpdateResult = await ExecutePostAsUpdateAsync(
            new ReferentialId(persistedReferentialIdentity.ReferentialId)
        );

        _stateAfterPostAsUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingSchoolDocumentUuid.Value
        );
        _incomingDocumentUuidCount = await GuardedNoOpIntegrationTestSupport.ReadDocumentCountAsync(
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
    public void It_returns_update_success_and_preserves_the_existing_document_for_an_unchanged_post_as_update()
    {
        _postAsUpdateResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _postAsUpdateResult
            .As<UpsertResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(ExistingSchoolDocumentUuid);
        _incomingDocumentUuidCount.Should().Be(0);
    }

    [Test]
    public void It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_post_as_update()
    {
        _stateAfterPostAsUpdate.Should().BeEquivalentTo(_stateBeforePostAsUpdate);
        _stateAfterPostAsUpdate
            .Document.ResourceKeyId.Should()
            .Be(_mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]);
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
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                ExistingSchoolDocumentUuid,
                "pg-guarded-no-op-post-as-update-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpsertResult> ExecutePostAsUpdateAsync(ReferentialId referentialId)
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreatePostAsUpdateRequest(
                _mappingSet,
                IncomingSchoolDocumentUuid,
                "pg-guarded-no-op-post-as-update",
                referentialId
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Guarded_No_Op_Put_When_Current_State_Refreshes_Content_Version
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000010")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private GuardedNoOpPreLoadContentVersionBumpProbe _probe = null!;
    private GuardedNoOpPersistedState _stateBeforeUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            GuardedNoOpIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = GuardedNoOpIntegrationTestSupport.CreatePreLoadContentVersionBumpServiceProvider();
        _probe = _serviceProvider.GetRequiredService<GuardedNoOpPreLoadContentVersionBumpProbe>();

        await ExecuteCreateAsync();
        _stateBeforeUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );

        _updateResult = await ExecuteUpdateAsync();
        _stateAfterUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
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
    public void It_returns_update_success_without_a_repository_retry_when_current_state_refreshes_the_content_version()
    {
        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
        _probe.ContentVersionBumpCallCount.Should().Be(1);
        _probe.LoadCallCount.Should().Be(1);
        _probe.LoadedContentVersions.Should().Equal(_stateBeforeUpdate.Document.ContentVersion + 1);
    }

    [Test]
    public void It_preserves_rowsets_and_avoids_an_extra_content_version_bump_during_the_guarded_no_op_put()
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
            .Be(_mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]);
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
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpCurrentStateRefreshPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-guarded-no-op-current-state-refresh-put-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpdateResult> ExecuteUpdateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpCurrentStateRefreshPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            GuardedNoOpIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-guarded-no-op-current-state-refresh-put-update"
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_When_Current_State_Refreshes_Content_Version
{
    private static readonly DocumentUuid ExistingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000011")
    );
    private static readonly DocumentUuid IncomingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000012")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private GuardedNoOpPreLoadContentVersionBumpProbe _probe = null!;
    private GuardedNoOpPersistedState _stateBeforePostAsUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private long _incomingDocumentUuidCount;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            GuardedNoOpIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = GuardedNoOpIntegrationTestSupport.CreatePreLoadContentVersionBumpServiceProvider();
        _probe = _serviceProvider.GetRequiredService<GuardedNoOpPreLoadContentVersionBumpProbe>();

        await ExecuteCreateAsync();
        _stateBeforePostAsUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingSchoolDocumentUuid.Value
        );

        var persistedReferentialIdentity =
            await GuardedNoOpIntegrationTestSupport.ReadReferentialIdentityRowAsync(
                _database,
                _stateBeforePostAsUpdate.Document.DocumentId,
                _mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]
            );

        _postAsUpdateResult = await ExecutePostAsUpdateAsync(
            new ReferentialId(persistedReferentialIdentity.ReferentialId)
        );

        _stateAfterPostAsUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingSchoolDocumentUuid.Value
        );
        _incomingDocumentUuidCount = await GuardedNoOpIntegrationTestSupport.ReadDocumentCountAsync(
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
    public void It_returns_update_success_without_a_repository_retry_when_post_as_update_refreshes_current_state_freshness()
    {
        _postAsUpdateResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _postAsUpdateResult
            .As<UpsertResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(ExistingSchoolDocumentUuid);
        _incomingDocumentUuidCount.Should().Be(0);
        _probe.ContentVersionBumpCallCount.Should().Be(1);
        _probe.LoadCallCount.Should().Be(1);
        _probe.LoadedContentVersions.Should().Equal(_stateBeforePostAsUpdate.Document.ContentVersion + 1);
    }

    [Test]
    public void It_preserves_rowsets_and_avoids_an_extra_content_version_bump_during_the_guarded_no_op_post_as_update()
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
            .Be(_mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]);
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
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpCurrentStateRefreshPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                ExistingSchoolDocumentUuid,
                "pg-guarded-no-op-current-state-refresh-post-as-update-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpsertResult> ExecutePostAsUpdateAsync(ReferentialId referentialId)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpCurrentStateRefreshPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreatePostAsUpdateRequest(
                _mappingSet,
                IncomingSchoolDocumentUuid,
                "pg-guarded-no-op-current-state-refresh-post-as-update",
                referentialId
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Guarded_No_Op_Put_After_A_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000007")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private GuardedNoOpPersistedState _stateBeforeNoOpUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterNoOpUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            GuardedNoOpIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = GuardedNoOpIntegrationTestSupport.CreateServiceProvider();

        await ExecuteCreateAsync();
        await ExecuteReorderUpdateAsync();
        _stateBeforeNoOpUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );

        _updateResult = await ExecuteNoOpUpdateAsync();
        _stateAfterNoOpUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
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
    public void It_returns_update_success_for_an_unchanged_put_after_reorder()
    {
        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
    }

    [Test]
    public void It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_put_after_reorder()
    {
        _stateBeforeNoOpUpdate.Addresses.Select(address => address.City).Should().Equal("Dallas", "Austin");
        _stateAfterNoOpUpdate.Should().BeEquivalentTo(_stateBeforeNoOpUpdate);
        _stateAfterNoOpUpdate
            .Document.ResourceKeyId.Should()
            .Be(_mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]);
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
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpPutAfterFullSurfaceCollectionReorder",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-guarded-no-op-put-after-reorder-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task ExecuteReorderUpdateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpPutAfterFullSurfaceCollectionReorder",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var reorderResult = await repository.UpdateDocumentById(
            GuardedNoOpIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-guarded-no-op-put-after-reorder-initial-update",
                GuardedNoOpIntegrationTestSupport.ReorderedRequestBodyJson
            )
        );

        reorderResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    private async Task<UpdateResult> ExecuteNoOpUpdateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpPutAfterFullSurfaceCollectionReorder",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            GuardedNoOpIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-guarded-no-op-put-after-reorder-no-op-update",
                GuardedNoOpIntegrationTestSupport.ReorderedRequestBodyJson
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_After_A_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture
{
    private static readonly DocumentUuid ExistingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000008")
    );
    private static readonly DocumentUuid IncomingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000009")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private GuardedNoOpPersistedState _stateBeforePostAsUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private long _incomingDocumentUuidCount;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            GuardedNoOpIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = GuardedNoOpIntegrationTestSupport.CreateServiceProvider();

        await ExecuteCreateAsync();
        await ExecuteReorderUpdateAsync();
        _stateBeforePostAsUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingSchoolDocumentUuid.Value
        );

        var persistedReferentialIdentity =
            await GuardedNoOpIntegrationTestSupport.ReadReferentialIdentityRowAsync(
                _database,
                _stateBeforePostAsUpdate.Document.DocumentId,
                _mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]
            );

        _postAsUpdateResult = await ExecutePostAsUpdateAsync(
            new ReferentialId(persistedReferentialIdentity.ReferentialId)
        );

        _stateAfterPostAsUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingSchoolDocumentUuid.Value
        );
        _incomingDocumentUuidCount = await GuardedNoOpIntegrationTestSupport.ReadDocumentCountAsync(
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
    public void It_returns_update_success_and_preserves_the_existing_document_for_an_unchanged_post_as_update_after_reorder()
    {
        _postAsUpdateResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _postAsUpdateResult
            .As<UpsertResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(ExistingSchoolDocumentUuid);
        _incomingDocumentUuidCount.Should().Be(0);
    }

    [Test]
    public void It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_post_as_update_after_reorder()
    {
        _stateBeforePostAsUpdate.Addresses.Select(address => address.City).Should().Equal("Dallas", "Austin");
        _stateAfterPostAsUpdate.Should().BeEquivalentTo(_stateBeforePostAsUpdate);
        _stateAfterPostAsUpdate
            .Document.ResourceKeyId.Should()
            .Be(_mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]);
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
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpPostAsUpdateAfterFullSurfaceCollectionReorder",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                ExistingSchoolDocumentUuid,
                "pg-guarded-no-op-post-as-update-after-reorder-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task ExecuteReorderUpdateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpPostAsUpdateAfterFullSurfaceCollectionReorder",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var reorderResult = await repository.UpdateDocumentById(
            GuardedNoOpIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                ExistingSchoolDocumentUuid,
                "pg-guarded-no-op-post-as-update-after-reorder-initial-update",
                GuardedNoOpIntegrationTestSupport.ReorderedRequestBodyJson
            )
        );

        reorderResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    private async Task<UpsertResult> ExecutePostAsUpdateAsync(ReferentialId referentialId)
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpPostAsUpdateAfterFullSurfaceCollectionReorder",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreatePostAsUpdateRequest(
                _mappingSet,
                IncomingSchoolDocumentUuid,
                "pg-guarded-no-op-post-as-update-after-reorder-no-op-update",
                referentialId,
                GuardedNoOpIntegrationTestSupport.ReorderedRequestBodyJson
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Stale_Guarded_No_Op_Put_With_A_Focused_Stable_Key_Fixture
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000004")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private GuardedNoOpPersistedState _stateBeforeUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            GuardedNoOpIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = GuardedNoOpIntegrationTestSupport.CreateStaleCompareServiceProvider();

        await ExecuteCreateAsync();
        _stateBeforeUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );

        _updateResult = await ExecuteUpdateAsync();
        _stateAfterUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
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
    public void It_retries_and_returns_update_success_after_the_no_op_compare_goes_stale()
    {
        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
    }

    [Test]
    public void It_preserves_the_rowsets_but_keeps_the_concurrent_content_version_bump()
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
            .Be(_mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]);
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
                    InstanceName: "PostgresqlRelationalWriteStaleGuardedNoOpPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-stale-guarded-no-op-put-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpdateResult> ExecuteUpdateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteStaleGuardedNoOpPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            GuardedNoOpIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-stale-guarded-no-op-put-update"
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Stale_Guarded_No_Op_Post_As_Update_With_A_Focused_Stable_Key_Fixture
{
    private static readonly DocumentUuid ExistingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000005")
    );
    private static readonly DocumentUuid IncomingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000006")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private GuardedNoOpPersistedState _stateBeforePostAsUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private long _incomingDocumentUuidCount;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            GuardedNoOpIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = GuardedNoOpIntegrationTestSupport.CreateStaleCompareServiceProvider();

        await ExecuteCreateAsync();
        _stateBeforePostAsUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingSchoolDocumentUuid.Value
        );

        var persistedReferentialIdentity =
            await GuardedNoOpIntegrationTestSupport.ReadReferentialIdentityRowAsync(
                _database,
                _stateBeforePostAsUpdate.Document.DocumentId,
                _mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]
            );

        _postAsUpdateResult = await ExecutePostAsUpdateAsync(
            new ReferentialId(persistedReferentialIdentity.ReferentialId)
        );

        _stateAfterPostAsUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingSchoolDocumentUuid.Value
        );
        _incomingDocumentUuidCount = await GuardedNoOpIntegrationTestSupport.ReadDocumentCountAsync(
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
    public void It_retries_and_returns_update_success_for_a_stale_post_as_update_no_op_compare()
    {
        _postAsUpdateResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _postAsUpdateResult
            .As<UpsertResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(ExistingSchoolDocumentUuid);
        _incomingDocumentUuidCount.Should().Be(0);
    }

    [Test]
    public void It_preserves_the_existing_rowsets_but_keeps_the_concurrent_content_version_bump()
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
            .Be(_mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]);
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
                    InstanceName: "PostgresqlRelationalWriteStaleGuardedNoOpPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                ExistingSchoolDocumentUuid,
                "pg-stale-guarded-no-op-post-as-update-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpsertResult> ExecutePostAsUpdateAsync(ReferentialId referentialId)
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteStaleGuardedNoOpPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreatePostAsUpdateRequest(
                _mappingSet,
                IncomingSchoolDocumentUuid,
                "pg-stale-guarded-no-op-post-as-update",
                referentialId
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Guarded_No_Op_Put_With_A_Commit_Window_Race
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000007")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private GuardedNoOpCommitWindowProbe _freshnessProbe = null!;
    private GuardedNoOpPersistedState _stateBeforeUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            GuardedNoOpIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = GuardedNoOpIntegrationTestSupport.CreateCommitWindowServiceProvider();
        _freshnessProbe = _serviceProvider.GetRequiredService<GuardedNoOpCommitWindowProbe>();

        await ExecuteCreateAsync();
        _stateBeforeUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );

        _updateResult = await ExecuteUpdateAsync(_stateBeforeUpdate.Document.DocumentId);
        _stateAfterUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
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
            .Be(_mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]);
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
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpCommitWindowPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-guarded-no-op-commit-window-put-create"
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
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpCommitWindowPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var coordinator = scope.ServiceProvider.GetRequiredService<GuardedNoOpCommitWindowCoordinator>();
        var pendingCommitTask = coordinator.BeginPendingContentVersionBumpAsync(documentId);

        await coordinator.WaitUntilWriteIsPendingAsync();

        try
        {
            return await repository.UpdateDocumentById(
                GuardedNoOpIntegrationTestSupport.CreateUpdateRequest(
                    _mappingSet,
                    SchoolDocumentUuid,
                    "pg-guarded-no-op-commit-window-put-update"
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
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_With_A_Commit_Window_Race
{
    private static readonly DocumentUuid ExistingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000008")
    );
    private static readonly DocumentUuid IncomingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000009")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private GuardedNoOpCommitWindowProbe _freshnessProbe = null!;
    private GuardedNoOpPersistedState _stateBeforePostAsUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private long _incomingDocumentUuidCount;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            GuardedNoOpIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = GuardedNoOpIntegrationTestSupport.CreateCommitWindowServiceProvider();
        _freshnessProbe = _serviceProvider.GetRequiredService<GuardedNoOpCommitWindowProbe>();

        await ExecuteCreateAsync();
        _stateBeforePostAsUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingSchoolDocumentUuid.Value
        );

        var persistedReferentialIdentity =
            await GuardedNoOpIntegrationTestSupport.ReadReferentialIdentityRowAsync(
                _database,
                _stateBeforePostAsUpdate.Document.DocumentId,
                _mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]
            );

        _postAsUpdateResult = await ExecutePostAsUpdateAsync(
            _stateBeforePostAsUpdate.Document.DocumentId,
            new ReferentialId(persistedReferentialIdentity.ReferentialId)
        );

        _stateAfterPostAsUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            ExistingSchoolDocumentUuid.Value
        );
        _incomingDocumentUuidCount = await GuardedNoOpIntegrationTestSupport.ReadDocumentCountAsync(
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
            .Be(_mappingSet.ResourceKeyIdByResource[GuardedNoOpIntegrationTestSupport.SchoolResource]);
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
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpCommitWindowPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                ExistingSchoolDocumentUuid,
                "pg-guarded-no-op-commit-window-post-as-update-create"
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
                    InstanceName: "PostgresqlRelationalWriteGuardedNoOpCommitWindowPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var coordinator = scope.ServiceProvider.GetRequiredService<GuardedNoOpCommitWindowCoordinator>();
        var pendingCommitTask = coordinator.BeginPendingContentVersionBumpAsync(documentId);

        await coordinator.WaitUntilWriteIsPendingAsync();

        try
        {
            return await repository.UpsertDocument(
                GuardedNoOpIntegrationTestSupport.CreatePostAsUpdateRequest(
                    _mappingSet,
                    IncomingSchoolDocumentUuid,
                    "pg-guarded-no-op-commit-window-post-as-update",
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
