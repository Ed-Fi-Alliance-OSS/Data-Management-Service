// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Npgsql;
using static EdFi.DataManagementService.Backend.PartitionUtility;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

public interface IGetDocumentById
{
    public Task<GetResult> GetById(IGetRequest getRequest);
}

public class GetDocumentById(
    NpgsqlDataSource _dataSource,
    ISqlAction _sqlAction,
    ILogger<GetDocumentById> _logger)
    : IGetDocumentById
{
    public async Task<GetResult> GetById(IGetRequest getRequest)
    {
        _logger.LogDebug("Entering GetDocumentById.GetById - {TraceId}", getRequest.TraceId);

        try
        {
            int documentPartitionKey = PartitionKeyFor(getRequest.DocumentUuid).Value;

            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            JsonNode? edfiDoc = await _sqlAction.GetDocumentById(
                getRequest.DocumentUuid.Value, documentPartitionKey, connection, transaction
            );

            if (edfiDoc != null)
            {
                return new GetResult.GetSuccess(getRequest.DocumentUuid, edfiDoc, DateTime.Now);
            }
            else
            {
                return new GetResult.GetFailureNotExists();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDocumentById failure - {TraceId}", getRequest.TraceId);
            return new GetResult.UnknownFailure("Unknown Failure");
        }
    }
}
