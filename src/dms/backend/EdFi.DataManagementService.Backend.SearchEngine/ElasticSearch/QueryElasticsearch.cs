// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.SearchEngine.ElasticSearch;

public static partial class QueryElasticsearch
{
    [GeneratedRegex(@"^\$\.")]
    private static partial Regex JsonPathPrefixRegex();

    private static JsonArray SortDirective()
    {
        return new(new JsonObject { ["_doc"] = new JsonObject { ["order"] = "asc" } });
    }

    private static string IndexFromResourceInfo(ResourceInfo resourceInfo)
    {
        return $@"{resourceInfo.ProjectName.Value}-{resourceInfo.ResourceName.Value}"
            .ToLower()
            .Replace("$", "-")
            .Replace(".", "-");
    }

    private static string QueryFieldFrom(JsonPath documentPath)
    {
        return JsonPathPrefixRegex().Replace(documentPath.Value, "");
    }

    public static async Task<QueryResult> QueryDocuments(
        ElasticsearchClient client,
        IQueryRequest queryRequest,
        ILogger logger
    )
    {
        logger.LogDebug("Entering QueryElasticsearch.Query - {TraceId}", queryRequest.TraceId.Value);

        try
        {
            string indexName = IndexFromResourceInfo(queryRequest.ResourceInfo);

            var indexExistsResponse = await client.Indices.ExistsAsync(indexName);
            if (!indexExistsResponse.IsValidResponse)
            {
                logger.LogError("Index does not exist: {IndexName}", indexName);
                return new QueryResult.UnknownFailure($"Index {indexName} does not exist.");
            }

            JsonArray terms = [];
            foreach (QueryElement queryElement in queryRequest.QueryElements)
            {
                if (queryElement.DocumentPaths.Length == 1)
                {
                    terms.Add(
                        new JsonObject
                        {
                            ["match_phrase"] = new JsonObject
                            {
                                [$@"edfidoc.{QueryFieldFrom(queryElement.DocumentPaths[0])}"] =
                                    queryElement.Value,
                            },
                        }
                    );
                }
                else
                {
                    JsonObject[] possibleTerms = queryElement
                        .DocumentPaths.Select(documentPath => new JsonObject
                        {
                            ["match_phrase"] = new JsonObject
                            {
                                [$@"edfidoc.{QueryFieldFrom(documentPath)}"] = queryElement.Value,
                            },
                        })
                        .ToArray();

                    terms.Add(
                        new JsonObject
                        {
                            ["bool"] = new JsonObject { ["should"] = new JsonArray(possibleTerms) },
                        }
                    );
                }
            }

            if (terms.Count == 0)
            {
                logger.LogWarning("No terms were added to the query.");
                return new QueryResult.QuerySuccess(new JsonArray(), 0);
            }

            JsonObject query = new()
            {
                ["query"] = new JsonObject { ["bool"] = new JsonObject { ["must"] = terms } },
                ["sort"] = SortDirective(),
            };

            if (queryRequest.PaginationParameters.Limit != null)
            {
                query.Add(new("size", queryRequest.PaginationParameters.Limit));
            }

            if (queryRequest.PaginationParameters.Offset != null)
            {
                query.Add(new("from", queryRequest.PaginationParameters.Offset));
            }

            var response = await client.SearchAsync<JsonNode>(s =>
                s.Index(indexName).Query(q => q.QueryString(qs => qs.Query(query.ToJsonString())))
            );

            if (!response.IsValidResponse)
            {
                logger.LogError(
                    "Elasticsearch query failed - {TraceId}: {Error}",
                    queryRequest.TraceId.Value,
                    response.DebugInformation
                );
                return new QueryResult.UnknownFailure("Query failed");
            }

            JsonArray hits = new JsonArray(response.Documents.ToArray());

            int totalCount = (int)response.Total;

            JsonNode[] documents =
                hits["hits"]
                    ?.AsArray()
                    .Select(hit => hit!["_source"]!["edfidoc"]!.DeepClone())
                    .Where(_ => true)
                    .ToArray() ?? Array.Empty<JsonNode>();

            return new QueryResult.QuerySuccess(new JsonArray(documents), totalCount);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Uncaught Query failure - {TraceId}", queryRequest.TraceId.Value);
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
    }
}
