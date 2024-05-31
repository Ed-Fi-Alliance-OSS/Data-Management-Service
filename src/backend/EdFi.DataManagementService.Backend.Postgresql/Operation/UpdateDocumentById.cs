// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Npgsql;
using static EdFi.DataManagementService.Backend.PartitionUtility;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

public interface IUpdateDocumentById
{
    public Task<UpdateResult> UpdateById(IUpdateRequest updateRequest);
}

public class UpdateDocumentById(
    NpgsqlDataSource _dataSource,
    ISqlAction _sqlAction,
    ILogger<UpdateDocumentById> _logger
) : IUpdateDocumentById
{
    public async Task<UpdateResult> UpdateById(IUpdateRequest updateRequest)
    {
        _logger.LogDebug("Entering UpdateDocumentById.UpdateById - {TraceId}", updateRequest.TraceId);

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            int rowsAffected = await _sqlAction.UpdateDocumentEdFiDoc(
                PartitionKeyFor(updateRequest.DocumentUuid).Value,
                updateRequest.DocumentUuid.Value,
                JsonSerializer.Deserialize<JsonElement>(updateRequest.EdfiDoc),
                connection,
                transaction
            );

            switch (rowsAffected)
            {
                case 1:
                    return new UpdateResult.UpdateSuccess();
                case 0:
                    _logger.LogInformation(
                        "Failure: Record to update does not exist - {TraceId}",
                        updateRequest.TraceId
                    );
                    return new UpdateResult.UpdateFailureNotExists();
                default:
                    _logger.LogCritical(
                        "UpdateDocumentById rows affected was '{RowsAffected}' for {DocumentUuid} - Should never happen - {TraceId}",
                        rowsAffected,
                        updateRequest.DocumentUuid,
                        updateRequest.TraceId
                    );
                    return new UpdateResult.UnknownFailure("Unknown Failure");
            }
        }
        catch (PostgresException pe)
        {
            if (pe.SqlState == PostgresErrorCodes.SerializationFailure)
            {
                _logger.LogDebug(
                    pe,
                    "Transaction conflict on Documents table update - {TraceId}",
                    updateRequest.TraceId
                );
                await transaction.RollbackAsync();
                return new UpdateResult.UpdateFailureWriteConflict();
            }

            _logger.LogError(pe, "Failure on Documents table update - {TraceId}", updateRequest.TraceId);
            await transaction.RollbackAsync();
            return new UpdateResult.UnknownFailure("Update failure");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failure on Documents table update - {TraceId}", updateRequest.TraceId);
            await transaction.RollbackAsync();
            return new UpdateResult.UnknownFailure("Update failure");
        }
    }
}
