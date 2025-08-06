// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware;

internal class ValidateQueryMiddleware(ILogger _logger, int _maximumPageSize) : IPipelineStep
{
    private static readonly string[] _disallowedQueryFields = ["limit", "offset", "totalCount"];

    /// <summary>
    /// Finds and sets PaginationParameters on the requestInfo by parsing the client request.
    /// Returns any errors found for those parameters.
    /// </summary>
    private static List<string> SetPaginationParametersOn(RequestInfo requestInfo, int maxPageSize)
    {
        int? offset = null;
        int? limit = null;
        bool totalCount = false;
        List<string> errors = [];

        if (requestInfo.FrontendRequest.QueryParameters.ContainsKey("offset"))
        {
            if (
                !int.TryParse(requestInfo.FrontendRequest.QueryParameters["offset"], out int offsetVal)
                || offsetVal < 0
            )
            {
                errors.Add("Offset must be a numeric value greater than or equal to 0.");
            }
            else
            {
                offset = offsetVal;
            }
        }

        if (requestInfo.FrontendRequest.QueryParameters.ContainsKey("limit"))
        {
            if (
                !int.TryParse(requestInfo.FrontendRequest.QueryParameters["limit"], out int limitVal)
                || limitVal < 0
                || limitVal > maxPageSize
            )
            {
                errors.Add($"Limit must be omitted or set to a numeric value between 0 and {maxPageSize}.");
            }
            else
            {
                limit = limitVal;
            }
        }

        if (requestInfo.FrontendRequest.QueryParameters.ContainsKey("totalCount"))
        {
            if (!bool.TryParse(requestInfo.FrontendRequest.QueryParameters["totalCount"], out totalCount))
            {
                errors.Add("TotalCount must be a boolean value.");
            }
            else
            {
                totalCount = bool.TryParse(
                    requestInfo.FrontendRequest.QueryParameters["totalCount"],
                    out bool totalValue
                )
                    ? totalValue
                    : totalCount;
            }
        }

        if (errors.Count == 0)
        {
            requestInfo.PaginationParameters = new PaginationParameters(
                limit,
                offset,
                totalCount,
                maxPageSize
            );
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

        if (
            matchingQueryField.DocumentPathsWithType[0].Type == "date-time"
            && DateTime.TryParse(
                clientQueryTerm.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime dateTimeValue
            )
        )
        {
            string fullDateTimeString = dateTimeValue
                .ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            return new QueryElementAndType(
                QueryFieldName: clientQueryTerm.Key,
                DocumentPathsAndTypes: matchingQueryField.DocumentPathsWithType,
                Value: fullDateTimeString
            );
        }

        return new QueryElementAndType(
            QueryFieldName: clientQueryTerm.Key,
            DocumentPathsAndTypes: matchingQueryField.DocumentPathsWithType,
            Value: clientQueryTerm.Value
        );
    }

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ValidateQueryMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        List<string> errors = SetPaginationParametersOn(requestInfo, _maximumPageSize);

        if (errors.Count > 0)
        {
            JsonNode failureResponse = FailureResponse.ForBadRequest(
                "The request could not be processed. See 'errors' for details.",
                requestInfo.FrontendRequest.TraceId,
                [],
                errors.ToArray()
            );

            _logger.LogDebug(
                "'{Status}'.'{EndpointName}' - {TraceId}",
                "400",
                requestInfo.PathComponents.EndpointName,
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(StatusCode: 400, Body: failureResponse, []);
            return;
        }

        IEnumerable<KeyValuePair<string, string>> nonPaginationQueryTerms =
            requestInfo.FrontendRequest.QueryParameters.ExceptBy(_disallowedQueryFields, (term) => term.Key);

        QueryField[] possibleQueryFields = requestInfo.ResourceSchema.QueryFields.ToArray();

        List<QueryElement> queryElements = [];

        Dictionary<string, string[]> validationErrors = [];

        foreach (KeyValuePair<string, string> clientQueryTerm in nonPaginationQueryTerms)
        {
            QueryElementAndType? queryElementAndType = QueryElementFrom(clientQueryTerm, possibleQueryFields);

            if (queryElementAndType == null)
            {
                JsonNode failureResponse = FailureResponse.ForBadRequest(
                    "The request could not be processed. See 'errors' for details.",
                    requestInfo.FrontendRequest.TraceId,
                    [],
                    [$@"The query field '{clientQueryTerm.Key}' is not valid for this resource."]
                );

                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 400,
                    Body: failureResponse,
                    []
                );
                return;
            }

            string jsonPathString = queryElementAndType.DocumentPathsAndTypes[0].JsonPathString;
            string queryFieldName = queryElementAndType.QueryFieldName;
            string queryFieldValue = queryElementAndType.Value;

            switch (queryElementAndType.DocumentPathsAndTypes[0].Type)
            {
                case "boolean":
                    if (!bool.TryParse(queryFieldValue, out _))
                    {
                        AddValidationError(validationErrors, jsonPathString, queryFieldValue, queryFieldName);
                    }
                    queryFieldValue = queryFieldValue.ToLower();
                    break;
                case "date":
                    if (
                        DateTime.TryParse(
                            queryFieldValue,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var dateTime
                        )
                    )
                    {
                        // query parameter was valid but ensure we only pass the date portion downstream to queries
                        queryFieldValue = dateTime.ToString("yyyy-MM-dd");
                    }
                    else
                    {
                        AddValidationError(validationErrors, jsonPathString, queryFieldValue, queryFieldName);
                    }
                    break;
                case "date-time":
                    if (
                        !DateTime.TryParse(
                            queryFieldValue,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out _
                        )
                    )
                    {
                        AddValidationError(validationErrors, jsonPathString, queryFieldValue, queryFieldName);
                    }
                    break;

                case "number":
                    if (!decimal.TryParse(queryFieldValue, out _))
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
                            queryFieldValue,
                            "HH:mm:ss",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
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
                    queryFieldValue
                )
            );
        }

        if (validationErrors.Count != 0)
        {
            _logger.LogDebug(
                "Query parameter format error - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 400,
                Body: ForDataValidation(
                    "Data validation failed. See 'validationErrors' for details.",
                    traceId: requestInfo.FrontendRequest.TraceId,
                    validationErrors,
                    []
                ),
                Headers: []
            );
            return;
        }
        else
        {
            requestInfo.QueryElements = queryElements.ToArray();

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
