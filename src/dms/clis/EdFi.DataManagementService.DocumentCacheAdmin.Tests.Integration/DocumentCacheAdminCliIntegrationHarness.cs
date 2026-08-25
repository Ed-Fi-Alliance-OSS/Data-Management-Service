// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

[SetUpFixture]
public sealed class DocumentCacheAdminCliIntegrationHarnessSetUpFixture
{
    [OneTimeTearDown]
    public async Task TearDown() => await DocumentCacheAdminCliBaselineCache.DisposeAllAsync();
}

internal sealed class DocumentCacheAdminCliTarget : IAsyncDisposable
{
    private const long TargetDataStoreId = 1;

    private readonly PostgresqlGeneratedDdlTestDatabase? _postgresqlDatabase;
    private readonly IMssqlGeneratedDdlBaselineLease? _mssqlLease;
    private readonly bool _ownsPostgresqlDatabase;
    private readonly bool _ownsMssqlLease;
    private readonly string _tenantKey;
    private readonly long _dataStoreId;

    private DocumentCacheAdminCliTarget(
        RelationalProviderToken providerToken,
        string appSettingsDatastore,
        string connectionString,
        string apiSchemaDirectory,
        PostgresqlGeneratedDdlTestDatabase? postgresqlDatabase,
        IMssqlGeneratedDdlBaselineLease? mssqlLease,
        bool ownsPostgresqlDatabase,
        bool ownsMssqlLease,
        string tenantKey = "",
        long dataStoreId = TargetDataStoreId
    )
    {
        ProviderToken = providerToken;
        AppSettingsDatastore = appSettingsDatastore;
        ConnectionString = connectionString;
        ApiSchemaDirectory = apiSchemaDirectory;
        _postgresqlDatabase = postgresqlDatabase;
        _mssqlLease = mssqlLease;
        _ownsPostgresqlDatabase = ownsPostgresqlDatabase;
        _ownsMssqlLease = ownsMssqlLease;
        _tenantKey = tenantKey;
        _dataStoreId = dataStoreId;
        State = new DocumentCacheAdminCliStateInspector(
            providerToken,
            postgresqlDatabase,
            mssqlLease?.Database
        );
    }

    public string TenantKey => _tenantKey;

    public long DataStoreId => _dataStoreId;

    public RelationalProviderToken ProviderToken { get; }

    public string AppSettingsDatastore { get; }

    public string ConnectionString { get; }

    public string ApiSchemaDirectory { get; }

    public DocumentCacheAdminCliStateInspector State { get; }

    public static async Task<DocumentCacheAdminCliTarget> CreatePostgresqlAsync()
    {
        RequirePostgresqlConfigured();

        PostgresqlGeneratedDdlBaselineDatabase baseline =
            await DocumentCacheAdminCliBaselineCache.GetPostgresqlBaselineAsync();
        PostgresqlGeneratedDdlTestDatabase database = await baseline.CreateIsolatedDatabaseAsync();

        return new(
            RelationalProviderToken.Postgresql,
            RelationalProviderToken.Postgresql.Value,
            database.ConnectionString,
            DocumentCacheAdminCliFixture.Shared.ApiSchemaDirectory,
            database,
            mssqlLease: null,
            ownsPostgresqlDatabase: true,
            ownsMssqlLease: false
        );
    }

    public static async Task<DocumentCacheAdminCliTarget> CreateMssqlAsync()
    {
        RequireMssqlConfigured();

        IMssqlGeneratedDdlBaselineDatabase baseline =
            await DocumentCacheAdminCliBaselineCache.GetMssqlBaselineAsync();
        IMssqlGeneratedDdlBaselineLease lease = await baseline.AcquireRestoredDatabaseAsync();

        return new(
            RelationalProviderToken.SqlServer,
            DocumentCacheAdminCommandSurface.MssqlAppSettingsDatastoreValue,
            lease.Database.ConnectionString,
            DocumentCacheAdminCliFixture.Shared.ApiSchemaDirectory,
            postgresqlDatabase: null,
            lease,
            ownsPostgresqlDatabase: false,
            ownsMssqlLease: true
        );
    }

    public DocumentCacheAdminCliTarget CreateAlias(
        long dataStoreId,
        string tenantKey,
        string connectionString
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dataStoreId);
        ArgumentNullException.ThrowIfNull(tenantKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return new(
            ProviderToken,
            AppSettingsDatastore,
            connectionString,
            ApiSchemaDirectory,
            _postgresqlDatabase,
            _mssqlLease,
            ownsPostgresqlDatabase: false,
            ownsMssqlLease: false,
            tenantKey,
            dataStoreId
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsPostgresqlDatabase && _postgresqlDatabase is not null)
        {
            await _postgresqlDatabase.DisposeAsync();
        }

        if (_ownsMssqlLease && _mssqlLease is not null)
        {
            await _mssqlLease.DisposeAsync();
        }
    }

    private static void RequirePostgresqlConfigured()
    {
        try
        {
            _ = BaselineDatabaseConfiguration.DatabaseConnectionString;
        }
        catch (InvalidOperationException exception)
        {
            Assert.Ignore(
                "PostgreSQL generated-DDL integration tests require ConnectionStrings__DatabaseConnection. "
                    + exception.Message
            );
        }
    }

    private static void RequireMssqlConfigured()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require ConnectionStrings__MssqlAdmin.");
        }
    }
}

internal sealed class DocumentCacheAdminCliStateInspector(
    RelationalProviderToken providerToken,
    PostgresqlGeneratedDdlTestDatabase? postgresqlDatabase,
    MssqlGeneratedDdlTestDatabase? mssqlDatabase
)
{
    public async Task<DocumentCacheAdminCliLifecycleState> ReadLifecycleAsync()
    {
        IReadOnlyDictionary<string, object?> row = await QuerySingleRowAsync(
            """
            SELECT
                "ProjectionLifecycleState" AS "ProjectionLifecycleState",
                "CacheAheadRecoveryRequired" AS "CacheAheadRecoveryRequired"
            FROM dms."DocumentCacheState"
            WHERE "StateId" = 1
            """,
            """
            SELECT
                [ProjectionLifecycleState] AS [ProjectionLifecycleState],
                [CacheAheadRecoveryRequired] AS [CacheAheadRecoveryRequired]
            FROM [dms].[DocumentCacheState]
            WHERE [StateId] = 1
            """
        );

        return new(
            RequireString(row, "ProjectionLifecycleState"),
            RequireBoolean(row, "CacheAheadRecoveryRequired")
        );
    }

    public async Task<DocumentCacheAdminCliMutableCounts> ReadMutableCountsAsync()
    {
        IReadOnlyDictionary<string, object?> row = await QuerySingleRowAsync(
            """
            SELECT
                (SELECT COUNT(*) FROM dms."DocumentCache") AS "DocumentCacheRows",
                (SELECT COUNT(*) FROM dms."DocumentProjectionWork") AS "DocumentProjectionWorkRows"
            """,
            """
            SELECT
                (SELECT COUNT(*) FROM [dms].[DocumentCache]) AS [DocumentCacheRows],
                (SELECT COUNT(*) FROM [dms].[DocumentProjectionWork]) AS [DocumentProjectionWorkRows]
            """
        );

        return new(RequireInt64(row, "DocumentCacheRows"), RequireInt64(row, "DocumentProjectionWorkRows"));
    }

    public async Task<long> ReadCanonicalDocumentCountAsync()
    {
        IReadOnlyDictionary<string, object?> row = await QuerySingleRowAsync(
            """
            SELECT COUNT(*) AS "DocumentRows"
            FROM dms."Document"
            """,
            """
            SELECT COUNT(*) AS [DocumentRows]
            FROM [dms].[Document]
            """
        );

        return RequireInt64(row, "DocumentRows");
    }

    public async Task<DateTime?> ReadOldestWorkFirstEnqueuedAtAsync()
    {
        IReadOnlyDictionary<string, object?> row = await QuerySingleRowAsync(
            """
            SELECT MIN("FirstEnqueuedAt") AS "FirstEnqueuedAt"
            FROM dms."DocumentProjectionWork"
            """,
            """
            SELECT MIN([FirstEnqueuedAt]) AS [FirstEnqueuedAt]
            FROM [dms].[DocumentProjectionWork]
            """
        );

        object? value = row["FirstEnqueuedAt"];
        return value switch
        {
            null => null,
            DateTime dateTime => dateTime,
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
            _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
        };
    }

    public async Task<string> ReadPhysicalSourceFingerprintAsync()
    {
        IReadOnlyDictionary<string, object?> row = await QuerySingleRowAsync(
            """
            SELECT "SourceIdentity" AS "SourceIdentity"
            FROM dms."DataStoreIdentity"
            WHERE "DataStoreIdentitySingletonId" = 1
            """,
            """
            SELECT [SourceIdentity] AS [SourceIdentity]
            FROM [dms].[DataStoreIdentity]
            WHERE [DataStoreIdentitySingletonId] = 1
            """
        );

        Guid sourceIdentity = RequireGuid(row, "SourceIdentity");
        return DocumentCachePhysicalSourceFingerprintCalculator.Compute(providerToken, sourceIdentity).Value;
    }

    public Task SetLifecycleAsync(string lifecycleState, bool cacheAheadRecoveryRequired)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lifecycleState);

        return postgresqlDatabase is not null
            ? postgresqlDatabase.ExecuteNonQueryAsync(
                """
                UPDATE "dms"."DocumentCacheState"
                SET "ProjectionLifecycleState" = @lifecycleState,
                    "CacheAheadRecoveryRequired" = @cacheAheadRecoveryRequired
                WHERE "StateId" = 1;
                """,
                new NpgsqlParameter("lifecycleState", NpgsqlDbType.Varchar) { Value = lifecycleState },
                new NpgsqlParameter("cacheAheadRecoveryRequired", NpgsqlDbType.Boolean)
                {
                    Value = cacheAheadRecoveryRequired,
                }
            )
            : (
                mssqlDatabase ?? throw new InvalidOperationException("No target database is configured.")
            ).ExecuteNonQueryAsync(
                """
                UPDATE [dms].[DocumentCacheState]
                SET [ProjectionLifecycleState] = @lifecycleState,
                    [CacheAheadRecoveryRequired] = @cacheAheadRecoveryRequired
                WHERE [StateId] = 1;
                """,
                new Microsoft.Data.SqlClient.SqlParameter("lifecycleState", lifecycleState),
                new Microsoft.Data.SqlClient.SqlParameter(
                    "cacheAheadRecoveryRequired",
                    cacheAheadRecoveryRequired
                )
            );
    }

    public async Task SetMssqlReadCommittedSnapshotAsync(bool enabled)
    {
        MssqlGeneratedDdlTestDatabase database = RequireMssqlDatabase();
        SqlConnection.ClearAllPools();

        string quotedDatabaseName = MssqlTestDatabaseHelper.QuoteIdentifier(database.DatabaseName);
        string enabledSql = enabled ? "ON" : "OFF";

        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"""
            ALTER DATABASE {quotedDatabaseName}
            SET READ_COMMITTED_SNAPSHOT {enabledSql} WITH ROLLBACK IMMEDIATE;
            """
        );

        SqlConnection.ClearAllPools();
    }

    public Task<bool> ReadMssqlReadCommittedSnapshotEnabledAsync()
    {
        MssqlGeneratedDdlTestDatabase database = RequireMssqlDatabase();

        return ReadMssqlAdminBitAsync(
            $"""
            SELECT CONVERT(int, [is_read_committed_snapshot_on])
            FROM [sys].[databases]
            WHERE [name] = N'{MssqlTestDatabaseHelper.EscapeSqlLiteral(database.DatabaseName)}';
            """
        );
    }

    public async Task SetMssqlNestedTriggersAsync(bool enabled)
    {
        RequireMssqlDatabase();
        int enabledValue = enabled ? 1 : 0;

        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"""
            EXEC sp_configure 'nested triggers', {enabledValue};
            RECONFIGURE;
            """
        );

        SqlConnection.ClearAllPools();
    }

    public Task<bool> ReadMssqlNestedTriggersEnabledAsync()
    {
        RequireMssqlDatabase();

        return ReadMssqlAdminBitAsync(
            """
            SELECT CONVERT(int, [value_in_use])
            FROM [sys].[configurations]
            WHERE [name] = N'nested triggers';
            """
        );
    }

    public async Task<DocumentCacheAdminCliSeededDocument> InsertPostgresqlCanonicalDocumentAsync(
        long contentVersion = 10
    )
    {
        PostgresqlGeneratedDdlTestDatabase database = RequirePostgresqlDatabase();
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        Guid documentUuid = Guid.NewGuid();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await database.QueryRowsAsync(
            """
            WITH resource_key AS (
                SELECT "ResourceKeyId"
                FROM "dms"."ResourceKey"
                ORDER BY "ResourceKeyId"
                LIMIT 1
            )
            INSERT INTO "dms"."Document" (
                "DocumentUuid",
                "ResourceKeyId",
                "ContentVersion",
                "ContentLastModifiedAt"
            )
            SELECT
                @documentUuid,
                resource_key."ResourceKeyId",
                @contentVersion,
                @observedAt
            FROM resource_key
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = documentUuid },
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = contentVersion },
            new NpgsqlParameter("observedAt", NpgsqlDbType.TimestampTz) { Value = observedAt }
        );

        long documentId = RequireInt64(rows.Single(), "DocumentId");
        return new(documentId, documentUuid, contentVersion);
    }

    public async Task<DocumentCacheAdminCliSeededDocument> InsertMssqlCanonicalDocumentAsync(
        long contentVersion = 10
    )
    {
        MssqlGeneratedDdlTestDatabase database = RequireMssqlDatabase();
        DateTime observedAt = DateTime.UtcNow;
        Guid documentUuid = Guid.NewGuid();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await database.QueryRowsAsync(
            """
            DECLARE @inserted TABLE ([DocumentId] bigint NOT NULL);

            WITH [resource_key] AS (
                SELECT TOP (1) [ResourceKeyId]
                FROM [dms].[ResourceKey]
                ORDER BY [ResourceKeyId]
            )
            INSERT INTO [dms].[Document] (
                [DocumentUuid],
                [ResourceKeyId],
                [ContentVersion],
                [ContentLastModifiedAt]
            )
            OUTPUT inserted.[DocumentId]
            INTO @inserted
            SELECT
                @documentUuid,
                [resource_key].[ResourceKeyId],
                @contentVersion,
                @observedAt
            FROM [resource_key];

            SELECT [DocumentId]
            FROM @inserted;
            """,
            new SqlParameter("documentUuid", documentUuid),
            new SqlParameter("contentVersion", contentVersion),
            new SqlParameter("observedAt", observedAt)
        );

        long documentId = RequireInt64(rows.Single(), "DocumentId");
        return new(documentId, documentUuid, contentVersion);
    }

    public Task InsertPostgresqlDocumentCacheAsync(
        DocumentCacheAdminCliSeededDocument document,
        long? cacheContentVersion = null,
        string documentJson = """{"seeded":true}"""
    )
    {
        ArgumentNullException.ThrowIfNull(document);

        return RequirePostgresqlDatabase()
            .ExecuteNonQueryAsync(
                """
                INSERT INTO "dms"."DocumentCache" (
                    "DocumentId",
                    "DocumentUuid",
                    "ProjectName",
                    "ResourceName",
                    "ResourceVersion",
                    "ContentVersion",
                    "StreamEtag",
                    "LastModifiedAt",
                    "DocumentJson",
                    "ComputedAt"
                )
                SELECT
                    document."DocumentId",
                    document."DocumentUuid",
                    resource_key."ProjectName",
                    resource_key."ResourceName",
                    resource_key."ResourceVersion",
                    @contentVersion,
                    @streamEtag,
                    document."ContentLastModifiedAt",
                    @documentJson::jsonb,
                    @computedAt
                FROM "dms"."Document" AS document
                INNER JOIN "dms"."ResourceKey" AS resource_key
                    ON resource_key."ResourceKeyId" = document."ResourceKeyId"
                WHERE document."DocumentId" = @documentId;
                """,
                new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = document.DocumentId },
                new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint)
                {
                    Value = cacheContentVersion ?? document.ContentVersion,
                },
                new NpgsqlParameter("streamEtag", NpgsqlDbType.Varchar)
                {
                    Value = $"cli-etag-{document.DocumentId}",
                },
                new NpgsqlParameter("documentJson", NpgsqlDbType.Jsonb) { Value = documentJson },
                new NpgsqlParameter("computedAt", NpgsqlDbType.TimestampTz) { Value = DateTimeOffset.UtcNow }
            );
    }

    public Task InsertMssqlDocumentCacheAsync(
        DocumentCacheAdminCliSeededDocument document,
        long? cacheContentVersion = null,
        string documentJson = """{"seeded":true}"""
    )
    {
        ArgumentNullException.ThrowIfNull(document);

        return RequireMssqlDatabase()
            .ExecuteNonQueryAsync(
                """
                INSERT INTO [dms].[DocumentCache] (
                    [DocumentId],
                    [DocumentUuid],
                    [ProjectName],
                    [ResourceName],
                    [ResourceVersion],
                    [ContentVersion],
                    [StreamEtag],
                    [LastModifiedAt],
                    [DocumentJson],
                    [ComputedAt]
                )
                SELECT
                    [document].[DocumentId],
                    [document].[DocumentUuid],
                    [resource_key].[ProjectName],
                    [resource_key].[ResourceName],
                    [resource_key].[ResourceVersion],
                    @contentVersion,
                    @streamEtag,
                    [document].[ContentLastModifiedAt],
                    @documentJson,
                    @computedAt
                FROM [dms].[Document] AS [document]
                INNER JOIN [dms].[ResourceKey] AS [resource_key]
                    ON [resource_key].[ResourceKeyId] = [document].[ResourceKeyId]
                WHERE [document].[DocumentId] = @documentId;
                """,
                new SqlParameter("documentId", document.DocumentId),
                new SqlParameter("contentVersion", cacheContentVersion ?? document.ContentVersion),
                new SqlParameter("streamEtag", $"cli-etag-{document.DocumentId}"),
                new SqlParameter("documentJson", documentJson),
                new SqlParameter("computedAt", DateTime.UtcNow)
            );
    }

    public Task InsertPostgresqlProjectionWorkAsync(
        DocumentCacheAdminCliSeededDocument document,
        DateTimeOffset? firstEnqueuedAt = null,
        long? requiredContentVersion = null
    )
    {
        ArgumentNullException.ThrowIfNull(document);

        DateTimeOffset enqueuedAt = firstEnqueuedAt ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        return RequirePostgresqlDatabase()
            .ExecuteNonQueryAsync(
                """
                INSERT INTO "dms"."DocumentProjectionWork" (
                    "DocumentId",
                    "RequiredContentVersion",
                    "FirstEnqueuedAt",
                    "LastEnqueuedAt"
                )
                VALUES (
                    @documentId,
                    @requiredContentVersion,
                    @firstEnqueuedAt,
                    @lastEnqueuedAt
                );
                """,
                new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = document.DocumentId },
                new NpgsqlParameter("requiredContentVersion", NpgsqlDbType.Bigint)
                {
                    Value = requiredContentVersion ?? document.ContentVersion,
                },
                new NpgsqlParameter("firstEnqueuedAt", NpgsqlDbType.TimestampTz) { Value = enqueuedAt },
                new NpgsqlParameter("lastEnqueuedAt", NpgsqlDbType.TimestampTz)
                {
                    Value = enqueuedAt.AddSeconds(5),
                }
            );
    }

    public Task InsertMssqlProjectionWorkAsync(
        DocumentCacheAdminCliSeededDocument document,
        DateTime? firstEnqueuedAt = null,
        long? requiredContentVersion = null
    )
    {
        ArgumentNullException.ThrowIfNull(document);

        DateTime enqueuedAt = firstEnqueuedAt ?? DateTime.UtcNow.AddMinutes(-5);
        return RequireMssqlDatabase()
            .ExecuteNonQueryAsync(
                """
                INSERT INTO [dms].[DocumentProjectionWork] (
                    [DocumentId],
                    [RequiredContentVersion],
                    [FirstEnqueuedAt],
                    [LastEnqueuedAt]
                )
                VALUES (
                    @documentId,
                    @requiredContentVersion,
                    @firstEnqueuedAt,
                    @lastEnqueuedAt
                );
                """,
                new SqlParameter("documentId", document.DocumentId),
                new SqlParameter("requiredContentVersion", requiredContentVersion ?? document.ContentVersion),
                new SqlParameter("firstEnqueuedAt", enqueuedAt),
                new SqlParameter("lastEnqueuedAt", enqueuedAt.AddSeconds(5))
            );
    }

    public async Task<DocumentCacheAdminCliSeededDocument> InsertPostgresqlDescriptorDocumentAsync(
        string codeValue,
        long contentVersion = 10
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeValue);

        PostgresqlGeneratedDdlTestDatabase database = RequirePostgresqlDatabase();
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        Guid documentUuid = Guid.NewGuid();
        const string descriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";
        string uri = $"{descriptorNamespace}#{codeValue}";
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await database.QueryRowsAsync(
            """
            WITH resource_key AS (
                SELECT "ResourceKeyId"
                FROM "dms"."ResourceKey"
                WHERE "ProjectName" = 'Ed-Fi'
                  AND "ResourceName" = 'SchoolTypeDescriptor'
            ),
            inserted_document AS (
                INSERT INTO "dms"."Document" (
                    "DocumentUuid",
                    "ResourceKeyId",
                    "ContentVersion",
                    "ContentLastModifiedAt"
                )
                SELECT
                    @documentUuid,
                    resource_key."ResourceKeyId",
                    @contentVersion,
                    @observedAt
                FROM resource_key
                RETURNING "DocumentId", "ResourceKeyId", "ContentVersion"
            )
            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Discriminator",
                "Uri",
                "ContentVersion",
                "ContentLastModifiedAt"
            )
            SELECT
                inserted_document."DocumentId",
                inserted_document."ResourceKeyId",
                @namespace,
                @codeValue,
                @shortDescription,
                'SchoolTypeDescriptor',
                @uri,
                inserted_document."ContentVersion",
                @observedAt
            FROM inserted_document
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = documentUuid },
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = contentVersion },
            new NpgsqlParameter("observedAt", NpgsqlDbType.TimestampTz) { Value = observedAt },
            new NpgsqlParameter("namespace", NpgsqlDbType.Varchar) { Value = descriptorNamespace },
            new NpgsqlParameter("codeValue", NpgsqlDbType.Varchar) { Value = codeValue },
            new NpgsqlParameter("shortDescription", NpgsqlDbType.Varchar) { Value = codeValue },
            new NpgsqlParameter("uri", NpgsqlDbType.Varchar) { Value = uri }
        );

        long documentId = RequireInt64(rows.Single(), "DocumentId");
        return new(documentId, documentUuid, contentVersion);
    }

    public async Task<DocumentCacheAdminCliSeededDocument> InsertMssqlDescriptorDocumentAsync(
        string codeValue,
        long contentVersion = 10
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeValue);

        MssqlGeneratedDdlTestDatabase database = RequireMssqlDatabase();
        DateTime observedAt = DateTime.UtcNow;
        Guid documentUuid = Guid.NewGuid();
        const string descriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";
        string uri = $"{descriptorNamespace}#{codeValue}";
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await database.QueryRowsAsync(
            """
            DECLARE @inserted TABLE (
                [DocumentId] bigint NOT NULL,
                [ResourceKeyId] smallint NOT NULL,
                [ContentVersion] bigint NOT NULL
            );

            WITH [resource_key] AS (
                SELECT [ResourceKeyId]
                FROM [dms].[ResourceKey]
                WHERE [ProjectName] = N'Ed-Fi'
                  AND [ResourceName] = N'SchoolTypeDescriptor'
            )
            INSERT INTO [dms].[Document] (
                [DocumentUuid],
                [ResourceKeyId],
                [ContentVersion],
                [ContentLastModifiedAt]
            )
            OUTPUT inserted.[DocumentId], inserted.[ResourceKeyId], inserted.[ContentVersion]
            INTO @inserted
            SELECT
                @documentUuid,
                [resource_key].[ResourceKeyId],
                @contentVersion,
                @observedAt
            FROM [resource_key];

            INSERT INTO [dms].[Descriptor] (
                [DocumentId],
                [ResourceKeyId],
                [Namespace],
                [CodeValue],
                [ShortDescription],
                [Discriminator],
                [Uri],
                [ContentVersion],
                [ContentLastModifiedAt]
            )
            SELECT
                [inserted_document].[DocumentId],
                [inserted_document].[ResourceKeyId],
                @namespace,
                @codeValue,
                @shortDescription,
                N'SchoolTypeDescriptor',
                @uri,
                [inserted_document].[ContentVersion],
                @observedAt
            FROM @inserted AS [inserted_document];

            SELECT [DocumentId]
            FROM @inserted;
            """,
            new SqlParameter("documentUuid", documentUuid),
            new SqlParameter("contentVersion", contentVersion),
            new SqlParameter("observedAt", observedAt),
            new SqlParameter("namespace", descriptorNamespace),
            new SqlParameter("codeValue", codeValue),
            new SqlParameter("shortDescription", codeValue),
            new SqlParameter("uri", uri)
        );

        long documentId = RequireInt64(rows.Single(), "DocumentId");
        return new(documentId, documentUuid, contentVersion);
    }

    public Task ClearPostgresqlProjectionWorkAsync() =>
        RequirePostgresqlDatabase().ExecuteNonQueryAsync("""DELETE FROM "dms"."DocumentProjectionWork";""");

    public Task ClearMssqlProjectionWorkAsync() =>
        RequireMssqlDatabase().ExecuteNonQueryAsync("""DELETE FROM [dms].[DocumentProjectionWork];""");

    public async Task<IReadOnlyDictionary<long, long>> ReadPostgresqlWorkVersionsByDocumentIdAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await RequirePostgresqlDatabase()
            .QueryRowsAsync(
                """
                SELECT "DocumentId", "RequiredContentVersion"
                FROM "dms"."DocumentProjectionWork"
                ORDER BY "DocumentId";
                """
            );

        return rows.ToDictionary(
            row => RequireInt64(row, "DocumentId"),
            row => RequireInt64(row, "RequiredContentVersion")
        );
    }

    public async Task<IReadOnlyDictionary<long, long>> ReadMssqlWorkVersionsByDocumentIdAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await RequireMssqlDatabase()
            .QueryRowsAsync(
                """
                SELECT [DocumentId], [RequiredContentVersion]
                FROM [dms].[DocumentProjectionWork]
                ORDER BY [DocumentId];
                """
            );

        return rows.ToDictionary(
            row => RequireInt64(row, "DocumentId"),
            row => RequireInt64(row, "RequiredContentVersion")
        );
    }

    public async Task<IReadOnlyDictionary<long, string>> ReadPostgresqlCachedJsonByDocumentIdAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await RequirePostgresqlDatabase()
            .QueryRowsAsync(
                """
                SELECT "DocumentId", "DocumentJson"::text AS "DocumentJson"
                FROM "dms"."DocumentCache"
                ORDER BY "DocumentId";
                """
            );

        return rows.ToDictionary(
            row => RequireInt64(row, "DocumentId"),
            row => RequireString(row, "DocumentJson")
        );
    }

    public async Task<IReadOnlyDictionary<long, string>> ReadMssqlCachedJsonByDocumentIdAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await RequireMssqlDatabase()
            .QueryRowsAsync(
                """
                SELECT [DocumentId], [DocumentJson] AS [DocumentJson]
                FROM [dms].[DocumentCache]
                ORDER BY [DocumentId];
                """
            );

        return rows.ToDictionary(
            row => RequireInt64(row, "DocumentId"),
            row => RequireString(row, "DocumentJson")
        );
    }

    public Task<DocumentCacheAdminCliPostgresqlInsertTransaction> BeginPostgresqlCanonicalInsertTransactionAsync(
        long contentVersion = 10
    ) =>
        DocumentCacheAdminCliPostgresqlInsertTransaction.BeginAsync(
            RequirePostgresqlDatabase().ConnectionString,
            contentVersion
        );

    public Task<DocumentCacheAdminCliPostgresqlDocumentCacheLockTransaction> BeginPostgresqlDocumentCacheLockTransactionAsync(
        long documentId
    ) =>
        DocumentCacheAdminCliPostgresqlDocumentCacheLockTransaction.BeginAsync(
            RequirePostgresqlDatabase().ConnectionString,
            documentId
        );

    public Task<DocumentCacheAdminCliMssqlInsertTransaction> BeginMssqlCanonicalInsertTransactionAsync(
        long contentVersion = 10
    ) =>
        DocumentCacheAdminCliMssqlInsertTransaction.BeginAsync(
            RequireMssqlDatabase().ConnectionString,
            contentVersion
        );

    public async Task<long> ReadAdministrativeMutexGrantedCountAsync()
    {
        IReadOnlyDictionary<string, object?> row = await QuerySingleRowAsync(
            """
            SELECT COUNT(*) AS "GrantedLockCount"
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND database = (
                  SELECT database.oid
                  FROM pg_database AS database
                  WHERE database.datname = current_database()
              )
              AND classid = 811646948::oid
              AND objid = (
                  SELECT database.oid
                  FROM pg_database AS database
                  WHERE database.datname = current_database()
              )
              AND mode = 'ExclusiveLock'
              AND granted;
            """,
            """
            SELECT COUNT(*) AS [GrantedLockCount]
            FROM [sys].[dm_tran_locks]
            WHERE [resource_type] = N'APPLICATION'
              AND [resource_database_id] = DB_ID()
              AND [request_mode] = N'X'
              AND [request_status] = N'GRANT'
              AND [resource_description] LIKE N'%EdFi.DMS.DocumentProjection.Admi%';
            """
        );

        return RequireInt64(row, "GrantedLockCount");
    }

    public async Task<long> ReadAdministrativeMutexWaitingCountAsync()
    {
        IReadOnlyDictionary<string, object?> row = await QuerySingleRowAsync(
            """
            SELECT COUNT(*) AS "WaitingLockCount"
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND database = (
                  SELECT database.oid
                  FROM pg_database AS database
                  WHERE database.datname = current_database()
              )
              AND classid = 811646948::oid
              AND objid = (
                  SELECT database.oid
                  FROM pg_database AS database
                  WHERE database.datname = current_database()
              )
              AND mode = 'ExclusiveLock'
              AND NOT granted;
            """,
            """
            SELECT COUNT(*) AS [WaitingLockCount]
            FROM [sys].[dm_tran_locks]
            WHERE [resource_type] = N'APPLICATION'
              AND [resource_database_id] = DB_ID()
              AND [request_mode] = N'X'
              AND [request_status] = N'WAIT'
              AND [resource_description] LIKE N'%EdFi.DMS.DocumentProjection.Admi%';
            """
        );

        return RequireInt64(row, "WaitingLockCount");
    }

    private PostgresqlGeneratedDdlTestDatabase RequirePostgresqlDatabase() =>
        postgresqlDatabase
        ?? throw new InvalidOperationException("This helper is only available for PostgreSQL targets.");

    private MssqlGeneratedDdlTestDatabase RequireMssqlDatabase() =>
        mssqlDatabase
        ?? throw new InvalidOperationException("This helper is only available for SQL Server targets.");

    private static async Task<bool> ReadMssqlAdminBitAsync(string sql)
    {
        await using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync();
        return value is not null && value != DBNull.Value && Convert.ToInt32(value) == 1;
    }

    private async Task<IReadOnlyDictionary<string, object?>> QuerySingleRowAsync(
        string postgresqlSql,
        string mssqlSql
    )
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = postgresqlDatabase is not null
            ? await postgresqlDatabase.QueryRowsAsync(postgresqlSql)
            : await (
                mssqlDatabase ?? throw new InvalidOperationException("No target database is configured.")
            ).QueryRowsAsync(mssqlSql);

        if (rows.Count != 1)
        {
            throw new InvalidOperationException($"Expected one state row but received {rows.Count}.");
        }

        return rows[0];
    }

    private static string RequireString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        row[columnName]?.ToString()
        ?? throw new InvalidOperationException($"Expected non-null string column '{columnName}'.");

    private static bool RequireBoolean(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        object? value = row[columnName];
        return value switch
        {
            bool boolean => boolean,
            byte byteValue => byteValue != 0,
            short shortValue => shortValue != 0,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            null => throw new InvalidOperationException($"Expected non-null boolean column '{columnName}'."),
            _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
        };
    }

    private static long RequireInt64(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        object? value = row[columnName];
        return value is not null
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Expected non-null integer column '{columnName}'.");
    }

    private static Guid RequireGuid(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        object? value = row[columnName];
        return value switch
        {
            Guid guid => guid,
            string text when Guid.TryParseExact(text, "D", out Guid guid) => guid,
            null => throw new InvalidOperationException($"Expected non-null UUID column '{columnName}'."),
            _ => throw new InvalidOperationException(
                $"Expected UUID column '{columnName}' but received {value.GetType().Name}."
            ),
        };
    }
}

internal sealed record DocumentCacheAdminCliLifecycleState(
    string ProjectionLifecycleState,
    bool CacheAheadRecoveryRequired
);

internal sealed record DocumentCacheAdminCliMutableCounts(long DocumentCacheRows, long WorkRows);

internal sealed record DocumentCacheAdminCliSeededDocument(
    long DocumentId,
    Guid DocumentUuid,
    long ContentVersion
);

internal sealed class DocumentCacheAdminCliPostgresqlInsertTransaction : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private bool _completed;

    private DocumentCacheAdminCliPostgresqlInsertTransaction(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long documentId
    )
    {
        _connection = connection;
        _transaction = transaction;
        DocumentId = documentId;
    }

    public long DocumentId { get; }

    public static async Task<DocumentCacheAdminCliPostgresqlInsertTransaction> BeginAsync(
        string connectionString,
        long contentVersion
    )
    {
        NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

        try
        {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                WITH resource_key AS (
                    SELECT "ResourceKeyId"
                    FROM "dms"."ResourceKey"
                    ORDER BY "ResourceKeyId"
                    LIMIT 1
                )
                INSERT INTO "dms"."Document" (
                    "DocumentUuid",
                    "ResourceKeyId",
                    "ContentVersion",
                    "ContentLastModifiedAt"
                )
                SELECT
                    @documentUuid,
                    resource_key."ResourceKeyId",
                    @contentVersion,
                    @observedAt
                FROM resource_key
                RETURNING "DocumentId";
                """;
            command.Parameters.Add(
                new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() }
            );
            command.Parameters.Add(
                new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = contentVersion }
            );
            command.Parameters.Add(
                new NpgsqlParameter("observedAt", NpgsqlDbType.TimestampTz) { Value = DateTimeOffset.UtcNow }
            );

            object? result = await command.ExecuteScalarAsync();
            long documentId = result is not null
                ? Convert.ToInt64(result, CultureInfo.InvariantCulture)
                : throw new InvalidOperationException("Expected inserted DocumentId.");

            return new(connection, transaction, documentId);
        }
        catch
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task CommitAsync()
    {
        await _transaction.CommitAsync();
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            try
            {
                await _transaction.RollbackAsync();
            }
            catch (InvalidOperationException)
            {
                // Best-effort cleanup for a transaction already completed by the provider.
            }
        }

        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

internal sealed class DocumentCacheAdminCliPostgresqlDocumentCacheLockTransaction : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private bool _disposed;

    private DocumentCacheAdminCliPostgresqlDocumentCacheLockTransaction(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        _connection = connection;
        _transaction = transaction;
    }

    public static async Task<DocumentCacheAdminCliPostgresqlDocumentCacheLockTransaction> BeginAsync(
        string connectionString,
        long documentId
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(documentId);

        NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

        try
        {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT "DocumentId"
                FROM "dms"."DocumentCache"
                WHERE "DocumentId" = @documentId
                FOR UPDATE;
                """;
            command.Parameters.Add(
                new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId }
            );

            object? result = await command.ExecuteScalarAsync();
            if (result is null || result == DBNull.Value)
            {
                throw new InvalidOperationException("Expected a DocumentCache row to lock.");
            }

            return new(connection, transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await _transaction.RollbackAsync();
        }
        catch (InvalidOperationException)
        {
            // Best-effort cleanup for a provider-released transaction.
        }

        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

internal sealed class DocumentCacheAdminCliMssqlInsertTransaction : IAsyncDisposable
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private bool _completed;

    private DocumentCacheAdminCliMssqlInsertTransaction(
        SqlConnection connection,
        SqlTransaction transaction,
        long documentId
    )
    {
        _connection = connection;
        _transaction = transaction;
        DocumentId = documentId;
    }

    public long DocumentId { get; }

    public static async Task<DocumentCacheAdminCliMssqlInsertTransaction> BeginAsync(
        string connectionString,
        long contentVersion
    )
    {
        SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            await using SqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DECLARE @inserted TABLE ([DocumentId] bigint NOT NULL);

                WITH [resource_key] AS (
                    SELECT TOP (1) [ResourceKeyId]
                    FROM [dms].[ResourceKey]
                    ORDER BY [ResourceKeyId]
                )
                INSERT INTO [dms].[Document] (
                    [DocumentUuid],
                    [ResourceKeyId],
                    [ContentVersion],
                    [ContentLastModifiedAt]
                )
                OUTPUT inserted.[DocumentId]
                INTO @inserted
                SELECT
                    @documentUuid,
                    [resource_key].[ResourceKeyId],
                    @contentVersion,
                    @observedAt
                FROM [resource_key];

                SELECT [DocumentId]
                FROM @inserted;
                """;
            command.Parameters.Add(new SqlParameter("documentUuid", Guid.NewGuid()));
            command.Parameters.Add(new SqlParameter("contentVersion", contentVersion));
            command.Parameters.Add(new SqlParameter("observedAt", DateTime.UtcNow));

            object? result = await command.ExecuteScalarAsync();
            long documentId = result is not null
                ? Convert.ToInt64(result, CultureInfo.InvariantCulture)
                : throw new InvalidOperationException("Expected inserted DocumentId.");

            return new(connection, transaction, documentId);
        }
        catch
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task CommitAsync()
    {
        await _transaction.CommitAsync();
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            try
            {
                await _transaction.RollbackAsync();
            }
            catch (InvalidOperationException)
            {
                // Best-effort cleanup for a transaction already completed by the provider.
            }
        }

        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

internal sealed class DocumentCacheAdminCliProcessHarness : IAsyncDisposable
{
    private const string ConfigurationServiceClientId = "document-cache-admin-cli-integration";
    private const string ConfigurationServiceScope = "edfi_admin_api/full_access";
    private const string ConfigurationServiceSecret = "secret-from-environment";
    private const string EncryptionKey = "DocumentCacheAdminCliHarnessEncryptionKey";
    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    private readonly string _tempDirectory;
    private readonly string _settingsPath;
    private readonly string _secretFromEnvironment = ConfigurationServiceSecret;

    private DocumentCacheAdminCliProcessHarness(
        DocumentCacheAdminCliTarget target,
        DocumentCacheAdminTestConfigurationService configurationService,
        string tempDirectory,
        string settingsPath
    )
    {
        Target = target;
        ConfigurationService = configurationService;
        _tempDirectory = tempDirectory;
        _settingsPath = settingsPath;
    }

    public DocumentCacheAdminCliTarget Target { get; }

    public DocumentCacheAdminTestConfigurationService ConfigurationService { get; }

    public string SecretFromEnvironment => _secretFromEnvironment;

    public static Task<DocumentCacheAdminCliProcessHarness> CreateAsync(DocumentCacheAdminCliTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "dms-document-cache-admin-cli",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(tempDirectory);

        DocumentCacheAdminTestConfigurationService configurationService =
            DocumentCacheAdminTestConfigurationService.Start(target, EncryptionKey);
        string settingsPath = Path.Combine(tempDirectory, "appsettings.cli-harness.json");
        File.WriteAllText(settingsPath, BuildSettings(target, configurationService.BaseUri));

        return Task.FromResult(
            new DocumentCacheAdminCliProcessHarness(target, configurationService, tempDirectory, settingsPath)
        );
    }

    public async Task<DocumentCacheAdminCliProcessResult> RunAsync(params string[] arguments)
    {
        await using DocumentCacheAdminCliRunningProcess runningProcess = Start(arguments);
        return await runningProcess.WaitForExitAsync(TimeSpan.FromSeconds(120));
    }

    public DocumentCacheAdminCliRunningProcess Start(params string[] arguments)
    {
        var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot(),
        };

        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--project");
        process.StartInfo.ArgumentList.Add(ToolProjectPath());
        process.StartInfo.ArgumentList.Add("--no-build");
        process.StartInfo.ArgumentList.Add("--");
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.ArgumentList.Add(DocumentCacheAdminCommandSurface.SettingsOptionName);
        process.StartInfo.ArgumentList.Add(_settingsPath);

        process.StartInfo.Environment["DOTNET_ENVIRONMENT"] = "";
        process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "";
        process.StartInfo.Environment["AppSettings__Datastore"] = Target.AppSettingsDatastore;
        process.StartInfo.Environment["AppSettings__UseApiSchemaPath"] = "true";
        process.StartInfo.Environment["AppSettings__ApiSchemaPath"] = Target.ApiSchemaDirectory;
        process.StartInfo.Environment["ConfigurationServiceSettings__BaseUrl"] =
            ConfigurationService.BaseUri.ToString();
        process.StartInfo.Environment["ConfigurationServiceSettings__ClientId"] =
            ConfigurationServiceClientId;
        process.StartInfo.Environment["ConfigurationServiceSettings__ClientSecret"] =
            ConfigurationServiceSecret;
        process.StartInfo.Environment["ConfigurationServiceSettings__Scope"] = ConfigurationServiceScope;
        process.StartInfo.Environment["ConfigurationServiceSettings__EncryptionKey"] = EncryptionKey;

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Unable to start DocumentCacheAdmin process.");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();

        return new(process, standardOutput, standardError);
    }

    public async ValueTask DisposeAsync()
    {
        await ConfigurationService.DisposeAsync();

        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup for diagnostics-friendly temp files.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup for diagnostics-friendly temp files.
        }
    }

    private static string BuildSettings(DocumentCacheAdminCliTarget target, Uri configurationServiceBaseUri)
    {
        var settings = new JsonObject
        {
            ["AppSettings"] = new JsonObject
            {
                ["Datastore"] = target.AppSettingsDatastore,
                ["UseApiSchemaPath"] = true,
                ["ApiSchemaPath"] = target.ApiSchemaDirectory,
                ["AllowIdentityUpdateOverrides"] = string.Empty,
                ["MaximumPageSize"] = 500,
                ["DefaultPartitionCount"] = 10,
                ["BypassAuthorization"] = true,
            },
            ["ConfigurationServiceSettings"] = new JsonObject
            {
                ["BaseUrl"] = configurationServiceBaseUri.ToString(),
                ["ClientId"] = ConfigurationServiceClientId,
                ["Scope"] = ConfigurationServiceScope,
                ["EncryptionKey"] = EncryptionKey,
            },
            ["DataManagement"] = new JsonObject
            {
                ["DocumentCache"] = new JsonObject
                {
                    ["ReadAcceleration"] = new JsonObject { ["Enabled"] = false },
                    ["Projector"] = new JsonObject
                    {
                        ["PollInterval"] = "00:00:05",
                        ["PageSize"] = 100,
                        ["MaxConcurrentTargets"] = 1,
                        ["FailureBackoff"] = "00:00:05",
                        ["BaselineHighWaterMark"] = 100,
                    },
                    ["Administration"] = new JsonObject { ["WorkflowTimeout"] = "00:05:00" },
                    ["Status"] = new JsonObject
                    {
                        ["StatusObservationTimeout"] = "00:00:01",
                        ["EndpointTimeout"] = "00:00:05",
                    },
                },
            },
        };

        return settings.ToJsonString(_writeOptions);
    }

    private static string ToolProjectPath() =>
        Path.Combine(
            RepositoryRoot(),
            "src",
            "dms",
            "clis",
            "EdFi.DataManagementService.DocumentCacheAdmin",
            "EdFi.DataManagementService.DocumentCacheAdmin.csproj"
        );

    private static string RepositoryRoot() =>
        FixturePathResolver.FindRepositoryRoot(AppContext.BaseDirectory);
}

internal sealed class DocumentCacheAdminCliRunningProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _standardOutput;
    private readonly Task<string> _standardError;
    private DocumentCacheAdminCliProcessResult? _result;

    public DocumentCacheAdminCliRunningProcess(
        Process process,
        Task<string> standardOutput,
        Task<string> standardError
    )
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _standardOutput = standardOutput ?? throw new ArgumentNullException(nameof(standardOutput));
        _standardError = standardError ?? throw new ArgumentNullException(nameof(standardError));
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public async Task<DocumentCacheAdminCliProcessResult?> TryWaitForExitAsync(TimeSpan timeout)
    {
        if (_result is not null)
        {
            return _result;
        }

        try
        {
            await _process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return null;
        }

        return await CompleteAsync();
    }

    public async Task<DocumentCacheAdminCliProcessResult> WaitForExitAsync(TimeSpan timeout)
    {
        if (_result is not null)
        {
            return _result;
        }

        try
        {
            await _process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            await KillAsync();

            throw new TimeoutException(
                "DocumentCacheAdmin process timed out.\n"
                    + $"stdout:\n{await _standardOutput}\n"
                    + $"stderr:\n{await _standardError}"
            );
        }

        return await CompleteAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_result is null && !HasExited)
        {
            await KillAsync();
        }

        _process.Dispose();
    }

    private async Task<DocumentCacheAdminCliProcessResult> CompleteAsync()
    {
        _result ??= new(_process.ExitCode, await _standardOutput, await _standardError);
        return _result;
    }

    private async Task KillAsync()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited after the timeout fired.
        }

        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Preserve the original timeout context.
        }
    }
}

internal sealed record DocumentCacheAdminCliProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError
)
{
    public JsonObject ReadStandardOutputJsonObject()
    {
        JsonNode? parsed = JsonNode.Parse(StandardOutput);
        return parsed as JsonObject
            ?? throw new InvalidOperationException("Expected stdout to contain one JSON object.");
    }
}

internal sealed class DocumentCacheAdminTestConfigurationService : IAsyncDisposable
{
    private const string Token = "document-cache-admin-cli-harness-token";
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _serverTask;
    private readonly DocumentCacheAdminCliTarget _target;
    private readonly string _encryptionKey;
    private readonly object _sync = new();
    private int _tokenRequestCount;
    private int _dataStoresRequestCount;
    private string? _lastTokenRequestBody;
    private string? _lastDataStoresAuthorizationHeader;
    private string? _lastDataStoresTenantHeader;
    private bool _disposed;

    private DocumentCacheAdminTestConfigurationService(
        HttpListener listener,
        Uri baseUri,
        DocumentCacheAdminCliTarget target,
        string encryptionKey
    )
    {
        _listener = listener;
        BaseUri = baseUri;
        _target = target;
        _encryptionKey = encryptionKey;
        _serverTask = Task.Run(RunAsync);
    }

    public Uri BaseUri { get; }

    public int TokenRequestCount => Volatile.Read(ref _tokenRequestCount);

    public int DataStoresRequestCount => Volatile.Read(ref _dataStoresRequestCount);

    public string? LastTokenRequestBody
    {
        get
        {
            lock (_sync)
            {
                return _lastTokenRequestBody;
            }
        }
    }

    public string? LastDataStoresAuthorizationHeader
    {
        get
        {
            lock (_sync)
            {
                return _lastDataStoresAuthorizationHeader;
            }
        }
    }

    public string? LastDataStoresTenantHeader
    {
        get
        {
            lock (_sync)
            {
                return _lastDataStoresTenantHeader;
            }
        }
    }

    public static DocumentCacheAdminTestConfigurationService Start(
        DocumentCacheAdminCliTarget target,
        string encryptionKey
    )
    {
        int port = GetEphemeralPort();
        Uri baseUri = new($"http://127.0.0.1:{port}/");
        var listener = new HttpListener();
        listener.Prefixes.Add(baseUri.ToString());
        listener.Start();
        return new(listener, baseUri, target, encryptionKey);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cancellationTokenSource.CancelAsync();
        _listener.Stop();
        _listener.Close();

        try
        {
            await _serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Expected during shutdown.
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }

    private async Task RunAsync()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_cancellationTokenSource.Token);
            }
            catch (Exception exception)
                when (exception
                        is OperationCanceledException
                            or HttpListenerException
                            or ObjectDisposedException
                )
            {
                break;
            }

            await HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath.Trim('/') ?? string.Empty;

        if (
            string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, "connect/token", StringComparison.OrdinalIgnoreCase)
        )
        {
            await HandleTokenAsync(context);
            return;
        }

        if (
            string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, "v3/dataStores", StringComparison.OrdinalIgnoreCase)
        )
        {
            await HandleDataStoresAsync(context);
            return;
        }

        if (
            string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, "v3/tenants", StringComparison.OrdinalIgnoreCase)
        )
        {
            await WriteJsonResponseAsync(context.Response, Array.Empty<object>());
            return;
        }

        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
        context.Response.Close();
    }

    private async Task HandleTokenAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        string body = await reader.ReadToEndAsync();

        Interlocked.Increment(ref _tokenRequestCount);
        lock (_sync)
        {
            _lastTokenRequestBody = body;
        }

        await WriteJsonResponseAsync(
            context.Response,
            new
            {
                access_token = Token,
                token_type = "Bearer",
                expires_in = 3600,
            }
        );
    }

    private async Task HandleDataStoresAsync(HttpListenerContext context)
    {
        Interlocked.Increment(ref _dataStoresRequestCount);
        lock (_sync)
        {
            _lastDataStoresAuthorizationHeader = context.Request.Headers["Authorization"];
            _lastDataStoresTenantHeader = context.Request.Headers["Tenant"];
        }

        await WriteJsonResponseAsync(
            context.Response,
            new[]
            {
                new
                {
                    Id = _target.DataStoreId,
                    DataStoreType = "Operational",
                    Name = "DocumentCacheAdmin CLI integration target",
                    ConnectionString = EncryptToBase64(_target.ConnectionString, _encryptionKey),
                    ProviderToken = _target.ProviderToken.Value,
                    DataStoreContexts = Array.Empty<object>(),
                },
            }
        );
    }

    private static async Task WriteJsonResponseAsync(HttpListenerResponse response, object payload)
    {
        byte[] responseBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/json";
        response.ContentLength64 = responseBytes.Length;
        await response.OutputStream.WriteAsync(responseBytes);
        response.Close();
    }

    private static string EncryptToBase64(string plainText, string encryptionKey)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(encryptionKey.PadRight(32, '0')[..32]);
        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        byte[] result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    private static int GetEphemeralPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal static class DocumentCacheAdminCliBaselineCache
{
    private static readonly Lazy<Task<PostgresqlGeneratedDdlBaselineDatabase>> _postgresqlBaseline = new(
        BuildPostgresqlBaselineAsync,
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    private static readonly Lazy<Task<IMssqlGeneratedDdlBaselineDatabase>> _mssqlBaseline = new(
        BuildMssqlBaselineAsync,
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    public static Task<PostgresqlGeneratedDdlBaselineDatabase> GetPostgresqlBaselineAsync() =>
        _postgresqlBaseline.Value;

    public static Task<IMssqlGeneratedDdlBaselineDatabase> GetMssqlBaselineAsync() => _mssqlBaseline.Value;

    public static async Task DisposeAllAsync()
    {
        if (_postgresqlBaseline.IsValueCreated)
        {
            try
            {
                PostgresqlGeneratedDdlBaselineDatabase baseline = await _postgresqlBaseline.Value;
                await baseline.DisposeAsync();
            }
            catch
            {
                // Best-effort teardown; do not mask test failures.
            }
        }

        if (_mssqlBaseline.IsValueCreated)
        {
            try
            {
                IMssqlGeneratedDdlBaselineDatabase baseline = await _mssqlBaseline.Value;
                await baseline.DisposeAsync();
            }
            catch
            {
                // Best-effort teardown; do not mask test failures.
            }
        }
    }

    private static Task<PostgresqlGeneratedDdlBaselineDatabase> BuildPostgresqlBaselineAsync() =>
        PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            DocumentCacheAdminCliFixture.PostgresqlFixtureSignature,
            DocumentCacheAdminCliFixture.Shared.PostgresqlDdl
        );

    private static Task<IMssqlGeneratedDdlBaselineDatabase> BuildMssqlBaselineAsync() =>
        MssqlGeneratedDdlBaselineDatabaseFactory.CreateAsync(
            DocumentCacheAdminCliFixture.MssqlFixtureSignature,
            DocumentCacheAdminCliFixture.Shared.MssqlDdl
        );
}

internal sealed class DocumentCacheAdminCliFixture
{
    private static readonly Lazy<DocumentCacheAdminCliFixture> _shared = new(
        Build,
        LazyThreadSafetyMode.ExecutionAndPublication
    );
    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    private DocumentCacheAdminCliFixture(string apiSchemaDirectory, string postgresqlDdl, string mssqlDdl)
    {
        ApiSchemaDirectory = apiSchemaDirectory;
        PostgresqlDdl = postgresqlDdl;
        MssqlDdl = mssqlDdl;
    }

    public static DocumentCacheAdminCliFixture Shared => _shared.Value;

    public string ApiSchemaDirectory { get; }

    public string PostgresqlDdl { get; }

    public string MssqlDdl { get; }

    public static string PostgresqlFixtureSignature =>
        "document-cache-admin-cli-harness:descriptor-runtime:pgsql";

    public static string MssqlFixtureSignature => "document-cache-admin-cli-harness:descriptor-runtime:mssql";

    private static DocumentCacheAdminCliFixture Build()
    {
        string materializedDirectory = MaterializeDescriptorRuntimeFixture();
        EffectiveSchemaSet effectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(
            materializedDirectory
        );
        (_, string postgresqlDdl) = DdlPipelineHelpers.BuildDdlForDialect(
            effectiveSchemaSet,
            SqlDialect.Pgsql,
            strict: true
        );
        (_, string mssqlDdl) = DdlPipelineHelpers.BuildDdlForDialect(
            effectiveSchemaSet,
            SqlDialect.Mssql,
            strict: true
        );

        return new(materializedDirectory, postgresqlDdl, mssqlDdl);
    }

    private static string MaterializeDescriptorRuntimeFixture()
    {
        string repositoryRoot = FixturePathResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        string sourceFixtureDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "dms",
            "backend",
            "EdFi.DataManagementService.Backend.IntegrationFixtures",
            "descriptor-runtime"
        );

        (IReadOnlyList<string> apiSchemaFiles, IReadOnlyList<string> dialects) = ReadFixtureManifest(
            sourceFixtureDirectory
        );

        string materializedDirectory = Path.Combine(
            Path.GetTempPath(),
            "dms-document-cache-admin-cli-fixtures",
            "descriptor-runtime"
        );

        if (Directory.Exists(materializedDirectory))
        {
            Directory.Delete(materializedDirectory, recursive: true);
        }
        Directory.CreateDirectory(materializedDirectory);

        string inputsDirectory = Path.Combine(materializedDirectory, "inputs");
        Directory.CreateDirectory(inputsDirectory);

        List<BootstrapApiSchemaProject> bootstrapProjects = [];
        List<string> materializedFileNames = [];
        foreach (string apiSchemaFile in apiSchemaFiles)
        {
            string sourcePath = FixturePathResolver.ResolveFixtureInputPath(
                sourceFixtureDirectory,
                apiSchemaFile,
                repositoryRoot
            );
            string fileName = Path.GetFileName(sourcePath);
            string targetPath = Path.Combine(inputsDirectory, fileName);

            JsonObject root =
                JsonNode.Parse(File.ReadAllText(sourcePath)) as JsonObject
                ?? throw new InvalidOperationException(
                    $"ApiSchema file '{sourcePath}' must be a JSON object."
                );

            AugmentForRuntime(root);
            bootstrapProjects.Add(ReadBootstrapProject(root, sourcePath, $"inputs/{fileName}"));

            File.WriteAllText(targetPath, root.ToJsonString(_writeOptions));
            materializedFileNames.Add(fileName);
        }

        WriteFixtureManifest(materializedDirectory, materializedFileNames, dialects);
        WriteBootstrapManifest(materializedDirectory, bootstrapProjects);

        return materializedDirectory;
    }

    private static (IReadOnlyList<string> ApiSchemaFiles, IReadOnlyList<string> Dialects) ReadFixtureManifest(
        string fixtureDirectory
    )
    {
        string manifestPath = Path.Combine(fixtureDirectory, "fixture.json");
        using FileStream stream = File.OpenRead(manifestPath);
        using JsonDocument document = JsonDocument.Parse(stream);

        JsonElement apiSchemaFilesElement = document.RootElement.GetProperty("apiSchemaFiles");
        List<string> apiSchemaFiles = [];
        foreach (JsonElement entry in apiSchemaFilesElement.EnumerateArray())
        {
            string? apiSchemaFile = entry.GetString();
            if (string.IsNullOrWhiteSpace(apiSchemaFile))
            {
                throw new InvalidOperationException(
                    $"Fixture manifest '{manifestPath}' contains an empty apiSchemaFiles entry."
                );
            }
            apiSchemaFiles.Add(apiSchemaFile);
        }

        List<string> dialects = [];
        if (
            document.RootElement.TryGetProperty("dialects", out JsonElement dialectsElement)
            && dialectsElement.ValueKind == JsonValueKind.Array
        )
        {
            foreach (JsonElement entry in dialectsElement.EnumerateArray())
            {
                string? dialect = entry.GetString();
                if (!string.IsNullOrWhiteSpace(dialect))
                {
                    dialects.Add(dialect);
                }
            }
        }

        return (apiSchemaFiles, dialects);
    }

    private static void AugmentForRuntime(JsonObject root)
    {
        if (root["projectSchema"] is not JsonObject projectSchema)
        {
            return;
        }

        AugmentProjectSchemaDefaults(projectSchema);

        if (projectSchema["resourceSchemas"] is JsonObject resourceSchemas)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in resourceSchemas)
            {
                if (entry.Value is JsonObject resourceSchema)
                {
                    AugmentResourceSchemaDefaults(resourceSchema);
                }
            }
        }
    }

    private static void AugmentProjectSchemaDefaults(JsonObject projectSchema)
    {
        AddIfMissing(projectSchema, "resourceNameMapping", () => new JsonObject());
        AddIfMissing(projectSchema, "caseInsensitiveEndpointNameMapping", () => new JsonObject());
        AddIfMissing(projectSchema, "educationOrganizationHierarchy", () => new JsonObject());
        AddIfMissing(projectSchema, "educationOrganizationTypes", () => new JsonArray());
        AddIfMissing(projectSchema, "domains", () => new JsonArray());
        AddIfMissing(projectSchema, "description", () => JsonValue.Create(string.Empty));
    }

    private static void AugmentResourceSchemaDefaults(JsonObject resourceSchema)
    {
        AddIfMissing(resourceSchema, "booleanJsonPaths", () => new JsonArray());
        AddIfMissing(resourceSchema, "numericJsonPaths", () => new JsonArray());
        AddIfMissing(resourceSchema, "dateJsonPaths", () => new JsonArray());
        AddIfMissing(resourceSchema, "dateTimeJsonPaths", () => new JsonArray());
        AddIfMissing(resourceSchema, "authorizationPathways", () => new JsonArray());
        AddIfMissing(resourceSchema, "decimalPropertyValidationInfos", () => new JsonArray());
        AddIfMissing(resourceSchema, "queryFieldMapping", () => new JsonObject());
        AddIfMissing(
            resourceSchema,
            "securableElements",
            () =>
                new JsonObject
                {
                    ["Namespace"] = new JsonArray(),
                    ["EducationOrganization"] = new JsonArray(),
                    ["Student"] = new JsonArray(),
                    ["Contact"] = new JsonArray(),
                    ["Staff"] = new JsonArray(),
                }
        );
    }

    private static void AddIfMissing(JsonObject target, string propertyName, Func<JsonNode?> defaultFactory)
    {
        if (!target.ContainsKey(propertyName))
        {
            target[propertyName] = defaultFactory();
        }
    }

    private static BootstrapApiSchemaProject ReadBootstrapProject(
        JsonObject root,
        string sourcePath,
        string schemaPath
    )
    {
        JsonObject projectSchema =
            root["projectSchema"] as JsonObject
            ?? throw new InvalidOperationException(
                $"ApiSchema file '{sourcePath}' is missing 'projectSchema'."
            );

        return new(
            GetRequiredProjectString(projectSchema, "projectName", sourcePath),
            GetRequiredProjectString(projectSchema, "projectEndpointName", sourcePath),
            GetRequiredProjectBoolean(projectSchema, "isExtensionProject", sourcePath),
            schemaPath
        );
    }

    private static string GetRequiredProjectString(
        JsonObject projectSchema,
        string propertyName,
        string sourcePath
    )
    {
        JsonNode? valueNode = projectSchema[propertyName];
        if (valueNode is null)
        {
            throw new InvalidOperationException(
                $"ApiSchema file '{sourcePath}' is missing 'projectSchema.{propertyName}'."
            );
        }

        string value = valueNode.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"ApiSchema file '{sourcePath}' has an empty 'projectSchema.{propertyName}'."
            );
        }

        return value;
    }

    private static bool GetRequiredProjectBoolean(
        JsonObject projectSchema,
        string propertyName,
        string sourcePath
    )
    {
        JsonNode? valueNode = projectSchema[propertyName];
        if (valueNode is null)
        {
            throw new InvalidOperationException(
                $"ApiSchema file '{sourcePath}' is missing 'projectSchema.{propertyName}'."
            );
        }

        return valueNode.GetValue<bool>();
    }

    private static void WriteFixtureManifest(
        string materializedDirectory,
        IReadOnlyList<string> apiSchemaFileNames,
        IReadOnlyList<string> dialects
    )
    {
        JsonArray apiSchemaFiles = [];
        foreach (string apiSchemaFileName in apiSchemaFileNames)
        {
            apiSchemaFiles.Add(apiSchemaFileName);
        }

        JsonArray dialectEntries = [];
        foreach (string dialect in dialects)
        {
            dialectEntries.Add(dialect);
        }

        var manifest = new JsonObject { ["apiSchemaFiles"] = apiSchemaFiles, ["dialects"] = dialectEntries };

        File.WriteAllText(
            Path.Combine(materializedDirectory, "fixture.json"),
            manifest.ToJsonString(_writeOptions)
        );
    }

    private static void WriteBootstrapManifest(
        string materializedDirectory,
        IReadOnlyList<BootstrapApiSchemaProject> projects
    )
    {
        JsonArray projectEntries = [];
        foreach (BootstrapApiSchemaProject project in projects)
        {
            projectEntries.Add(
                new JsonObject
                {
                    ["projectName"] = project.ProjectName,
                    ["projectEndpointName"] = project.ProjectEndpointName,
                    ["isExtensionProject"] = project.IsExtensionProject,
                    ["schemaPath"] = project.SchemaPath,
                }
            );
        }

        var manifest = new JsonObject { ["version"] = 1, ["projects"] = projectEntries };

        File.WriteAllText(
            Path.Combine(materializedDirectory, "bootstrap-api-schema-manifest.json"),
            manifest.ToJsonString(_writeOptions)
        );
    }

    private sealed record BootstrapApiSchemaProject(
        string ProjectName,
        string ProjectEndpointName,
        bool IsExtensionProject,
        string SchemaPath
    );
}
