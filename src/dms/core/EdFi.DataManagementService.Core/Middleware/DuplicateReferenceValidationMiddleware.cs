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
/// Middleware responsible for validating duplicate references in DocumentReferenceArrays.
/// This middleware checks for duplicate ReferentialIds within reference arrays,
/// excluding arrays already covered by ArrayUniquenessConstraints.
/// </summary>
internal class DuplicateReferenceValidationMiddleware(ILogger logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        logger.LogDebug(
            "Entering DuplicateReferenceValidationMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        var validationError = ValidateDuplicateReferences(context);
        if (validationError.HasValue)
        {
            var (errorKey, message) = validationError.Value;
            var validationErrors = new Dictionary<string, string[]> { [errorKey] = [message] };

            context.FrontendResponse = ValidationErrorFactory.CreateValidationErrorResponse(
                validationErrors,
                context.FrontendRequest.TraceId
            );

            logger.LogDebug(
                "Duplicate reference detected - {TraceId}",
                context.FrontendRequest.TraceId.Value
            );
            return;
        }

        await next();
    }

    /// <summary>
    /// Validates all document reference arrays for duplicate ReferentialIds
    /// </summary>
    /// <param name="context">The pipeline context containing document and schema information</param>
    /// <returns>A validation error tuple if duplicates are found, null otherwise</returns>
    private static (string errorKey, string message)? ValidateDuplicateReferences(PipelineContext context)
    {
        // Get paths already handled by ArrayUniquenessConstraints to avoid double-validation
        // This replicates the exact logic from the original middleware (lines 57-65)
        var uniquenessParentPaths = ArrayPathHelper.GetUniquenessParentPaths(
            context.ResourceSchema.ArrayUniquenessConstraints
        );

        foreach (var referenceArray in context.DocumentInfo.DocumentReferenceArrays)
        {
            // Skip arrays already covered by ArrayUniquenessConstraints
            if (uniquenessParentPaths.Contains(referenceArray.arrayPath.Value))
            {
                continue;
            }

            if (referenceArray.DocumentReferences.Length > 1)
            {
                var duplicateIndex = FindDuplicateReferenceIndex(referenceArray.DocumentReferences);
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
    /// Uses HashSet for O(n) performance instead of O(n²) nested loop comparison
    /// </summary>
    /// <param name="documentReferences">Array of document references to check</param>
    /// <returns>Index of the first duplicate found, or -1 if no duplicates exist</returns>
    private static int FindDuplicateReferenceIndex(DocumentReference[] documentReferences)
    {
        // Use HashSet for O(n) duplicate detection instead of O(n²)
        var seenIds = new HashSet<string>();

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
