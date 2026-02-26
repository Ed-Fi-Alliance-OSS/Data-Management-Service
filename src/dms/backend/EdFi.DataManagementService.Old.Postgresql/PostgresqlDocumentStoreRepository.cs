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

    /// <summary>
    /// Executes an operation within a transaction, handling commit/rollback,
    /// commit-time serialization/deadlock detection, timing, and error logging.
    /// </summary>
    private async Task<TResult> ExecuteTransactionalAsync<TResult>(
        string operationName,
        string traceId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<TResult>> operation,
        Func<TResult, bool> shouldCommit,
        TResult writeConflictResult,
        TResult unknownFailureResult
    )
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.{OperationName} - {TraceId}",
            operationName,
            traceId
        );

        var sw = Stopwatch.StartNew();
        try
        {
            await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(_isolationLevel);

            TResult result = await operation(connection, transaction);

            if (shouldCommit(result))
            {
                try
                {
                    await transaction.CommitAsync();
                }
                catch (PostgresException pe)
                    when (pe.SqlState == PostgresErrorCodes.SerializationFailure
                        || pe.SqlState == PostgresErrorCodes.DeadlockDetected
                    )
                {
                    _logger.LogDebug(
                        pe,
                        "Transaction conflict on commit for {OperationName} - {TraceId}",
                        operationName,
                        traceId
                    );
                    return writeConflictResult;
                }
            }
            else
            {
                await transaction.RollbackAsync();
            }

            sw.Stop();
            _logger.LogInformation(
                "{OperationName} completed in {TransactionDurationMs}ms with result {ResultType} - {TraceId}",
                operationName,
                sw.ElapsedMilliseconds,
                result!.GetType().Name,
                traceId
            );

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogCritical(
                ex,
                "Uncaught {OperationName} failure after {TransactionDurationMs}ms - {TraceId}",
                operationName,
                sw.ElapsedMilliseconds,
                traceId
            );
            return unknownFailureResult;
        }
    }

    public async Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
    {
        return await ExecuteTransactionalAsync(
            "UpsertDocument",
            upsertRequest.TraceId.Value,
            async (connection, transaction) =>
                await _upsertDocument.Upsert(upsertRequest, connection, transaction),
            result => result is UpsertResult.InsertSuccess or UpsertResult.UpdateSuccess,
            new UpsertResult.UpsertFailureWriteConflict(),
            new UpsertResult.UnknownFailure("Unknown Failure")
        );
    }

    public async Task<GetResult> GetDocumentById(IGetRequest getRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.GetDocumentById - {TraceId}",
            getRequest.TraceId.Value
        );

        var sw = Stopwatch.StartNew();
        try
        {
            await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync();

            GetResult result = await _getDocumentById.GetById(getRequest, connection, null);

            sw.Stop();
            _logger.LogInformation(
                "GetDocumentById completed in {TransactionDurationMs}ms with result {ResultType} - {TraceId}",
                sw.ElapsedMilliseconds,
                result.GetType().Name,
                getRequest.TraceId.Value
            );

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogCritical(
                ex,
                "Uncaught GetDocumentById failure after {TransactionDurationMs}ms - {TraceId}",
                sw.ElapsedMilliseconds,
                getRequest.TraceId.Value
            );
            return new GetResult.UnknownFailure("Unknown Failure");
        }
    }

    public async Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
    {
        return await ExecuteTransactionalAsync(
            "UpdateDocumentById",
            updateRequest.TraceId.Value,
            async (connection, transaction) =>
                await _updateDocumentById.UpdateById(updateRequest, connection, transaction),
            result => result is UpdateResult.UpdateSuccess,
            new UpdateResult.UpdateFailureWriteConflict(),
            new UpdateResult.UnknownFailure("Unknown Failure")
        );
    }

    public async Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
    {
        return await ExecuteTransactionalAsync(
            "DeleteDocumentById",
            deleteRequest.TraceId.Value,
            async (connection, transaction) =>
                await _deleteDocumentById.DeleteById(deleteRequest, connection, transaction),
            result => result is DeleteResult.DeleteSuccess,
            new DeleteResult.DeleteFailureWriteConflict(),
            new DeleteResult.UnknownFailure("Unknown Failure")
        );
    }

    public async Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
    {
        _logger.LogDebug(
            "Entering PostgresqlDocumentStoreRepository.QueryDocuments - {TraceId}",
            queryRequest.TraceId.Value
        );

        var sw = Stopwatch.StartNew();
        try
        {
            QueryResult result = await _queryDocument.QueryDocuments(queryRequest);

            sw.Stop();
            _logger.LogInformation(
                "QueryDocuments completed in {TransactionDurationMs}ms with result {ResultType} - {TraceId}",
                sw.ElapsedMilliseconds,
                result.GetType().Name,
                queryRequest.TraceId.Value
            );

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogCritical(
                ex,
                "Uncaught QueryDocuments failure after {TransactionDurationMs}ms - {TraceId}",
                sw.ElapsedMilliseconds,
                queryRequest.TraceId.Value
            );
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
    }
}
