// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
///        "must": [
///          {
///            "match_phrase": {
///              "edfidoc.schoolYearDescription": "Year 2025"
///            }
///          },
///          {
///            "match_phrase": {
///              "edfidoc.currentSchoolYear": false
///            }
///          },
///          {
///            "bool": {
///              "should": [
///                {
///                  "terms": {
///                    "securityelements.EducationOrganization": {
///                      "index": "edfi.dms.educationorganizationhierarchytermslookup",
///                      "id": "6001010",
///                      "path": "hierarchy.array"
///                    }
///                  }
///                }
///              ]
///            }
///          }
///        ]
///      }
///    }
///  }
///
/// </summary>
public static partial class QueryOpenSearch
{
    [GeneratedRegex(@"^\$\.")]
    public static partial Regex JsonPathPrefixRegex();

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

    private static string QueryFieldFrom(JsonPath documentPath)
    {
        return JsonPathPrefixRegex().Replace(documentPath.Value, "");
    }

    /// <summary>
    /// Builds the query terms from query elements
    /// </summary>
    /// <param name="queryElements">Query elements to build terms from</param>
    /// <returns>Array of JSON objects representing query terms</returns>
    public static JsonArray BuildQueryTerms(IEnumerable<QueryElement> queryElements)
    {
        JsonArray terms = [];
        foreach (QueryElement queryElement in queryElements)
        {
            // If just one document path, it's a pure AND
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
                // If more than one document path, it's an OR
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
                    new JsonObject { ["bool"] = new JsonObject { ["should"] = new JsonArray(possibleTerms) } }
                );
            }
        }
        return terms;
    }

    /// <summary>
    /// Builds authorization filters from authorization securable info
    /// </summary>
    /// <param name="queryRequest">Query request containing authorization information</param>
    /// <param name="logger">Logger for warnings</param>
    /// <returns>List of JSON objects representing authorization filters</returns>
    public static List<JsonObject> BuildAuthorizationFilters(IQueryRequest queryRequest, ILogger logger)
    {
        // Helper to get all values from filters based on the filter type
        List<string> GetFilterValues(
            string filterType = SecurityElementNameConstants.EducationOrganization
        ) =>
            queryRequest
                .AuthorizationStrategyEvaluators.SelectMany(evaluator =>
                    evaluator
                        .Filters.Where(f => f.GetType().Name == filterType)
                        .Select(f => f.Value?.ToString())
                        .Where(ns => !string.IsNullOrEmpty(ns))
                        .Cast<string>()
                )
                .Distinct()
                .ToList();

        return queryRequest
            .AuthorizationSecurableInfo.Select(authorizationSecurableInfo =>
            {
                switch (authorizationSecurableInfo.SecurableKey)
                {
                    case SecurityElementNameConstants.Namespace:
                        var namespaces = GetFilterValues(SecurityElementNameConstants.Namespace);
                        return BuildNamespaceFilter(namespaces);

                    case SecurityElementNameConstants.EducationOrganization:
                        var edOrgIds = GetFilterValues();
                        return BuildEducationOrganizationFilter(edOrgIds);

                    case SecurityElementNameConstants.StudentUniqueId:
                        var studentEdOrgIds = GetFilterValues();
                        return BuildStudentFilter(studentEdOrgIds);

                    case SecurityElementNameConstants.ContactUniqueId:
                        var contactEdOrgIds = GetFilterValues();
                        return BuildContactFilter(contactEdOrgIds);

                    case SecurityElementNameConstants.StaffUniqueId:
                        var staffEdOrgIds = GetFilterValues();
                        return BuildStaffFilter(staffEdOrgIds);
                }
                return null;
            })
            .Where(filter => filter != null)
            .Cast<JsonObject>()
            .ToList();

        JsonObject? BuildNamespaceFilter(List<string> namespaces)
        {
            if (namespaces.Count > 1)
            {
                return new JsonObject
                {
                    ["terms"] = new JsonObject
                    {
                        [$"securityelements.{SecurityElementNameConstants.Namespace}"] = new JsonArray(
                            namespaces.Select(ns => JsonValue.Create(ns)).ToArray()
                        ),
                    },
                };
            }
            else if (namespaces.Count == 1)
            {
                return new JsonObject
                {
                    ["match_phrase"] = new JsonObject
                    {
                        [$"securityelements.{SecurityElementNameConstants.Namespace}"] = namespaces[0],
                    },
                };
            }

            return null;
        }

        JsonObject? BuildEducationOrganizationFilter(List<string> edOrgIds)
        {
            if (edOrgIds.Count == 1)
            {
                // If we have education organization IDs, use them in the filter
                // Use first ID for now as the lookup id - may need to handle multiple differently
                return new JsonObject
                {
                    ["terms"] = new JsonObject
                    {
                        [$"securityelements.{SecurityElementNameConstants.EducationOrganization}.Id"] =
                            new JsonObject
                            {
                                ["index"] = "edfi.dms.educationorganizationhierarchytermslookup",
                                ["id"] = edOrgIds[0],
                                ["path"] = "hierarchy.array",
                            },
                    },
                };
            }
            else if (edOrgIds.Count > 1)
            {
                var shouldArray = new JsonArray(
                    edOrgIds
                        .Select(id => new JsonObject
                        {
                            ["terms"] = new JsonObject
                            {
                                [
                                    $"securityelements.{SecurityElementNameConstants.EducationOrganization}.Id"
                                ] = new JsonObject
                                {
                                    ["index"] = "edfi.dms.educationorganizationhierarchytermslookup",
                                    ["id"] = id,
                                    ["path"] = "hierarchy.array",
                                },
                            },
                        })
                        .ToArray()
                );
                return new JsonObject { ["bool"] = new JsonObject { ["should"] = shouldArray } };
            }

            return null;
        }

        JsonObject? BuildStudentFilter(List<string> studentEdOrgIds)
        {
            if (studentEdOrgIds.Count == 1)
            {
                return new JsonObject
                {
                    ["terms"] = new JsonObject
                    {
                        [$"studentschoolauthorizationedorgids.array"] = new JsonObject
                        {
                            ["index"] = "edfi.dms.educationorganizationhierarchytermslookup",
                            ["id"] = studentEdOrgIds[0],
                            ["path"] = "hierarchy.array",
                        },
                    },
                };
            }
            else if (studentEdOrgIds.Count > 1)
            {
                var shouldArray = new JsonArray(
                    studentEdOrgIds
                        .Select(id => new JsonObject
                        {
                            ["terms"] = new JsonObject
                            {
                                ["studentschoolauthorizationedorgids.array"] = new JsonObject
                                {
                                    ["index"] = "edfi.dms.educationorganizationhierarchytermslookup",
                                    ["id"] = id,
                                    ["path"] = "hierarchy.array",
                                },
                            },
                        })
                        .ToArray()
                );
                return new JsonObject { ["bool"] = new JsonObject { ["should"] = shouldArray } };
            }

            return null;
        }

        JsonObject? BuildContactFilter(List<string> contactEdOrgIds)
        {
            if (contactEdOrgIds.Count == 1)
            {
                return new JsonObject
                {
                    ["terms"] = new JsonObject
                    {
                        [$"contactstudentschoolauthorizationedorgids.array"] = new JsonObject
                        {
                            ["index"] = "edfi.dms.educationorganizationhierarchytermslookup",
                            ["id"] = contactEdOrgIds[0],
                            ["path"] = "hierarchy.array",
                        },
                    },
                };
            }
            else if (contactEdOrgIds.Count > 1)
            {
                var shouldArray = new JsonArray(
                    contactEdOrgIds
                        .Select(id => new JsonObject
                        {
                            ["terms"] = new JsonObject
                            {
                                ["contactstudentschoolauthorizationedorgids.array"] = new JsonObject
                                {
                                    ["index"] = "edfi.dms.educationorganizationhierarchytermslookup",
                                    ["id"] = id,
                                    ["path"] = "hierarchy.array",
                                },
                            },
                        })
                        .ToArray()
                );
                return new JsonObject { ["bool"] = new JsonObject { ["should"] = shouldArray } };
            }

            return null;
        }

        JsonObject? BuildStaffFilter(List<string> staffEdOrgIds)
        {
            if (staffEdOrgIds.Count == 1)
            {
                return new JsonObject
                {
                    ["terms"] = new JsonObject
                    {
                        ["staffeducationorganizationauthorizationedorgids.array"] = new JsonObject
                        {
                            ["index"] = "edfi.dms.educationorganizationhierarchytermslookup",
                            ["id"] = staffEdOrgIds[0],
                            ["path"] = "hierarchy.array",
                        },
                    },
                };
            }
            else if (staffEdOrgIds.Count > 1)
            {
                var shouldArray = new JsonArray(
                    staffEdOrgIds
                        .Select(id => new JsonObject
                        {
                            ["terms"] = new JsonObject
                            {
                                ["staffeducationorganizationauthorizationedorgids.array"] = new JsonObject
                                {
                                    ["index"] = "edfi.dms.educationorganizationhierarchytermslookup",
                                    ["id"] = id,
                                    ["path"] = "hierarchy.array",
                                },
                            },
                        })
                        .ToArray()
                );
                return new JsonObject { ["bool"] = new JsonObject { ["should"] = shouldArray } };
            }

            return null;
        }
    }

    /// <summary>
    /// Builds a complete query object for OpenSearch from the provided query request
    /// </summary>
    /// <param name="queryRequest">The query request object</param>
    /// <param name="logger">Logger for warnings</param>
    /// <returns>A JsonObject representing the OpenSearch query</returns>
    public static JsonObject BuildQueryObject(IQueryRequest queryRequest, ILogger logger)
    {
        // Build query terms from query elements
        JsonArray terms = BuildQueryTerms(queryRequest.QueryElements);

        // Build authorization filters
        var authorizationFilters = BuildAuthorizationFilters(queryRequest, logger);

        // Add authorization filters to terms if there are any
        if (authorizationFilters.Any())
        {
            terms.Add(
                new JsonObject
                {
                    ["bool"] = new JsonObject { ["should"] = new JsonArray(authorizationFilters.ToArray()) },
                }
            );
        }

        // Build the final query object
        JsonObject query = new()
        {
            ["query"] = new JsonObject { ["bool"] = new JsonObject { ["must"] = terms } },
            ["sort"] = SortDirective(),
        };

        // Add in PaginationParameters if any
        if (queryRequest.PaginationParameters.Limit != null)
        {
            query.Add(new("size", queryRequest.PaginationParameters.Limit));
        }

        if (queryRequest.PaginationParameters.Offset != null)
        {
            query.Add(new("from", queryRequest.PaginationParameters.Offset));
        }

        return query;
    }

    public static async Task<QueryResult> QueryDocuments(
        IOpenSearchClient client,
        IQueryRequest queryRequest,
        ILogger logger
    )
    {
        logger.LogDebug("Entering QueryOpenSearch.Query - {TraceId}", queryRequest.TraceId.Value);

        try
        {
            string indexName = IndexFromResourceInfo(queryRequest.ResourceInfo);

            // Build the query using the extracted method
            JsonObject query = BuildQueryObject(queryRequest, logger);

            logger.LogDebug("Query - {TraceId} - {Query}", queryRequest.TraceId.Value, query.ToJsonString());

            BytesResponse response = await client.Http.PostAsync<BytesResponse>(
                $"/{indexName}/_search",
                d => d.Body(query.ToJsonString())
            );

            if (response.Success)
            {
                JsonNode hits = JsonSerializer.Deserialize<JsonNode>(response.Body)!["hits"]!;

                if (hits is null)
                {
                    return new QueryResult.QuerySuccess(new JsonArray(), 0);
                }

                int totalCount = hits!["total"]!["value"]!.GetValue<int>();

                JsonNode[] documents = hits!["hits"]!
                    .AsArray()
                    // DeepClone() so they can be placed in a new JsonArray
                    .Select(node => node!["_source"]!["edfidoc"]!.DeepClone())!
                    .ToArray()!;

                return new QueryResult.QuerySuccess(new JsonArray(documents), totalCount);
            }

            logger.LogCritical(
                "Unsuccessful OpenSearch Response - {TraceId} - {DebugInformation}",
                queryRequest.TraceId.Value,
                response.DebugInformation
            );
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Uncaught Query failure - {TraceId}", queryRequest.TraceId.Value);
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
    }
}
