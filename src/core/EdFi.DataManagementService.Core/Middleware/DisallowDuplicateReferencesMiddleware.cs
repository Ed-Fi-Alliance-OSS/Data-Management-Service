// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
            context.FrontendRequest.TraceId
        );

        var validationErrors = new Dictionary<string, string[]>();

        // Find duplicates in document references
        if (context.DocumentInfo.DocumentReferences.GroupBy(d => d.ReferentialId).Any(g => g.Count() > 1))
        {
            // if duplicates are found, they should be reported
            ValidateDuplicates(
                context.DocumentInfo.DocumentReferences,
                item => item.ReferentialId.Value,
                item => item.ResourceInfo.ResourceName.Value,
                validationErrors
            );
        }

        // Find duplicates in descriptor references
        if (context.DocumentInfo.DescriptorReferences.GroupBy(d => d.ReferentialId).Any(g => g.Count() > 1))
        {
            // if duplicates are found, they should be reported
            ValidateDuplicates(
                context.DocumentInfo.DescriptorReferences,
                item => item.ReferentialId.Value,
                item => item.Path.Value,
                validationErrors
            );
        }

        if (validationErrors.Any())
        {
            logger.LogDebug("Duplicated reference Id - {TraceId}", context.FrontendRequest.TraceId);

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

    private static void ValidateDuplicates<T>(
        IEnumerable<T> items,
        Func<T, Guid> getReferentialId,
        Func<T, string> getPath,
        Dictionary<string, string[]> validationErrors
    )
    {
        var seenItems = new HashSet<Guid>();
        var positions = new Dictionary<string, int>();

        foreach (var item in items)
        {
            Guid referentialId = getReferentialId(item);
            string path = getPath(item);

            // the propertyName varies according to the origin (DescriptorReference or DocumentReferences)
            string propertyName = path.StartsWith("$", StringComparison.InvariantCultureIgnoreCase)
                ? path
                : $"$.{path}";

            positions.TryAdd(propertyName, 1);

            if (!seenItems.Add(referentialId))
            {
                path = path.StartsWith("$", StringComparison.InvariantCultureIgnoreCase)
                    ? ExtractArrayName(path)
                    : path;

                string errorMessage =
                    $"The {GetOrdinal(positions[propertyName])} item of the {path} has the same identifying values as another item earlier in the list.";

                if (validationErrors.ContainsKey(propertyName))
                {
                    var existingMessages = validationErrors[propertyName].ToList();
                    existingMessages.Add(errorMessage);
                    validationErrors[propertyName] = existingMessages.ToArray();
                }
                else
                {
                    validationErrors[propertyName] = [errorMessage];
                }
            }
            positions[propertyName]++;
        }
    }

    private static string GetOrdinal(int number)
    {
        if (number % 100 == 11 || number % 100 == 12 || number % 100 == 13)
            return $"{number}th";

        return (number % 10) switch
        {
            2 => $"{number}nd",
            3 => $"{number}rd",
            _ => $"{number}th"
        };
    }

    private static string ExtractArrayName(string path)
    {
        // Logic to extract the array name from the JSON path, e.g., "gradeLevels".
        string[] parts = path.Split('.');
        return parts.Length > 1 ? parts[1].Trim('[', ']', '*') : string.Empty;
    }
}
