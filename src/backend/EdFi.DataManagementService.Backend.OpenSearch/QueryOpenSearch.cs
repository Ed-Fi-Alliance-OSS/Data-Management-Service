// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
            string openSearchIndex = IndexFromResourceInfo(queryRequest.ResourceInfo);

            var response = await client.SearchAsync<DynamicResponse>(s => s.Index(openSearchIndex));

            var y = response.Documents.ToArray()[0].Body.hits.hits;

            // var x = await client.LowLevel.SearchAsync<SearchResponse<Document>>(index: openSearchIndex, )
            return new QueryResult.QuerySuccess([new JsonObject(y)], 1);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Uncaught Query failure - {TraceId}", queryRequest.TraceId);
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
    }
}
