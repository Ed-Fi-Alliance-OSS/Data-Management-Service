// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.External.Backend.QueryResult;

namespace EdFi.DataManagementService.Core.Handler;
internal class GetByKeyHandler(IQueryHandler _queryHandler, ILogger _logger)
    : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering GetByKeyHandler - {TraceId}", context.FrontendRequest.TraceId);


        QueryResult result = await _queryHandler.QueryDocuments(
            new QueryRequest(
                resourceInfo: context.ResourceInfo,
                searchParameters: new Dictionary<string, string>(),
                paginationParameters: context.PaginationParameters,
                TraceId: context.FrontendRequest.TraceId
            ));

        _logger.LogDebug(
            "Document store GetByKeyHandler returned {GetResult}- {TraceId}",
            result.GetType().FullName,
            context.FrontendRequest.TraceId
        );

        context.FrontendResponse = result switch
        {
            QuerySuccess success => new FrontendResponse(StatusCode: 200, Body: JsonNodesToString(success.EdfiDocs), Headers: []),
            QueryFailureInvalidQuery => new FrontendResponse(StatusCode: 404, Body: null, Headers: []),
            UnknownFailure failure => new FrontendResponse(StatusCode: 500, Body: failure.FailureMessage, Headers: []),
            _ => new(StatusCode: 500, Body: "Unknown GetResult", Headers: [])
        };
    }

    public static string JsonNodesToString(JsonNode[] jsonNodes)
    {
        if (jsonNodes == null || jsonNodes.Length == 0)
        {
            return "[]";
        }

        var jsonArray = new JsonArray();

        foreach (var node in jsonNodes)
        {
            if (node != null)
            {
                jsonArray.Add(node);
            }
        }

        return jsonArray.ToString();
    }
}
