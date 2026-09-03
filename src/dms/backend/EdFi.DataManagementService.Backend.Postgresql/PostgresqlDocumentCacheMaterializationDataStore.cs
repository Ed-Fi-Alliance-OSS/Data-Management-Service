// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlDocumentCacheMaterializationDataStore(
    NpgsqlDataSourceCache dataSourceCache,
    ILogger<PostgresqlDocumentCacheMaterializationDataStore> logger
) : IDocumentCacheMaterializationDataStore
{
    private readonly NpgsqlDataSourceCache _dataSourceCache =
        dataSourceCache ?? throw new ArgumentNullException(nameof(dataSourceCache));
    private readonly ILogger<PostgresqlDocumentCacheMaterializationDataStore> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public SqlDialect Dialect => SqlDialect.Pgsql;

    public DocumentCacheMaterializationRequest BindToTargetDataStore(
        DocumentCacheMaterializationRequest request
    )
    {
        DocumentCacheMaterializationDataStoreGuards.RequireBoundTargetDataStore(request, Dialect);
        return request;
    }

    public async Task<TResult> ExecuteReaderAsync<TResult>(
        DocumentCacheMaterializationRequest request,
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(readAsync);
        DocumentCacheMaterializationDataStoreGuards.RequireValidatedTargetContext(request, Dialect);

        _logger.LogDebug(
            "Executing PostgreSQL DocumentCache materialization command for target {TargetKey} with {ParameterCount} parameters",
            LogSanitizer.SanitizeForLog(request.TargetContext.TargetKey.ToString()),
            command.Parameters.Count
        );

        await using var leased = await OpenConnectionAsync(request, cancellationToken).ConfigureAwait(false);
        await using var dbCommand = leased.Connection.CreateCommand();
        dbCommand.CommandText = command.CommandText;

        AddParameters(dbCommand, command.Parameters);

        await using var reader = new DbRelationalCommandReader(
            await dbCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)
        );

        return await readAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HydratedPage> HydrateAsync(
        DocumentCacheMaterializationRequest request,
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        HydrationExecutionOptions executionOptions,
        CancellationToken cancellationToken = default
    )
    {
        DocumentCacheMaterializationDataStoreGuards.RequireValidatedTargetContext(request, Dialect);

        await using var leased = await OpenConnectionAsync(request, cancellationToken).ConfigureAwait(false);

        return await HydrationExecutor
            .ExecuteAsync(
                leased.Connection,
                plan,
                keyset,
                SqlDialect.Pgsql,
                transaction: null,
                executionOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a connection whose data-source lease travels with it, so the caller releases both
    /// together and the source cannot be disposed mid-materialization.
    /// </summary>
    private async Task<LeasedNpgsqlConnection> OpenConnectionAsync(
        DocumentCacheMaterializationRequest request,
        CancellationToken cancellationToken
    )
    {
        var dataStore = DocumentCacheMaterializationDataStoreGuards.RequireBoundTargetDataStore(
            request,
            Dialect
        );

        return await _dataSourceCache
            .OpenLeasedConnectionAsync(dataStore.ConnectionString, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void AddParameters(DbCommand dbCommand, IReadOnlyList<RelationalParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            var dbParameter = dbCommand.CreateParameter();
            dbParameter.ParameterName = parameter.Name;
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            parameter.ConfigureParameter?.Invoke(dbParameter);
            dbCommand.Parameters.Add(dbParameter);
        }
    }
}
