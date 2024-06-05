// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
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
    private DeleteResult HandlePostgresException(string tableName, ITraceId traceId, PostgresException pe)
    {
        if (pe.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            _logger.LogDebug(
                pe,
                "Transaction conflict on {TableName} table delete - {TraceId}",
                tableName,
                traceId
            );
            return new DeleteResult.DeleteFailureWriteConflict();
        }

        _logger.LogError(pe, "Failure on {TableName} table insert - {TraceId}", tableName, traceId);
        return new DeleteResult.UnknownFailure("Delete failure");
    }

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
            int rowsAffectedOnAliasTable = 0;
            int rowsAffected = 0;

            var document = await _sqlAction.FindDocumentByDocumentUuid(
                documentPartitionKey,
                deleteRequest.DocumentUuid,
                connection,
                transaction
            );

            if (document == null)
            {
                _logger.LogInformation(
                    "Failure: Record to delete does not exist - {TraceId}",
                    deleteRequest.TraceId
                );
                return new DeleteResult.DeleteFailureNotExists();
            }

            try
            {
                rowsAffectedOnAliasTable = await _sqlAction.DeleteAliasByDocumentId(
                    documentPartitionKey,
                    document.Id,
                    connection,
                    transaction
                );
            }
            catch (PostgresException pe)
            {
                return HandlePostgresException("Aliases", deleteRequest.TraceId, pe);
            }

            if (rowsAffectedOnAliasTable == 0)
            {
                _logger.LogCritical(
                    "Failure: Associated Alias records are not available for the Document {DocumentId} - {TraceId}",
                    deleteRequest.DocumentUuid,
                    deleteRequest.TraceId
                );

                // We will still try to delete from Documents table
            }

            try
            {
                rowsAffected = await _sqlAction.DeleteDocumentByDocumentId(
                    documentPartitionKey,
                    document.Id,
                    connection,
                    transaction
                );
            }
            catch (PostgresException pe)
            {
                return HandlePostgresException("Documents", deleteRequest.TraceId, pe);
            }

            switch (rowsAffected)
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
                        rowsAffected,
                        deleteRequest.DocumentUuid,
                        deleteRequest.TraceId
                    );
                    return new DeleteResult.UnknownFailure("Unknown Failure");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteDocumentById failure - {TraceId}", deleteRequest.TraceId);
            return new DeleteResult.UnknownFailure("Unknown Failure");
        }
    }
}
