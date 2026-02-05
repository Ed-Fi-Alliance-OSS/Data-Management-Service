// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Provides helper methods for reading normalized ApiSchema JSON nodes with consistent validation errors.
/// </summary>
internal static class RelationalModelSetSchemaHelpers
{
    /// <summary>
    /// Requires that the supplied node is a non-null <see cref="JsonObject"/>.
    /// </summary>
    /// <param name="node">The node to validate.</param>
    /// <param name="propertyName">The JSON property label used for diagnostics.</param>
    /// <returns>The validated <see cref="JsonObject"/>.</returns>
    internal static JsonObject RequireObject(JsonNode? node, string propertyName)
    {
        return node switch
        {
            JsonObject jsonObject => jsonObject,
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be an object, invalid ApiSchema."
            ),
        };
    }

    /// <summary>
    /// Requires that the specified property is present on the supplied object and contains a non-empty string value.
    /// </summary>
    /// <param name="node">The object to read.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The validated string value.</returns>
    internal static string RequireString(JsonObject node, string propertyName)
    {
        var value = node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a string, invalid ApiSchema."
            ),
        };

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Expected {propertyName} to be non-empty, invalid ApiSchema."
            );
        }

        return value;
    }

    /// <summary>
    /// Attempts to read an optional string property, returning <see langword="null"/> when the property
    /// is absent or explicitly null.
    /// </summary>
    /// <param name="node">The object to read.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The validated string value, or <see langword="null"/>.</returns>
    internal static string? TryGetOptionalString(JsonObject node, string propertyName)
    {
        if (!node.TryGetPropertyValue(propertyName, out var value))
        {
            return null;
        }

        if (value is null)
        {
            return null;
        }

        if (value is not JsonValue jsonValue)
        {
            throw new InvalidOperationException(
                $"Expected {propertyName} to be a string, invalid ApiSchema."
            );
        }

        var text = jsonValue.GetValue<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"Expected {propertyName} to be non-empty, invalid ApiSchema."
            );
        }

        return text;
    }

    /// <summary>
    /// Determines the resource name for a schema entry, using <c>resourceName</c> when present and
    /// falling back to the schema entry key.
    /// </summary>
    /// <param name="resourceKey">The resource schema entry key.</param>
    /// <param name="resourceSchema">The resource schema object.</param>
    /// <returns>The resolved resource name.</returns>
    internal static string GetResourceName(string resourceKey, JsonObject resourceSchema)
    {
        if (resourceSchema.TryGetPropertyValue("resourceName", out var resourceNameNode))
        {
            return resourceNameNode switch
            {
                JsonValue jsonValue => RequireNonEmpty(jsonValue.GetValue<string>(), "resourceName"),
                null => throw new InvalidOperationException(
                    "Expected resourceName to be present, invalid ApiSchema."
                ),
                _ => throw new InvalidOperationException(
                    "Expected resourceName to be a string, invalid ApiSchema."
                ),
            };
        }

        return RequireNonEmpty(resourceKey, "resourceName");
    }

    /// <summary>
    /// Requires that a value is a non-null, non-whitespace string.
    /// </summary>
    /// <param name="value">The value to validate.</param>
    /// <param name="propertyName">The label used for diagnostics.</param>
    /// <returns>The validated string.</returns>
    internal static string RequireNonEmpty(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Expected {propertyName} to be non-empty.");
        }

        return value;
    }

    /// <summary>
    /// Formats a qualified resource name for diagnostics.
    /// </summary>
    /// <param name="resource">The resource identifier.</param>
    /// <returns>A formatted label.</returns>
    internal static string FormatResource(QualifiedResourceName resource)
    {
        return $"{resource.ProjectName}:{resource.ResourceName}";
    }

    /// <summary>
    /// Builds the base name for an identity path by concatenating Pascal-cased segments.
    /// </summary>
    /// <param name="identityJsonPath">The identity JSON path.</param>
    /// <returns>The base name for identity column naming.</returns>
    internal static string BuildIdentityPartBaseName(JsonPathExpression identityJsonPath)
    {
        List<string> segments = [];

        foreach (var segment in identityJsonPath.Segments)
        {
            switch (segment)
            {
                case JsonPathSegment.Property property:
                    segments.Add(property.Name);
                    break;
                case JsonPathSegment.AnyArrayElement:
                    throw new InvalidOperationException(
                        $"Identity path '{identityJsonPath.Canonical}' must not include array segments."
                    );
            }
        }

        if (segments.Count == 0)
        {
            throw new InvalidOperationException(
                $"Identity path '{identityJsonPath.Canonical}' must include at least one property segment."
            );
        }

        StringBuilder builder = new();

        foreach (var segment in segments)
        {
            builder.Append(RelationalNameConventions.ToPascalCase(segment));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the implied prefixes used to resolve collection override base names.
    /// </summary>
    internal static IReadOnlyList<string> BuildCollectionOverridePrefixes(
        string rootBaseName,
        string parentSuffix,
        params string?[] additionalRoots
    )
    {
        HashSet<string> prefixes = new(StringComparer.Ordinal);

        void AddPrefix(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                prefixes.Add(value);
            }
        }

        AddPrefix(rootBaseName + parentSuffix);
        AddPrefix(rootBaseName);
        AddPrefix(parentSuffix);

        if (additionalRoots is not null)
        {
            foreach (var additionalRoot in additionalRoots)
            {
                if (string.IsNullOrWhiteSpace(additionalRoot))
                {
                    continue;
                }

                AddPrefix(additionalRoot + parentSuffix);
                AddPrefix(additionalRoot);
            }
        }

        return prefixes.ToArray();
    }

    /// <summary>
    /// Resolves a collection override value to an effective segment name, stripping implied prefixes when present.
    /// </summary>
    internal static string ResolveCollectionOverrideBaseName(
        string overrideName,
        IReadOnlyList<string> impliedPrefixes,
        string jsonPath,
        string resourceLabel
    )
    {
        if (string.IsNullOrWhiteSpace(overrideName))
        {
            throw new InvalidOperationException(
                $"relational.nameOverrides entry for '{jsonPath}' on resource '{resourceLabel}' must be non-empty."
            );
        }

        if (impliedPrefixes.Count == 0)
        {
            return overrideName;
        }

        HashSet<string> suffixes = new(StringComparer.Ordinal);

        foreach (var prefix in impliedPrefixes)
        {
            foreach (var suffix in BuildPascalCaseSuffixes(prefix))
            {
                suffixes.Add(suffix);
            }
        }

        var orderedSuffixes = suffixes
            .OrderByDescending(value => value.Length)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToArray();

        foreach (var suffix in orderedSuffixes)
        {
            if (!overrideName.StartsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = overrideName[suffix.Length..];

            if (string.IsNullOrWhiteSpace(remainder))
            {
                throw new InvalidOperationException(
                    $"relational.nameOverrides entry for '{jsonPath}' on resource '{resourceLabel}' must extend "
                        + $"the implied prefix '{suffix}'."
                );
            }

            return remainder;
        }

        return overrideName;
    }

    private static IEnumerable<string> BuildPascalCaseSuffixes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        List<int> boundaries = [0];

        for (var index = 1; index < value.Length; index++)
        {
            if (!char.IsUpper(value[index]))
            {
                continue;
            }

            var previous = value[index - 1];
            var nextIsLower = index + 1 < value.Length && char.IsLower(value[index + 1]);

            if (char.IsLower(previous) || nextIsLower)
            {
                boundaries.Add(index);
            }
        }

        foreach (var boundary in boundaries)
        {
            var suffix = value[boundary..];

            if (!string.IsNullOrWhiteSpace(suffix))
            {
                yield return suffix;
            }
        }
    }

    /// <summary>
    /// Reads the <c>isResourceExtension</c> flag to determine whether the schema entry represents a
    /// resource-extension document rather than a concrete base resource.
    /// </summary>
    /// <param name="resourceContext">The resource schema context.</param>
    /// <returns><see langword="true"/> for resource extensions; otherwise <see langword="false"/>.</returns>
    internal static bool IsResourceExtension(ConcreteResourceSchemaContext resourceContext)
    {
        var resource = new QualifiedResourceName(
            resourceContext.Project.ProjectSchema.ProjectName,
            resourceContext.ResourceName
        );

        return IsResourceExtension(resourceContext.ResourceSchema, resource);
    }

    /// <summary>
    /// Reads the <c>isResourceExtension</c> flag from a resource schema object.
    /// </summary>
    /// <param name="resourceSchema">The resource schema object.</param>
    /// <param name="resource">The resource identifier for diagnostics.</param>
    /// <returns><see langword="true"/> for resource extensions; otherwise <see langword="false"/>.</returns>
    internal static bool IsResourceExtension(JsonObject resourceSchema, QualifiedResourceName resource)
    {
        return IsResourceExtension(resourceSchema, FormatResource(resource));
    }

    /// <summary>
    /// Builds (and caches) a minimal <c>ApiSchema.json</c>-shaped root node for per-resource pipelines.
    /// </summary>
    /// <param name="apiSchemaRootsByProjectEndpoint">Cache of root nodes by project endpoint name.</param>
    /// <param name="projectEndpointName">The project endpoint name for the resource.</param>
    /// <param name="projectSchema">The project schema node.</param>
    /// <param name="cloneProjectSchema">Whether to clone the project schema before caching.</param>
    /// <returns>A root object containing the <c>projectSchema</c> property.</returns>
    internal static JsonObject GetApiSchemaRoot(
        IDictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint,
        string projectEndpointName,
        JsonObject projectSchema,
        bool cloneProjectSchema
    )
    {
        if (apiSchemaRootsByProjectEndpoint.TryGetValue(projectEndpointName, out var apiSchemaRoot))
        {
            return apiSchemaRoot;
        }

        var rootSchema = cloneProjectSchema ? CloneProjectSchema(projectSchema) : projectSchema;

        apiSchemaRoot = new JsonObject { ["projectSchema"] = rootSchema };

        apiSchemaRootsByProjectEndpoint[projectEndpointName] = apiSchemaRoot;

        return apiSchemaRoot;
    }

    /// <summary>
    /// Builds a lookup of base resources keyed by resource name.
    /// </summary>
    /// <typeparam name="TEntry">The entry type stored in the lookup.</typeparam>
    /// <param name="resources">The ordered list of concrete resources.</param>
    /// <param name="entryFactory">Factory for lookup entries.</param>
    /// <returns>The lookup keyed by resource name.</returns>
    internal static Dictionary<string, List<TEntry>> BuildBaseResourceLookup<TEntry>(
        IReadOnlyList<ConcreteResourceModel> resources,
        Func<int, ConcreteResourceModel, TEntry> entryFactory
    )
    {
        Dictionary<string, List<TEntry>> lookup = new(StringComparer.Ordinal);

        for (var index = 0; index < resources.Count; index++)
        {
            var resource = resources[index];
            var resourceName = resource.ResourceKey.Resource.ResourceName;

            if (!lookup.TryGetValue(resourceName, out var entries))
            {
                entries = [];
                lookup.Add(resourceName, entries);
            }

            entries.Add(entryFactory(index, resource));
        }

        return lookup;
    }

    /// <summary>
    /// Resolves the single concrete base resource entry for a resource extension, with consistent validation.
    /// </summary>
    /// <typeparam name="TEntry">The entry type stored in the lookup.</typeparam>
    /// <param name="resourceName">The extension resource name used to locate base resources.</param>
    /// <param name="resource">The resource identifier for diagnostics.</param>
    /// <param name="baseResourcesByName">The lookup of base resources keyed by resource name.</param>
    /// <param name="entryResourceSelector">Selector for the resource identifier on each entry.</param>
    /// <returns>The matching base resource entry.</returns>
    internal static TEntry ResolveBaseResourceForExtension<TEntry>(
        string resourceName,
        QualifiedResourceName resource,
        IReadOnlyDictionary<string, List<TEntry>> baseResourcesByName,
        Func<TEntry, QualifiedResourceName> entryResourceSelector
    )
    {
        if (!baseResourcesByName.TryGetValue(resourceName, out var baseEntries))
        {
            throw new InvalidOperationException(
                $"Resource extension '{FormatResource(resource)}' did not match a concrete base resource."
            );
        }

        if (baseEntries.Count != 1)
        {
            var candidates = string.Join(
                ", ",
                baseEntries
                    .Select(entry => FormatResource(entryResourceSelector(entry)))
                    .OrderBy(name => name, StringComparer.Ordinal)
            );

            throw new InvalidOperationException(
                $"Resource extension '{FormatResource(resource)}' matched multiple concrete resources: "
                    + $"{candidates}."
            );
        }

        return baseEntries[0];
    }

    /// <summary>
    /// Determines whether the prefix segments match the beginning of the path segments.
    /// </summary>
    /// <param name="prefix">The prefix segments to compare.</param>
    /// <param name="path">The full path segments.</param>
    /// <returns><see langword="true"/> when the prefix matches; otherwise <see langword="false"/>.</returns>
    internal static bool IsPrefixOf(
        IReadOnlyList<JsonPathSegment> prefix,
        IReadOnlyList<JsonPathSegment> path
    )
    {
        if (prefix.Count > path.Count)
        {
            return false;
        }

        for (var index = 0; index < prefix.Count; index++)
        {
            var prefixSegment = prefix[index];
            var pathSegment = path[index];

            if (prefixSegment.GetType() != pathSegment.GetType())
            {
                return false;
            }

            if (
                prefixSegment is JsonPathSegment.Property prefixProperty
                && pathSegment is JsonPathSegment.Property pathProperty
                && !string.Equals(prefixProperty.Name, pathProperty.Name, StringComparison.Ordinal)
            )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads the <c>isResourceExtension</c> flag from a resource schema node.
    /// </summary>
    private static bool IsResourceExtension(JsonObject resourceSchema, string resourceLabel)
    {
        if (
            !resourceSchema.TryGetPropertyValue("isResourceExtension", out var resourceExtensionNode)
            || resourceExtensionNode is null
        )
        {
            throw new InvalidOperationException(
                $"Expected isResourceExtension to be on ResourceSchema for resource '{resourceLabel}', "
                    + "invalid ApiSchema."
            );
        }

        return resourceExtensionNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<bool>(),
            _ => throw new InvalidOperationException(
                $"Expected isResourceExtension to be a boolean for resource '{resourceLabel}', "
                    + "invalid ApiSchema."
            ),
        };
    }

    /// <summary>
    /// Deep-clones a project schema into a detached JSON object so subsequent derivation passes may safely
    /// traverse and validate it.
    /// </summary>
    private static JsonObject CloneProjectSchema(JsonObject projectSchema)
    {
        var detachedSchema = projectSchema.DeepClone();

        if (detachedSchema is not JsonObject detachedObject)
        {
            throw new InvalidOperationException("Project schema must be an object.");
        }

        return detachedObject;
    }

    /// <summary>
    /// Resolves the schema node for a JSON path within a resource schema.
    /// </summary>
    /// <param name="rootSchemaNode">The root schema node.</param>
    /// <param name="path">The JSON path to resolve.</param>
    /// <param name="resource">The resource identifier for diagnostics.</param>
    /// <param name="pathRole">A label used in error messages (e.g., Reference, Identity).</param>
    /// <returns>The resolved <see cref="JsonObject"/> schema node.</returns>
    internal static JsonObject ResolveSchemaForPath(
        JsonNode? rootSchemaNode,
        JsonPathExpression path,
        QualifiedResourceName resource,
        string pathRole
    )
    {
        if (rootSchemaNode is not JsonObject rootSchema)
        {
            throw new InvalidOperationException("Json schema root must be an object.");
        }

        var current = rootSchema;

        foreach (var segment in path.Segments)
        {
            var schemaKind = JsonSchemaTraversalConventions.DetermineSchemaKind(current);

            switch (segment)
            {
                case JsonPathSegment.Property property:
                    if (schemaKind != SchemaKind.Object)
                    {
                        throw new InvalidOperationException(
                            $"Expected object schema for '{path.Canonical}' while resolving "
                                + $"'{property.Name}' on resource '{FormatResource(resource)}'."
                        );
                    }

                    if (
                        !current.TryGetPropertyValue("properties", out var propertiesNode)
                        || propertiesNode is null
                    )
                    {
                        throw new InvalidOperationException(
                            $"Expected properties to be present for '{path.Canonical}' on resource "
                                + $"'{FormatResource(resource)}'."
                        );
                    }

                    if (propertiesNode is not JsonObject propertiesObject)
                    {
                        throw new InvalidOperationException(
                            $"Expected properties to be an object for '{path.Canonical}' on resource "
                                + $"'{FormatResource(resource)}'."
                        );
                    }

                    if (
                        !propertiesObject.TryGetPropertyValue(property.Name, out var propertyNode)
                        || propertyNode is null
                    )
                    {
                        throw new InvalidOperationException(
                            $"{pathRole} path '{path.Canonical}' was not found in jsonSchemaForInsert for "
                                + $"resource '{FormatResource(resource)}'."
                        );
                    }

                    if (propertyNode is not JsonObject propertySchema)
                    {
                        throw new InvalidOperationException(
                            $"Expected schema object at '{path.Canonical}' for resource "
                                + $"'{FormatResource(resource)}'."
                        );
                    }

                    current = propertySchema;
                    break;
                case JsonPathSegment.AnyArrayElement:
                    if (schemaKind != SchemaKind.Array)
                    {
                        throw new InvalidOperationException(
                            $"Expected array schema for '{path.Canonical}' on resource "
                                + $"'{FormatResource(resource)}'."
                        );
                    }

                    if (!current.TryGetPropertyValue("items", out var itemsNode) || itemsNode is null)
                    {
                        throw new InvalidOperationException(
                            $"Expected array items for '{path.Canonical}' on resource "
                                + $"'{FormatResource(resource)}'."
                        );
                    }

                    if (itemsNode is not JsonObject itemsSchema)
                    {
                        throw new InvalidOperationException(
                            $"Expected array items schema to be an object for '{path.Canonical}' on "
                                + $"resource '{FormatResource(resource)}'."
                        );
                    }

                    current = itemsSchema;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported JSONPath segment for '{path.Canonical}' on resource "
                            + $"'{FormatResource(resource)}'."
                    );
            }
        }

        return current;
    }

    /// <summary>
    /// Orders resource schema entries deterministically by resource name and schema key.
    /// </summary>
    /// <param name="resourceSchemas">The resource schema object to enumerate.</param>
    /// <param name="resourceSchemasPath">The JSON label used for diagnostics.</param>
    /// <param name="requireNonEmptyKey">Whether entry keys must be non-empty.</param>
    /// <returns>The ordered resource schema entries.</returns>
    internal static IReadOnlyList<ResourceSchemaEntry> OrderResourceSchemas(
        JsonObject resourceSchemas,
        string resourceSchemasPath,
        bool requireNonEmptyKey = false
    )
    {
        List<ResourceSchemaEntry> entries = new(resourceSchemas.Count);

        foreach (var resourceSchemaEntry in resourceSchemas)
        {
            if (resourceSchemaEntry.Value is null)
            {
                throw new InvalidOperationException(
                    $"Expected {resourceSchemasPath} entries to be non-null, invalid ApiSchema."
                );
            }

            if (resourceSchemaEntry.Value is not JsonObject resourceSchema)
            {
                throw new InvalidOperationException(
                    $"Expected {resourceSchemasPath} entries to be objects, invalid ApiSchema."
                );
            }

            if (requireNonEmptyKey && string.IsNullOrWhiteSpace(resourceSchemaEntry.Key))
            {
                throw new InvalidOperationException(
                    "Expected resource schema entry key to be non-empty, invalid ApiSchema."
                );
            }

            var resourceName = GetResourceName(resourceSchemaEntry.Key, resourceSchema);

            entries.Add(new ResourceSchemaEntry(resourceSchemaEntry.Key, resourceName, resourceSchema));
        }

        return entries
            .OrderBy(entry => entry.ResourceName, StringComparer.Ordinal)
            .ThenBy(entry => entry.ResourceKey, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>
/// Captures the normalized inputs for a single resource schema entry within a project schema.
/// </summary>
internal sealed record ResourceSchemaEntry(
    string ResourceKey,
    string ResourceName,
    JsonObject ResourceSchema
);
