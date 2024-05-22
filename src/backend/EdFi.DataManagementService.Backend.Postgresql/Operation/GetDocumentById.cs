// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
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

public class GetDocumentById(NpgsqlDataSource _dataSource, ILogger<GetDocumentById> _logger)
    : IGetDocumentById
{
    public async Task<GetResult> GetById(IGetRequest getRequest)
    {
        _logger.LogDebug("Entering GetDocumentById.GetById - {TraceId}", getRequest.TraceId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync();

            await using NpgsqlCommand command =
                new(
                    @"SELECT EdfiDoc FROM public.Documents WHERE DocumentPartitionKey = $1 AND DocumentUuid = $2;",
                    connection
                )
                {
                    Parameters =
                    {
                        new() { Value = PartitionKeyFor(getRequest.DocumentUuid).Value },
                        new() { Value = getRequest.DocumentUuid.Value },
                    }
                };

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

            if (!reader.HasRows)
            {
                return new GetResult.GetFailureNotExists();
            }

            // Assumes only one row returned
            await reader.ReadAsync();
            JsonNode? edfiDoc = (await reader.GetFieldValueAsync<JsonElement>(0)).Deserialize<JsonNode>();

            if (edfiDoc == null)
            {
                return new GetResult.UnknownFailure(
                    $"Unable to parse JSON for document {getRequest.DocumentUuid}."
                );
            }

            // TODO: Documents table needs a last modified datetime
            return new GetResult.GetSuccess(getRequest.DocumentUuid, edfiDoc, DateTime.Now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDocumentById failure - {TraceId}", getRequest.TraceId);
            return new GetResult.UnknownFailure("Unknown Failure");
        }
    }
}
