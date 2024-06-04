// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

public interface IGetDocumentByResourceName
{
    public Task<GetResult> GetByResourceName(IGetRequest getRequest, int offset, int limit);
}

public class GetDocumentByResourceName(NpgsqlDataSource _dataSource, ILogger<GetDocumentByResourceName> _logger) : IGetDocumentByResourceName
{
    public async Task<GetResult> GetByResourceName(IGetRequest getRequest, int offset, int limit)
    {
        _logger.LogDebug("Entering GetDocumentByResourceName.GetByResourceName - {TraceId}", getRequest.TraceId);
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync();

            await using NpgsqlCommand command =
                new(
                    @"SELECT EdfiDoc FROM public.Documents WHERE resourcename = $1 OFFSET $2 ROWS FETCH FIRST $3 ROWS ONLY;",
                    connection
                )
                {
                    Parameters =
                    {
                        new() { Value = getRequest.ResourceInfo.ResourceName.Value },
                        new() { Value = offset },
                        new () { Value = limit },
                    }
                };

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

            var documents = new List<JsonNode>();

            while (await reader.ReadAsync())
            {
                JsonNode? edfiDoc = JsonNode.Parse(reader.GetString(0));

                if (edfiDoc != null)
                {
                    documents.Add(edfiDoc);
                }
                else
                {
                    _logger.LogWarning("Unable to parse JSON for a document.");
                }
            }

            // TODO: Documents table needs a last modified datetime
            return new GetResult.GetSuccess(getRequest.DocumentUuid, JsonSerializer.Serialize(documents), DateTime.Now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDocumentByResourceName.GetByResourceName failure - {TraceId}", getRequest.TraceId);
            return new GetResult.UnknownFailure("Unknown Failure");
        }
    }
}

