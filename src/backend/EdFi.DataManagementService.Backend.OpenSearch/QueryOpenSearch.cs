// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;

namespace EdFi.DataManagementService.Backend.Postgresql;

public static class QueryOpenSearch
{
    /// <summary>
    /// Returns OpenSearch index name from the given ResourceInfo.
    /// OpenSearch indexes are required to be lowercase only, with no pound signs or periods.
    /// </summary>
    private static string IndexFromResourceInfo(ResourceInfo resourceInfo)
    {
        return `${resourceInfo.projectName}$${resourceInfo.resourceVersion}$${resourceInfo.resourceName}`
            .toLowerCase()
            .replace(/\./g, '-');
    }


    public static async Task<QueryResult> Query(IQueryRequest queryRequest, OpenSearchClient client, ILogger logger)
    {
        logger.LogDebug(
            "Entering QueryOpenSearch.Query - {TraceId}",
            queryRequest.TraceId
        );

        try
        {
            var x = IndexFromResourceInfo(queryRequest.ResourceInfo);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Uncaught Query failure - {TraceId}", queryRequest.TraceId);
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
    }
}
