// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Validation;

/// <summary>
/// The outcome of validating the resource-property filters of a request.
/// </summary>
/// <remarks>
/// Three outcomes rather than an error list, because the two failures answer with different response
/// shells: an unrecognized field is a bad-request fault naming one field, while faulty values are a
/// data-validation fault keyed by document path. Collapsing them would leave the caller inferring
/// which shell to use from the shape of an error collection.
/// </remarks>
internal abstract record ResourceQueryFilterResult
{
    private ResourceQueryFilterResult() { }

    /// <summary>
    /// Every supplied filter matched a query field and parsed as its type.
    /// </summary>
    /// <param name="QueryElements">
    /// One element per supplied filter, in request order, carrying the document paths and type of the
    /// matched query field.
    /// </param>
    public sealed record Valid(QueryElement[] QueryElements) : ResourceQueryFilterResult;

    /// <summary>
    /// A query parameter matched no query field of this resource. Evaluation stops at the first one.
    /// </summary>
    /// <param name="QueryFieldName">The field name exactly as the client supplied it.</param>
    public sealed record UnknownQueryField(string QueryFieldName) : ResourceQueryFilterResult;

    /// <summary>
    /// One or more filter values did not parse as the type of the query field they matched.
    /// </summary>
    /// <param name="ValidationErrors">
    /// Messages keyed by the document path of the matched query field. Several filters can share one
    /// path, so a path can carry several messages. Concrete rather than an interface because the
    /// data-validation response builder takes this type: copying into a fresh dictionary to satisfy a
    /// narrower declared type would put the serialized body's key order at the mercy of that copy.
    /// </param>
    public sealed record InvalidValues(Dictionary<string, string[]> ValidationErrors)
        : ResourceQueryFilterResult;
}

/// <summary>
/// Matches a request's query parameters against the query fields of a resource and converts them into
/// typed query elements.
/// </summary>
/// <remarks>
/// Pure, and shared by every operation that filters the same candidate set, so filter behavior cannot
/// drift between them. An operation whose boundaries were computed over a different candidate set than
/// its pages would silently skip or duplicate documents, and nothing in the response would say so.
/// Which parameter names are the caller's own is the caller's knowledge, so the excluded names are
/// supplied rather than assumed here: excluding a name is not accepting it, and an operation that does
/// not recognize a name rejects it in its own validation.
/// </remarks>
internal static class ResourceQueryFilterValidator
{
    /// <summary>
    /// Validates the resource-property filters of a request.
    /// </summary>
    /// <param name="queryParameters">The request's query parameters.</param>
    /// <param name="possibleQueryFields">The query fields this resource exposes.</param>
    /// <param name="ordinalExcludedNames">
    /// Parameter names this operation owns, matched case-sensitively, consistent with how the operation
    /// parses them.
    /// </param>
    /// <param name="ignoreCaseExcludedNames">
    /// Parameter names this operation owns, matched case-insensitively, consistent with how the
    /// operation looks them up.
    /// </param>
    internal static ResourceQueryFilterResult Validate(
        IReadOnlyDictionary<string, string> queryParameters,
        QueryField[] possibleQueryFields,
        IReadOnlyList<string> ordinalExcludedNames,
        IReadOnlyList<string> ignoreCaseExcludedNames
    )
    {
        ArgumentNullException.ThrowIfNull(queryParameters);
        ArgumentNullException.ThrowIfNull(possibleQueryFields);
        ArgumentNullException.ThrowIfNull(ordinalExcludedNames);
        ArgumentNullException.ThrowIfNull(ignoreCaseExcludedNames);

        IEnumerable<KeyValuePair<string, string>> filterQueryTerms = queryParameters
            .ExceptBy(ordinalExcludedNames, (term) => term.Key)
            .ExceptBy(ignoreCaseExcludedNames, (term) => term.Key, StringComparer.OrdinalIgnoreCase);

        List<QueryElement> queryElements = [];

        Dictionary<string, string[]> validationErrors = [];

        foreach (KeyValuePair<string, string> clientQueryTerm in filterQueryTerms)
        {
            QueryElementAndType? queryElementAndType = QueryElementFrom(clientQueryTerm, possibleQueryFields);

            if (queryElementAndType is null)
            {
                return new ResourceQueryFilterResult.UnknownQueryField(clientQueryTerm.Key);
            }

            string jsonPathString = queryElementAndType.DocumentPathsAndTypes[0].JsonPathString;
            string queryFieldName = queryElementAndType.QueryFieldName;
            string queryFieldValue = queryElementAndType.Value;
            string type = queryElementAndType.DocumentPathsAndTypes[0].Type;

            switch (type)
            {
                case "boolean":
                    // Canonicalized from the parsed value rather than by folding the supplied text.
                    // The two differ on every input bool.TryParse accepts but does not spell
                    // canonically: it ignores surrounding whitespace, so folding " true " leaves the
                    // padding on the value a filter is later compared against, which matches nothing.
                    // Deriving the text from the parsed boolean also removes a culture-sensitive fold
                    // on a fixed protocol token.
                    if (bool.TryParse(queryFieldValue, out bool booleanValue))
                    {
                        queryFieldValue = booleanValue ? "true" : "false";
                    }
                    else
                    {
                        AddValidationError(validationErrors, jsonPathString, queryFieldValue, queryFieldName);
                    }
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
                        $"Resource query filter validation found an unsupported type {type}"
                    );
            }

            // Convert QueryElementAndType to QueryElement
            queryElements.Add(
                new(
                    queryElementAndType.QueryFieldName,
                    queryElementAndType
                        .DocumentPathsAndTypes.Select(x => new JsonPath(x.JsonPathString))
                        .ToArray(),
                    queryFieldValue,
                    type
                )
            );
        }

        return validationErrors.Count != 0
            ? new ResourceQueryFilterResult.InvalidValues(validationErrors)
            : new ResourceQueryFilterResult.Valid([.. queryElements]);
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
            queryField =>
                queryField is not null
                && string.Equals(
                    queryField.QueryFieldName,
                    clientQueryTerm.Key,
                    StringComparison.OrdinalIgnoreCase
                ),
            null
        );

        if (matchingQueryField is null)
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
