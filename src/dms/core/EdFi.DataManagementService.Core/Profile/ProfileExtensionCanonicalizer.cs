// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Rewrites profile extension rule names to the API schema's extension keys so the
/// rest of the profile runtime agrees with the schema-derived JSON shape (DMS-1233).
/// </summary>
/// <remarks>
/// <para>
/// Profile XML preserves the authored extension name (e.g. <c>Sample</c>), but the
/// schema, the relational write plan, and reconstituted documents all expose the
/// extension payload under the project endpoint key (e.g. <c>sample</c>). Without
/// normalization the write pipeline derives the compiled scope <c>$._ext.Sample</c>,
/// which cannot be resolved case-sensitively against <c>jsonSchemaForInsert</c> and
/// throws (a 500), and read/stored traversal misclassifies the <c>_ext</c> member.
/// </para>
/// <para>
/// Canonicalizing once, at the profile-load seam, keeps every downstream consumer
/// (<see cref="ContentTypeScopeDiscovery"/>, <see cref="ProfileTreeNavigator"/>,
/// <see cref="WritableRequestShaper"/>, <see cref="StoredBodyShaper"/>,
/// <see cref="StoredSideExistenceLookupBuilder"/>, <see cref="ReadableProfileProjector"/>,
/// and <see cref="ProfileResponseFilter"/>) aligned on the canonical key without each
/// having to be schema-aware.
/// </para>
/// <para>
/// An extension rule whose name matches no schema extension key is dropped from the
/// canonicalized definition. Such a rule only survives load when its parent is
/// <c>ExcludeOnly</c>/<c>IncludeAll</c> (a genuinely unknown extension under
/// <c>IncludeOnly</c> is a validation error that drops the whole profile). Excluding a
/// non-existent extension is a no-op, and dropping the rule prevents an unresolved
/// runtime scope from being emitted for it.
/// </para>
/// </remarks>
internal static class ProfileExtensionCanonicalizer
{
    /// <summary>
    /// Returns a copy of <paramref name="definition"/> with every extension rule name
    /// rewritten to the matching schema extension key and unmatched extension rules
    /// removed. Returns the original instance unchanged when no rewrite is needed.
    /// </summary>
    public static ProfileDefinition Canonicalize(
        ProfileDefinition definition,
        IEffectiveApiSchemaProvider effectiveApiSchemaProvider
    )
    {
        ApiSchemaDocuments apiSchemaDocuments = effectiveApiSchemaProvider.Documents;
        ProjectSchema[] projectSchemas =
        [
            apiSchemaDocuments.GetCoreProjectSchema(),
            .. apiSchemaDocuments.GetExtensionProjectSchemas(),
        ];

        List<ResourceProfile>? rewrittenResources = null;

        for (int i = 0; i < definition.Resources.Count; i++)
        {
            ResourceProfile resourceProfile = definition.Resources[i];
            IReadOnlyDictionary<string, string> extensionKeys = BuildExtensionKeyMap(
                resourceProfile.ResourceName,
                projectSchemas
            );

            ContentTypeDefinition? canonicalRead = CanonicalizeContentType(
                resourceProfile.ReadContentType,
                extensionKeys
            );
            ContentTypeDefinition? canonicalWrite = CanonicalizeContentType(
                resourceProfile.WriteContentType,
                extensionKeys
            );

            if (
                ReferenceEquals(canonicalRead, resourceProfile.ReadContentType)
                && ReferenceEquals(canonicalWrite, resourceProfile.WriteContentType)
            )
            {
                rewrittenResources?.Add(resourceProfile);
                continue;
            }

            rewrittenResources ??= [.. definition.Resources.Take(i)];
            rewrittenResources.Add(
                resourceProfile with
                {
                    ReadContentType = canonicalRead,
                    WriteContentType = canonicalWrite,
                }
            );
        }

        return rewrittenResources is null ? definition : definition with { Resources = rewrittenResources };
    }

    /// <summary>
    /// Builds a case-insensitive map of extension key (any casing) to the canonical
    /// schema key for the named resource, gathered from every <c>_ext.properties</c>
    /// object anywhere in the resource's <c>jsonSchemaForInsert</c>.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildExtensionKeyMap(
        string resourceName,
        ProjectSchema[] projectSchemas
    )
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (ProjectSchema projectSchema in projectSchemas)
        {
            JsonNode? resourceNode = projectSchema.FindResourceSchemaNodeByResourceName(
                new ResourceName(resourceName)
            );
            if (resourceNode?["jsonSchemaForInsert"] is JsonObject jsonSchemaForInsert)
            {
                CollectExtensionKeys(jsonSchemaForInsert, map);
                break;
            }
        }

        return map;
    }

    /// <summary>
    /// Recursively scans a JSON schema node for <c>_ext.properties</c> objects, adding
    /// each extension key to <paramref name="map"/> (keyed by itself for canonical lookup).
    /// </summary>
    private static void CollectExtensionKeys(JsonNode? node, Dictionary<string, string> map)
    {
        if (node is JsonObject obj)
        {
            if (obj["_ext"] is JsonObject extNode && extNode["properties"] is JsonObject extProperties)
            {
#pragma warning disable S3267 // Populating a dictionary in place; a LINQ rewrite would not improve clarity.
                foreach (var extension in extProperties)
                {
                    map[extension.Key] = extension.Key;
                }
#pragma warning restore S3267
            }

            foreach (var property in obj)
            {
                CollectExtensionKeys(property.Value, map);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                CollectExtensionKeys(item, map);
            }
        }
    }

    private static ContentTypeDefinition? CanonicalizeContentType(
        ContentTypeDefinition? contentType,
        IReadOnlyDictionary<string, string> extensionKeys
    )
    {
        if (contentType is null)
        {
            return null;
        }

        IReadOnlyList<ObjectRule> objects = CanonicalizeObjects(contentType.Objects, extensionKeys);
        IReadOnlyList<CollectionRule> collections = CanonicalizeCollections(
            contentType.Collections,
            extensionKeys
        );
        IReadOnlyList<ExtensionRule> extensions = CanonicalizeExtensions(
            contentType.Extensions,
            extensionKeys
        );

        if (
            ReferenceEquals(objects, contentType.Objects)
            && ReferenceEquals(collections, contentType.Collections)
            && ReferenceEquals(extensions, contentType.Extensions)
        )
        {
            return contentType;
        }

        return contentType with
        {
            Objects = objects,
            Collections = collections,
            Extensions = extensions,
        };
    }

    private static IReadOnlyList<ObjectRule> CanonicalizeObjects(
        IReadOnlyList<ObjectRule>? objects,
        IReadOnlyDictionary<string, string> extensionKeys
    )
    {
        if (objects is null || objects.Count == 0)
        {
            return objects ?? [];
        }

        ObjectRule[]? rewritten = null;

        for (int i = 0; i < objects.Count; i++)
        {
            ObjectRule canonical = CanonicalizeObject(objects[i], extensionKeys);
            if (!ReferenceEquals(canonical, objects[i]))
            {
                rewritten ??= [.. objects];
                rewritten[i] = canonical;
            }
        }

        return rewritten ?? objects;
    }

    private static ObjectRule CanonicalizeObject(
        ObjectRule rule,
        IReadOnlyDictionary<string, string> extensionKeys
    )
    {
        IReadOnlyList<ObjectRule>? nestedObjects = rule.NestedObjects is null
            ? null
            : CanonicalizeObjects(rule.NestedObjects, extensionKeys);
        IReadOnlyList<CollectionRule>? collections = rule.Collections is null
            ? null
            : CanonicalizeCollections(rule.Collections, extensionKeys);
        IReadOnlyList<ExtensionRule>? extensions = rule.Extensions is null
            ? null
            : CanonicalizeExtensions(rule.Extensions, extensionKeys);

        if (
            ReferenceEquals(nestedObjects, rule.NestedObjects)
            && ReferenceEquals(collections, rule.Collections)
            && ReferenceEquals(extensions, rule.Extensions)
        )
        {
            return rule;
        }

        return rule with
        {
            NestedObjects = nestedObjects,
            Collections = collections,
            Extensions = extensions,
        };
    }

    private static IReadOnlyList<CollectionRule> CanonicalizeCollections(
        IReadOnlyList<CollectionRule>? collections,
        IReadOnlyDictionary<string, string> extensionKeys
    )
    {
        if (collections is null || collections.Count == 0)
        {
            return collections ?? [];
        }

        CollectionRule[]? rewritten = null;

        for (int i = 0; i < collections.Count; i++)
        {
            CollectionRule canonical = CanonicalizeCollection(collections[i], extensionKeys);
            if (!ReferenceEquals(canonical, collections[i]))
            {
                rewritten ??= [.. collections];
                rewritten[i] = canonical;
            }
        }

        return rewritten ?? collections;
    }

    private static CollectionRule CanonicalizeCollection(
        CollectionRule rule,
        IReadOnlyDictionary<string, string> extensionKeys
    )
    {
        IReadOnlyList<ObjectRule>? nestedObjects = rule.NestedObjects is null
            ? null
            : CanonicalizeObjects(rule.NestedObjects, extensionKeys);
        IReadOnlyList<CollectionRule>? nestedCollections = rule.NestedCollections is null
            ? null
            : CanonicalizeCollections(rule.NestedCollections, extensionKeys);
        IReadOnlyList<ExtensionRule>? extensions = rule.Extensions is null
            ? null
            : CanonicalizeExtensions(rule.Extensions, extensionKeys);

        if (
            ReferenceEquals(nestedObjects, rule.NestedObjects)
            && ReferenceEquals(nestedCollections, rule.NestedCollections)
            && ReferenceEquals(extensions, rule.Extensions)
        )
        {
            return rule;
        }

        return rule with
        {
            NestedObjects = nestedObjects,
            NestedCollections = nestedCollections,
            Extensions = extensions,
        };
    }

    /// <summary>
    /// Rewrites each extension rule name to its canonical schema key, recurses into the
    /// rule's own objects/collections, and drops rules whose name matches no schema key.
    /// </summary>
    private static IReadOnlyList<ExtensionRule> CanonicalizeExtensions(
        IReadOnlyList<ExtensionRule>? extensions,
        IReadOnlyDictionary<string, string> extensionKeys
    )
    {
        if (extensions is null || extensions.Count == 0)
        {
            return extensions ?? [];
        }

        List<ExtensionRule>? rewritten = null;

        for (int i = 0; i < extensions.Count; i++)
        {
            ExtensionRule rule = extensions[i];

            if (!extensionKeys.TryGetValue(rule.Name, out string? canonicalName))
            {
                // Unknown extension: drop it so no unresolved runtime scope is emitted.
                rewritten ??= [.. extensions.Take(i)];
                continue;
            }

            IReadOnlyList<ObjectRule>? objects = rule.Objects is null
                ? null
                : CanonicalizeObjects(rule.Objects, extensionKeys);
            IReadOnlyList<CollectionRule>? collections = rule.Collections is null
                ? null
                : CanonicalizeCollections(rule.Collections, extensionKeys);

            bool nameChanged = !string.Equals(rule.Name, canonicalName, StringComparison.Ordinal);
            bool childrenChanged =
                !ReferenceEquals(objects, rule.Objects) || !ReferenceEquals(collections, rule.Collections);

            if (!nameChanged && !childrenChanged)
            {
                rewritten?.Add(rule);
                continue;
            }

            rewritten ??= [.. extensions.Take(i)];
            rewritten.Add(rule with { Name = canonicalName, Objects = objects, Collections = collections });
        }

        return rewritten ?? extensions;
    }
}
