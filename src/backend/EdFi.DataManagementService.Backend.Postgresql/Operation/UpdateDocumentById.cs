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
                    return await Task.FromResult<UpdateResult>(new UpdateResult.UpdateSuccess());
                case 0:
                    _logger.LogInformation(
                        "Failure: Record to update does not exist - {TraceId}",
                        updateRequest.TraceId
                    );
                    return await Task.FromResult<UpdateResult>(new UpdateResult.UpdateFailureNotExists());
                default:
                    _logger.LogError(
                        "UpdateDocumentById rows affected was '{RowsAffected}' for {DocumentUuid} - {TraceId}",
                        rowsAffected,
                        updateRequest.DocumentUuid,
                        updateRequest.TraceId
                    );
                    return await Task.FromResult<UpdateResult>(
                        new UpdateResult.UnknownFailure("Unknown Failure")
                    );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateDocumentById failure - {TraceId}", updateRequest.TraceId);
            return await Task.FromResult<UpdateResult>(new UpdateResult.UnknownFailure("Unknown Failure"));
        }
    }
}
