// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlDocumentCacheReadLookupAdapter(
    NpgsqlDataSourceCache dataSourceCache,
    IRelationalWriteExceptionClassifier writeExceptionClassifier,
    IDocumentCacheProviderCommandTimeoutClassifier providerCommandTimeoutClassifier,
    ILogger<PostgresqlDocumentCacheReadLookupAdapter> logger,
    IDocumentCacheReadResponseShaper? responseShaper = null
) : DocumentCacheReadLookupAdapterBase(responseShaper)
{
    private readonly NpgsqlDataSourceCache _dataSourceCache =
        dataSourceCache ?? throw new ArgumentNullException(nameof(dataSourceCache));
    private readonly IRelationalWriteExceptionClassifier _writeExceptionClassifier =
        writeExceptionClassifier ?? throw new ArgumentNullException(nameof(writeExceptionClassifier));
    private readonly IDocumentCacheProviderCommandTimeoutClassifier _providerCommandTimeoutClassifier =
        providerCommandTimeoutClassifier
        ?? throw new ArgumentNullException(nameof(providerCommandTimeoutClassifier));
    private readonly ILogger<PostgresqlDocumentCacheReadLookupAdapter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    protected override SqlDialect Dialect => SqlDialect.Pgsql;

    protected override RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

    protected override async Task<TResult> ExecuteReaderAsync<TResult>(
        DocumentCacheTargetExecutionContext targetContext,
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(readAsync);

        _logger.LogDebug(
            "Executing PostgreSQL DocumentCache read lookup for target {TargetKey} with {ParameterCount} parameters",
            LogSanitizer.SanitizeForLog(targetContext.TargetKey.ToString()),
            command.Parameters.Count
        );

        NpgsqlDataSource dataSource = _dataSourceCache.GetOrCreate(targetContext.ConnectionInput.Value);
        await using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using NpgsqlCommand dbCommand = connection.CreateCommand();
        dbCommand.CommandText = command.CommandText;

        AddParameters(dbCommand, command.Parameters);

        await using var reader = new DbRelationalCommandReader(
            await dbCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)
        );

        return await readAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    protected override bool IsCacheUnavailable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return _providerCommandTimeoutClassifier.IsProviderCommandTimeout(exception)
            || exception is DbException dbException
                && _writeExceptionClassifier.IsTransientFailure(dbException);
    }

    private static void AddParameters(NpgsqlCommand dbCommand, IReadOnlyList<RelationalParameter> parameters)
    {
        foreach (RelationalParameter parameter in parameters)
        {
            NpgsqlParameter dbParameter = dbCommand.CreateParameter();
            dbParameter.ParameterName = parameter.Name;
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            parameter.ConfigureParameter?.Invoke(dbParameter);
            dbCommand.Parameters.Add(dbParameter);
        }
    }
}
