// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.ApiSchema.Model;
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
    /// <summary>
    /// Validates all array uniqueness constraints for the current document
    /// </summary>
    /// <returns>A validation error tuple if violations are found, null otherwise</returns>
    private static (string errorKey, string message)? ValidateArrayUniquenessConstraints(
        PipelineContext context,
        ILogger logger
    )
    {
        foreach (var constraint in context.ResourceSchema.ArrayUniquenessConstraints)
        {
            var validationError = ValidateConstraint(constraint, context, logger);
            if (validationError.HasValue)
            {
                return validationError;
            }
        }

        return null;
    }

    /// <summary>
    /// Validates a single array uniqueness constraint, handling both simple paths and nested constraints
    /// </summary>
    private static (string errorKey, string message)? ValidateConstraint(
        ArrayUniquenessConstraint constraint,
        PipelineContext context,
        ILogger logger,
        string? parentBasePath = null
    )
    {
        // Handle simple paths constraint
        if (constraint.Paths != null && constraint.Paths.Count > 0)
        {
            var validationError = ValidatePathsConstraint(constraint.Paths, context, logger, parentBasePath);
            if (validationError.HasValue)
            {
                return validationError;
            }
        }

        // Handle nested constraints
        if (constraint.NestedConstraints != null)
        {
            foreach (var nestedConstraint in constraint.NestedConstraints)
            {
                string nestedBasePath =
                    nestedConstraint.BasePath?.Value
                    ?? throw new InvalidOperationException("Nested constraint must have a basePath");

                var validationError = ValidateConstraint(nestedConstraint, context, logger, nestedBasePath);
                if (validationError.HasValue)
                {
                    return validationError;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Validates uniqueness for a set of paths within an array
    /// </summary>
    private static (string errorKey, string message)? ValidatePathsConstraint(
        IReadOnlyList<External.Model.JsonPath> paths,
        PipelineContext context,
        ILogger logger,
        string? basePath = null
    )
    {
        if (paths.Count == 0)
        {
            return null;
        }

        // Determine the array root path
        string arrayRootPath;
        List<string> relativePaths;

        if (basePath != null)
        {
            // For nested constraints, use the provided base path
            arrayRootPath = basePath;
            relativePaths = paths
                .Select(p => ArrayPathHelper.GetRelativePath(arrayRootPath, p.Value))
                .ToList();
        }
        else
        {
            // For simple constraints, detect the array root path from the first path
            arrayRootPath = ArrayPathHelper.GetArrayRootPath(paths);
            relativePaths = paths
                .Select(p => ArrayPathHelper.GetRelativePath(arrayRootPath, p.Value))
                .ToList();
        }

        // Use existing duplicate detection logic
        (string? arrayPath, int dupeIndex) = context.ParsedBody.FindDuplicatesWithArrayPath(
            arrayRootPath,
            relativePaths,
            logger
        );

        if (dupeIndex >= 0 && arrayPath != null)
        {
            return ValidationErrorFactory.BuildValidationError(arrayPath, dupeIndex);
        }

        return null;
    }

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        logger.LogDebug(
            "Entering ArrayUniquenessValidationMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        if (context.ResourceSchema.ArrayUniquenessConstraints.Count > 0)
        {
            (string errorKey, string message)? validationError = ValidateArrayUniquenessConstraints(
                context,
                logger
            );
            if (validationError.HasValue)
            {
                (string errorKey, string message) = validationError.Value;
                Dictionary<string, string[]> validationErrors = new() { [errorKey] = [message] };

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
}
