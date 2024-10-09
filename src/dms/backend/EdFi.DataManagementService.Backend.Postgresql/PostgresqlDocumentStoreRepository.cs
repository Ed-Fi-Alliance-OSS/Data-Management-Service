// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

public class PostgresqlDocumentStoreRepository(
    NpgsqlDataSource _dataSource,
    ILogger<PostgresqlDocumentStoreRepository> _logger,
    IGetDocumentById _getDocumentById,
    IUpdateDocumentById _updateDocumentById,
    IUpsertDocument _upsertDocument,
    IDeleteDocumentById _deleteDocumentById,
    IQueryDocument _queryDocument,
    IOptions<DatabaseOptions> _databaseOptions
) : IDocumentStoreRepository, IQueryHandler
{
    private readonly IsolationLevel _isolationLevel = _databaseOptions.Value.IsolationLevel;

    public async Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.UpsertDocument - {TraceId}",
            upsertRequest.TraceId
        );

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(_isolationLevel);

            UpsertResult result = await _upsertDocument.Upsert(
                upsertRequest,
                connection,
                transaction,
                upsertRequest.TraceId
            );

            switch (result)
            {
                case UpsertResult.InsertSuccess:
                case UpsertResult.UpdateSuccess:
                    await transaction.CommitAsync();
                    break;
                default:
                    await transaction.RollbackAsync();
                    break;
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Uncaught UpsertDocument failure - {TraceId}", upsertRequest.TraceId);
            return new UpsertResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<GetResult> GetDocumentById(IGetRequest getRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.GetDocumentById - {TraceId}",
            getRequest.TraceId
        );

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(_isolationLevel);

            GetResult result = await _getDocumentById.GetById(getRequest, connection, transaction);

            switch (result)
            {
                case GetResult.GetSuccess:
                    await transaction.CommitAsync();
                    break;
                default:
                    await transaction.RollbackAsync();
                    break;
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Uncaught GetDocumentById failure - {TraceId}", getRequest.TraceId);
            return new GetResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.UpdateDocumentById - {TraceId}",
            updateRequest.TraceId
        );

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(_isolationLevel);

            UpdateResult result = await _updateDocumentById.UpdateById(
                updateRequest,
                connection,
                transaction,
                updateRequest.TraceId
            );

            switch (result)
            {
                case UpdateResult.UpdateSuccess:
                    await transaction.CommitAsync();
                    break;
                default:
                    await transaction.RollbackAsync();
                    break;
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Uncaught UpdateDocumentById failure - {TraceId}", updateRequest.TraceId);
            return new UpdateResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.DeleteDocumentById  - {TraceId}",
            deleteRequest.TraceId
        );

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(_isolationLevel);
            DeleteResult result = await _deleteDocumentById.DeleteById(
                deleteRequest,
                connection,
                transaction
            );

            switch (result)
            {
                case DeleteResult.DeleteSuccess:
                    await transaction.CommitAsync();
                    break;
                default:
                    await transaction.RollbackAsync();
                    break;
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Uncaught DeleteDocumentById failure - {TraceId}", deleteRequest.TraceId);
            return new DeleteResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.QueryDocuments - {TraceId}",
            queryRequest.TraceId
        );

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(_isolationLevel);

            QueryResult result = await _queryDocument.QueryDocuments(queryRequest, connection, transaction);

            switch (result)
            {
                case QueryResult.QuerySuccess:
                    await transaction.CommitAsync();
                    break;
                default:
                    await transaction.RollbackAsync();
                    break;
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Uncaught QueryDocuments failure - {TraceId}", queryRequest.TraceId);
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
    }
}
