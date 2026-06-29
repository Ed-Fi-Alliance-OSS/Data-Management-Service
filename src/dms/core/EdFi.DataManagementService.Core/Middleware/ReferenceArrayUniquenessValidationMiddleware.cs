// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
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
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        logger.LogDebug(
            "Entering ReferenceUniquenessValidationMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // DocumentInfo is extracted from the raw submitted body, but a writable profile
        // may hide submitted reference collections that the shaper strips from the write body.
        // Validate duplicate references against the profile-shaped reference arrays so hidden
        // submitted collections are accepted and ignored. Non-profile writes keep using the raw
        // DocumentInfo extracted earlier in the pipeline.
        IReadOnlyList<DocumentReferenceArray> referenceArrays;
        if (requestInfo.BackendProfileWriteContext is not null)
        {
            try
            {
                (_, DocumentReferenceArray[] shapedReferenceArrays) =
                    requestInfo.ResourceSchema.ExtractReferences(
                        ProfileWriteValidationBody.Effective(requestInfo),
                        logger
                    );
                referenceArrays = shapedReferenceArrays;
            }
            catch (ReferenceExtractionValidationException ex)
            {
                // Mirror ExtractDocumentInfoMiddleware's relational handling so a malformed shaped
                // reference surfaces the same data-validation response rather than throwing.
                requestInfo.FrontendResponse = ValidationErrorFactory.CreateValidationErrorResponse(
                    ValidationErrorFactory.BuildWriteValidationErrors(ex.ValidationFailures),
                    requestInfo.FrontendRequest.TraceId
                );
                return;
            }
        }
        else
        {
            referenceArrays = requestInfo.DocumentInfo.DocumentReferenceArrays;
        }

        (string errorKey, string message)? validationError = ValidateUniqueReferences(referenceArrays);
        if (validationError.HasValue)
        {
            var (errorKey, message) = validationError.Value;
            Dictionary<string, string[]> validationErrors = new() { [errorKey] = [message] };

            requestInfo.FrontendResponse = ValidationErrorFactory.CreateValidationErrorResponse(
                validationErrors,
                requestInfo.FrontendRequest.TraceId
            );

            logger.LogDebug(
                "Duplicate reference detected - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );
            return;
        }

        await next();
    }

    /// <summary>
    /// Validates all document reference arrays have unique ReferentialIds
    /// </summary>
    /// <returns>A validation error tuple if duplicates are found, null otherwise</returns>
    private static (string errorKey, string message)? ValidateUniqueReferences(
        IReadOnlyList<DocumentReferenceArray> documentReferenceArrays
    )
    {
        foreach (var referenceArray in documentReferenceArrays)
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
