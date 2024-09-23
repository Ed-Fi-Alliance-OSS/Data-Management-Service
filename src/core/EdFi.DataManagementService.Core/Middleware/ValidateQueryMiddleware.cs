// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

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
    private static QueryElementAndType? QueryElementFrom(
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

        return new QueryElementAndType(
            QueryFieldName: clientQueryTerm.Key,
            DocumentPathsAndTypes: matchingQueryField.DocumentPathsWithType,
            Value: clientQueryTerm.Value
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

        Dictionary<string, string[]> validationErrors = [];

        foreach (KeyValuePair<string, string> clientQueryTerm in nonPaginationQueryTerms)
        {
            QueryElementAndType? queryElementAndType = QueryElementFrom(clientQueryTerm, possibleQueryFields);

            if (queryElementAndType == null)
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

            string jsonPathString = queryElementAndType.DocumentPathsAndTypes[0].JsonPathString;
            string queryFieldName = queryElementAndType.QueryFieldName;
            object queryFieldValue = queryElementAndType.Value;

            switch (queryElementAndType.DocumentPathsAndTypes[0].Type)
            {
                case "boolean":
                    if (!bool.TryParse(queryFieldValue?.ToString(), out _))
                    {
                        AddValidationError(validationErrors, jsonPathString, queryFieldValue!, queryFieldName);
                    }
                    break;
                case "date":
                    if (
                        !DateTime.TryParseExact(
                            queryFieldValue.ToString(),
                            "yyyy-MM-dd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None,
                            out _
                        )
                    )
                    {
                        AddValidationError(validationErrors, jsonPathString, queryFieldValue, queryFieldName);
                    }
                    break;
                case "date-time":
                    if (
                        !DateTime.TryParse(
                            queryFieldValue.ToString(),
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None,
                            out _
                        )
                    )
                    {
                        AddValidationError(validationErrors, jsonPathString, queryFieldValue, queryFieldName);
                    }
                    break;

                case "number":
                    if (!decimal.TryParse(queryFieldValue.ToString(), out _))
                    {
                        AddValidationError(validationErrors, jsonPathString, queryFieldValue, queryFieldName);
                    }
                    break;

                case "string":
                    if (queryFieldValue is not string)
                    {
                        AddValidationError(validationErrors, jsonPathString, queryFieldValue, queryFieldName);
                    }
                    break;

                case "time":
                    if (
                        !DateTime.TryParseExact(
                            queryFieldValue.ToString(),
                            "HH:mm:ss",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None,
                            out _
                        )
                    )
                    {
                        AddValidationError(validationErrors, jsonPathString, queryFieldValue, queryFieldName);
                    }
                    break;
                default:
                    throw new InvalidOperationException(
                        $"ValidateQueryMiddleware found an unsupported type {queryElementAndType.DocumentPathsAndTypes[0].Type}"
                    );
            }

            // Convert QueryElementAndType to QueryElement
            queryElements.Add(
                new(
                    queryElementAndType.QueryFieldName,
                    queryElementAndType
                        .DocumentPathsAndTypes.Select(x => new JsonPath(x.JsonPathString))
                        .ToArray(),
                    queryElementAndType.Value
                )
            );
        }

        if (validationErrors.Count != 0)
        {
            _logger.LogDebug("Query parameter format error - {TraceId}", context.FrontendRequest.TraceId);

            context.FrontendResponse = new FrontendResponse(
                StatusCode: 400,
                Body: ForDataValidation(
                    "Data validation failed. See 'validationErrors' for details.",
                    traceId: context.FrontendRequest.TraceId,
                    validationErrors,
                    []
                ),
                Headers: []
            );
            return;
        }
        else
        {
            context.QueryElements = queryElements.ToArray();

            await next();
        }
    }

    private static void AddValidationError(
        Dictionary<string, string[]> errors,
        string jsonPathString,
        object queryValue,
        string queryFieldName
    )
    {
        if (!errors.ContainsKey(jsonPathString))
        {
            errors[jsonPathString] = [];
        }

        string errorMessage = $"The value '{queryValue}' is not valid for {queryFieldName}.";
        string[] updatedErrors = new string[errors[jsonPathString].Length + 1];
        errors[jsonPathString].CopyTo(updatedErrors, 0);
        updatedErrors[^1] = errorMessage;

        errors[jsonPathString] = updatedErrors;
    }
}
