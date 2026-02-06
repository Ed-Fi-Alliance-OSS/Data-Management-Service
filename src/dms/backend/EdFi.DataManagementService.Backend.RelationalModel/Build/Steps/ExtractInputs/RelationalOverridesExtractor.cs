// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;

internal static class RelationalOverridesExtractor
{
    /// <summary>
    /// Resolves the <c>relational</c> block for a resource, validating descriptor restrictions.
    /// </summary>
    internal static JsonObject? GetRelationalObject(
        JsonObject resourceSchema,
        string projectName,
        string resourceName,
        bool isDescriptor
    )
    {
        if (!resourceSchema.TryGetPropertyValue("relational", out var relationalNode))
        {
            return null;
        }

        if (isDescriptor)
        {
            throw new InvalidOperationException(
                $"Descriptor resource '{projectName}:{resourceName}' must not define relational overrides."
            );
        }

        if (relationalNode is null)
        {
            return null;
        }

        if (relationalNode is not JsonObject relationalObject)
        {
            throw new InvalidOperationException(
                $"Expected relational to be an object for resource '{projectName}:{resourceName}'."
            );
        }

        return relationalObject;
    }

    /// <summary>
    /// Extracts and normalizes <c>rootTableNameOverride</c> from the relational block.
    /// </summary>
    internal static string? ExtractRootTableNameOverride(
        JsonObject? relationalObject,
        bool isResourceExtension,
        string projectName,
        string resourceName
    )
    {
        if (relationalObject is null)
        {
            return null;
        }

        if (!relationalObject.TryGetPropertyValue("rootTableNameOverride", out var overrideNode))
        {
            return null;
        }

        if (overrideNode is null)
        {
            throw new InvalidOperationException(
                $"relational.rootTableNameOverride must be non-empty on resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        if (overrideNode is not JsonValue overrideValue)
        {
            throw new InvalidOperationException(
                $"relational.rootTableNameOverride must be a string on resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        var overrideText = overrideValue.GetValue<string>();

        if (string.IsNullOrWhiteSpace(overrideText))
        {
            throw new InvalidOperationException(
                $"relational.rootTableNameOverride must be non-empty on resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        var normalized = RelationalNameConventions.ToPascalCase(overrideText);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException(
                $"relational.rootTableNameOverride must normalize to a non-empty name on resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        if (isResourceExtension)
        {
            var expectedExtensionName = $"{RelationalNameConventions.ToPascalCase(resourceName)}Extension";

            if (!string.Equals(normalized, expectedExtensionName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"relational.rootTableNameOverride is not supported for resource extension "
                        + $"'{projectName}:{resourceName}'."
                );
            }

            return null;
        }

        return normalized;
    }

    /// <summary>
    /// Extracts and normalizes <c>resourceSchema.relational.nameOverrides</c> entries.
    /// </summary>
    internal static IReadOnlyDictionary<string, NameOverrideEntry> ExtractNameOverrides(
        JsonObject? relationalObject,
        IReadOnlyList<DocumentReferenceMapping> referenceMappings,
        bool isResourceExtension,
        JsonNode jsonSchemaForInsert,
        string projectEndpointName,
        string projectName,
        string resourceName
    )
    {
        if (relationalObject is null)
        {
            return new Dictionary<string, NameOverrideEntry>(StringComparer.Ordinal);
        }

        if (!relationalObject.TryGetPropertyValue("nameOverrides", out var nameOverridesNode))
        {
            return new Dictionary<string, NameOverrideEntry>(StringComparer.Ordinal);
        }

        if (nameOverridesNode is null)
        {
            return new Dictionary<string, NameOverrideEntry>(StringComparer.Ordinal);
        }

        if (nameOverridesNode is not JsonObject nameOverridesObject)
        {
            throw new InvalidOperationException(
                $"Expected relational.nameOverrides to be an object for resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        Dictionary<string, NameOverrideEntry> overrides = new(StringComparer.Ordinal);
        string? extensionProjectKey = null;
        var referenceIdentityPaths = RelationalModelSetSchemaHelpers.BuildReferenceIdentityPathSet(
            referenceMappings
        );

        foreach (var overrideEntry in nameOverridesObject.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            JsonPathExpression compiledPath;
            var overrideKey = overrideEntry.Key;

            try
            {
                compiledPath = JsonPathExpressionCompiler.Compile(overrideKey);
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{overrideKey}' on resource "
                        + $"'{projectName}:{resourceName}' is not a valid JSONPath.",
                    ex
                );
            }

            var resolvedPath = compiledPath;

            if (isResourceExtension && !IsExtensionRootPath(compiledPath))
            {
                extensionProjectKey ??= ResolveExtensionProjectKey(
                    jsonSchemaForInsert,
                    projectEndpointName,
                    projectName,
                    resourceName
                );
                resolvedPath = PrefixExtensionRoot(compiledPath, extensionProjectKey);
            }

            var overrideNode = overrideEntry.Value;

            if (overrideNode is null)
            {
                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{overrideKey}' is null on resource "
                        + $"'{projectName}:{resourceName}'."
                );
            }

            if (overrideNode is not JsonValue overrideValue)
            {
                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{overrideKey}' must be a string on resource "
                        + $"'{projectName}:{resourceName}'."
                );
            }

            var overrideText = overrideValue.GetValue<string>();

            if (string.IsNullOrWhiteSpace(overrideText))
            {
                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{overrideKey}' must be non-empty on resource "
                        + $"'{projectName}:{resourceName}'."
                );
            }

            var normalizedOverride = RelationalNameConventions.ToPascalCase(overrideText);

            if (string.IsNullOrWhiteSpace(normalizedOverride))
            {
                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{overrideKey}' must normalize to a non-empty "
                        + $"name on resource '{projectName}:{resourceName}'."
                );
            }

            if (IsInsideReferenceObjectPath(resolvedPath, referenceMappings, out var referencePath))
            {
                if (!referenceIdentityPaths.Contains(resolvedPath.Canonical))
                {
                    throw new InvalidOperationException(
                        $"relational.nameOverrides entry '{overrideKey}' (canonical '{resolvedPath.Canonical}') "
                            + $"on resource '{projectName}:{resourceName}' targets a non-identity path inside "
                            + $"reference object '{referencePath}'. Only reference identity paths may be overridden."
                    );
                }
            }

            var overrideKind =
                resolvedPath.Segments.Count > 0
                && resolvedPath.Segments[^1] is JsonPathSegment.AnyArrayElement
                    ? NameOverrideKind.Collection
                    : NameOverrideKind.Column;

            if (
                !overrides.TryAdd(
                    resolvedPath.Canonical,
                    new NameOverrideEntry(
                        overrideKey,
                        resolvedPath.Canonical,
                        normalizedOverride,
                        overrideKind
                    )
                )
            )
            {
                var existing = overrides[resolvedPath.Canonical];

                throw new InvalidOperationException(
                    $"relational.nameOverrides entry '{overrideKey}' (canonical '{resolvedPath.Canonical}') "
                        + $"duplicates '{existing.RawKey}' on resource '{projectName}:{resourceName}'."
                );
            }
        }

        return overrides;
    }

    private static bool IsExtensionRootPath(JsonPathExpression path)
    {
        return path.Segments.Count > 0 && path.Segments[0] is JsonPathSegment.Property { Name: "_ext" };
    }

    private static JsonPathExpression PrefixExtensionRoot(JsonPathExpression path, string projectKey)
    {
        List<JsonPathSegment> segments =
        [
            new JsonPathSegment.Property("_ext"),
            new JsonPathSegment.Property(projectKey),
        ];

        segments.AddRange(path.Segments);

        return JsonPathExpressionCompiler.FromSegments(segments);
    }

    private static string ResolveExtensionProjectKey(
        JsonNode jsonSchemaForInsert,
        string projectEndpointName,
        string projectName,
        string resourceName
    )
    {
        if (jsonSchemaForInsert is not JsonObject rootSchema)
        {
            throw new InvalidOperationException(
                $"Expected jsonSchemaForInsert to be an object on resource '{projectName}:{resourceName}'."
            );
        }

        if (!rootSchema.TryGetPropertyValue("properties", out var propertiesNode))
        {
            throw new InvalidOperationException(
                $"Extension resource '{projectName}:{resourceName}' is missing jsonSchemaForInsert.properties."
            );
        }

        if (propertiesNode is not JsonObject propertiesObject)
        {
            throw new InvalidOperationException(
                $"Expected jsonSchemaForInsert.properties to be an object on resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        if (!propertiesObject.TryGetPropertyValue("_ext", out var extNode) || extNode is null)
        {
            throw new InvalidOperationException(
                $"Extension resource '{projectName}:{resourceName}' is missing jsonSchemaForInsert.properties._ext."
            );
        }

        if (extNode is not JsonObject extSchema)
        {
            throw new InvalidOperationException(
                $"Expected jsonSchemaForInsert.properties._ext to be an object on resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        if (!extSchema.TryGetPropertyValue("properties", out var projectKeysNode))
        {
            throw new InvalidOperationException(
                $"Extension resource '{projectName}:{resourceName}' is missing "
                    + "jsonSchemaForInsert.properties._ext.properties."
            );
        }

        if (projectKeysNode is not JsonObject projectKeysObject)
        {
            throw new InvalidOperationException(
                $"Expected jsonSchemaForInsert.properties._ext.properties to be an object on resource "
                    + $"'{projectName}:{resourceName}'."
            );
        }

        var endpointKey = FindMatchingProjectKey(projectKeysObject, projectEndpointName);

        if (endpointKey is not null)
        {
            return endpointKey;
        }

        var nameKey = FindMatchingProjectKey(projectKeysObject, projectName);

        if (nameKey is not null)
        {
            return nameKey;
        }

        throw new InvalidOperationException(
            $"Extension project key '{projectEndpointName}' not found under jsonSchemaForInsert "
                + $"._ext on resource '{projectName}:{resourceName}'."
        );
    }

    private static string? FindMatchingProjectKey(JsonObject projectKeysObject, string match)
    {
        foreach (var entry in projectKeysObject)
        {
            if (RelationalModelSetSchemaHelpers.ExtensionProjectKeyComparer.Equals(entry.Key, match))
            {
                return entry.Key;
            }
        }

        return null;
    }

    private static bool IsInsideReferenceObjectPath(
        JsonPathExpression path,
        IReadOnlyList<DocumentReferenceMapping> referenceMappings,
        out string referencePath
    )
    {
        foreach (var mapping in referenceMappings)
        {
            var referenceObjectPath = mapping.ReferenceObjectPath;

            if (path.Segments.Count <= referenceObjectPath.Segments.Count)
            {
                continue;
            }

            if (RelationalModelSetSchemaHelpers.IsPrefixOf(referenceObjectPath.Segments, path.Segments))
            {
                referencePath = referenceObjectPath.Canonical;
                return true;
            }
        }

        referencePath = string.Empty;
        return false;
    }
}
