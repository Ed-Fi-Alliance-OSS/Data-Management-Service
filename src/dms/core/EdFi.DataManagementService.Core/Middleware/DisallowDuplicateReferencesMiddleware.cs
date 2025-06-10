// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema.Helpers;
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
            "Entering DisallowDuplicateReferencesMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        var validationErrors = new Dictionary<string, List<string>>();

        if (context.ResourceSchema.ArrayUniquenessConstraints.Count > 0)
        {
            foreach (var constraintGroup in context.ResourceSchema.ArrayUniquenessConstraints)
            {
                // 1. Detect array root path (eg: "$.requiredImmunizations[*]")
                string arrayRootPath = GetArrayRootPath(constraintGroup);

                // 2. Get relative paths (eg: "dates[*].immunizationDate", "immunizationTypeDescriptor")
                List<string> relativePaths = constraintGroup
                    .Select(p => GetRelativePath(arrayRootPath, p.Value))
                    .ToList();

                // 3. Call FindDuplicatesWithArrayPath
                (string? arrayPath, int dupeIndex) = context.ParsedBody.FindDuplicatesWithArrayPath(
                    arrayRootPath,
                    relativePaths,
                    logger
                );

                if (dupeIndex >= 0 && arrayPath != null)
                {
                    (string errorKey, string message) = BuildValidationError(arrayPath, dupeIndex);
                    validationErrors[errorKey] = [message];
                    break;
                }
            }
        }

        // Reference arrays
        if (!validationErrors.Any())
        {
            // Get al the Paths from ArrayUniquenessConstraints
            var uniquenessParentPaths = context
                .ResourceSchema.ArrayUniquenessConstraints.SelectMany(group => group)
                .Select(jsonPath =>
                {
                    int lastDot = jsonPath.Value.LastIndexOf('.');
                    return lastDot > 0 ? jsonPath.Value.Substring(0, lastDot) : jsonPath.Value;
                })
                .ToHashSet();

            foreach (var referenceArray in context.DocumentInfo.DocumentReferenceArrays)
            {
                if (uniquenessParentPaths.Contains(referenceArray.arrayPath.Value))
                {
                    continue;
                }

                if (referenceArray.DocumentReferences.Length > 1)
                {
                    var seen = new HashSet<string>();
                    for (int i = 0; i < referenceArray.DocumentReferences.Length; i++)
                    {
                        string id = referenceArray.DocumentReferences[i].ReferentialId.ToString();
                        if (!seen.Add(id))
                        {
                            (string errorKey, string message) = BuildValidationError(
                                referenceArray.arrayPath.Value,
                                i
                            );
                            validationErrors[errorKey] = [message];
                            break;
                        }
                    }

                    if (validationErrors.Any())
                    {
                        break;
                    }
                }
            }
        }

        if (validationErrors.Any())
        {
            logger.LogDebug("Duplicated reference Id - {TraceId}", context.FrontendRequest.TraceId.Value);

            // Convert to Dictionary<string, string[]> for ForDataValidation
            Dictionary<string, string[]> validationErrorsArray = validationErrors.ToDictionary(
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

    private static (string errorKey, string message) BuildValidationError(string arrayPath, int index)
    {
        string errorKey = arrayPath.Substring(0, arrayPath.IndexOf("[*]", StringComparison.Ordinal));
        string[] parts = errorKey.Split('.');
        string shortArrayName = parts[^1];
        string message =
            $"The {GetOrdinal(index + 1)} item of the {shortArrayName} has the same identifying values as another item earlier in the list.";
        return (errorKey, message);
    }

    private static string GetArrayRootPath(IEnumerable<JsonPath> paths)
    {
        // Find the common path until the first [*]
        List<string[]> splitPaths = paths
            .Select(p => p.Value.Split(["[*]"], StringSplitOptions.None))
            .ToList();
        return splitPaths[0][0] + "[*]";
    }

    private static string GetRelativePath(string root, string fullPath)
    {
        if (fullPath.StartsWith(root))
        {
            return fullPath.Substring(root.Length).TrimStart('.');
        }
        return fullPath;
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
