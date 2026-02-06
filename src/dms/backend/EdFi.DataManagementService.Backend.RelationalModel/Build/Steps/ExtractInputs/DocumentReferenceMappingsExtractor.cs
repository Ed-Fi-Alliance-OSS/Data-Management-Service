// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs.ApiSchemaNodeRequirements;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;

/// <summary>
/// Extracts and validates <c>documentPathsMapping</c> reference bindings for a resource schema.
/// </summary>
internal static class DocumentReferenceMappingsExtractor
{
    /// <summary>
    /// Extracts document reference mappings and validates identity component usage.
    /// </summary>
    internal static IReadOnlyList<DocumentReferenceMapping> ExtractDocumentReferenceMappings(
        JsonObject resourceSchema,
        string projectName,
        string resourceName,
        IReadOnlySet<string> identityJsonPaths
    )
    {
        var documentPathsMapping = GetDocumentPathsMappingOrEmpty(resourceSchema);
        var state = new DocumentReferenceMappingExtractionState();

        foreach (var mapping in documentPathsMapping.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            ProcessDocumentPathsMappingEntry(
                mapping.Key,
                mapping.Value,
                projectName,
                resourceName,
                identityJsonPaths,
                state
            );
        }

        ValidateIdentityJsonPathsCovered(projectName, resourceName, identityJsonPaths, state);
        return state.ReferenceMappings.ToArray();
    }

    /// <summary>
    /// Accumulates extracted document reference mappings and derived path sets while iterating
    /// <c>documentPathsMapping</c> entries.
    /// </summary>
    private sealed class DocumentReferenceMappingExtractionState
    {
        public List<DocumentReferenceMapping> ReferenceMappings { get; } = [];

        public HashSet<string> MappedIdentityPaths { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads <c>documentPathsMapping</c> from the resource schema, returning an empty object when the
    /// property is missing or null, and throwing when the property is not an object.
    /// </summary>
    private static JsonObject GetDocumentPathsMappingOrEmpty(JsonObject resourceSchema)
    {
        if (!resourceSchema.TryGetPropertyValue("documentPathsMapping", out var documentPathsMappingNode))
        {
            return new JsonObject();
        }

        return documentPathsMappingNode switch
        {
            null => new JsonObject(),
            JsonObject mappingObject => mappingObject,
            _ => throw new InvalidOperationException(
                "Expected documentPathsMapping to be an object, invalid ApiSchema."
            ),
        };
    }

    /// <summary>
    /// Validates and processes a single <c>documentPathsMapping</c> entry, updating the extraction state.
    /// </summary>
    private static void ProcessDocumentPathsMappingEntry(
        string mappingKey,
        JsonNode? mappingNode,
        string projectName,
        string resourceName,
        IReadOnlySet<string> identityJsonPaths,
        DocumentReferenceMappingExtractionState state
    )
    {
        var mappingObject = RequireDocumentPathsMappingEntryObject(mappingNode);

        var isReference =
            mappingObject["isReference"]?.GetValue<bool>()
            ?? throw new InvalidOperationException(
                "Expected isReference to be on documentPathsMapping entry, invalid ApiSchema."
            );

        if (!isReference)
        {
            ProcessDocumentPathsMappingPathEntry(
                mappingKey,
                mappingObject,
                projectName,
                resourceName,
                identityJsonPaths,
                state
            );
            return;
        }

        var isDescriptor =
            mappingObject["isDescriptor"]?.GetValue<bool>()
            ?? throw new InvalidOperationException(
                "Expected isDescriptor to be on documentPathsMapping entry, invalid ApiSchema."
            );

        if (isDescriptor)
        {
            ProcessDocumentPathsMappingDescriptorEntry(
                mappingKey,
                mappingObject,
                projectName,
                resourceName,
                identityJsonPaths,
                state
            );
            return;
        }

        ProcessDocumentPathsMappingReferenceEntry(
            mappingKey,
            mappingObject,
            projectName,
            resourceName,
            identityJsonPaths,
            state
        );
    }

    /// <summary>
    /// Ensures the mapping value for a <c>documentPathsMapping</c> entry is a non-null object, and
    /// throws a schema validation exception otherwise.
    /// </summary>
    private static JsonObject RequireDocumentPathsMappingEntryObject(JsonNode? mappingNode)
    {
        return mappingNode switch
        {
            null => throw new InvalidOperationException(
                "Expected documentPathsMapping entries to be non-null, invalid ApiSchema."
            ),
            JsonObject mappingObject => mappingObject,
            _ => throw new InvalidOperationException(
                "Expected documentPathsMapping entries to be objects, invalid ApiSchema."
            ),
        };
    }

    /// <summary>
    /// Processes a non-reference <c>documentPathsMapping</c> entry with a single <c>path</c> property.
    /// </summary>
    private static void ProcessDocumentPathsMappingPathEntry(
        string mappingKey,
        JsonObject mappingObject,
        string projectName,
        string resourceName,
        IReadOnlySet<string> identityJsonPaths,
        DocumentReferenceMappingExtractionState state
    )
    {
        var path = RequireString(mappingObject, "path");
        var pathExpression = JsonPathExpressionCompiler.Compile(path);

        state.MappedIdentityPaths.Add(pathExpression.Canonical);
    }

    /// <summary>
    /// Processes a descriptor <c>documentPathsMapping</c> entry with a single <c>path</c> property.
    /// </summary>
    private static void ProcessDocumentPathsMappingDescriptorEntry(
        string mappingKey,
        JsonObject mappingObject,
        string projectName,
        string resourceName,
        IReadOnlySet<string> identityJsonPaths,
        DocumentReferenceMappingExtractionState state
    )
    {
        var path = RequireString(mappingObject, "path");
        var pathExpression = JsonPathExpressionCompiler.Compile(path);

        state.MappedIdentityPaths.Add(pathExpression.Canonical);
    }

    /// <summary>
    /// Processes a reference <c>documentPathsMapping</c> entry, extracting JSONPath bindings, validating
    /// identity component completeness, and adding the resulting <see cref="DocumentReferenceMapping"/> to
    /// the extraction state.
    /// </summary>
    private static void ProcessDocumentPathsMappingReferenceEntry(
        string mappingKey,
        JsonObject mappingObject,
        string projectName,
        string resourceName,
        IReadOnlySet<string> identityJsonPaths,
        DocumentReferenceMappingExtractionState state
    )
    {
        var referenceJsonPathsNode = GetReferenceJsonPathsNode(
            mappingKey,
            mappingObject,
            projectName,
            resourceName
        );
        var referenceJsonPaths = ExtractReferenceJsonPaths(
            mappingKey,
            referenceJsonPathsNode,
            projectName,
            resourceName,
            out var referenceObjectPath
        );

        foreach (var referenceJsonPath in referenceJsonPaths)
        {
            state.MappedIdentityPaths.Add(referenceJsonPath.ReferenceJsonPath.Canonical);
        }

        var referencePaths = referenceJsonPaths
            .Select(binding => binding.ReferenceJsonPath.Canonical)
            .ToArray();
        var referenceIsPartOfIdentity = referencePaths.Any(identityJsonPaths.Contains);
        ValidateReferenceIdentityCompleteness(
            mappingKey,
            projectName,
            resourceName,
            referenceObjectPath,
            referenceIsPartOfIdentity,
            referenceJsonPaths,
            identityJsonPaths
        );

        var targetProjectName = RequireString(mappingObject, "projectName");
        var targetResourceName = RequireString(mappingObject, "resourceName");
        var isRequired = mappingObject["isRequired"]?.GetValue<bool>() ?? false;

        if (referenceIsPartOfIdentity && !isRequired)
        {
            throw new InvalidOperationException(
                $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' is "
                    + "mapped to identityJsonPaths but isRequired is false. "
                    + "Identity references must be required."
            );
        }

        state.ReferenceMappings.Add(
            new DocumentReferenceMapping(
                mappingKey,
                new QualifiedResourceName(targetProjectName, targetResourceName),
                isRequired,
                referenceIsPartOfIdentity,
                referenceObjectPath,
                referenceJsonPaths
            )
        );
    }

    /// <summary>
    /// Validates that every identity JSONPath defined by <c>identityJsonPaths</c> is represented by at least
    /// one <c>documentPathsMapping</c> entry.
    /// </summary>
    private static void ValidateIdentityJsonPathsCovered(
        string projectName,
        string resourceName,
        IReadOnlySet<string> identityJsonPaths,
        DocumentReferenceMappingExtractionState state
    )
    {
        var missingIdentityPaths = identityJsonPaths
            .Where(path => !state.MappedIdentityPaths.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (missingIdentityPaths.Length > 0)
        {
            throw new InvalidOperationException(
                $"identityJsonPaths on resource '{projectName}:{resourceName}' were not found in "
                    + $"documentPathsMapping: {string.Join(", ", missingIdentityPaths)}."
            );
        }
    }

    /// <summary>
    /// Validates and returns the <c>referenceJsonPaths</c> array for a document reference mapping entry.
    /// </summary>
    private static JsonArray GetReferenceJsonPathsNode(
        string mappingKey,
        JsonObject mappingObject,
        string projectName,
        string resourceName
    )
    {
        if (!mappingObject.TryGetPropertyValue("referenceJsonPaths", out var referenceJsonPathsNode))
        {
            throw new InvalidOperationException(
                $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' "
                    + "is missing referenceJsonPaths."
            );
        }

        if (referenceJsonPathsNode is null)
        {
            throw new InvalidOperationException(
                $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' "
                    + "has null referenceJsonPaths."
            );
        }

        if (referenceJsonPathsNode is not JsonArray referenceJsonPathsArray)
        {
            throw new InvalidOperationException(
                "Expected referenceJsonPaths to be an array on documentPathsMapping entry, "
                    + "invalid ApiSchema."
            );
        }

        if (referenceJsonPathsArray.Count == 0)
        {
            throw new InvalidOperationException(
                $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' "
                    + "has no referenceJsonPaths entries."
            );
        }

        return referenceJsonPathsArray;
    }

    /// <summary>
    /// Extracts the identity/reference JSONPath bindings for a document reference mapping and returns the
    /// compiled reference object path.
    /// </summary>
    private static IReadOnlyList<ReferenceJsonPathBinding> ExtractReferenceJsonPaths(
        string mappingKey,
        JsonArray referenceJsonPathsArray,
        string projectName,
        string resourceName,
        out JsonPathExpression referenceObjectPath
    )
    {
        List<ReferenceJsonPathBinding> referenceJsonPaths = new(referenceJsonPathsArray.Count);
        JsonPathExpression? referencePrefix = null;
        Dictionary<string, string> referencePathsByIdentityPath = new(StringComparer.Ordinal);

        foreach (var referenceJsonPath in referenceJsonPathsArray)
        {
            if (referenceJsonPath is null)
            {
                throw new InvalidOperationException(
                    "Expected referenceJsonPaths to not contain null entries, invalid ApiSchema."
                );
            }

            if (referenceJsonPath is not JsonObject referenceJsonPathObject)
            {
                throw new InvalidOperationException(
                    "Expected referenceJsonPaths entries to be objects, invalid ApiSchema."
                );
            }

            var identityJsonPath = RequireString(referenceJsonPathObject, "identityJsonPath");
            var referenceJsonPathValue = RequireString(referenceJsonPathObject, "referenceJsonPath");
            var identityPath = JsonPathExpressionCompiler.Compile(identityJsonPath);
            var referencePath = JsonPathExpressionCompiler.Compile(referenceJsonPathValue);
            var prefixPath = ExtractReferencePrefixPath(mappingKey, projectName, resourceName, referencePath);

            if (referencePrefix is null)
            {
                referencePrefix = prefixPath;
            }
            else if (
                !string.Equals(
                    referencePrefix.Value.Canonical,
                    prefixPath.Canonical,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidOperationException(
                    $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' "
                        + $"has inconsistent referenceJsonPaths prefix '{referencePrefix.Value.Canonical}' "
                        + $"and '{prefixPath.Canonical}'."
                );
            }

            if (!referencePathsByIdentityPath.TryAdd(identityPath.Canonical, referencePath.Canonical))
            {
                var existingReferencePath = referencePathsByIdentityPath[identityPath.Canonical];

                throw new InvalidOperationException(
                    $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' "
                        + $"has duplicate identityJsonPath '{identityPath.Canonical}' mapped to "
                        + $"'{existingReferencePath}' and '{referencePath.Canonical}'."
                );
            }

            referenceJsonPaths.Add(new ReferenceJsonPathBinding(identityPath, referencePath));
        }

        referenceObjectPath =
            referencePrefix
            ?? throw new InvalidOperationException(
                $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' "
                    + "has no referenceJsonPaths entries."
            );

        return referenceJsonPaths.ToArray();
    }

    /// <summary>
    /// Extracts the reference object path prefix from a reference JSONPath by removing the terminal property
    /// segment.
    /// </summary>
    private static JsonPathExpression ExtractReferencePrefixPath(
        string mappingKey,
        string projectName,
        string resourceName,
        JsonPathExpression referencePath
    )
    {
        if (referencePath.Segments.Count == 0 || referencePath.Segments[^1] is not JsonPathSegment.Property)
        {
            throw new InvalidOperationException(
                $"referenceJsonPath '{referencePath.Canonical}' on documentPathsMapping entry '{mappingKey}' "
                    + $"for resource '{projectName}:{resourceName}' must end with a property segment."
            );
        }

        var prefixSegments = referencePath.Segments.Take(referencePath.Segments.Count - 1).ToArray();
        return JsonPathExpressionCompiler.FromSegments(prefixSegments);
    }

    /// <summary>
    /// Validates that all identity-component reference paths are present in the resource's
    /// <c>identityJsonPaths</c>.
    /// </summary>
    private static void ValidateReferenceIdentityCompleteness(
        string mappingKey,
        string projectName,
        string resourceName,
        JsonPathExpression referenceObjectPath,
        bool derivedIsPartOfIdentity,
        IReadOnlyList<ReferenceJsonPathBinding> referenceJsonPaths,
        IReadOnlySet<string> identityJsonPaths
    )
    {
        if (!derivedIsPartOfIdentity)
        {
            return;
        }

        var referencePaths = referenceJsonPaths
            .Select(binding => binding.ReferenceJsonPath.Canonical)
            .ToArray();
        var missing = referencePaths
            .Where(path => !identityJsonPaths.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (missing.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"documentPathsMapping entry '{mappingKey}' on resource '{projectName}:{resourceName}' has "
                + $"reference identity paths for '{referenceObjectPath.Canonical}' but identityJsonPaths "
                + "is missing reference path(s): "
                + string.Join(", ", missing)
        );
    }
}
