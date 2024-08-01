// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;

namespace EdFi.DataManagementService.Backend.Postgresql;

public class OpenSearchQueryHandlerRepository(
    ILogger<OpenSearchQueryHandlerRepository> _logger,
) : IQueryHandler
{
    public async Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
    {
        _logger.LogDebug(
            "Entering OpenSearchQueryHandlerRepository.QueryDocuments - {TraceId}",
            queryRequest.TraceId
        );

        Uri openSearchServerUri = new("http://localhost:9200");
        OpenSearchClient client = new(openSearchServerUri);

        try
        {
            QueryResult result = await QueryOpenSearch.QueryDocuments(queryRequest);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Uncaught QueryDocuments failure - {TraceId}", queryRequest.TraceId);
            return new QueryResult.UnknownFailure("Unknown Failure");
        }
    }
}
