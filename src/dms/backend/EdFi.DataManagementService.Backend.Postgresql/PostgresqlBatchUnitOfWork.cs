// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Be.Vlaanderen.Basisregisters.Generators.Guid;
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
    private static readonly Guid ReferentialNamespace = new("edf1edf1-3df1-3df1-3df1-3df1edf1edf1");

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

    public async Task<DocumentUuid?> ResolveDocumentUuidAsync(
        ResourceInfo resourceInfo,
        DocumentIdentity identity,
        TraceId traceId
    )
    {
        ReferentialId referentialId = CreateReferentialId(resourceInfo, identity);
        PartitionKey partitionKey = PartitionKeyFor(referentialId);

        var document = await _sqlAction.FindDocumentByReferentialId(
            referentialId,
            partitionKey,
            _connection,
            _transaction,
            traceId
        );

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

    private static ReferentialId CreateReferentialId(ResourceInfo resourceInfo, DocumentIdentity identity)
    {
        string resourceSegment = $"{resourceInfo.ProjectName.Value}{resourceInfo.ResourceName.Value}";
        string identitySegment = string.Join(
            "#",
            identity.DocumentIdentityElements.Select(element =>
                $"${element.IdentityJsonPath.Value}={element.IdentityValue}"
            )
        );

        Guid referentialGuid = Deterministic.Create(ReferentialNamespace, resourceSegment + identitySegment);
        return new ReferentialId(referentialGuid);
    }
}

internal sealed class PostgresqlBatchUnitOfWorkFactory : IBatchUnitOfWorkFactory
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresqlBatchUnitOfWork> _logger;
    private readonly IUpsertDocument _upsertDocument;
    private readonly IUpdateDocumentById _updateDocumentById;
    private readonly IDeleteDocumentById _deleteDocumentById;
    private readonly ISqlAction _sqlAction;
    private readonly IsolationLevel _isolationLevel;

    public PostgresqlBatchUnitOfWorkFactory(
        NpgsqlDataSource dataSource,
        ILogger<PostgresqlBatchUnitOfWork> logger,
        IUpsertDocument upsertDocument,
        IUpdateDocumentById updateDocumentById,
        IDeleteDocumentById deleteDocumentById,
        ISqlAction sqlAction,
        IOptions<DatabaseOptions> databaseOptions
    )
    {
        _dataSource = dataSource;
        _logger = logger;
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
                _logger,
                _upsertDocument,
                _updateDocumentById,
                _deleteDocumentById,
                _sqlAction
            );
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
