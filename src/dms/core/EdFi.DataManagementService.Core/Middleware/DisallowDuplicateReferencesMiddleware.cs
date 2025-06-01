// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
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

        // Get al the Paths from ArrayUniquenessConstraints
        var uniquenessPaths = context
            .ResourceSchema.ArrayUniquenessConstraints.SelectMany(g => g)
            .Select(p => p.Value)
            .ToHashSet();

        // Get all the reference paths that are not part of ArrayUniquenessConstraints
        var referencePaths = context
            .ResourceSchema.DocumentPaths.Where(p => p.IsReference && HasSafeReferenceJsonPaths(p))
            .SelectMany(p => p.ReferenceJsonPathsElements.Select(e => e.ReferenceJsonPath.Value))
            .Where(path => !uniquenessPaths.Contains(path))
            .ToList();

        var allPaths = uniquenessPaths.Concat(referencePaths).ToList();

        (string? arrayPath, int dupeIndex) = context.ParsedBody.FindDuplicatesWithArrayPath(allPaths, logger);

        if (dupeIndex > 0 && arrayPath != null)
        {
            string errorKey = arrayPath.Substring(0, arrayPath.IndexOf("[*]", StringComparison.Ordinal));
            string[] parts = errorKey.Split('.');
            string shortArrayName = parts[parts.Length - 1];
            string message =
                $"The {GetOrdinal(dupeIndex + 1)} item of the {shortArrayName} has the same identifying values as another item earlier in the list.";

            validationErrors[errorKey] = [message];
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

    private static bool HasSafeReferenceJsonPaths(DocumentPath path)
    {
        try
        {
            _ = path.ReferenceJsonPathsElements;
            return true;
        }
        catch
        {
            return false;
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
