// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;

namespace EdFi.DataManagementService.Tests.E2E.Management;

internal sealed record RepresentationRestampE2EProviderSql(
    string SetLifecycle,
    string ReadCanonicalContentVersion,
    string ReadRequiredWorkVersion,
    string IsProjected,
    string ReadResidualWork,
    string ReadDocumentCacheState
);

/// <summary>
/// The <c>dms.DocumentCacheState</c> singleton as observed before a scenario mutates it. Both
/// columns are captured so cleanup restores the exact pre-scenario state: <c>SetLifecycleAsync</c>
/// also writes the cache-ahead latch, so restoring the lifecycle alone would clobber a set latch.
/// </summary>
internal sealed record RepresentationRestampE2EDocumentCacheStateObservation(
    DocumentCacheLifecycleState LifecycleState,
    bool CacheAheadRecoveryRequired
);

/// <summary>
/// One durable <c>dms.DocumentProjectionWork</c> row joined to its canonical document and, when
/// present, its cache row. Read only for diagnostics: it tells a reader which rows kept a drain
/// alive and whether they belong to the scenario under test.
/// </summary>
internal sealed record RepresentationRestampE2EResidualWorkRow(
    long DocumentId,
    Guid DocumentUuid,
    long RequiredContentVersion,
    long CanonicalContentVersion,
    long? CacheContentVersion,
    DateTimeOffset FirstEnqueuedAt
);

internal interface IRepresentationRestampE2EProviderOperations
{
    RepresentationRestampE2EProviderSql Sql { get; }

    DbConnection OpenConnection(string connectionString);

    Task SetLifecycleAsync(
        DbConnection connection,
        DocumentCacheLifecycleState lifecycleState,
        CancellationToken cancellationToken,
        bool cacheAheadRecoveryRequired = false
    );

    Task<RepresentationRestampE2EDocumentCacheStateObservation> ReadDocumentCacheStateAsync(
        DbConnection connection,
        string targetDescription,
        CancellationToken cancellationToken
    );

    Task<long> ReadCanonicalContentVersionAsync(
        DbConnection connection,
        Guid documentUuid,
        CancellationToken cancellationToken
    );

    Task<long?> ReadRequiredWorkVersionAsync(
        DbConnection connection,
        Guid documentUuid,
        CancellationToken cancellationToken
    );

    Task<bool> IsProjectedAsync(
        DbConnection connection,
        Guid documentUuid,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<RepresentationRestampE2EResidualWorkRow>> ReadResidualWorkAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    );
}

internal abstract class RepresentationRestampE2EProviderOperations(RepresentationRestampE2EProviderSql sql)
    : IRepresentationRestampE2EProviderOperations
{
    public RepresentationRestampE2EProviderSql Sql { get; } = sql;

    public abstract DbConnection OpenConnection(string connectionString);

    public async Task SetLifecycleAsync(
        DbConnection connection,
        DocumentCacheLifecycleState lifecycleState,
        CancellationToken cancellationToken,
        bool cacheAheadRecoveryRequired = false
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = Sql.SetLifecycle;
        AddParameter(command, "projectionLifecycleState", lifecycleState.ToString());
        AddParameter(command, "cacheAheadRecoveryRequired", cacheAheadRecoveryRequired);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RepresentationRestampE2EDocumentCacheStateObservation> ReadDocumentCacheStateAsync(
        DbConnection connection,
        string targetDescription,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = Sql.ReadDocumentCacheState;
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return RepresentationRestampE2EHarness.ParseDocumentCacheState(null, null, targetDescription);
        }

        return RepresentationRestampE2EHarness.ParseDocumentCacheState(
            reader.GetValue(0),
            reader.GetValue(1),
            targetDescription
        );
    }

    public async Task<long> ReadCanonicalContentVersionAsync(
        DbConnection connection,
        Guid documentUuid,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = CreateDocumentCommand(
            connection,
            Sql.ReadCanonicalContentVersion,
            documentUuid
        );
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<long?> ReadRequiredWorkVersionAsync(
        DbConnection connection,
        Guid documentUuid,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = CreateDocumentCommand(
            connection,
            Sql.ReadRequiredWorkVersion,
            documentUuid
        );
        object? scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null ? null : Convert.ToInt64(scalar);
    }

    public async Task<bool> IsProjectedAsync(
        DbConnection connection,
        Guid documentUuid,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = CreateDocumentCommand(connection, Sql.IsProjected, documentUuid);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<RepresentationRestampE2EResidualWorkRow>> ReadResidualWorkAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = Sql.ReadResidualWork;
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<RepresentationRestampE2EResidualWorkRow> rows = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(
                new RepresentationRestampE2EResidualWorkRow(
                    reader.GetInt64(0),
                    reader.GetGuid(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetInt64(4),
                    ToUtcTimestamp(reader.GetValue(5))
                )
            );
        }

        return rows;
    }

    private static DateTimeOffset ToUtcTimestamp(object value) =>
        value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime.ToUniversalTime()
            ),
            _ => DateTimeOffset
                .Parse(
                    Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                        ?? throw new InvalidOperationException("DocumentProjectionWork timestamp was null."),
                    System.Globalization.CultureInfo.InvariantCulture
                )
                .ToUniversalTime(),
        };

    private static DbCommand CreateDocumentCommand(
        DbConnection connection,
        string commandText,
        Guid documentUuid
    )
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        AddParameter(command, "documentUuid", documentUuid);
        return command;
    }

    private static void AddParameter(DbCommand command, string parameterName, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

internal sealed class PostgresqlRepresentationRestampE2EProviderOperations()
    : RepresentationRestampE2EProviderOperations(
        new(
            """
            UPDATE dms."DocumentCacheState"
            SET "ProjectionLifecycleState" = @projectionLifecycleState,
                "CacheAheadRecoveryRequired" = @cacheAheadRecoveryRequired
            WHERE "StateId" = 1;
            """,
            """
            SELECT "ContentVersion"
            FROM dms."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            """
            SELECT work."RequiredContentVersion"
            FROM dms."DocumentProjectionWork" AS work
            INNER JOIN dms."Document" AS document ON document."DocumentId" = work."DocumentId"
            WHERE document."DocumentUuid" = @documentUuid;
            """,
            """
            SELECT EXISTS (
                SELECT 1
                FROM dms."Document" AS document
                INNER JOIN dms."DocumentCache" AS cache
                    ON cache."DocumentId" = document."DocumentId"
                WHERE document."DocumentUuid" = @documentUuid
                  AND cache."ContentVersion" = document."ContentVersion"
                  AND NOT EXISTS (
                      SELECT 1
                      FROM dms."DocumentProjectionWork" AS work
                      WHERE work."DocumentId" = document."DocumentId"
                  )
            );
            """,
            """
            SELECT work."DocumentId",
                   document."DocumentUuid",
                   work."RequiredContentVersion",
                   document."ContentVersion",
                   cache."ContentVersion",
                   work."FirstEnqueuedAt"
            FROM dms."DocumentProjectionWork" AS work
            INNER JOIN dms."Document" AS document ON document."DocumentId" = work."DocumentId"
            LEFT JOIN dms."DocumentCache" AS cache ON cache."DocumentId" = work."DocumentId"
            ORDER BY work."FirstEnqueuedAt", work."DocumentId"
            LIMIT 50;
            """,
            """
            SELECT "ProjectionLifecycleState", "CacheAheadRecoveryRequired"
            FROM dms."DocumentCacheState"
            WHERE "StateId" = 1;
            """
        )
    )
{
    public override DbConnection OpenConnection(string connectionString) =>
        new NpgsqlConnection(connectionString);
}

internal sealed class MssqlRepresentationRestampE2EProviderOperations()
    : RepresentationRestampE2EProviderOperations(
        new(
            """
            UPDATE [dms].[DocumentCacheState]
            SET [ProjectionLifecycleState] = @projectionLifecycleState,
                [CacheAheadRecoveryRequired] = @cacheAheadRecoveryRequired
            WHERE [StateId] = 1;
            """,
            """
            SELECT [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            """
            SELECT work.[RequiredContentVersion]
            FROM [dms].[DocumentProjectionWork] AS work
            INNER JOIN [dms].[Document] AS document ON document.[DocumentId] = work.[DocumentId]
            WHERE document.[DocumentUuid] = @documentUuid;
            """,
            """
            SELECT CAST(
                CASE WHEN EXISTS (
                    SELECT 1
                    FROM [dms].[Document] AS document
                    INNER JOIN [dms].[DocumentCache] AS cache
                        ON cache.[DocumentId] = document.[DocumentId]
                    WHERE document.[DocumentUuid] = @documentUuid
                      AND cache.[ContentVersion] = document.[ContentVersion]
                      AND NOT EXISTS (
                          SELECT 1
                          FROM [dms].[DocumentProjectionWork] AS work
                          WHERE work.[DocumentId] = document.[DocumentId]
                      )
                ) THEN 1 ELSE 0 END
                AS bit
            );
            """,
            """
            SELECT TOP (50)
                   work.[DocumentId],
                   document.[DocumentUuid],
                   work.[RequiredContentVersion],
                   document.[ContentVersion],
                   cache.[ContentVersion],
                   work.[FirstEnqueuedAt]
            FROM [dms].[DocumentProjectionWork] AS work
            INNER JOIN [dms].[Document] AS document ON document.[DocumentId] = work.[DocumentId]
            LEFT JOIN [dms].[DocumentCache] AS cache ON cache.[DocumentId] = work.[DocumentId]
            ORDER BY work.[FirstEnqueuedAt], work.[DocumentId];
            """,
            """
            SELECT [ProjectionLifecycleState], [CacheAheadRecoveryRequired]
            FROM [dms].[DocumentCacheState]
            WHERE [StateId] = 1;
            """
        )
    )
{
    public override DbConnection OpenConnection(string connectionString) =>
        new SqlConnection(connectionString);
}

internal static class RepresentationRestampE2EHarness
{
    private const string ConfigurationServiceClientId = "CMSReadOnlyAccess";
    private const string ConfigurationServiceClientSecret = "ValidClientSecret1234567890!Abcd";
    private const string ConfigurationServiceScope = "edfi_admin_api/readonly_access";
    private const string ConfigurationServiceEncryptionKey = "secret!_32_chars_xxxxxxxxxxxxxxx";

    public static Task ExecuteTrackingRestampAsync(Guid documentUuid) =>
        ExecuteRestampAsync(documentUuid, DocumentCacheRepresentationRestampMode.Tracking);

    public static Task ExecuteDisabledRestampAsync(Guid documentUuid) =>
        ExecuteRestampAsync(documentUuid, DocumentCacheRepresentationRestampMode.Disabled);

    private static async Task ExecuteRestampAsync(
        Guid documentUuid,
        DocumentCacheRepresentationRestampMode mode
    )
    {
        string containerName = AppSettings.DmsContainerName;
        string schemaCopyDirectory = Path.Combine(
            Path.GetTempPath(),
            "dms1318-restamp-schema",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(schemaCopyDirectory);

        await RunWithDirectoryCleanupAsync(
            schemaCopyDirectory,
            async () =>
            {
                var projectionExpected = false;
                // Cleanup undoes only what actually happened: the container is restarted only after a
                // stop that succeeded, and DocumentCacheState is restored only after it was observed,
                // which happens before any mutation. A wrong container name fails on the copy below,
                // before DMS is touched; an unreadable state row fails before the lifecycle is changed.
                var dmsStopped = false;
                RepresentationRestampE2EDocumentCacheStateObservation? observedState = null;
                IRepresentationRestampE2EProviderOperations providerOperations = ProviderOperationsFor(
                    ProviderFor(AppSettings.DatabaseEngine)
                );
                string connectionString = AppSettings.DataStoreAdminConnectionString;

                await RunWithCleanupAsync(
                    $"representation restamp ({mode}) for document {documentUuid} against DMS container '{containerName}'",
                    async () =>
                    {
                        await RunDockerAsync(
                            "cp",
                            containerName,
                            ["cp", $"{containerName}:/app/ApiSchema", schemaCopyDirectory]
                        );
                        await RunDockerAsync("stop", containerName, ["stop", containerName]);
                        dmsStopped = true;

                        await using DocumentCacheAdminCliTarget target = CreateExternalTarget(
                            schemaCopyDirectory
                        );
                        providerOperations = ProviderOperationsFor(target.ProviderToken);
                        connectionString = target.ConnectionString;
                        // Capture the pre-scenario state before the first mutation so cleanup can put the
                        // shared E2E database back exactly. Without this the first restamp scenario left
                        // the database in Tracking for the rest of the run, every later write enqueued
                        // work nobody drained, and a later Disabled restamp orphaned a work row that kept
                        // the next Tracking drain spinning (DMS-1528).
                        observedState = await ReadDocumentCacheStateAsync(
                            providerOperations,
                            connectionString,
                            TargetDescription(target),
                            CancellationToken.None
                        );
                        await SetLifecycleAsync(
                            providerOperations,
                            connectionString,
                            mode == DocumentCacheRepresentationRestampMode.Tracking
                                ? DocumentCacheLifecycleState.Tracking
                                : DocumentCacheLifecycleState.Disabled,
                            CancellationToken.None
                        );
                        await using DocumentCacheAdminTestConfigurationService configurationService =
                            DocumentCacheAdminTestConfigurationService.Start(
                                target,
                                ConfigurationServiceEncryptionKey
                            );
                        long originalContentVersion = await ReadCanonicalContentVersionAsync(
                            providerOperations,
                            connectionString,
                            documentUuid,
                            CancellationToken.None
                        );
                        long? originalRequiredWorkVersion = await ReadRequiredWorkVersionAsync(
                            providerOperations,
                            connectionString,
                            documentUuid,
                            CancellationToken.None
                        );
                        await ExecuteRestampCommandsAsync(
                            documentUuid,
                            mode,
                            (command, arguments) =>
                                RunCliAsync(
                                    command,
                                    target.DataStoreId,
                                    configurationService.BaseUri,
                                    target.AppSettingsDatastore,
                                    target.ApiSchemaDirectory,
                                    arguments
                                ),
                            async () =>
                            {
                                long restampedContentVersion = await ReadCanonicalContentVersionAsync(
                                    providerOperations,
                                    connectionString,
                                    documentUuid,
                                    CancellationToken.None
                                );
                                restampedContentVersion.Should().BeGreaterThan(originalContentVersion);
                                if (mode == DocumentCacheRepresentationRestampMode.Tracking)
                                {
                                    long? restampedRequiredWorkVersion = await ReadRequiredWorkVersionAsync(
                                        providerOperations,
                                        connectionString,
                                        documentUuid,
                                        CancellationToken.None
                                    );
                                    restampedRequiredWorkVersion.Should().Be(restampedContentVersion);
                                }
                                else
                                {
                                    long? restampedRequiredWorkVersion = await ReadRequiredWorkVersionAsync(
                                        providerOperations,
                                        connectionString,
                                        documentUuid,
                                        CancellationToken.None
                                    );
                                    restampedRequiredWorkVersion
                                        .Should()
                                        .Be(
                                            originalRequiredWorkVersion,
                                            "Disabled restamp must not enqueue or update projection work"
                                        );
                                }
                            },
                            async () =>
                            {
                                await DrainOrdinaryProjectorAsync(target, documentUuid);
                                projectionExpected = true;
                            }
                        );
                    },
                    // Nested so a failed state restore cannot skip the restart: the restore is the
                    // inner action and the restart is its cleanup, which runs either way.
                    () =>
                        RunWithCleanupAsync(
                            $"cleanup after representation restamp ({mode}) for DMS container '{containerName}'",
                            () =>
                                RestoreDocumentCacheStateAsync(
                                    observedState,
                                    (lifecycleState, cacheAheadRecoveryRequired) =>
                                        SetLifecycleAsync(
                                            providerOperations,
                                            connectionString,
                                            lifecycleState,
                                            CancellationToken.None,
                                            cacheAheadRecoveryRequired
                                        )
                                ),
                            async () =>
                            {
                                if (!dmsStopped)
                                {
                                    return;
                                }

                                await RunDockerAsync("start", containerName, ["start", containerName]);
                                await WaitForDmsAsync();
                                if (projectionExpected)
                                {
                                    await WaitForProjectedCacheAsync(
                                        providerOperations,
                                        connectionString,
                                        documentUuid
                                    );
                                }
                            }
                        )
                );
            }
        );
    }

    internal static async Task ExecuteRestampCommandsAsync(
        Guid documentUuid,
        DocumentCacheRepresentationRestampMode mode,
        Func<string, IReadOnlyList<string>, Task<JsonObject>> runCliAsync,
        Func<Task> verifyCanonicalStateAsync,
        Func<Task> drainOrdinaryProjectorAsync
    )
    {
        string modeArgument = mode switch
        {
            DocumentCacheRepresentationRestampMode.Tracking => "tracking",
            DocumentCacheRepresentationRestampMode.Disabled => "disabled",
            _ => throw new InvalidOperationException($"Unsupported representation-restamp mode '{mode}'."),
        };
        // This acknowledges the operator's external writer fence; it does not establish that fence.
        JsonObject preview = await runCliAsync(
            "restamp-preview",
            [
                "--mode",
                modeArgument,
                "--reason",
                "E2E observable representation restamp verification",
                "--document-uuid",
                documentUuid.ToString(),
                "--offline-writer-admission",
                "closedAndDrained",
            ]
        );
        Guid operationId = preview["result"]!["operationId"]!.GetValue<Guid>();
        JsonObject execute = await runCliAsync(
            "restamp-execute",
            [
                "--operation-id",
                operationId.ToString(),
                "--confirm",
                "representationRestamp",
                "--offline-writer-admission",
                "closedAndDrained",
            ]
        );
        execute["result"]!["state"]!.GetValue<string>().Should().Be("completed", "state must complete");
        execute["result"]!["claimLevel"]!
            .GetValue<string>()
            .Should()
            .Be(
                mode == DocumentCacheRepresentationRestampMode.Tracking
                    ? "projectionWorkQueued"
                    : "canonicalOnlyComplete",
                "claimLevel must match the restamp mode"
            );
        await verifyCanonicalStateAsync();
        if (mode == DocumentCacheRepresentationRestampMode.Tracking)
        {
            await drainOrdinaryProjectorAsync();
        }
    }

    internal static RelationalProviderToken ProviderFor(string databaseEngine)
    {
        if (string.Equals(databaseEngine, "mssql", StringComparison.OrdinalIgnoreCase))
        {
            return RelationalProviderToken.SqlServer;
        }

        if (string.Equals(databaseEngine, "postgresql", StringComparison.OrdinalIgnoreCase))
        {
            return RelationalProviderToken.Postgresql;
        }

        throw new InvalidOperationException($"Unsupported E2E database engine '{databaseEngine}'.");
    }

    internal static IRepresentationRestampE2EProviderOperations ProviderOperationsFor(
        RelationalProviderToken providerToken
    ) =>
        providerToken.Value switch
        {
            RelationalProviderToken.PostgresqlValue =>
                new PostgresqlRepresentationRestampE2EProviderOperations(),
            RelationalProviderToken.SqlServerValue => new MssqlRepresentationRestampE2EProviderOperations(),
            _ => throw new InvalidOperationException("Unsupported representation-restamp E2E provider."),
        };

    private static DocumentCacheAdminCliTarget CreateExternalTarget(string schemaDirectory) =>
        ProviderFor(AppSettings.DatabaseEngine).Value switch
        {
            RelationalProviderToken.PostgresqlValue => DocumentCacheAdminCliTarget.CreateExternalPostgresql(
                AppSettings.DataStoreAdminConnectionString,
                dataStoreId: 1,
                Path.Combine(schemaDirectory, "ApiSchema")
            ),
            RelationalProviderToken.SqlServerValue => DocumentCacheAdminCliTarget.CreateExternalMssql(
                AppSettings.DataStoreAdminConnectionString,
                dataStoreId: 1,
                Path.Combine(schemaDirectory, "ApiSchema")
            ),
            _ => throw new InvalidOperationException("Unsupported representation-restamp E2E provider."),
        };

    private static async Task SetLifecycleAsync(
        IRepresentationRestampE2EProviderOperations providerOperations,
        string connectionString,
        DocumentCacheLifecycleState lifecycleState,
        CancellationToken cancellationToken,
        bool cacheAheadRecoveryRequired = false
    )
    {
        await using DbConnection connection = providerOperations.OpenConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await providerOperations.SetLifecycleAsync(
            connection,
            lifecycleState,
            cancellationToken,
            cacheAheadRecoveryRequired
        );
    }

    private static async Task<RepresentationRestampE2EDocumentCacheStateObservation> ReadDocumentCacheStateAsync(
        IRepresentationRestampE2EProviderOperations providerOperations,
        string connectionString,
        string targetDescription,
        CancellationToken cancellationToken
    )
    {
        await using DbConnection connection = providerOperations.OpenConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await providerOperations.ReadDocumentCacheStateAsync(
            connection,
            targetDescription,
            cancellationToken
        );
    }

    /// <summary>
    /// Interprets the <c>dms.DocumentCacheState</c> row strictly. A missing row, an unknown lifecycle
    /// value, or an unreadable latch fails the <see cref="LifecycleCapturePhase"/> before anything is
    /// mutated; guessing here would silently reintroduce the cross-scenario coupling this guards against.
    /// </summary>
    internal static RepresentationRestampE2EDocumentCacheStateObservation ParseDocumentCacheState(
        object? lifecycleValue,
        object? cacheAheadRecoveryRequiredValue,
        string targetDescription
    )
    {
        if (lifecycleValue is null || lifecycleValue is DBNull)
        {
            throw new InvalidOperationException(
                $"Representation-restamp phase '{LifecycleCapturePhase}' failed for target {targetDescription}: "
                    + "dms.DocumentCacheState has no readable singleton row (StateId = 1)."
            );
        }

        string lifecycleText = Convert.ToString(
            lifecycleValue,
            System.Globalization.CultureInfo.InvariantCulture
        )!;
        // Enum.TryParse also accepts numeric text such as "1"; only the exact member name is a valid
        // column value, matching the DDL CHECK constraint.
        if (
            !Enum.TryParse(lifecycleText, ignoreCase: false, out DocumentCacheLifecycleState lifecycleState)
            || !Enum.IsDefined(lifecycleState)
            || !string.Equals(lifecycleState.ToString(), lifecycleText, StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException(
                $"Representation-restamp phase '{LifecycleCapturePhase}' failed for target {targetDescription}: "
                    + $"dms.DocumentCacheState.ProjectionLifecycleState has unsupported value '{lifecycleText}'."
            );
        }

        if (cacheAheadRecoveryRequiredValue is null || cacheAheadRecoveryRequiredValue is DBNull)
        {
            throw new InvalidOperationException(
                $"Representation-restamp phase '{LifecycleCapturePhase}' failed for target {targetDescription}: "
                    + "dms.DocumentCacheState.CacheAheadRecoveryRequired is unreadable."
            );
        }

        return new RepresentationRestampE2EDocumentCacheStateObservation(
            lifecycleState,
            Convert.ToBoolean(
                cacheAheadRecoveryRequiredValue,
                System.Globalization.CultureInfo.InvariantCulture
            )
        );
    }

    /// <summary>
    /// Restores the observed <c>dms.DocumentCacheState</c> for both restamp modes. Nothing is written
    /// when nothing was observed, because then nothing was mutated either.
    /// </summary>
    internal static Task RestoreDocumentCacheStateAsync(
        RepresentationRestampE2EDocumentCacheStateObservation? observedState,
        Func<DocumentCacheLifecycleState, bool, Task> setStateAsync
    ) =>
        observedState is null
            ? Task.CompletedTask
            : setStateAsync(observedState.LifecycleState, observedState.CacheAheadRecoveryRequired);

    /// <summary>
    /// Phase names reported when a representation-restamp harness phase exceeds its budget. Setup
    /// (service provider, effective schema bootstrap, runtime mapping-set compilation) and the drain
    /// loop have separate budgets so a timeout says which one ran out instead of one undifferentiated
    /// two-minute failure.
    /// </summary>
    internal const string ProjectorSetupPhase = "ProjectorSetup";
    internal const string OrdinaryDrainPhase = "OrdinaryDrain";
    internal const string LifecycleCapturePhase = "LifecycleCapture";

    internal static readonly TimeSpan ProjectorSetupBudget = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan OrdinaryDrainBudget = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Diagnostic reads of the queued work rows are bounded separately from the phase budgets: the
    /// pre-drain snapshot must not eat into the drain, and the failure snapshot runs after the drain
    /// budget is already spent.
    /// </summary>
    internal static readonly TimeSpan ResidualWorkReadBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A residual row the projector cannot acknowledge (for example a WorkVersionMismatch anomaly left
    /// by a Disabled restamp) makes every page report PageProcessed with nothing acknowledged. The drain
    /// stops after this many such pages in a row and reports the residual rows instead of spinning
    /// until the budget expires.
    /// </summary>
    internal const int MaxConsecutivePagesWithoutAcknowledgement = 20;

    private sealed record ProjectionRuntime(
        ServiceProvider ServiceProvider,
        DocumentCacheProjectionTargetRuntimeContext Context
    );

    private static async Task DrainOrdinaryProjectorAsync(
        DocumentCacheAdminCliTarget target,
        Guid documentUuid
    )
    {
        string targetDescription = TargetDescription(target);
        IRepresentationRestampE2EProviderOperations providerOperations = ProviderOperationsFor(
            target.ProviderToken
        );
        ProjectionRuntime runtime = await RunPhaseAsync(
            ProjectorSetupPhase,
            ProjectorSetupBudget,
            targetDescription,
            cancellationToken => CreateProjectionRuntimeAsync(target, cancellationToken)
        );
        await using ServiceProvider serviceProvider = runtime.ServiceProvider;
        await using DocumentCacheProjectionTargetRuntimeContext context = runtime.Context;
        IDocumentCacheProjectionDrainPageProcessor processor =
            serviceProvider.GetRequiredService<IDocumentCacheProjectionDrainPageProcessor>();

        IReadOnlyList<RepresentationRestampE2EResidualWorkRow> queuedBeforeDrain =
            await ReadResidualWorkBoundedAsync(providerOperations, target.ConnectionString);
        WriteHarnessOutput(
            $"Ordinary drain for target {targetDescription} and document {documentUuid} starts with "
                + $"{queuedBeforeDrain.Count} queued work row(s): {DescribeResidualWork(queuedBeforeDrain, documentUuid)}"
        );

        var tally = new RepresentationRestampDrainTally();
        Func<Task<string>> describeDiagnosticsAsync = () =>
            DescribeDrainAsync(tally, context, providerOperations, target.ConnectionString, documentUuid);
        await RunPhaseAsync(
            OrdinaryDrainPhase,
            OrdinaryDrainBudget,
            targetDescription,
            cancellationToken =>
                DrainUntilOwnWorkProjectedAsync(
                    tally,
                    token =>
                        processor.ProcessPageAsync(
                            new DocumentCacheProjectionDrainPageRequest(
                                context,
                                DocumentCacheProjectionDrainInvocationKind.Ordinary
                            ),
                            token
                        ),
                    token =>
                        IsProjectedAsync(providerOperations, target.ConnectionString, documentUuid, token),
                    describeDiagnosticsAsync,
                    targetDescription,
                    MaxConsecutivePagesWithoutAcknowledgement,
                    cancellationToken
                ),
            describeDiagnosticsAsync
        );
        WriteHarnessOutput(
            $"Ordinary drain for target {targetDescription} projected document {documentUuid}. {tally.Describe()}"
        );
    }

    /// <summary>
    /// Harness diagnostics go to NUnit's captured test output, which lands in the TRX for the scenario
    /// and in the console for failed tests. The E2E project's Serilog <c>TestLogger</c> is an instance
    /// the static harness cannot reach, and nothing assigns Serilog's static logger, so writing there
    /// would be silently dropped.
    /// </summary>
    private static void WriteHarnessOutput(string message) =>
        TestContext.Out.WriteLine($"[RepresentationRestamp] {message}");

    /// <summary>
    /// The ordinary drain loop, isolated from other scenarios' work. The scenario's obligation is that
    /// its own Tracking restamp work gets projected, so the drain completes as soon as the own document
    /// is projected and merely reports any foreign rows still queued. NoEligibleWork without the own
    /// document projected, any outcome other than PageProcessed, and a run of pages that acknowledge
    /// nothing all fail with the drain diagnostics instead of waiting for the budget.
    /// </summary>
    internal static async Task DrainUntilOwnWorkProjectedAsync(
        RepresentationRestampDrainTally tally,
        Func<CancellationToken, Task<DocumentCacheProjectionDrainPageResult>> processPageAsync,
        Func<CancellationToken, Task<bool>> isOwnDocumentProjectedAsync,
        Func<Task<string>> describeDiagnosticsAsync,
        string targetDescription,
        int maxConsecutivePagesWithoutAcknowledgement,
        CancellationToken cancellationToken
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConsecutivePagesWithoutAcknowledgement);
        while (true)
        {
            DocumentCacheProjectionDrainPageResult result = await processPageAsync(cancellationToken);
            tally.Record(result);
            switch (result.Outcome)
            {
                case DocumentCacheProjectionDrainPageOutcome.PageProcessed:
                    if (await isOwnDocumentProjectedAsync(cancellationToken))
                    {
                        return;
                    }

                    if (
                        tally.ConsecutivePagesWithoutAcknowledgement
                        >= maxConsecutivePagesWithoutAcknowledgement
                    )
                    {
                        throw new InvalidOperationException(
                            await DrainFailureMessageAsync(
                                targetDescription,
                                $"made no progress: {maxConsecutivePagesWithoutAcknowledgement} consecutive page(s) acknowledged no work while the own document is still not projected",
                                describeDiagnosticsAsync
                            )
                        );
                    }

                    continue;

                case DocumentCacheProjectionDrainPageOutcome.NoEligibleWork:
                    if (await isOwnDocumentProjectedAsync(cancellationToken))
                    {
                        return;
                    }

                    throw new InvalidOperationException(
                        await DrainFailureMessageAsync(
                            targetDescription,
                            "reported NoEligibleWork but the Tracking restamp's own document is not projected, so its enqueued work was never acknowledged",
                            describeDiagnosticsAsync
                        )
                    );

                default:
                    throw new InvalidOperationException(
                        await DrainFailureMessageAsync(
                            targetDescription,
                            $"ended with outcome '{result.Outcome}' instead of PageProcessed or NoEligibleWork",
                            describeDiagnosticsAsync
                        )
                    );
            }
        }
    }

    private static async Task<string> DrainFailureMessageAsync(
        string targetDescription,
        string reason,
        Func<Task<string>> describeDiagnosticsAsync
    ) =>
        $"Representation-restamp phase '{OrdinaryDrainPhase}' failed for target {targetDescription}: "
        + $"the ordinary drain {reason}. {await DescribeSafelyAsync(describeDiagnosticsAsync)}";

    private static async Task<bool> IsProjectedAsync(
        IRepresentationRestampE2EProviderOperations providerOperations,
        string connectionString,
        Guid documentUuid,
        CancellationToken cancellationToken
    )
    {
        await using DbConnection connection = providerOperations.OpenConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await providerOperations.IsProjectedAsync(connection, documentUuid, cancellationToken);
    }

    private static async Task<
        IReadOnlyList<RepresentationRestampE2EResidualWorkRow>
    > ReadResidualWorkBoundedAsync(
        IRepresentationRestampE2EProviderOperations providerOperations,
        string connectionString
    )
    {
        using var readBudget = new CancellationTokenSource(ResidualWorkReadBudget);
        return await ReadResidualWorkAsync(providerOperations, connectionString, readBudget.Token);
    }

    /// <summary>
    /// Builds the drain diagnostics attached to a drain failure: the page tallies, the projector's own
    /// per-document failure snapshot, and a fresh read of the work rows still queued. The residual read
    /// gets its own short budget because the drain budget has already been spent when this runs.
    /// </summary>
    private static async Task<string> DescribeDrainAsync(
        RepresentationRestampDrainTally tally,
        DocumentCacheProjectionTargetRuntimeContext context,
        IRepresentationRestampE2EProviderOperations providerOperations,
        string connectionString,
        Guid documentUuid
    )
    {
        IReadOnlyList<RepresentationRestampE2EResidualWorkRow> residualWork =
            await ReadResidualWorkBoundedAsync(providerOperations, connectionString);
        return FormatDrainDiagnostics(
            tally,
            residualWork,
            DescribeProjectorFailures(context.FailureBackoffState.CreateFailureDiagnosticsSnapshot()),
            documentUuid
        );
    }

    internal static string FormatDrainDiagnostics(
        RepresentationRestampDrainTally tally,
        IReadOnlyList<RepresentationRestampE2EResidualWorkRow> residualWork,
        IReadOnlyList<string> projectorFailures,
        Guid documentUuid
    ) =>
        $"{tally.Describe()} Residual work rows ({residualWork.Count}, own document {documentUuid}): "
        + $"{DescribeResidualWork(residualWork, documentUuid)}. Projector failure diagnostics ({projectorFailures.Count}): "
        + $"{(projectorFailures.Count == 0 ? "none" : string.Join("; ", projectorFailures))}.";

    internal static string DescribeResidualWork(
        IReadOnlyList<RepresentationRestampE2EResidualWorkRow> residualWork,
        Guid documentUuid
    ) =>
        residualWork.Count == 0
            ? "none"
            : string.Join(
                "; ",
                residualWork.Select(row =>
                    $"[{(row.DocumentUuid == documentUuid ? "own" : "foreign")} DocumentId={row.DocumentId} "
                    + $"DocumentUuid={row.DocumentUuid} RequiredContentVersion={row.RequiredContentVersion} "
                    + $"CanonicalContentVersion={row.CanonicalContentVersion} "
                    + $"CacheContentVersion={(row.CacheContentVersion is null ? "absent" : row.CacheContentVersion.Value.ToString())} "
                    + $"FirstEnqueuedAt={row.FirstEnqueuedAt:O}]"
                )
            );

    internal static IReadOnlyList<string> DescribeProjectorFailures(
        DocumentCacheProjectionFailureDiagnostics diagnostics
    ) =>
        diagnostics
            .DocumentDiagnostics.Select(diagnostic =>
                $"DocumentId={diagnostic.DocumentId} {diagnostic.Category}: {diagnostic.Message}"
            )
            .ToList();

    private static async Task<IReadOnlyList<RepresentationRestampE2EResidualWorkRow>> ReadResidualWorkAsync(
        IRepresentationRestampE2EProviderOperations providerOperations,
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        await using DbConnection connection = providerOperations.OpenConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await providerOperations.ReadResidualWorkAsync(connection, cancellationToken);
    }

    private static async Task<ProjectionRuntime> CreateProjectionRuntimeAsync(
        DocumentCacheAdminCliTarget target,
        CancellationToken cancellationToken
    )
    {
        ServiceProvider serviceProvider = await CreateProjectionServiceProviderAsync(
            target.ProviderToken,
            target.AppSettingsDatastore,
            target.ApiSchemaDirectory,
            cancellationToken
        );
        try
        {
            DocumentCacheProjectionTargetRuntimeContext context = await serviceProvider
                .GetRequiredService<IDocumentCacheProjectionTargetRuntimeContextFactory>()
                .CreateAsync(CreateExecutionContext(target), cancellationToken);
            return new ProjectionRuntime(serviceProvider, context);
        }
        catch
        {
            await serviceProvider.DisposeAsync();
            throw;
        }
    }

    private static DocumentCacheTargetExecutionContext CreateExecutionContext(
        DocumentCacheAdminCliTarget target
    ) =>
        new(
            DocumentCacheTargetKey.Create(target.TenantKey, target.DataStoreId),
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                true,
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(10),
                10,
                1,
                TimeSpan.FromSeconds(1),
                1000,
                TimeSpan.FromMinutes(1)
            ),
            new DocumentCacheTargetDataStoreMetadata(target.DataStoreId, target.AppSettingsDatastore),
            new DocumentCacheTargetConnectionInput(target.ProviderToken, target.ConnectionString),
            new DocumentCachePhysicalSourceFingerprint(
                "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            ),
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            new DocumentCacheInventoryValidationResult(
                DocumentCacheInventoryStatus.Satisfied,
                "Inventory satisfied."
            ),
            new DocumentCacheEnqueueTriggerValidationResult(
                DocumentCacheEnqueueTriggerStatus.Satisfied,
                "Enqueue trigger satisfied."
            ),
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );

    private static string TargetDescription(DocumentCacheAdminCliTarget target) =>
        $"'{target.TenantKey}':{target.DataStoreId}";

    /// <summary>
    /// Runs one harness phase under its own budget. Only a cancellation caused by this phase's budget
    /// becomes a <see cref="TimeoutException"/> that names the phase, the elapsed time, and the budget;
    /// cancellation from another token and every other failure propagate unchanged.
    /// </summary>
    internal static async Task<T> RunPhaseAsync<T>(
        string phaseName,
        TimeSpan budget,
        string targetDescription,
        Func<CancellationToken, Task<T>> action,
        Func<Task<string>>? describeTimeoutAsync = null
    )
    {
        using var budgetSource = new CancellationTokenSource(budget);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await action(budgetSource.Token);
        }
        catch (OperationCanceledException exception) when (budgetSource.IsCancellationRequested)
        {
            throw new TimeoutException(
                PhaseTimeoutMessage(
                    phaseName,
                    stopwatch.Elapsed,
                    budget,
                    targetDescription,
                    await DescribeSafelyAsync(describeTimeoutAsync)
                ),
                exception
            );
        }
    }

    internal static async Task RunPhaseAsync(
        string phaseName,
        TimeSpan budget,
        string targetDescription,
        Func<CancellationToken, Task> action,
        Func<Task<string>>? describeTimeoutAsync = null
    ) =>
        await RunPhaseAsync(
            phaseName,
            budget,
            targetDescription,
            async cancellationToken =>
            {
                await action(cancellationToken);
                return true;
            },
            describeTimeoutAsync
        );

    /// <summary>
    /// Diagnostics must never hide the failure they describe: a failing describer is reported inline
    /// instead of replacing the timeout or drain failure.
    /// </summary>
    private static async Task<string?> DescribeSafelyAsync(Func<Task<string>>? describeAsync)
    {
        if (describeAsync is null)
        {
            return null;
        }

        try
        {
            return await describeAsync();
        }
        catch (Exception exception)
        {
            return $"Drain diagnostics unavailable: {exception.GetType().Name}: {exception.Message}";
        }
    }

    internal static string PhaseTimeoutMessage(
        string phaseName,
        TimeSpan elapsed,
        TimeSpan budget,
        string targetDescription,
        string? detail = null
    ) =>
        $"Timed out in representation-restamp phase '{phaseName}' after {elapsed} (budget {budget}) "
        + $"for target {targetDescription}."
        + (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}");

    internal static async Task<ServiceProvider> CreateProjectionServiceProviderAsync(
        RelationalProviderToken providerToken,
        string appSettingsDatastore,
        string apiSchemaDirectory,
        CancellationToken cancellationToken
    )
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:Datastore"] = appSettingsDatastore,
                    ["AppSettings:UseApiSchemaPath"] = "true",
                    ["AppSettings:ApiSchemaPath"] = apiSchemaDirectory,
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services
            .AddOptions<EdFi.DataManagementService.Core.Configuration.AppSettings>()
            .Bind(configuration.GetSection("AppSettings"));
        services.AddDmsDefaultConfiguration(
            Log.Logger,
            configuration.GetSection("CircuitBreaker"),
            configuration.GetSection("DeadlockRetry"),
            maskRequestBodyInLogs: false
        );
        services.AddSingleton<IOptions<DocumentCacheOptions>>(Options.Create(new DocumentCacheOptions()));
        services.AddSingleton(
            new DeadlockRetrySettings
            {
                MaxRetryAttempts = 0,
                BaseDelayMilliseconds = 1,
                UseJitter = false,
            }
        );
        switch (providerToken.Value)
        {
            case RelationalProviderToken.PostgresqlValue:
                services.AddPostgresqlDocumentCacheRuntimeServices(configuration);
                break;
            case RelationalProviderToken.SqlServerValue:
                services.AddMssqlDocumentCacheRuntimeServices(configuration);
                break;
            default:
                throw new InvalidOperationException("Unsupported representation-restamp E2E provider.");
        }

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        try
        {
            await serviceProvider
                .GetRequiredService<IEffectiveSchemaBootstrapper>()
                .InitializeAsync(cancellationToken);
            return serviceProvider;
        }
        catch
        {
            await serviceProvider.DisposeAsync();
            throw;
        }
    }

    private static async Task<JsonObject> RunCliAsync(
        string command,
        long dataStoreId,
        Uri configurationServiceBaseUri,
        string appSettingsDatastore,
        string apiSchemaDirectory,
        IReadOnlyList<string> commandArguments
    )
    {
        IReadOnlyList<string> arguments = BuildCliProcessArguments(
            ToolProjectPath(),
            BuildConfiguration(),
            command,
            dataStoreId,
            commandArguments
        );

        ProcessResult result = await RunProcessAsync(
            "dotnet",
            arguments,
            environment: new Dictionary<string, string>
            {
                ["AppSettings__Datastore"] = appSettingsDatastore,
                ["AppSettings__UseApiSchemaPath"] = "true",
                ["AppSettings__ApiSchemaPath"] = apiSchemaDirectory,
                ["ConfigurationServiceSettings__BaseUrl"] = configurationServiceBaseUri.ToString(),
                ["ConfigurationServiceSettings__ClientId"] = ConfigurationServiceClientId,
                ["ConfigurationServiceSettings__ClientSecret"] = ConfigurationServiceClientSecret,
                ["ConfigurationServiceSettings__Scope"] = ConfigurationServiceScope,
                ["ConfigurationServiceSettings__EncryptionKey"] = ConfigurationServiceEncryptionKey,
            }
        );

        result
            .ExitCode.Should()
            .Be(0, $"stdout: {result.StandardOutput}{Environment.NewLine}stderr: {result.StandardError}");
        return JsonNode.Parse(result.StandardOutput)!.AsObject();
    }

    private static async Task WaitForDmsAsync()
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{AppSettings.DmsPort}"),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync("/health", timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The restarted DMS process is not listening yet.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        }

        throw new TimeoutException("DMS did not become healthy after the representation restamp.");
    }

    private static async Task<long> ReadCanonicalContentVersionAsync(
        IRepresentationRestampE2EProviderOperations providerOperations,
        string connectionString,
        Guid documentUuid,
        CancellationToken cancellationToken
    )
    {
        await using DbConnection connection = providerOperations.OpenConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await providerOperations.ReadCanonicalContentVersionAsync(
            connection,
            documentUuid,
            cancellationToken
        );
    }

    private static async Task<long?> ReadRequiredWorkVersionAsync(
        IRepresentationRestampE2EProviderOperations providerOperations,
        string connectionString,
        Guid documentUuid,
        CancellationToken cancellationToken
    )
    {
        await using DbConnection connection = providerOperations.OpenConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await providerOperations.ReadRequiredWorkVersionAsync(
            connection,
            documentUuid,
            cancellationToken
        );
    }

    private static async Task WaitForProjectedCacheAsync(
        IRepresentationRestampE2EProviderOperations providerOperations,
        string connectionString,
        Guid documentUuid
    )
    {
        await WaitForConditionAsync(
            async cancellationToken =>
            {
                await using DbConnection connection = providerOperations.OpenConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                return await providerOperations.IsProjectedAsync(connection, documentUuid, cancellationToken);
            },
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(2),
            $"representation-restamp projection for document {documentUuid}: projection work to be absent and the document-cache content version to equal the canonical content version"
        );
    }

    internal static async Task WaitForConditionAsync(
        Func<CancellationToken, Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan pollInterval,
        string timeoutContext
    )
    {
        using var timeoutSource = new CancellationTokenSource(timeout);

        try
        {
            while (true)
            {
                if (await condition(timeoutSource.Token))
                {
                    return;
                }

                await Task.Delay(pollInterval, timeoutSource.Token);
            }
        }
        catch (OperationCanceledException exception) when (timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after {timeout} waiting for {timeoutContext}.", exception);
        }
    }

    internal static async Task RunWithDirectoryCleanupAsync(string directory, Func<Task> action)
    {
        try
        {
            await action();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="cleanup"/> after <paramref name="action"/> whether or not the action
    /// threw, without letting a cleanup failure replace the primary failure. A single failure
    /// propagates unchanged; when both fail the result is a flattened AggregateException whose
    /// first inner exception is the primary one. Flattening keeps that ordering when a cleanup
    /// delegate is itself built from this helper.
    /// </summary>
    internal static async Task RunWithCleanupAsync(string context, Func<Task> action, Func<Task> cleanup)
    {
        Exception? primaryFailure = null;

        try
        {
            await action();
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        try
        {
            await cleanup();
        }
        catch (Exception cleanupFailure)
        {
            if (primaryFailure is null)
            {
                throw;
            }

            throw new AggregateException(
                $"{context}: the primary failure was followed by a cleanup failure",
                primaryFailure,
                cleanupFailure
            ).Flatten();
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    /// <summary>
    /// Describes a Docker command that ran and reported failure. The container name is the first
    /// thing to check: it differs by image mode, and a name that does not exist otherwise surfaces
    /// much later as an unrelated API schema loading error from the DocumentCacheAdmin CLI.
    /// </summary>
    internal static string DockerFailureMessage(
        string operation,
        string containerName,
        int exitCode,
        string standardOutput,
        string standardError
    ) =>
        string.Join(
            Environment.NewLine,
            $"docker {operation} failed for DMS container '{containerName}' (exit code {exitCode}). "
                + "Set AppSettings__DmsContainerName to the running DMS container "
                + $"(local image: {AppSettings.DefaultDmsContainerName}; published image: dms-published-dms-1).",
            $"stdout: {standardOutput.Trim()}",
            $"stderr: {standardError.Trim()}"
        );

    /// <summary>
    /// Describes a Docker command that could not be launched at all, which carries no exit code to
    /// report. Kept distinct from <see cref="DockerFailureMessage"/> so a missing Docker CLI is not
    /// mistaken for a container that rejected the operation.
    /// </summary>
    internal static string DockerStartFailureMessage(string operation, string containerName) =>
        $"failed to start docker for operation '{operation}' on DMS container '{containerName}'; "
        + "is Docker installed and on PATH?";

    /// <summary>
    /// Runs one Docker command against the DMS container and fails immediately if it did not
    /// succeed. Without this the discarded exit code let a wrong container name run on until the
    /// DocumentCacheAdmin CLI failed to load an API schema that was never copied out.
    /// </summary>
    private static async Task RunDockerAsync(
        string operation,
        string containerName,
        IReadOnlyList<string> arguments
    )
    {
        ProcessResult result;

        try
        {
            result = await RunProcessAsync("docker", arguments);
        }
        catch (Exception exception)
            when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                DockerStartFailureMessage(operation, containerName),
                exception
            );
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                DockerFailureMessage(
                    operation,
                    containerName,
                    result.ExitCode,
                    result.StandardOutput,
                    result.StandardError
                )
            );
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null
    )
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryRoot(),
            },
        };

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        process.Start().Should().BeTrue();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new(process.ExitCode, await standardOutput, await standardError);
    }

    /// <summary>
    /// Builds the <c>dotnet</c> argument list that launches the DocumentCacheAdmin CLI. The
    /// configuration is passed explicitly because <c>dotnet run</c> defaults to Debug, so a
    /// Release test run would otherwise try to start the CLI out of an unbuilt bin/Debug.
    /// </summary>
    internal static IReadOnlyList<string> BuildCliProcessArguments(
        string toolProjectPath,
        string buildConfiguration,
        string command,
        long dataStoreId,
        IReadOnlyList<string> commandArguments
    ) =>
        [
            "run",
            "--project",
            toolProjectPath,
            "--configuration",
            buildConfiguration,
            "--no-build",
            "--",
            command,
            "--data-store-id",
            dataStoreId.ToString(),
            .. commandArguments,
            "--json",
        ];

    private static string ToolProjectPath() =>
        Path.Combine(
            RepositoryRoot(),
            "src",
            "dms",
            "clis",
            "EdFi.DataManagementService.DocumentCacheAdmin",
            "EdFi.DataManagementService.DocumentCacheAdmin.csproj"
        );

    private static string BuildConfiguration() =>
        BuildConfiguration(new DirectoryInfo(AppContext.BaseDirectory));

    /// <summary>
    /// Resolves the build configuration the CLI has to be launched from. The test assembly's own
    /// output directory is the only reliable signal: it is written to bin/&lt;configuration&gt;, and
    /// the CLI is built into the matching directory by the E2E project's build-order reference to
    /// it. Falls back to Debug, which is what <c>dotnet run</c> would have used anyway, so a
    /// layout without a configuration segment behaves exactly as it does today.
    /// </summary>
    internal static string BuildConfiguration(DirectoryInfo startDirectory)
    {
        DirectoryInfo? directory = startDirectory;
        while (directory is not null)
        {
            if (directory.Name is "Debug" or "Release")
            {
                return directory.Name;
            }

            directory = directory.Parent;
        }

        return "Debug";
    }

    private static string RepositoryRoot() => RepositoryRoot(new DirectoryInfo(AppContext.BaseDirectory));

    internal static string RepositoryRoot(DirectoryInfo startDirectory)
    {
        DirectoryInfo? directory = startDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "dms", "EdFi.DataManagementService.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

/// <summary>
/// Running totals for one ordinary drain, kept by the harness so a drain failure can say how many
/// pages ran and whether any of them acknowledged work. Consecutive pages without an acknowledgement
/// are counted because a residual row the projector cannot acknowledge produces exactly that pattern.
/// </summary>
internal sealed class RepresentationRestampDrainTally
{
    public int Pages { get; private set; }

    public int ProcessedItems { get; private set; }

    public int AcknowledgedOrRemovedItems { get; private set; }

    public int DocumentScopedFailures { get; private set; }

    public int ConsecutivePagesWithoutAcknowledgement { get; private set; }

    public DocumentCacheProjectionDrainPageOutcome? LastOutcome { get; private set; }

    public void Record(DocumentCacheProjectionDrainPageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Pages++;
        ProcessedItems += result.ProcessedItemCount;
        AcknowledgedOrRemovedItems += result.AcknowledgedOrRemovedItemCount;
        DocumentScopedFailures += result.DocumentScopedFailureCount;
        LastOutcome = result.Outcome;
        ConsecutivePagesWithoutAcknowledgement =
            result.Outcome == DocumentCacheProjectionDrainPageOutcome.PageProcessed
            && result.AcknowledgedOrRemovedItemCount == 0
                ? ConsecutivePagesWithoutAcknowledgement + 1
                : 0;
    }

    public string Describe() =>
        $"Drain pages={Pages} processed={ProcessedItems} acknowledgedOrRemoved={AcknowledgedOrRemovedItems} "
        + $"documentScopedFailures={DocumentScopedFailures} consecutivePagesWithoutAcknowledgement={ConsecutivePagesWithoutAcknowledgement} "
        + $"lastOutcome={(LastOutcome is null ? "none" : LastOutcome.Value.ToString())}.";
}
