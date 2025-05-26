// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;
using JsonPath = Json.Path.JsonPath;

namespace EdFi.DataManagementService.Core.Middleware;

internal class DisallowDuplicateReferencesMiddleware(ILogger logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        logger.LogDebug(
            "Entering DuplicateReferencesMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        var validationErrors = new Dictionary<string, List<string>>();

        // Validation for values on ArrayUniquenessConstraints
        ValidateArrayUniquenessConstraints(context, validationErrors);

        // Validation for Reference not part of ArrayUniquenessConstraints
        // Eg: BellSchedules has a collection of classPeriodReference that are not a part of the array uniqueness constraints
        ValidateReferences(context, validationErrors);

        if (validationErrors.Any())
        {
            logger.LogDebug("Duplicated reference Id - {TraceId}", context.FrontendRequest.TraceId.Value);

            // Convert to Dictionary<string, string[]> for ForDataValidation
            var validationErrorsArray = validationErrors.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToArray()
            );

            context.FrontendResponse = new FrontendResponse(
                StatusCode: 400,
                Body: ForDataValidation(
                    "Data validation failed. See 'validationErrors' for details.",
                    traceId: context.FrontendRequest.TraceId,
                    validationErrorsArray,
                    []
                ),
                Headers: []
            );
            return;
        }

        await next();
    }

    private static void ValidateReferences(
        PipelineContext context,
        Dictionary<string, List<string>> validationErrors
    )
    {
        var uniquenessPaths = context
            .ResourceSchema.ArrayUniquenessConstraints.SelectMany(g => g)
            .Select(p => p.Value)
            .ToHashSet();

        var referencePaths = context
            .ResourceSchema.DocumentPaths.Where(p => p.IsReference)
            .Where(p =>
            {
                try
                {
                    _ = p.ReferenceJsonPathsElements;
                    return true;
                }
                catch
                {
                    return false;
                }
            })
            .SelectMany(p => p.ReferenceJsonPathsElements.Select(e => e.ReferenceJsonPath.Value))
            .Where(path => !uniquenessPaths.Contains(path))
            .ToList();

        // Group paths by the base path of the object containing the
        // e.g: $.classPeriods[*].classPeriodReference.classPeriodName => base: $.classPeriods[*].classPeriodReference
        var groupedPaths = referencePaths
            .GroupBy(path =>
            {
                int lastIndex = path.LastIndexOf('.');
                return lastIndex > 0 ? path.Substring(0, lastIndex) : path;
            })
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (basePath, fields) in groupedPaths)
        {
            var baseJsonPath = JsonPath.Parse(basePath);
            var baseResults = baseJsonPath.Evaluate(context.ParsedBody);

            var keys = new Dictionary<string, int>();

            for (int i = 0; i < baseResults.Matches.Count; i++)
            {
                var obj = baseResults.Matches[i].Value as JsonObject;
                if (obj == null)
                {
                    continue;
                }

                // Build composite key by joining values of each field in ‘fields’.
                var keyParts = new List<string>();
                foreach (string fieldPath in fields)
                {
                    // Extract the field property, e.g. “classPeriodName”.
                    string property = fieldPath[(basePath.Length + 1)..]; // +1 for hte point

                    if (obj.TryGetPropertyValue(property, out var value))
                    {
                        keyParts.Add(value?.ToString() ?? "");
                    }
                    else
                    {
                        keyParts.Add("");
                    }
                }

                string compositeKey = string.Join("|", keyParts);

                if (keys.ContainsKey(compositeKey))
                {
                    string arrayName = basePath.Substring(basePath.LastIndexOf('.') + 1);
                    string errorKey = basePath.Substring(
                        0,
                        basePath.IndexOf("[*]", StringComparison.Ordinal)
                    );

                    string message =
                        $"The {GetOrdinal(i + 1)} item of the {arrayName} has the same identifying values as another item earlier in the list.";

                    if (!validationErrors.TryGetValue(errorKey, out var messages))
                    {
                        validationErrors[errorKey] = messages = new List<string>();
                    }
                    messages.Add(message);
                }
                else
                {
                    keys[compositeKey] = i;
                }
            }
        }
    }

    private static void ValidateArrayUniquenessConstraints(
        PipelineContext context,
        Dictionary<string, List<string>> validationErrors
    )
    {
        var constraints = context.ResourceSchema.ArrayUniquenessConstraints;
        var body = context.ParsedBody;

        foreach (var group in constraints)
        {
            var firstPath = JsonPath.Parse(group[0].Value);
            var firstResult = firstPath.Evaluate(body);
            if (firstResult.Matches.Count <= 1)
            {
                continue;
            }

            // Extract the array base path, e.g: "$.items[*]" → "$.items"
            string arrayPath = group[0].Value[..group[0].Value.IndexOf("[*]", StringComparison.Ordinal)];
            string[] arrayParts = arrayPath.Split('.');
            string shortArrayName = arrayParts[arrayParts.Length - 1];
            string errorKey = arrayPath;

            // Group fields by each item
            var itemPath = JsonPath.Parse($"{arrayPath}[*]");
            var arrayResult = itemPath.Evaluate(body);

            var lastSeen = new Dictionary<string, int>();
            for (int i = 0; i < arrayResult.Matches.Count; i++)
            {
                var match = arrayResult.Matches[i];
                var item = match.Value as JsonObject;
                if (item == null)
                {
                    continue;
                }

                var keyParts = new List<string>();
                foreach (var fieldPath in group)
                {
                    // e.g: "$.items[*].assessmentItemReference.assessmentIdentifier"
                    string relativePath = fieldPath.Value[(arrayPath.Length + 3)..];

                    // Add "$." to JsonPath accept the value
                    var fullFieldPath = JsonPath.Parse($"$.{relativePath}");
                    var fieldResult = fullFieldPath.Evaluate(item);
                    keyParts.Add(fieldResult.Matches.FirstOrDefault()?.Value?.ToString() ?? "");
                }

                string compositeKey = string.Join("|", keyParts);
                if (lastSeen.ContainsKey(compositeKey))
                {
                    string errorMessage =
                        $"The {GetOrdinal(i + 1)} item of the {shortArrayName} has the same identifying values as another item earlier in the list.";

                    if (!validationErrors.TryGetValue(errorKey, out var messages))
                    {
                        validationErrors[errorKey] = messages = [];
                    }

                    if (!messages.Contains(errorMessage))
                    {
                        messages.Add(errorMessage);
                    }
                }
                else
                {
                    lastSeen[compositeKey] = i;
                }
            }
        }
    }

    private static string GetOrdinal(int number)
    {
        if (number % 100 == 11 || number % 100 == 12 || number % 100 == 13)
        {
            return $"{number}th";
        }

        return (number % 10) switch
        {
            2 => $"{number}nd",
            3 => $"{number}rd",
            _ => $"{number}th",
        };
    }
}
