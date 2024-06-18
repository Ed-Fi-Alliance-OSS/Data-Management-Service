// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql.Model;
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
        try
        {
            int documentPartitionKey = PartitionKeyFor(deleteRequest.DocumentUuid).Value;

            Document? document = await _sqlAction.FindDocumentByDocumentUuid(
                documentPartitionKey,
                deleteRequest.DocumentUuid,
                connection,
                transaction,
                LockOption.BlockAll
            );

            if (document == null)
            {
                _logger.LogInformation(
                    "Failure: Record to delete does not exist - {TraceId}",
                    deleteRequest.TraceId
                );
                return new DeleteResult.DeleteFailureNotExists();
            }

            int rowsAffectedOnAliasDelete = await _sqlAction.DeleteAliasByDocumentId(
                documentPartitionKey,
                document.Id,
                connection,
                transaction
            );

            if (rowsAffectedOnAliasDelete == 0)
            {
                _logger.LogCritical(
                    "Failure: Associated Alias records are not available for the Document {DocumentId} - {TraceId}",
                    deleteRequest.DocumentUuid,
                    deleteRequest.TraceId
                );

                // We will still try to delete from Documents table
            }

            int rowsAffectedOnDocumentDelete = await _sqlAction.DeleteDocumentByDocumentId(
                documentPartitionKey,
                document.Id,
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
            _logger.LogDebug(pe, "Transaction conflict on UpdateById - {TraceId}", deleteRequest.TraceId);
            return new DeleteResult.DeleteFailureWriteConflict();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteDocumentById failure - {TraceId}", deleteRequest.TraceId);
            return new DeleteResult.UnknownFailure("Unknown Failure");
        }
    }
}
