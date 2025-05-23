// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
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

        // Validation for Reference Ids
        // Eg: BellSchedules has a collection of classPeriodReference that are not a part of the array uniqueness constraints
        if (context.DocumentInfo.DocumentReferences.GroupBy(d => d.ReferentialId).Any(g => g.Count() > 1))
        {
            ValidateDuplicates(
                context.DocumentInfo.DocumentReferences,
                item => item.ReferentialId.Value,
                item => item.ResourceInfo.ResourceName.Value,
                validationErrors
            );
        }

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

    private static void ValidateDuplicates<T>(
        IEnumerable<T> items,
        Func<T, Guid> getReferentialId,
        Func<T, string> getResourceNameFunc,
        Dictionary<string, List<string>> validationErrors
    )
    {
        var seenItems = new HashSet<Guid>();
        var positions = new Dictionary<string, int>();

        foreach (var item in items)
        {
            Guid referentialId = getReferentialId(item);
            string resourceName = getResourceNameFunc(item);
            string propertyName = $"$.{resourceName}";

            positions.TryAdd(propertyName, 1);

            if (!seenItems.Add(referentialId))
            {
                string errorMessage =
                    $"The {GetOrdinal(positions[propertyName])} item of the {resourceName} has the same identifying values as another item earlier in the list.";

                if (validationErrors.TryGetValue(propertyName, out var existingMessages))
                {
                    existingMessages.Add(errorMessage);
                }
                else
                {
                    validationErrors[propertyName] = [errorMessage];
                }
            }
            positions[propertyName]++;
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

internal record IndexedReferenceGroup(int index, List<DescriptorReference> references);

internal record KeyReferenceGroup(string key, List<IndexedReferenceGroup> indexGroups);
