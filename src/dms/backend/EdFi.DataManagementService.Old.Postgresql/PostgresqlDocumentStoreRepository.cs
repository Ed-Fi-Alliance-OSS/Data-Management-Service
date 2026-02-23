// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Diagnostics;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Old.Postgresql.Operation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DataManagementService.Old.Postgresql;

public class PostgresqlDocumentStoreRepository(
    NpgsqlDataSourceProvider _dataSourceProvider,
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
            upsertRequest.TraceId.Value
        );

        var sw = Stopwatch.StartNew();
        try
        {
            await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(_isolationLevel);

            UpsertResult result = await _upsertDocument.Upsert(upsertRequest, connection, transaction);

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

            sw.Stop();
            _logger.LogDebug(
                "UpsertDocument completed in {TransactionDurationMs}ms with result {ResultType} - {TraceId}",
                sw.ElapsedMilliseconds,
                result.GetType().Name,
                upsertRequest.TraceId.Value
            );

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogCritical(
                ex,
                "Uncaught UpsertDocument failure after {TransactionDurationMs}ms - {TraceId}",
                sw.ElapsedMilliseconds,
                upsertRequest.TraceId.Value
            );
            return new UpsertResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<GetResult> GetDocumentById(IGetRequest getRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.GetDocumentById - {TraceId}",
            getRequest.TraceId.Value
        );

        try
        {
            await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync();

            GetResult result = await _getDocumentById.GetById(getRequest, connection, null);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Uncaught GetDocumentById failure - {TraceId}", getRequest.TraceId.Value);
            return new GetResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.UpdateDocumentById - {TraceId}",
            updateRequest.TraceId.Value
        );

        var sw = Stopwatch.StartNew();
        try
        {
            await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(_isolationLevel);

            UpdateResult result = await _updateDocumentById.UpdateById(
                updateRequest,
                connection,
                transaction
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

            sw.Stop();
            _logger.LogDebug(
                "UpdateDocumentById completed in {TransactionDurationMs}ms with result {ResultType} - {TraceId}",
                sw.ElapsedMilliseconds,
                result.GetType().Name,
                updateRequest.TraceId.Value
            );

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogCritical(
                ex,
                "Uncaught UpdateDocumentById failure after {TransactionDurationMs}ms - {TraceId}",
                sw.ElapsedMilliseconds,
                updateRequest.TraceId.Value
            );
            return new UpdateResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.DeleteDocumentById - {TraceId}",
            deleteRequest.TraceId.Value
        );

        var sw = Stopwatch.StartNew();
        try
        {
            await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync();
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

            sw.Stop();
            _logger.LogDebug(
                "DeleteDocumentById completed in {TransactionDurationMs}ms with result {ResultType} - {TraceId}",
                sw.ElapsedMilliseconds,
                result.GetType().Name,
                deleteRequest.TraceId.Value
            );

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogCritical(
                ex,
                "Uncaught DeleteDocumentById failure after {TransactionDurationMs}ms - {TraceId}",
                sw.ElapsedMilliseconds,
                deleteRequest.TraceId.Value
            );
            return new DeleteResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.QueryDocuments - {TraceId}",
            queryRequest.TraceId.Value
        );

        try
        {
            return await _queryDocument.QueryDocuments(queryRequest);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                ex,
                "Uncaught QueryDocuments failure - {TraceId}",
                queryRequest.TraceId.Value
            );
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
    }
}
