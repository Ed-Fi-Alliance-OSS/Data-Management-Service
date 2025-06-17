// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema.Model;
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
    /// <summary>
    /// Extracts parent paths from ArrayUniquenessConstraints
    /// to determine which reference arrays are already covered by uniqueness constraints
    /// </summary>
    /// <param name="arrayUniquenessConstraints">The array uniqueness constraints from ResourceSchema</param>
    /// <returns>A HashSet of parent paths that are covered by uniqueness constraints</returns>
    private static HashSet<string> GetUniquenessParentPaths(
        IReadOnlyList<ArrayUniquenessConstraint> arrayUniquenessConstraints
    )
    {
        var parentPaths = new HashSet<string>();

        foreach (var constraint in arrayUniquenessConstraints)
        {
            // Add paths from simple constraints
            if (constraint.Paths != null)
            {
                var simplePaths = constraint.Paths.Select(jsonPath =>
                {
                    int lastDot = jsonPath.Value.LastIndexOf('.');
                    return lastDot > 0 ? jsonPath.Value.Substring(0, lastDot) : jsonPath.Value;
                });
                foreach (var parentPath in simplePaths)
                {
                    parentPaths.Add(parentPath);
                }
            }

            // Add paths from nested constraints
            if (constraint.NestedConstraints != null)
            {
                foreach (var nestedConstraint in constraint.NestedConstraints)
                {
                    if (nestedConstraint.BasePath != null)
                    {
                        parentPaths.Add(nestedConstraint.BasePath.Value.Value);
                    }

                    if (nestedConstraint.Paths != null)
                    {
                        foreach (var jsonPath in nestedConstraint.Paths)
                        {
                            // For nested constraints, combine base path with relative path
                            string? basePathValue = nestedConstraint.BasePath?.Value;
                            string fullPath = basePathValue + "." + jsonPath.Value.TrimStart('$', '.');
                            int lastDot = fullPath.LastIndexOf('.');
                            string parentPath = lastDot > 0 ? fullPath.Substring(0, lastDot) : fullPath;
                            parentPaths.Add(parentPath);
                        }
                    }
                }
            }
        }

        return parentPaths;
    }

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        logger.LogDebug(
            "Entering DuplicateReferenceValidationMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        (string errorKey, string message)? validationError = ValidateDuplicateReferences(context);
        if (validationError.HasValue)
        {
            var (errorKey, message) = validationError.Value;
            Dictionary<string, string[]> validationErrors = new() { [errorKey] = [message] };

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
    /// <returns>A validation error tuple if duplicates are found, null otherwise</returns>
    private static (string errorKey, string message)? ValidateDuplicateReferences(PipelineContext context)
    {
        // Get paths already handled by ArrayUniquenessConstraints to avoid double-validation
        HashSet<string> uniquenessParentPaths = GetUniquenessParentPaths(
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
