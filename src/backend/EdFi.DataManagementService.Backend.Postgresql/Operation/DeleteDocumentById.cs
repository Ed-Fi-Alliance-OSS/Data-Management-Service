// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Transactions;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Npgsql;
using static EdFi.DataManagementService.Backend.PartitionUtility;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

public interface IDeleteDocumentById
{
    public Task<DeleteResult> DeleteById(IDeleteRequest deleteRequest);
}

public class DeleteDocumentById(
    NpgsqlDataSource _dataSource,
    ISqlAction _sqlAction,
    ILogger<DeleteDocumentById> _logger
) : IDeleteDocumentById
{
    public async Task<DeleteResult> DeleteById(IDeleteRequest deleteRequest)
    {
        _logger.LogDebug("Entering DeleteDocumentById.DeleteById - {TraceId}", deleteRequest.TraceId);

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        int documentPartitionKey = PartitionKeyFor(deleteRequest.DocumentUuid).Value;
        int rowsAffectedOnAliasTable = 0;
        int rowsAffected = 0;

        try
        {
            try
            {
                rowsAffectedOnAliasTable = await _sqlAction.DeleteAliasByDocumentId(
                    documentPartitionKey,
                    deleteRequest.DocumentUuid,
                    connection
                );
            }
            catch (PostgresException pe)
            {
                return await HandlePostgresException("Aliases", pe);
            }

            if (rowsAffectedOnAliasTable == 0)
            {
                _logger.LogCritical(
                    "Failure: Associated Alias records are not available for the Document {DocumentId} - {TraceId}",
                    deleteRequest.DocumentUuid,
                    deleteRequest.TraceId
                );
            }

            try
            {
                rowsAffected = await _sqlAction.DeleteDocumentByDocumentId(
                    documentPartitionKey,
                    deleteRequest.DocumentUuid,
                    connection
                );
            }
            catch (PostgresException pe)
            {
                return await HandlePostgresException("Documents", pe);
            }

            switch (rowsAffected)
            {
                case 1:
                    await transaction.CommitAsync();
                    return await Task.FromResult<DeleteResult>(new DeleteResult.DeleteSuccess());
                case 0:
                    _logger.LogInformation(
                        "Failure: Record to delete does not exist - {TraceId}",
                        deleteRequest.TraceId
                    );
                    await transaction.RollbackAsync();
                    return await Task.FromResult<DeleteResult>(new DeleteResult.DeleteFailureNotExists());
                default:
                    _logger.LogError(
                        "DeleteDocumentById rows affected was '{RowsAffected}' for {DocumentUuid} - {TraceId}",
                        rowsAffected,
                        deleteRequest.DocumentUuid,
                        deleteRequest.TraceId
                    );
                    await transaction.RollbackAsync();
                    return await Task.FromResult<DeleteResult>(
                        new DeleteResult.UnknownFailure("Unknown Failure")
                    );
            }

            async Task<DeleteResult> HandlePostgresException(string tableName, PostgresException pe)
            {
                if (pe.SqlState == PostgresErrorCodes.SerializationFailure)
                {
                    _logger.LogDebug(
                        pe,
                        "Transaction conflict on {TableName} table delete - {TraceId}",
                        tableName,
                        deleteRequest.TraceId
                    );
                    await transaction.RollbackAsync();
                    return new DeleteResult.DeleteFailureWriteConflict();
                }

                _logger.LogError(
                    pe,
                    "Failure on {TableName} table insert - {TraceId}",
                    tableName,
                    deleteRequest.TraceId
                );
                await transaction.RollbackAsync();
                return new DeleteResult.UnknownFailure("Delete failure");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteDocumentById failure - {TraceId}", deleteRequest.TraceId);
            await transaction.RollbackAsync();
            return await Task.FromResult<DeleteResult>(new DeleteResult.UnknownFailure("Unknown Failure"));
        }
    }
}
