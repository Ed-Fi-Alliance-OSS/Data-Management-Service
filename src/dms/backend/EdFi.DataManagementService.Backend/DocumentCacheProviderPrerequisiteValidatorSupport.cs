// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal static class DocumentCacheProviderPrerequisiteValidatorSupport
{
    private const string ReadCommittedSnapshotCommandText = """
        SELECT CONVERT(int, [is_read_committed_snapshot_on])
        FROM [sys].[databases]
        WHERE [name] = DB_NAME();
        """;

    private const string NestedTriggersCommandText = """
        SELECT CONVERT(int, [value_in_use])
        FROM [sys].[configurations]
        WHERE [name] = N'nested triggers';
        """;

    public static async Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateInitializationAsync(
        Func<DbConnection> connectionFactory,
        DocumentCacheLifecycleObservation lifecycle,
        ILogger logger,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        DocumentCacheSqlServerPrerequisiteDetails details = await ReadSqlServerPrerequisitesAsync(
                connectionFactory,
                logger,
                cancellationToken
            )
            .ConfigureAwait(false);

        return DocumentCacheProviderPrerequisiteValidationResult.Initialization(details, lifecycle);
    }

    public static async Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPreflightAsync(
        Func<DbConnection> connectionFactory,
        ILogger logger,
        CancellationToken cancellationToken = default
    )
    {
        DocumentCacheSqlServerPrerequisiteDetails details = await ReadSqlServerPrerequisitesAsync(
                connectionFactory,
                logger,
                cancellationToken
            )
            .ConfigureAwait(false);

        return DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(details);
    }

    private static async Task<DocumentCacheSqlServerPrerequisiteDetails> ReadSqlServerPrerequisitesAsync(
        Func<DbConnection> connectionFactory,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await using var connection = connectionFactory();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            DocumentCacheProviderPrerequisiteResult readCommittedSnapshot = await ReadPrerequisiteAsync(
                    connection,
                    DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                    ReadCommittedSnapshotCommandText,
                    "SQL Server READ_COMMITTED_SNAPSHOT is enabled.",
                    "SQL Server READ_COMMITTED_SNAPSHOT is disabled.",
                    "SQL Server READ_COMMITTED_SNAPSHOT is unreadable.",
                    logger,
                    cancellationToken
                )
                .ConfigureAwait(false);

            DocumentCacheProviderPrerequisiteResult nestedTriggers = await ReadPrerequisiteAsync(
                    connection,
                    DocumentCacheProviderPrerequisiteName.NestedTriggers,
                    NestedTriggersCommandText,
                    "SQL Server nested triggers are enabled.",
                    "SQL Server nested triggers are disabled.",
                    "SQL Server nested triggers are unreadable.",
                    logger,
                    cancellationToken
                )
                .ConfigureAwait(false);

            return new DocumentCacheSqlServerPrerequisiteDetails(readCommittedSnapshot, nestedTriggers);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "DocumentCache SQL Server prerequisite validation failed while opening provider connection"
            );

            return new DocumentCacheSqlServerPrerequisiteDetails(
                Unreadable(
                    DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                    "SQL Server READ_COMMITTED_SNAPSHOT is unreadable."
                ),
                Unreadable(
                    DocumentCacheProviderPrerequisiteName.NestedTriggers,
                    "SQL Server nested triggers are unreadable."
                )
            );
        }
    }

    private static async Task<DocumentCacheProviderPrerequisiteResult> ReadPrerequisiteAsync(
        DbConnection connection,
        DocumentCacheProviderPrerequisiteName name,
        string commandText,
        string satisfiedMessage,
        string disabledMessage,
        string unreadableMessage,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        try
        {
            int? value = await ReadOptionalIntAsync(connection, commandText, cancellationToken)
                .ConfigureAwait(false);

            return value switch
            {
                1 => new DocumentCacheProviderPrerequisiteResult(
                    name,
                    DocumentCacheProviderPrerequisiteStatus.Satisfied,
                    satisfiedMessage
                ),
                0 => new DocumentCacheProviderPrerequisiteResult(
                    name,
                    DocumentCacheProviderPrerequisiteStatus.Disabled,
                    disabledMessage
                ),
                _ => Unreadable(name, unreadableMessage),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "DocumentCache SQL Server prerequisite {PrerequisiteName} validation failed while reading provider metadata",
                name
            );
            return Unreadable(name, unreadableMessage);
        }
    }

    private static async Task<int?> ReadOptionalIntAsync(
        DbConnection connection,
        string commandText,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = commandText;

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null || value == DBNull.Value
            ? null
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static DocumentCacheProviderPrerequisiteResult Unreadable(
        DocumentCacheProviderPrerequisiteName name,
        string message
    ) => new(name, DocumentCacheProviderPrerequisiteStatus.Unreadable, message);
}
