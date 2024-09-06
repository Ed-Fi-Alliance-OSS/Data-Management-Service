// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Backend.Model;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

internal class ValidateQueryMiddleware(ILogger _logger) : IPipelineStep
{
    private static readonly string[] _disallowedQueryFields = ["limit", "offset", "totalCount"];

    /// <summary>
    /// Finds and sets PaginationParameters on the context by parsing the client request.
    /// Returns any errors found for those parameters.
    /// </summary>
    private static List<string> SetPaginationParametersOn(PipelineContext context)
    {
        int? offset = null;
        int? limit = null;
        bool totalCount = false;
        List<string> errors = [];

        if (context.FrontendRequest.QueryParameters.ContainsKey("offset"))
        {
            if (
                !int.TryParse(context.FrontendRequest.QueryParameters["offset"], out int offsetVal)
                || offsetVal < 0
            )
            {
                errors.Add("Offset must be a numeric value greater than or equal to 0.");
            }
            else
            {
                offset = int.TryParse(context.FrontendRequest.QueryParameters["offset"], out int offsetResult)
                    ? offsetResult
                    : offset;
            }
        }

        if (context.FrontendRequest.QueryParameters.ContainsKey("limit"))
        {
            if (
                !int.TryParse(context.FrontendRequest.QueryParameters["limit"], out int limitVal)
                || limitVal < 0
            )
            {
                errors.Add("Limit must be a numeric value greater than or equal to 0.");
            }
            else
            {
                limit = int.TryParse(context.FrontendRequest.QueryParameters["limit"], out int limitResult)
                    ? limitResult
                    : limit;
            }
        }

        if (context.FrontendRequest.QueryParameters.ContainsKey("totalCount"))
        {
            if (!bool.TryParse(context.FrontendRequest.QueryParameters["totalCount"], out totalCount))
            {
                errors.Add("TotalCount must be a boolean value.");
            }
            else
            {
                totalCount = bool.TryParse(
                    context.FrontendRequest.QueryParameters["totalCount"],
                    out bool totalValue
                )
                    ? totalValue
                    : totalCount;
            }
        }

        if (errors.Count == 0)
        {
            context.PaginationParameters = new PaginationParameters(limit, offset, totalCount);
        }
        return errors;
    }

    /// <summary>
    /// Returns a QueryElement for the given client query term using the list of possible query fields,
    /// or null if there is not a match with a valid query field name.
    /// </summary>
    private static QueryElement? queryElementFrom(
        KeyValuePair<string, string> clientQueryTerm,
        QueryField[] possibleQueryFields
    )
    {
        QueryField? matchingQueryField = possibleQueryFields.FirstOrDefault(
            queryField => queryField?.QueryFieldName.ToLower() == clientQueryTerm.Key.ToLower(),
            null
        );

        if (matchingQueryField == null)
        {
            return null;
        }

        return new QueryElement(
            QueryFieldName: clientQueryTerm.Key,
            DocumentPaths: matchingQueryField.DocumentPaths,
            clientQueryTerm.Value
        );
    }

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering ValidateQueryMiddleware - {TraceId}", context.FrontendRequest.TraceId);

        List<string> errors = SetPaginationParametersOn(context);

        if (errors.Count > 0)
        {
            JsonNode failureResponse = FailureResponse.ForBadRequest(
                "The request could not be processed. See 'errors' for details.",
                context.FrontendRequest.TraceId,
                [],
                errors.ToArray()
            );

            _logger.LogDebug(
                "'{Status}'.'{EndpointName}' - {TraceId}",
                "400",
                context.PathComponents.EndpointName,
                context.FrontendRequest.TraceId
            );

            context.FrontendResponse = new FrontendResponse(StatusCode: 400, Body: failureResponse, []);
            return;
        }

        IEnumerable<KeyValuePair<string, string>> nonPaginationQueryTerms =
            context.FrontendRequest.QueryParameters.ExceptBy(_disallowedQueryFields, (term) => term.Key);

        QueryField[] possibleQueryFields = context.ResourceSchema.QueryFields.ToArray();

        List<QueryElement> queryElements = [];

        foreach (KeyValuePair<string, string> clientQueryTerm in nonPaginationQueryTerms)
        {
            QueryElement? queryElement = queryElementFrom(clientQueryTerm, possibleQueryFields);

            if (queryElement == null)
            {
                JsonNode failureResponse = FailureResponse.ForBadRequest(
                    "The request could not be processed. See 'errors' for details.",
                    context.FrontendRequest.TraceId,
                    [],
                    [$@"The query field '{clientQueryTerm.Key}' is not valid for this resource."]
                );

                context.FrontendResponse = new FrontendResponse(StatusCode: 400, Body: failureResponse, []);
                return;
            }

            queryElements.Add(queryElement);
        }

        context.QueryElements = queryElements.ToArray();

        await next();
    }
}
