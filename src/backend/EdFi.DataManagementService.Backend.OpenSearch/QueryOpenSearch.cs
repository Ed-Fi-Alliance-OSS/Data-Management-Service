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

/// <summary>
/// Queries OpenSearch for documents. Example query DSL usage:
///
///  {
///    "from": 100
///    "size": 20,
///    "query": {
///      "bool": {
///        "filter": [
///          {
///            "term": {
///              "edfidoc.schoolYearDescription": {
///                "value": "Year 2025"
///              }
///            }
///          },
///          {
///            "term": {
///              "edfidoc.currentSchoolYear": false
///            }
///          }
///        ]
///      }
///    }
///  }
///
/// </summary>
public static class QueryOpenSearch
{
    // Imposes a consistent sort order across queries without specifying a sort field
    private static JsonArray SortDirective()
    {
        return new(new JsonObject { ["_doc"] = new JsonObject { ["order"] = "asc" } });
    }

    /// <summary>
    /// Returns OpenSearch index name from the given ResourceInfo.
    /// OpenSearch indexes are required to be lowercase only, with no periods.
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

            // API client requested filters
            JsonArray terms = [];
            if (queryRequest.SearchParameters.Count > 0)
            {
                foreach (var searchParameter in queryRequest.SearchParameters)
                {
                    terms.Add(
                        new JsonObject
                        {
                            ["term"] = new JsonObject
                            {
                                [$@"edfidoc.{searchParameter.Key}"] = new JsonObject
                                {
                                    ["value"] = searchParameter.Value
                                }
                            }
                        }
                    );
                }
            }

            JsonObject query =
                new()
                {
                    ["query"] = new JsonObject { ["bool"] = new JsonObject { ["filter"] = terms } },
                    ["sort"] = SortDirective()
                };

            if (queryRequest.PaginationParameters.limit != null)
            {
                query.Add(new("size", queryRequest.PaginationParameters.limit));
            }

            if (queryRequest.PaginationParameters.offset != null)
            {
                query.Add(new("from", queryRequest.PaginationParameters.offset));
            }

            string queryJsonString = query.ToJsonString();

            BytesResponse response = await client.Http.PostAsync<BytesResponse>(
                $"/{indexName}/_search",
                d => d.Body(queryJsonString)
            );

            JsonNode? jsonResponse = JsonSerializer.Deserialize<JsonNode>(response.Body);

            JsonNode hits = jsonResponse!["hits"]!;

            int totalCount = hits!["total"]!["value"]!.GetValue<int>();

            JsonNode[] documents = hits!["hits"]!
                .AsArray()
                // DeepClone() so they can be placed in a new JsonArray
                .Select(node => node!["_source"]!["edfidoc"]!.DeepClone())!
                .ToArray()!;

            return new QueryResult.QuerySuccess(new JsonArray(documents), totalCount);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Uncaught Query failure - {TraceId}", queryRequest.TraceId);
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
    }
}
