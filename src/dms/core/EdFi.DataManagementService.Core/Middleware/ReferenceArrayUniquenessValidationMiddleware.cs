// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware responsible for validating reference array uniqueness
/// by checking for duplicate ReferentialIds within reference arrays
/// </summary>
internal class ReferenceArrayUniquenessValidationMiddleware(ILogger logger) : IPipelineStep
{
    public async Task Execute(RequestData requestData, Func<Task> next)
    {
        logger.LogDebug(
            "Entering ReferenceUniquenessValidationMiddleware - {TraceId}",
            requestData.FrontendRequest.TraceId.Value
        );

        (string errorKey, string message)? validationError = ValidateUniqueReferences(requestData);
        if (validationError.HasValue)
        {
            var (errorKey, message) = validationError.Value;
            Dictionary<string, string[]> validationErrors = new() { [errorKey] = [message] };

            requestData.FrontendResponse = ValidationErrorFactory.CreateValidationErrorResponse(
                validationErrors,
                requestData.FrontendRequest.TraceId
            );

            logger.LogDebug(
                "Duplicate reference detected - {TraceId}",
                requestData.FrontendRequest.TraceId.Value
            );
            return;
        }

        await next();
    }

    /// <summary>
    /// Validates all document reference arrays have unique ReferentialIds
    /// </summary>
    /// <returns>A validation error tuple if duplicates are found, null otherwise</returns>
    private static (string errorKey, string message)? ValidateUniqueReferences(RequestData requestData)
    {
        foreach (var referenceArray in requestData.DocumentInfo.DocumentReferenceArrays)
        {
            if (referenceArray.DocumentReferences.Length > 1)
            {
                int duplicateIndex = FindDuplicateReferenceIndex(referenceArray.DocumentReferences);
                if (duplicateIndex >= 0)
                {
                    return ValidationErrorFactory.BuildValidationError(
                        referenceArray.arrayPath.Value,
                        duplicateIndex
                    );
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the index of the first duplicate ReferentialId in a document reference array
    /// </summary>
    /// <param name="documentReferences">Array of document references to check</param>
    /// <returns>Index of the first duplicate found, or -1 if no duplicates exist</returns>
    private static int FindDuplicateReferenceIndex(DocumentReference[] documentReferences)
    {
        // Use HashSet for O(n) duplicate detection instead of O(n²)
        HashSet<string> seenIds = [];

        for (int i = 0; i < documentReferences.Length; i++)
        {
            string id = documentReferences[i].ReferentialId.ToString();
            if (!seenIds.Add(id))
            {
                return i; // Return index of first duplicate found
            }
        }

        return -1; // No duplicates found
    }
}
