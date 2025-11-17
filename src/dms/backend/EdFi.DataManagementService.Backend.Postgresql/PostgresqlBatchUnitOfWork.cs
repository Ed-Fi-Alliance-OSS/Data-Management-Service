// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using static EdFi.DataManagementService.Backend.PartitionUtility;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlBatchUnitOfWork : IBatchUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly ILogger<PostgresqlBatchUnitOfWork> _logger;
    private readonly IUpsertDocument _upsertDocument;
    private readonly IUpdateDocumentById _updateDocumentById;
    private readonly IDeleteDocumentById _deleteDocumentById;
    private readonly ISqlAction _sqlAction;
    private bool _completed;

    public PostgresqlBatchUnitOfWork(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ILogger<PostgresqlBatchUnitOfWork> logger,
        IUpsertDocument upsertDocument,
        IUpdateDocumentById updateDocumentById,
        IDeleteDocumentById deleteDocumentById,
        ISqlAction sqlAction
    )
    {
        _connection = connection;
        _transaction = transaction;
        _logger = logger;
        _upsertDocument = upsertDocument;
        _updateDocumentById = updateDocumentById;
        _deleteDocumentById = deleteDocumentById;
        _sqlAction = sqlAction;
    }

    public Task<UpsertResult> UpsertDocumentAsync(IUpsertRequest request)
    {
        return _upsertDocument.Upsert(request, _connection, _transaction);
    }

    public Task<UpdateResult> UpdateDocumentByIdAsync(IUpdateRequest request)
    {
        return _updateDocumentById.UpdateById(request, _connection, _transaction);
    }

    public Task<DeleteResult> DeleteDocumentByIdAsync(IDeleteRequest request)
    {
        return _deleteDocumentById.DeleteById(request, _connection, _transaction);
    }

    public async Task<DocumentUuid?> ResolveDocumentUuidAsync(ReferentialId referentialId, TraceId traceId)
    {
        PartitionKey partitionKey = PartitionKeyFor(referentialId);
        _logger.LogDebug(
            "Resolving referential id {ReferentialId} (partition {PartitionKey}) - TraceId {TraceId}",
            referentialId.Value,
            partitionKey.Value,
            traceId.Value
        );

        var document = await _sqlAction.FindDocumentByReferentialId(
            referentialId,
            partitionKey,
            _connection,
            _transaction,
            traceId
        );

        if (document == null)
        {
            _logger.LogDebug(
                "Referential id {ReferentialId} not found - TraceId {TraceId}",
                referentialId.Value,
                traceId.Value
            );
        }

        return document == null ? null : new DocumentUuid(document.DocumentUuid);
    }

    public Task CommitAsync()
    {
        _completed = true;
        return _transaction.CommitAsync();
    }

    public Task RollbackAsync()
    {
        _completed = true;
        return _transaction.RollbackAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_completed)
            {
                await _transaction.RollbackAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed rolling back batch unit of work transaction. Connection will still be disposed."
            );
        }
        finally
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}

internal sealed class PostgresqlBatchUnitOfWorkFactory : IBatchUnitOfWorkFactory
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IUpsertDocument _upsertDocument;
    private readonly IUpdateDocumentById _updateDocumentById;
    private readonly IDeleteDocumentById _deleteDocumentById;
    private readonly ISqlAction _sqlAction;
    private readonly IsolationLevel _isolationLevel;

    public PostgresqlBatchUnitOfWorkFactory(
        NpgsqlDataSource dataSource,
        ILoggerFactory loggerFactory,
        IUpsertDocument upsertDocument,
        IUpdateDocumentById updateDocumentById,
        IDeleteDocumentById deleteDocumentById,
        ISqlAction sqlAction,
        IOptions<DatabaseOptions> databaseOptions
    )
    {
        _dataSource = dataSource;
        _loggerFactory = loggerFactory;
        _upsertDocument = upsertDocument;
        _updateDocumentById = updateDocumentById;
        _deleteDocumentById = deleteDocumentById;
        _sqlAction = sqlAction;
        _isolationLevel = databaseOptions.Value.IsolationLevel;
    }

    public async Task<IBatchUnitOfWork> BeginAsync(
        TraceId traceId,
        IReadOnlyDictionary<string, string> headers
    )
    {
        var connection = await _dataSource.OpenConnectionAsync();

        try
        {
            var transaction = await connection.BeginTransactionAsync(_isolationLevel);

            return new PostgresqlBatchUnitOfWork(
                connection,
                transaction,
                _loggerFactory.CreateLogger<PostgresqlBatchUnitOfWork>(),
                _upsertDocument,
                _updateDocumentById,
                _deleteDocumentById,
                _sqlAction
            );
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException(
                "Failed to begin PostgreSQL batch unit of work transaction.",
                ex
            );
        }
    }
}
