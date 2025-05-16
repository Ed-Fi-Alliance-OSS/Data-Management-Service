// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware;

internal class DisallowDuplicateReferencesMiddleware(ILogger logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        logger.LogDebug(
            "Entering DuplicateReferencesMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        var validationErrors = new Dictionary<string, string[]>();

        // Only need Validation for values on ArrayUniquenessConstraints
        ValidateArrayUniquenessConstraints(context, validationErrors);

        if (validationErrors.Any())
        {
            logger.LogDebug("Duplicated reference Id - {TraceId}", context.FrontendRequest.TraceId.Value);

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

        await next();
    }

    private static void ValidateArrayUniquenessConstraints(
        PipelineContext context,
        Dictionary<string, string[]> validationErrors
    )
    {
        var constraints = context.ResourceSchema.ArrayUniquenessConstraints;
        var body = context.ParsedBody;

        foreach (var group in constraints)
        {
            // Obtain the base path (without the last level)
            string fullPath = group[0].Value; // Eg: "$.items[*].assessmentItemReference.assessmentIdentifier"
            int lastDot = fullPath.LastIndexOf('.');
            string propertyName = lastDot > 0 ? fullPath.Substring(0, lastDot) : fullPath; // "$.items[*].assessmentItemReference"

            var arrayPathMatch = Regex.Match(propertyName, @"^\$\.(.+?)\[\*\]");
            if (!arrayPathMatch.Success)
            {
                continue;
            }

            string arrayName = arrayPathMatch.Groups[1].Value;
            var arrayNode = GetNodeOrValueFromPath(body, arrayName) as JsonArray;
            if (arrayNode == null)
            {
                continue;
            }

            var fieldPaths = group.Select(p => Regex.Replace(p.Value, @"^\$\.\w+\[\*\]\.", "")).ToList();
            var lastSeen = new Dictionary<string, int>();

            for (int i = arrayNode.Count - 1; i >= 0; i--)
            {
                var item = arrayNode[i] as JsonObject;
                if (item == null)
                {
                    continue;
                }

                var keyParts = new List<string>();
                foreach (string fieldPath in fieldPaths)
                {
                    var value = GetNodeOrValueFromPath(item, fieldPath);
                    keyParts.Add(value?.ToString() ?? "");
                }
                string compositeKey = string.Join("|", keyParts);

                if (lastSeen.ContainsKey(compositeKey))
                {
                    string[] arrayNameParts = arrayName.Split('.');
                    string shortArrayName =
                        arrayNameParts.Length > 0 ? arrayNameParts[arrayNameParts.Length - 1] : arrayName;

                    string errorMessage =
                        $"The {GetOrdinal(lastSeen[compositeKey] + 1)} item of the {shortArrayName} has the same identifying values as another item earlier in the list.";
                    if (validationErrors.TryGetValue(propertyName, out var existingMessages))
                    {
                        if (!existingMessages.Contains(errorMessage))
                        {
                            validationErrors[propertyName] = existingMessages.Append(errorMessage).ToArray();
                        }
                    }
                    else
                    {
                        validationErrors[propertyName] = new[] { errorMessage };
                    }
                }
                else
                {
                    lastSeen[compositeKey] = i;
                }
            }
        }
    }

    // Helper to get nested property value from a JsonObject using dot notation
    private static JsonNode? GetNodeOrValueFromPath(JsonNode node, string path)
    {
        string[] parts = path.Split('.');
        JsonNode? current = node;
        foreach (string part in parts)
        {
            if (current is JsonObject obj && obj.TryGetPropertyValue(part, out var next))
            {
                current = next;
            }
            else
            {
                return null;
            }
        }
        return current;
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
