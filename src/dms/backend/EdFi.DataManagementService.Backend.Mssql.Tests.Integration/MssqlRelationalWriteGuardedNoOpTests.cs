// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Holds an uncommitted content-version bump on a target document from a second connection so a write
/// under test has to contend for the row the composite capture statement locks. The bump stays
/// uncommitted, and its row lock held, until <see cref="CommitAsync"/> runs.
/// </summary>
internal sealed class GuardedNoOpCompetingBumpCoordinator(IDataStoreSelection dataStoreSelection)
    : IAsyncDisposable
{
    private readonly IDataStoreSelection _dataStoreSelection =
        dataStoreSelection ?? throw new ArgumentNullException(nameof(dataStoreSelection));

    private SqlConnection? _connection;
    private SqlTransaction? _transaction;
    private bool _commitCompleted;
    private int _sessionId;
    private string? _connectionString;

    public async Task BeginUncommittedContentVersionBumpAsync(
        long documentId,
        CancellationToken cancellationToken = default
    )
    {
        var connectionString = _dataStoreSelection.GetSelectedDataStore().ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Selected data store does not have a valid connection string."
            );
        }

        _connectionString = connectionString;
        _connection = new SqlConnection(connectionString);
        await _connection.OpenAsync(cancellationToken);

        // Record this session id so the contended write can be proven to be lock-blocked by THIS
        // transaction before the commit is released.
        await using (var sessionIdCommand = _connection.CreateCommand())
        {
            sessionIdCommand.CommandText = "SELECT @@SPID;";
            _sessionId = Convert.ToInt32(
                await sessionIdCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture
            );
        }

        _transaction = (SqlTransaction)
            await _connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            UPDATE [dms].[Document]
            SET [ContentVersion] = [ContentVersion] + 1,
                [ContentLastModifiedAt] = @contentLastModifiedAt
            WHERE [DocumentId] = @documentId;
            """;
        command.Parameters.Add(new SqlParameter("@documentId", documentId));
        // The GETUTCDATE()-backed datetime2 stamp column stores the UTC instant, so write the
        // zero-offset sentinel's UTC clock face.
        command.Parameters.Add(
            new SqlParameter(
                "@contentLastModifiedAt",
                NoProfileGuardedNoOpScenarios.ConcurrentBumpContentLastModifiedAt.UtcDateTime
            )
        );

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

        if (rowsAffected != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one competing document content-version bump for document id '{documentId}', but affected {rowsAffected} rows."
            );
        }
    }

    /// <summary>
    /// Polls until SQL Server reports some request lock-blocked by this coordinator's session, proving
    /// the contended write has actually reached the server and is waiting on the uncommitted bump's
    /// X-lock. Returns without throwing when <paramref name="timeout"/> elapses: a capture that never
    /// blocks is the condition under test, and the caller's assertion reports it far more clearly than
    /// a timeout exception would. Uses its own short-lived connection because the coordinator's own
    /// connection is inside the uncommitted transaction.
    /// </summary>
    public async Task WaitUntilBlockingAnotherRequestAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException(
                "The uncommitted content-version bump must be started before waiting for it to block a request."
            );
        }

        await using var observerConnection = new SqlConnection(_connectionString);
        await observerConnection.OpenAsync(cancellationToken);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token
        );

        try
        {
            while (true)
            {
                await using var command = observerConnection.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*)
                    FROM sys.dm_exec_requests
                    WHERE [blocking_session_id] = @blockingSessionId;
                    """;
                command.Parameters.Add(new SqlParameter("@blockingSessionId", _sessionId));

                var blockedRequestCount = Convert.ToInt32(
                    await command.ExecuteScalarAsync(linkedSource.Token),
                    CultureInfo.InvariantCulture
                );

                if (blockedRequestCount > 0)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), linkedSource.Token);
            }
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            // Nothing ever blocked. Leave the verdict to the caller's assertion.
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null || _commitCompleted)
        {
            return;
        }

        await _transaction.CommitAsync(cancellationToken);
        _commitCompleted = true;
    }

    public async ValueTask DisposeAsync()
    {
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

internal sealed record GuardedNoOpDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion,
    long IdentityVersion,
    DateTimeOffset ContentLastModifiedAt,
    DateTimeOffset IdentityLastModifiedAt,
    DateTimeOffset CreatedAt
);

internal sealed record GuardedNoOpSchoolRow(
    long DocumentId,
    long SchoolId,
    string? ShortName,
    long ContentVersion,
    DateTimeOffset ContentLastModifiedAt
);

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
    IReadOnlyList<GuardedNoOpReferentialIdentityRow> ReferentialIdentities,
    long DocumentCount,
    long MaxChangeVersion
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

    // The provider-neutral request bodies live in the shared contract so every engine adapter exercises
    // identical inputs; this SQL Server adapter consumes them rather than defining its own.
    public const string RequestBodyJson = NoProfileGuardedNoOpScenarios.RequestBodyJson;
    public const string ReorderedRequestBodyJson = NoProfileGuardedNoOpScenarios.ReorderedRequestBodyJson;

    // Upper bound on how long to wait for the contended write to show up as lock-blocked. Reaching it
    // means the capture never took the row lock, which the fixture's assertion reports.
    public static readonly TimeSpan CompetingBumpBlockObservationTimeout = TimeSpan.FromSeconds(10);

    // Upper bound for the contended write to finish once the competing transaction releases its lock.
    public static readonly TimeSpan CompetingBumpCompletionTimeout = TimeSpan.FromSeconds(30);

    public static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    public static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false
    );

    public static ServiceProvider CreateServiceProvider(Action<IServiceCollection>? configureServices = null)
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlBackendIntegrationTestServices();
        configureServices?.Invoke(services);

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static ServiceProvider CreateCompetingBumpServiceProvider() =>
        CreateServiceProvider(static services => services.AddScoped<GuardedNoOpCompetingBumpCoordinator>());

    // Opens its own auto-commit SqlConnection (outside the write session's transaction) against the
    // scope's selected data store and applies the concurrent content-version bump with the shared
    // sentinel stamp, mirroring production stamping.
    public static async Task ExecuteConcurrentContentVersionBumpAsync(
        IDataStoreSelection dataStoreSelection,
        long documentId,
        string bumpDescription,
        CancellationToken cancellationToken
    )
    {
        var connectionString = dataStoreSelection.GetSelectedDataStore().ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Selected data store does not have a valid connection string."
            );
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE [dms].[Document]
            SET [ContentVersion] = [ContentVersion] + 1,
                [ContentLastModifiedAt] = @contentLastModifiedAt
            WHERE [DocumentId] = @documentId;
            """;
        command.Parameters.Add(new SqlParameter("@documentId", documentId));
        // The stamp column is a GETUTCDATE()-backed datetime2 holding the UTC instant, so write the
        // sentinel's UTC clock face; the zero-offset sentinel then round-trips exactly through
        // GetDateTimeOffset's readback normalization.
        command.Parameters.Add(
            new SqlParameter(
                "@contentLastModifiedAt",
                NoProfileGuardedNoOpScenarios.ConcurrentBumpContentLastModifiedAt.UtcDateTime
            )
        );

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

        if (rowsAffected != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one {bumpDescription} for document id '{documentId}', but affected {rowsAffected} rows."
            );
        }
    }

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
            DocumentUuid: documentUuid
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
            DocumentUuid: documentUuid
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
            DocumentUuid: documentUuid
        );

    public static async Task<GuardedNoOpPersistedState> ReadPersistedStateAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(database, documentUuid);
        var school = await ReadSchoolAsync(database, document.DocumentId);
        var addresses = await ReadSchoolAddressesAsync(database, document.DocumentId);
        var extensionAddresses = await ReadSchoolExtensionAddressesAsync(database, document.DocumentId);
        var referentialIdentities = await ReadAllReferentialIdentityRowsAsync(database);
        var documentCount = await ReadDocumentCountAsync(database);
        var maxChangeVersion = await ReadMaxChangeVersionAsync(database);

        return new GuardedNoOpPersistedState(
            document,
            school,
            addresses,
            extensionAddresses,
            referentialIdentities,
            documentCount,
            maxChangeVersion
        );
    }

    // Project the actual SQL Server readback into the full provider-neutral snapshot the shared
    // NoProfileGuardedNoOpScenarios contract compares field-for-field.
    public static NoProfileGuardedNoOpScenarios.PersistedState ToNeutral(GuardedNoOpPersistedState state) =>
        new(
            new NoProfileGuardedNoOpScenarios.DocumentRow(
                state.Document.DocumentId,
                state.Document.DocumentUuid,
                state.Document.ResourceKeyId,
                state.Document.ContentVersion,
                state.Document.IdentityVersion,
                state.Document.ContentLastModifiedAt,
                state.Document.IdentityLastModifiedAt,
                state.Document.CreatedAt
            ),
            new NoProfileGuardedNoOpScenarios.SchoolRow(
                state.School.DocumentId,
                state.School.SchoolId,
                state.School.ShortName,
                state.School.ContentVersion,
                state.School.ContentLastModifiedAt
            ),
            [
                .. state.Addresses.Select(address => new NoProfileGuardedNoOpScenarios.SchoolAddressRow(
                    address.CollectionItemId,
                    address.SchoolDocumentId,
                    address.Ordinal,
                    address.City
                )),
            ],
            [
                .. state.ExtensionAddresses.Select(
                    extensionAddress => new NoProfileGuardedNoOpScenarios.SchoolExtensionAddressRow(
                        extensionAddress.BaseCollectionItemId,
                        extensionAddress.SchoolDocumentId,
                        extensionAddress.Zone
                    )
                ),
            ],
            [
                .. state.ReferentialIdentities.Select(
                    referentialIdentity => new NoProfileGuardedNoOpScenarios.ReferentialIdentityRow(
                        referentialIdentity.ReferentialId,
                        referentialIdentity.DocumentId,
                        referentialIdentity.ResourceKeyId
                    )
                ),
            ],
            state.DocumentCount,
            state.MaxChangeVersion
        );

    public static async Task<GuardedNoOpReferentialIdentityRow> ReadReferentialIdentityRowAsync(
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
            ? new GuardedNoOpReferentialIdentityRow(
                GetGuid(rows[0], "ReferentialId"),
                GetInt64(rows[0], "DocumentId"),
                GetInt16(rows[0], "ResourceKeyId")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one referential identity row for document id '{documentId}' and resource key '{resourceKeyId}', but found {rows.Count}."
            );
    }

    private static async Task<long> ReadMaxChangeVersionAsync(MssqlGeneratedDdlTestDatabase database)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [dms].[GetMaxChangeVersion]() AS [MaxChangeVersion];
            """
        );

        return GetInt64(rows[0], "MaxChangeVersion");
    }

    private static async Task<
        IReadOnlyList<GuardedNoOpReferentialIdentityRow>
    > ReadAllReferentialIdentityRowsAsync(MssqlGeneratedDdlTestDatabase database)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [ReferentialId], [DocumentId], [ResourceKeyId]
            FROM [dms].[ReferentialIdentity]
            ORDER BY [ReferentialId];
            """
        );

        return rows.Select(row => new GuardedNoOpReferentialIdentityRow(
                GetGuid(row, "ReferentialId"),
                GetInt64(row, "DocumentId"),
                GetInt16(row, "ResourceKeyId")
            ))
            .ToArray();
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

    // The generated SQL Server DDL stores the update-tracking stamps in GETUTCDATE()-backed datetime2
    // columns, which read back as unspecified-kind DateTime values; normalize them to UTC
    // DateTimeOffset so the shared contract compares stamps field-for-field across engines.
    public static DateTimeOffset GetDateTimeOffset(
        IReadOnlyDictionary<string, object?> row,
        string columnName
    ) =>
        GetRequiredValue(row, columnName) switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(
                DateTime.SpecifyKind(value, DateTimeKind.Utc),
                TimeSpan.Zero
            ),
            _ => throw new InvalidOperationException(
                $"Expected column '{columnName}' to contain a DateTimeOffset value."
            ),
        };

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
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [DocumentUuid], [ResourceKeyId], [ContentVersion], [IdentityVersion],
                [ContentLastModifiedAt], [IdentityLastModifiedAt], [CreatedAt]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new GuardedNoOpDocumentRow(
                GetInt64(rows[0], "DocumentId"),
                GetGuid(rows[0], "DocumentUuid"),
                GetInt16(rows[0], "ResourceKeyId"),
                GetInt64(rows[0], "ContentVersion"),
                GetInt64(rows[0], "IdentityVersion"),
                GetDateTimeOffset(rows[0], "ContentLastModifiedAt"),
                GetDateTimeOffset(rows[0], "IdentityLastModifiedAt"),
                GetDateTimeOffset(rows[0], "CreatedAt")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private static async Task<GuardedNoOpSchoolRow> ReadSchoolAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [SchoolId], [ShortName], [ContentVersion], [ContentLastModifiedAt]
            FROM [edfi].[School]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Count == 1
            ? new GuardedNoOpSchoolRow(
                GetInt64(rows[0], "DocumentId"),
                GetInt64(rows[0], "SchoolId"),
                GetNullableString(rows[0], "ShortName"),
                GetInt64(rows[0], "ContentVersion"),
                GetDateTimeOffset(rows[0], "ContentLastModifiedAt")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private static async Task<IReadOnlyList<GuardedNoOpSchoolAddressRow>> ReadSchoolAddressesAsync(
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

        return rows.Select(row => new GuardedNoOpSchoolExtensionAddressRow(
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

/// <summary>
/// SQL Server adapters for the shared <c>NoProfileGuardedNoOp</c> scenarios. Each fixture owns only
/// the SQL Server provisioning, resolver registration, guarded no-op production-boundary invocation,
/// and SQL readback; the request bodies, persisted-state snapshot shapes, and behavioral assertions
/// come from <see cref="NoProfileGuardedNoOpScenarios"/> in Backend.Tests.Common.
/// </summary>
internal abstract class GuardedNoOpGeneratedDdlFixtureTestBase
{
    protected MappingSet _mappingSet = null!;
    protected MssqlGeneratedDdlTestDatabase _database = null!;
    protected ServiceProvider _serviceProvider = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        var fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            GuardedNoOpIntegrationTestSupport.FixtureRelativePath
        );

        _mappingSet = fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
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

    protected abstract ServiceProvider CreateServiceProvider();

    protected abstract Task SetUpTestAsync();
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
internal class Given_A_Mssql_Relational_Guarded_No_Op_Put_With_A_Focused_Stable_Key_Fixture
    : GuardedNoOpGeneratedDdlFixtureTestBase
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000001")
    );

    private GuardedNoOpPersistedState _stateBeforeUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    protected override ServiceProvider CreateServiceProvider() =>
        GuardedNoOpIntegrationTestSupport.CreateServiceProvider();

    protected override async Task SetUpTestAsync()
    {
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

    [Test]
    public void It_returns_update_success_for_an_unchanged_put() =>
        NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome(_updateResult, SchoolDocumentUuid);

    [Test]
    public void It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_put() =>
        NoProfileGuardedNoOpScenarios.AssertRowsetUnchanged(
            GuardedNoOpIntegrationTestSupport.ToNeutral(_stateBeforeUpdate),
            GuardedNoOpIntegrationTestSupport.ToNeutral(_stateAfterUpdate),
            _mappingSet,
            GuardedNoOpIntegrationTestSupport.SchoolResource
        );

    private async Task ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteGuardedNoOpPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-guarded-no-op-put-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpdateResult> ExecuteUpdateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteGuardedNoOpPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            GuardedNoOpIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-guarded-no-op-put-update"
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
internal class Given_A_Mssql_Relational_Guarded_No_Op_Post_As_Update_With_A_Focused_Stable_Key_Fixture
    : GuardedNoOpGeneratedDdlFixtureTestBase
{
    private static readonly DocumentUuid ExistingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000002")
    );
    private static readonly DocumentUuid IncomingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000003")
    );

    private GuardedNoOpPersistedState _stateBeforePostAsUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private long _incomingDocumentUuidCount;

    protected override ServiceProvider CreateServiceProvider() =>
        GuardedNoOpIntegrationTestSupport.CreateServiceProvider();

    protected override async Task SetUpTestAsync()
    {
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

    [Test]
    public void It_returns_update_success_and_preserves_the_existing_document_for_an_unchanged_post_as_update() =>
        NoProfileGuardedNoOpScenarios.AssertPostAsUpdateNoOpOutcome(
            _postAsUpdateResult,
            ExistingSchoolDocumentUuid,
            _incomingDocumentUuidCount
        );

    [Test]
    public void It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_post_as_update() =>
        NoProfileGuardedNoOpScenarios.AssertRowsetUnchanged(
            GuardedNoOpIntegrationTestSupport.ToNeutral(_stateBeforePostAsUpdate),
            GuardedNoOpIntegrationTestSupport.ToNeutral(_stateAfterPostAsUpdate),
            _mappingSet,
            GuardedNoOpIntegrationTestSupport.SchoolResource
        );

    private async Task ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteGuardedNoOpPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                ExistingSchoolDocumentUuid,
                "mssql-guarded-no-op-post-as-update-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpsertResult> ExecutePostAsUpdateAsync(ReferentialId referentialId)
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteGuardedNoOpPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreatePostAsUpdateRequest(
                _mappingSet,
                IncomingSchoolDocumentUuid,
                "mssql-guarded-no-op-post-as-update",
                referentialId
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
internal class Given_A_Mssql_Relational_Guarded_No_Op_Put_After_A_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture
    : GuardedNoOpGeneratedDdlFixtureTestBase
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000007")
    );

    private GuardedNoOpPersistedState _stateBeforeNoOpUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterNoOpUpdate = null!;
    private UpdateResult _updateResult = null!;

    protected override ServiceProvider CreateServiceProvider() =>
        GuardedNoOpIntegrationTestSupport.CreateServiceProvider();

    protected override async Task SetUpTestAsync()
    {
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

    [Test]
    public void It_returns_update_success_for_an_unchanged_put_after_reorder() =>
        NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome(_updateResult, SchoolDocumentUuid);

    [Test]
    public void It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_put_after_reorder() =>
        NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedAfterReorder(
            GuardedNoOpIntegrationTestSupport.ToNeutral(_stateBeforeNoOpUpdate),
            GuardedNoOpIntegrationTestSupport.ToNeutral(_stateAfterNoOpUpdate),
            _mappingSet,
            GuardedNoOpIntegrationTestSupport.SchoolResource
        );

    private async Task ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteGuardedNoOpPutAfterFullSurfaceCollectionReorder",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-guarded-no-op-put-after-reorder-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task ExecuteReorderUpdateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteGuardedNoOpPutAfterFullSurfaceCollectionReorder",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var reorderResult = await repository.UpdateDocumentById(
            GuardedNoOpIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-guarded-no-op-put-after-reorder-initial-update",
                GuardedNoOpIntegrationTestSupport.ReorderedRequestBodyJson
            )
        );

        reorderResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    private async Task<UpdateResult> ExecuteNoOpUpdateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteGuardedNoOpPutAfterFullSurfaceCollectionReorder",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            GuardedNoOpIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-guarded-no-op-put-after-reorder-no-op-update",
                GuardedNoOpIntegrationTestSupport.ReorderedRequestBodyJson
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
internal class Given_A_Mssql_Relational_Guarded_No_Op_Post_As_Update_After_A_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture
    : GuardedNoOpGeneratedDdlFixtureTestBase
{
    private static readonly DocumentUuid ExistingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000008")
    );
    private static readonly DocumentUuid IncomingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000009")
    );

    private GuardedNoOpPersistedState _stateBeforePostAsUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private long _incomingDocumentUuidCount;

    protected override ServiceProvider CreateServiceProvider() =>
        GuardedNoOpIntegrationTestSupport.CreateServiceProvider();

    protected override async Task SetUpTestAsync()
    {
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

    [Test]
    public void It_returns_update_success_and_preserves_the_existing_document_for_an_unchanged_post_as_update_after_reorder() =>
        NoProfileGuardedNoOpScenarios.AssertPostAsUpdateNoOpOutcome(
            _postAsUpdateResult,
            ExistingSchoolDocumentUuid,
            _incomingDocumentUuidCount
        );

    [Test]
    public void It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_post_as_update_after_reorder() =>
        NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedAfterReorder(
            GuardedNoOpIntegrationTestSupport.ToNeutral(_stateBeforePostAsUpdate),
            GuardedNoOpIntegrationTestSupport.ToNeutral(_stateAfterPostAsUpdate),
            _mappingSet,
            GuardedNoOpIntegrationTestSupport.SchoolResource
        );

    private async Task ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteGuardedNoOpPostAsUpdateAfterFullSurfaceCollectionReorder",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                ExistingSchoolDocumentUuid,
                "mssql-guarded-no-op-post-as-update-after-reorder-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task ExecuteReorderUpdateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteGuardedNoOpPostAsUpdateAfterFullSurfaceCollectionReorder",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var reorderResult = await repository.UpdateDocumentById(
            GuardedNoOpIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                ExistingSchoolDocumentUuid,
                "mssql-guarded-no-op-post-as-update-after-reorder-initial-update",
                GuardedNoOpIntegrationTestSupport.ReorderedRequestBodyJson
            )
        );

        reorderResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    private async Task<UpsertResult> ExecutePostAsUpdateAsync(ReferentialId referentialId)
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteGuardedNoOpPostAsUpdateAfterFullSurfaceCollectionReorder",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreatePostAsUpdateRequest(
                _mappingSet,
                IncomingSchoolDocumentUuid,
                "mssql-guarded-no-op-post-as-update-after-reorder-no-op-update",
                referentialId,
                GuardedNoOpIntegrationTestSupport.ReorderedRequestBodyJson
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
internal class Given_A_Mssql_Relational_Guarded_No_Op_Put_With_A_Competing_Uncommitted_Content_Version_Bump
    : GuardedNoOpGeneratedDdlFixtureTestBase
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000004")
    );

    private GuardedNoOpPersistedState _stateBeforeUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;
    private bool _completedBeforeCompetingCommit;
    private bool _completedAfterCompetingCommit;

    protected override ServiceProvider CreateServiceProvider() =>
        GuardedNoOpIntegrationTestSupport.CreateCompetingBumpServiceProvider();

    protected override async Task SetUpTestAsync()
    {
        await ExecuteCreateAsync();
        _stateBeforeUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );

        _updateResult = await ExecuteContendedUpdateAsync(_stateBeforeUpdate.Document.DocumentId);
        _stateAfterUpdate = await GuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );
    }

    [Test]
    public void It_blocks_the_capture_until_the_competing_transaction_commits() =>
        NoProfileGuardedNoOpScenarios.AssertCaptureBlockedUntilCompetingCommit(
            _completedBeforeCompetingCommit,
            _completedAfterCompetingCommit
        );

    [Test]
    public void It_returns_update_success_and_keeps_the_competing_content_version_bump()
    {
        NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome(_updateResult, SchoolDocumentUuid);
        NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedExceptOneContentVersionBump(
            GuardedNoOpIntegrationTestSupport.ToNeutral(_stateBeforeUpdate),
            GuardedNoOpIntegrationTestSupport.ToNeutral(_stateAfterUpdate),
            _mappingSet,
            GuardedNoOpIntegrationTestSupport.SchoolResource,
            NoProfileGuardedNoOpScenarios.ConcurrentBumpContentLastModifiedAt
        );
    }

    private async Task ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteGuardedNoOpCompetingBumpPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-guarded-no-op-competing-bump-put-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpdateResult> ExecuteContendedUpdateAsync(long documentId)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteGuardedNoOpCompetingBumpPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var coordinator = scope.ServiceProvider.GetRequiredService<GuardedNoOpCompetingBumpCoordinator>();

        await coordinator.BeginUncommittedContentVersionBumpAsync(documentId);

        var updateTask = repository.UpdateDocumentById(
            GuardedNoOpIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-guarded-no-op-competing-bump-put-update"
            )
        );

        await coordinator.WaitUntilBlockingAnotherRequestAsync(
            GuardedNoOpIntegrationTestSupport.CompetingBumpBlockObservationTimeout
        );

        _completedBeforeCompetingCommit = updateTask.IsCompleted;

        await coordinator.CommitAsync();

        var updateResult = await updateTask.WaitAsync(
            GuardedNoOpIntegrationTestSupport.CompetingBumpCompletionTimeout
        );

        _completedAfterCompetingCommit = true;

        return updateResult;
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
internal class Given_A_Mssql_Relational_Guarded_No_Op_Post_As_Update_With_A_Competing_Uncommitted_Content_Version_Bump
    : GuardedNoOpGeneratedDdlFixtureTestBase
{
    private static readonly DocumentUuid ExistingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000005")
    );
    private static readonly DocumentUuid IncomingSchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000006")
    );

    private GuardedNoOpPersistedState _stateBeforePostAsUpdate = null!;
    private GuardedNoOpPersistedState _stateAfterPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private long _incomingDocumentUuidCount;
    private bool _completedBeforeCompetingCommit;
    private bool _completedAfterCompetingCommit;

    protected override ServiceProvider CreateServiceProvider() =>
        GuardedNoOpIntegrationTestSupport.CreateCompetingBumpServiceProvider();

    protected override async Task SetUpTestAsync()
    {
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

        _postAsUpdateResult = await ExecuteContendedPostAsUpdateAsync(
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

    [Test]
    public void It_blocks_the_capture_until_the_competing_transaction_commits() =>
        NoProfileGuardedNoOpScenarios.AssertCaptureBlockedUntilCompetingCommit(
            _completedBeforeCompetingCommit,
            _completedAfterCompetingCommit
        );

    [Test]
    public void It_returns_update_success_and_preserves_the_existing_document_with_the_competing_content_version_bump()
    {
        NoProfileGuardedNoOpScenarios.AssertPostAsUpdateNoOpOutcome(
            _postAsUpdateResult,
            ExistingSchoolDocumentUuid,
            _incomingDocumentUuidCount
        );
        NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedExceptOneContentVersionBump(
            GuardedNoOpIntegrationTestSupport.ToNeutral(_stateBeforePostAsUpdate),
            GuardedNoOpIntegrationTestSupport.ToNeutral(_stateAfterPostAsUpdate),
            _mappingSet,
            GuardedNoOpIntegrationTestSupport.SchoolResource,
            NoProfileGuardedNoOpScenarios.ConcurrentBumpContentLastModifiedAt
        );
    }

    private async Task ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteGuardedNoOpCompetingBumpPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                ExistingSchoolDocumentUuid,
                "mssql-guarded-no-op-competing-bump-post-as-update-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpsertResult> ExecuteContendedPostAsUpdateAsync(
        long documentId,
        ReferentialId referentialId
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteGuardedNoOpCompetingBumpPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var coordinator = scope.ServiceProvider.GetRequiredService<GuardedNoOpCompetingBumpCoordinator>();

        await coordinator.BeginUncommittedContentVersionBumpAsync(documentId);

        var postAsUpdateTask = repository.UpsertDocument(
            GuardedNoOpIntegrationTestSupport.CreatePostAsUpdateRequest(
                _mappingSet,
                IncomingSchoolDocumentUuid,
                "mssql-guarded-no-op-competing-bump-post-as-update",
                referentialId
            )
        );

        await coordinator.WaitUntilBlockingAnotherRequestAsync(
            GuardedNoOpIntegrationTestSupport.CompetingBumpBlockObservationTimeout
        );

        _completedBeforeCompetingCommit = postAsUpdateTask.IsCompleted;

        await coordinator.CommitAsync();

        var postAsUpdateResult = await postAsUpdateTask.WaitAsync(
            GuardedNoOpIntegrationTestSupport.CompetingBumpCompletionTimeout
        );

        _completedAfterCompetingCommit = true;

        return postAsUpdateResult;
    }
}
