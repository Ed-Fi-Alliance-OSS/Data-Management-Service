// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;
using OpenSearch.Net;

namespace EdFi.DataManagementService.Backend.OpenSearch;

public static class QueryOpenSearch
{
    /// <summary>
    /// Returns OpenSearch index name from the given ResourceInfo.
    /// OpenSearch indexes are required to be lowercase only, with no pound signs or periods.
    /// </summary>
    private static string IndexFromResourceInfo(ResourceInfo resourceInfo)
    {
        return $@"{resourceInfo.ProjectName.Value}${resourceInfo.ResourceName.Value}"
            .ToLower()
            .Replace(".", "-");
    }

    public static async Task<QueryResult> QueryDocuments(
        IOpenSearchClient client,
        IQueryRequest queryRequest,
        ILogger logger
    )
    {
        logger.LogDebug("Entering QueryOpenSearch.Query - {TraceId}", queryRequest.TraceId);

        try
        {
            string indexName = IndexFromResourceInfo(queryRequest.ResourceInfo);

            var query = new
            {
                size = 5
            };

            var response = await client.Http.PostAsync<BytesResponse>($"/{indexName}/_search", d => d.SerializableBody(query));

            var jsonRawBody = JsonSerializer.Deserialize<JsonNode>(response.Body);

            JsonNode hits = jsonRawBody!["hits"]!;

            int totalCount = hits!["total"]!["value"]!.GetValue<int>();

            JsonNode[] documents = hits!["hits"]!.AsArray().Select(node => node!["_source"]!["edfidoc"]!.DeepClone())!.ToArray()!;

            return new QueryResult.QuerySuccess(new JsonArray(documents), totalCount);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Uncaught Query failure - {TraceId}", queryRequest.TraceId);
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
    }
}
