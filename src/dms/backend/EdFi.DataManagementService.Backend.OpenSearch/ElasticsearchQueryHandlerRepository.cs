// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.OpenSearch;

public class ElasticsearchQueryHandlerRepository(
    ElasticsearchClient elasticsearchClient,
    ILogger<ElasticsearchQueryHandlerRepository> logger
) : IQueryHandler
{
    public async Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
    {
        logger.LogDebug(
            "Entering ElasticsearchQueryHandlerRepository.QueryDocuments - {TraceId}",
            queryRequest.TraceId.Value
        );

        try
        {
            QueryResult result = await QueryElasticsearch.QueryDocuments(elasticsearchClient, queryRequest, logger);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Uncaught QueryDocuments failure - {TraceId}", queryRequest.TraceId.Value);
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
    }
}
