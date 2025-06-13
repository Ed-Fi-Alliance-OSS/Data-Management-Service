// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware responsible for validating array uniqueness constraints defined in the resource schema.
/// This middleware processes schema-defined constraint groups to ensure uniqueness within arrays
/// based on combinations of field values.
/// </summary>
internal class ArrayUniquenessValidationMiddleware(ILogger logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        logger.LogDebug(
            "Entering ArrayUniquenessValidationMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        if (context.ResourceSchema.ArrayUniquenessConstraints.Count > 0)
        {
            var validationError = ValidateArrayUniquenessConstraints(context, logger);
            if (validationError.HasValue)
            {
                var (errorKey, message) = validationError.Value;
                var validationErrors = new Dictionary<string, string[]> { [errorKey] = [message] };

                context.FrontendResponse = ValidationErrorFactory.CreateValidationErrorResponse(
                    validationErrors,
                    context.FrontendRequest.TraceId
                );

                logger.LogDebug(
                    "Array uniqueness constraint violation - {TraceId}",
                    context.FrontendRequest.TraceId.Value
                );
                return;
            }
        }

        await next();
    }

    /// <summary>
    /// Validates all array uniqueness constraints for the current document
    /// </summary>
    /// <param name="context">The pipeline context containing the document and schema information</param>
    /// <param name="logger">Logger for debugging information</param>
    /// <returns>A validation error tuple if violations are found, null otherwise</returns>
    private static (string errorKey, string message)? ValidateArrayUniquenessConstraints(
        PipelineContext context,
        ILogger logger
    )
    {
        foreach (var constraintGroup in context.ResourceSchema.ArrayUniquenessConstraints)
        {
            // 1. Detect array root path (e.g., "$.requiredImmunizations[*]")
            string arrayRootPath = ArrayPathHelper.GetArrayRootPath(constraintGroup);

            // 2. Get relative paths (e.g., "dates[*].immunizationDate", "immunizationTypeDescriptor")
            List<string> relativePaths = constraintGroup
                .Select(p => ArrayPathHelper.GetRelativePath(arrayRootPath, p.Value))
                .ToList();

            // 3. Call FindDuplicatesWithArrayPath (preserving existing logic for now)
            (string? arrayPath, int dupeIndex) = context.ParsedBody.FindDuplicatesWithArrayPath(
                arrayRootPath,
                relativePaths,
                logger
            );

            if (dupeIndex >= 0 && arrayPath != null)
            {
                return ValidationErrorFactory.BuildValidationError(arrayPath, dupeIndex);
            }
        }

        return null;
    }
}
