// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Npgsql;
using static EdFi.DataManagementService.Backend.PartitionUtility;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

public interface IDeleteDocumentById
{
    public Task<DeleteResult> DeleteById(
        IDeleteRequest deleteRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );
}

public class DeleteDocumentById(ISqlAction _sqlAction, ILogger<DeleteDocumentById> _logger)
    : IDeleteDocumentById
{
    public async Task<DeleteResult> DeleteById(
        IDeleteRequest deleteRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        _logger.LogDebug("Entering DeleteDocumentById.DeleteById - {TraceId}", deleteRequest.TraceId);
        int documentPartitionKey = PartitionKeyFor(deleteRequest.DocumentUuid).Value;

        try
        {
            int rowsAffectedOnDocumentDelete = await _sqlAction.DeleteDocumentByDocumentUuid(
                documentPartitionKey,
                deleteRequest.DocumentUuid,
                connection,
                transaction
            );

            switch (rowsAffectedOnDocumentDelete)
            {
                case 1:
                    return new DeleteResult.DeleteSuccess();
                case 0:
                    _logger.LogInformation(
                        "Failure: Record to delete does not exist - {TraceId}",
                        deleteRequest.TraceId
                    );
                    return new DeleteResult.DeleteFailureNotExists();
                default:
                    _logger.LogError(
                        "DeleteDocumentById rows affected was '{RowsAffected}' for {DocumentUuid} - {TraceId}",
                        rowsAffectedOnDocumentDelete,
                        deleteRequest.DocumentUuid,
                        deleteRequest.TraceId
                    );
                    return new DeleteResult.UnknownFailure("Unknown Failure");
            }
        }
        catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            _logger.LogDebug(pe, "Transaction conflict on DeleteById - {TraceId}", deleteRequest.TraceId);
            return new DeleteResult.DeleteFailureWriteConflict();
        }
        catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // The current transaction has been aborted due to an error,
            // and no further commands can be executed until the transaction is ended.
            // To resolve this, need to rollback the current transaction and then start a new one.
            // Please note that any data retrieved or manipulated may be stale because
            // it will be part of a new transaction.

            await transaction.RollbackAsync();

            await using var dbTransaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead
            );

            var referencingDocumentName = await _sqlAction.FindReferencingResourceNameByDocumentUuid(
                deleteRequest.DocumentUuid,
                documentPartitionKey,
                connection,
                dbTransaction,
                LockOption.BlockUpdateDelete
            );
            _logger.LogDebug(pe, "Foreign key violation on Delete - {TraceId}", deleteRequest.TraceId);
            return new DeleteResult.DeleteFailureReference(referencingDocumentName ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteById failure - {TraceId}", deleteRequest.TraceId);
            return new DeleteResult.UnknownFailure("Unknown Failure");
        }
    }
}
