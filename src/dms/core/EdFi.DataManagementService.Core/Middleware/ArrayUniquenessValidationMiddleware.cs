// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.ApiSchema.Model;
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
    /// <summary>
    /// Validates all array uniqueness constraints for the current document
    /// </summary>
    /// <returns>A list of all validation errors found</returns>
    private static List<(string errorKey, string message)> ValidateArrayUniquenessConstraints(
        PipelineContext context,
        ILogger logger
    )
    {
        List<(string errorKey, string message)> errors = [];

        foreach (var constraint in context.ResourceSchema.ArrayUniquenessConstraints)
        {
            errors.AddRange(ValidateSingleConstraint(constraint, context, logger));
        }

        return errors;
    }

    /// <summary>
    /// Validates a single array uniqueness constraint, handling both simple paths and nested constraints
    /// </summary>
    private static List<(string errorKey, string message)> ValidateSingleConstraint(
        ArrayUniquenessConstraint constraint,
        PipelineContext context,
        ILogger logger,
        string? parentBasePath = null
    )
    {
        List<(string errorKey, string message)> errors = [];

        // Handle simple paths constraint
        if (constraint.Paths != null && constraint.Paths.Count > 0)
        {
            List<(string errorKey, string message)> pathErrors = ValidatePathsConstraint(
                constraint.Paths,
                context,
                logger,
                parentBasePath
            );
            errors.AddRange(pathErrors);
        }

        // Handle nested constraints
        if (constraint.NestedConstraints != null)
        {
            foreach (var nestedConstraint in constraint.NestedConstraints)
            {
                string nestedBasePath =
                    nestedConstraint.BasePath?.Value
                    ?? throw new InvalidOperationException("Nested constraint must have a basePath");

                var nestedErrors = ValidateSingleConstraint(
                    nestedConstraint,
                    context,
                    logger,
                    nestedBasePath
                );
                errors.AddRange(nestedErrors);
            }
        }

        return errors;
    }

    /// <summary>
    /// Validates uniqueness for a set of paths within an array
    /// </summary>
    private static List<(string errorKey, string message)> ValidatePathsConstraint(
        IReadOnlyList<JsonPath> paths,
        PipelineContext context,
        ILogger logger,
        string? basePath = null
    )
    {
        List<(string errorKey, string message)> errors = [];

        if (paths.Count == 0)
        {
            return errors;
        }

        try
        {
            if (basePath != null)
            {
                // For nested constraints, only validate within each parent item
                var withinItemResult = ValidateNestedConstraintWithinItems(basePath, paths, context, logger);
                if (withinItemResult.HasValue)
                {
                    errors.Add(withinItemResult.Value);
                }

                return errors;
            }
            else
            {
                // For simple constraints, detect the array root path from the first path
                var arrayRootPath = ArrayPathHelper.GetArrayRootPath(paths);
                var relativePaths = paths
                    .Select(p => ArrayPathHelper.GetRelativePath(arrayRootPath, p.Value))
                    .ToList();

                logger.LogDebug(
                    "Array root path: {ArrayRootPath}, Relative paths: {RelativePaths}",
                    arrayRootPath,
                    string.Join(", ", relativePaths)
                );

                (string? arrayPath, int dupeIndex) = context.ParsedBody.FindDuplicatesWithArrayPath(
                    arrayRootPath,
                    relativePaths,
                    logger
                );

                if (dupeIndex >= 0 && arrayPath != null)
                {
                    logger.LogDebug(
                        "Duplicate found at index {DupeIndex} for path {ArrayPath}",
                        dupeIndex,
                        arrayPath
                    );
                    var validationError = ValidationErrorFactory.BuildValidationError(arrayPath, dupeIndex);
                    errors.Add(validationError);
                }
            }

            return errors;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unhandled exception in ValidatePathsConstraint for basePath: {BasePath}, paths: {Paths}",
                basePath,
                string.Join(", ", paths.Select(p => p.Value))
            );
            throw new InvalidOperationException(
                $"Array uniqueness validation failed for constraint with basePath '{basePath}'",
                ex
            );
        }
    }

    /// <summary>
    /// Validates nested constraints by checking for duplicates within individual parent items
    /// rather than across all items in the parent array
    /// </summary>
    private static (string errorKey, string message)? ValidateNestedConstraintWithinItems(
        string basePath,
        IReadOnlyList<JsonPath> paths,
        PipelineContext context,
        ILogger logger
    )
    {
        logger.LogDebug("Validating nested constraint within items - basePath: {BasePath}", basePath);

        try
        {
            // Get all items from the base path array
            List<JsonNode?> baseItems = context
                .ParsedBody.SelectNodesFromArrayPath(basePath, logger)
                .ToList();
            logger.LogDebug("Found {ItemCount} base items for path {BasePath}", baseItems.Count, basePath);

            for (int itemIndex = 0; itemIndex < baseItems.Count; itemIndex++)
            {
                JsonNode? item = baseItems[itemIndex];
                if (item == null)
                {
                    continue;
                }

                logger.LogDebug("Processing base item {ItemIndex}", itemIndex);

                // Check each path for duplicates within this specific item
                var error = paths
                    .Select(path => ValidateNestedArrayUniqueness(path, basePath, logger, itemIndex, item))
                    .FirstOrDefault(e => e.HasValue);

                if (error.HasValue)
                {
                    return error.Value;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            var pathValues = paths.Select(p => p.Value);
            logger.LogError(
                ex,
                "Unhandled exception in ValidateNestedConstraintWithinItems for basePath: {BasePath}, paths: {Paths}",
                basePath,
                string.Join(", ", pathValues)
            );
            throw new InvalidOperationException(
                $"Nested constraint validation failed for basePath '{basePath}'",
                ex
            );
        }
    }

    private static (string errorKey, string message)? ValidateNestedArrayUniqueness(
        JsonPath path,
        string basePath,
        ILogger logger,
        int itemIndex,
        JsonNode? item
    )
    {
        try
        {
            // The path in nested constraints is relative to the base path item
            var relativePath = path.Value;
            logger.LogDebug(
                "Checking for duplicates in item {ItemIndex} with path: {RelativePath}",
                itemIndex,
                relativePath
            );

            if (item == null)
            {
                logger.LogDebug("Item at index {ItemIndex} is null, skipping duplicate check", itemIndex);
                return null;
            }

            // Use duplicate detection, but on the individual item
            var duplicateResult = FindDuplicatesWithinSingleItem(item, relativePath, logger);

            if (duplicateResult.HasValue)
            {
                var (nestedArrayName, duplicateIndex) = duplicateResult.Value;
                // Construct the full error path: basePath[itemIndex].nestedArrayName[*]
                var itemPath = basePath.Replace("[*]", $"[{itemIndex}]");
                var fullErrorPath = $"{itemPath}.{nestedArrayName}[*]";
                logger.LogDebug("Found duplicates in nested array at path {ErrorPath}", fullErrorPath);
                return ValidationErrorFactory.BuildValidationError(fullErrorPath, duplicateIndex);
            }
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Error processing nested constraint for item {ItemIndex}, path {Path}",
                itemIndex,
                path.Value
            );
            throw new InvalidOperationException(
                $"Failed to process nested constraint for item {itemIndex}, path {path.Value}",
                ex
            );
        }
    }

    /// <summary>
    /// Finds duplicates within a single JSON item for a given path
    /// Returns the array name and duplicate index if found, null otherwise
    /// </summary>
    private static (string arrayName, int duplicateIndex)? FindDuplicatesWithinSingleItem(
        JsonNode item,
        string relativePath,
        ILogger logger
    )
    {
        try
        {
            logger.LogDebug("Finding duplicates within single item for path: {RelativePath}", relativePath);

            // For nested constraints, the path is already in the form "$.dates[*].immunizationDate"
            // We need to extract the array root and relative paths
            string arrayRootPath = ArrayPathHelper.GetArrayRootPath([new(relativePath)]);
            List<string> relativePathsList = new()
            {
                ArrayPathHelper.GetRelativePath(arrayRootPath, relativePath),
            };

            logger.LogDebug(
                "Array root path: {ArrayRootPath}, Relative path: {RelativePath}",
                arrayRootPath,
                relativePathsList[0]
            );

            // Use duplicate detection on the individual item
            var (_, dupeIndex) = item.FindDuplicatesWithArrayPath(arrayRootPath, relativePathsList, logger);

            if (dupeIndex >= 0)
            {
                // Extract array name from the array root path
                string arrayName = arrayRootPath.Replace("$.", "").Replace("[*]", "");
                logger.LogDebug(
                    "Found duplicate at index {DupeIndex} in array {ArrayName}",
                    dupeIndex,
                    arrayName
                );
                return (arrayName, dupeIndex);
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Error finding duplicates within single item for path {RelativePath}",
                relativePath
            );
            throw new InvalidOperationException(
                $"Failed to find duplicates within single item for path {relativePath}",
                ex
            );
        }
    }

    /// <summary>
    /// Aggregates validation errors by grouping messages under the same error key
    /// </summary>
    private static Dictionary<string, string[]> AggregateValidationErrors(
        List<(string errorKey, string message)> errors
    )
    {
        return errors
            .GroupBy(e => e.errorKey)
            .ToDictionary(g => g.Key, g => g.Select(e => e.message).ToArray());
    }

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        logger.LogDebug(
            "Entering ArrayUniquenessValidationMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        List<(string errorKey, string message)> validationErrors = ValidateArrayUniquenessConstraints(
            context,
            logger
        );
        if (validationErrors.Count > 0)
        {
            Dictionary<string, string[]> errorsGroupedByErrorKey = AggregateValidationErrors(
                validationErrors
            );

            context.FrontendResponse = ValidationErrorFactory.CreateValidationErrorResponse(
                errorsGroupedByErrorKey,
                context.FrontendRequest.TraceId
            );

            logger.LogDebug(
                "Array uniqueness constraint violations found: {ErrorCount} - {TraceId}",
                validationErrors.Count,
                context.FrontendRequest.TraceId.Value
            );
            return;
        }

        await next();
    }
}
