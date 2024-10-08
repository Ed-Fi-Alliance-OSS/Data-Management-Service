// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
        var documentPartitionKey = PartitionKeyFor(deleteRequest.DocumentUuid);

        try
        {
            // Create a transaction save point
            await transaction.SaveAsync("beforeDelete");

            int rowsAffectedOnDocumentDelete = await _sqlAction.DeleteDocumentByDocumentUuid(
                documentPartitionKey,
                deleteRequest.DocumentUuid,
                connection,
                transaction,
                deleteRequest.TraceId
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
        catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.DeadlockDetected)
        {
            _logger.LogDebug(pe, "Transaction deadlock on DeleteById - {TraceId}", deleteRequest.TraceId);
            return new DeleteResult.DeleteFailureWriteConflict();
        }
        catch (PostgresException pe) when (pe.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // Restore transaction save point to continue using transaction
            await transaction.RollbackAsync("beforeDelete");

            var referencingDocumentNames = await _sqlAction.FindReferencingResourceNamesByDocumentUuid(
                deleteRequest.DocumentUuid,
                documentPartitionKey,
                connection,
                transaction,
                deleteRequest.TraceId
            );
            _logger.LogDebug(pe, "Foreign key violation on Delete - {TraceId}", deleteRequest.TraceId);
            return new DeleteResult.DeleteFailureReference(referencingDocumentNames.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteById failure - {TraceId}", deleteRequest.TraceId);
            return new DeleteResult.UnknownFailure("Unknown Failure");
        }
    }
}
