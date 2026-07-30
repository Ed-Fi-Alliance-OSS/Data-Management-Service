// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlDocumentCacheMaterializationDataStore : IDocumentCacheMaterializationDataStore
{
    private readonly Func<string, DbConnection> _createConnection;
    private readonly ILogger<MssqlDocumentCacheMaterializationDataStore> _logger;

    public MssqlDocumentCacheMaterializationDataStore(
        ILogger<MssqlDocumentCacheMaterializationDataStore> logger
    )
        : this(connectionString => new SqlConnection(connectionString), logger) { }

    internal MssqlDocumentCacheMaterializationDataStore(
        Func<string, DbConnection> createConnection,
        ILogger<MssqlDocumentCacheMaterializationDataStore> logger
    )
    {
        _createConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public SqlDialect Dialect => SqlDialect.Mssql;

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
            "Executing SQL Server DocumentCache materialization command for target {TargetKey} with {ParameterCount} parameters",
            request.TargetContext.TargetKey,
            command.Parameters.Count
        );

        await using var connection = await OpenConnectionAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await using var dbCommand = connection.CreateCommand();
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

        await using var connection = await OpenConnectionAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return await HydrationExecutor
            .ExecuteAsync(
                connection,
                plan,
                keyset,
                SqlDialect.Mssql,
                transaction: null,
                executionOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<DbConnection> OpenConnectionAsync(
        DocumentCacheMaterializationRequest request,
        CancellationToken cancellationToken
    )
    {
        var dataStore = DocumentCacheMaterializationDataStoreGuards.RequireBoundTargetDataStore(
            request,
            Dialect
        );
        var connection = _createConnection(dataStore.ConnectionString);

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return connection;
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
