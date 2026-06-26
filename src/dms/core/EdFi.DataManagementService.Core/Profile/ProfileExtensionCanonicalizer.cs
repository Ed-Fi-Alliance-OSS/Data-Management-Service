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
/// Resolution is <em>location-aware</em>: each extension rule is matched against the
/// <c>_ext.properties</c> keys of the schema node at the rule's own position in the
/// profile tree (root, or inside a specific object/collection/extension), mirroring how
/// <see cref="ProfileDataValidator"/> navigates. A resource-wide key set would let a root
/// rule that only resolves at a nested location survive and emit an unresolved root
/// <c>$._ext.&lt;key&gt;</c> scope — reintroducing the same runtime 500 this change removes.
/// </para>
/// <para>
/// An extension rule whose name matches no schema extension key <em>at its location</em>
/// is dropped from the canonicalized definition. Such a rule only survives load when its
/// parent is <c>ExcludeOnly</c>/<c>IncludeAll</c> (a genuinely unknown extension under
/// <c>IncludeOnly</c> is a validation error that drops the whole profile). Excluding a
/// non-existent extension is a no-op, and dropping the rule prevents an unresolved runtime
/// scope from being emitted for it.
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
            JsonObject? rootProperties = FindResourceInsertProperties(
                resourceProfile.ResourceName,
                projectSchemas
            );

            ContentTypeDefinition? canonicalRead = CanonicalizeContentType(
                resourceProfile.ReadContentType,
                rootProperties
            );
            ContentTypeDefinition? canonicalWrite = CanonicalizeContentType(
                resourceProfile.WriteContentType,
                rootProperties
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
    /// Resolves the <c>properties</c> object of the named resource's <c>jsonSchemaForInsert</c>,
    /// which is the schema node the root content type members navigate against.
    /// </summary>
    private static JsonObject? FindResourceInsertProperties(
        string resourceName,
        ProjectSchema[] projectSchemas
    )
    {
        foreach (ProjectSchema projectSchema in projectSchemas)
        {
            JsonNode? resourceNode = projectSchema.FindResourceSchemaNodeByResourceName(
                new ResourceName(resourceName)
            );
            if (resourceNode?["jsonSchemaForInsert"] is JsonObject jsonSchemaForInsert)
            {
                return jsonSchemaForInsert["properties"] as JsonObject;
            }
        }

        return null;
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Schema navigation helpers
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>The <c>_ext.properties</c> object at the current schema location, or null.</summary>
    private static JsonObject? ExtensionPropertiesAt(JsonObject? schemaProperties) =>
        (schemaProperties?["_ext"] as JsonObject)?["properties"] as JsonObject;

    /// <summary>The <c>properties</c> object of a named member's schema node, or null.</summary>
    private static JsonObject? MemberProperties(JsonObject? schemaProperties, string memberName) =>
        (schemaProperties?[memberName] as JsonObject)?["properties"] as JsonObject;

    /// <summary>The <c>items.properties</c> object of a named collection's schema node, or null.</summary>
    private static JsonObject? CollectionItemProperties(
        JsonObject? schemaProperties,
        string collectionName
    ) =>
        ((schemaProperties?[collectionName] as JsonObject)?["items"] as JsonObject)?["properties"]
        as JsonObject;

    /// <summary>
    /// Matches <paramref name="name"/> against the extension keys at <paramref name="extProperties"/>
    /// case-insensitively (exact ordinal match preferred). Returns false when no key matches.
    /// </summary>
    private static bool TryResolveExtensionKey(
        JsonObject? extProperties,
        string name,
        out string canonicalKey
    )
    {
        canonicalKey = name;

        if (extProperties is null)
        {
            return false;
        }

        if (extProperties.ContainsKey(name))
        {
            return true;
        }

        var match = extProperties.FirstOrDefault(property =>
            property.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
        );
        if (match.Key is null)
        {
            return false;
        }

        canonicalKey = match.Key;
        return true;
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Tree canonicalization (schema-location-aware)
    // ───────────────────────────────────────────────────────────────────────

    private static ContentTypeDefinition? CanonicalizeContentType(
        ContentTypeDefinition? contentType,
        JsonObject? schemaProperties
    )
    {
        if (contentType is null)
        {
            return null;
        }

        IReadOnlyList<ObjectRule> objects = CanonicalizeObjects(contentType.Objects, schemaProperties);
        IReadOnlyList<CollectionRule> collections = CanonicalizeCollections(
            contentType.Collections,
            schemaProperties
        );
        IReadOnlyList<ExtensionRule> extensions = CanonicalizeExtensions(
            contentType.Extensions,
            schemaProperties
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
        JsonObject? schemaProperties
    )
    {
        if (objects is null || objects.Count == 0)
        {
            return objects ?? [];
        }

        ObjectRule[]? rewritten = null;

        for (int i = 0; i < objects.Count; i++)
        {
            ObjectRule canonical = CanonicalizeObject(
                objects[i],
                MemberProperties(schemaProperties, objects[i].Name)
            );
            if (!ReferenceEquals(canonical, objects[i]))
            {
                rewritten ??= [.. objects];
                rewritten[i] = canonical;
            }
        }

        return rewritten ?? objects;
    }

    private static ObjectRule CanonicalizeObject(ObjectRule rule, JsonObject? schemaProperties)
    {
        IReadOnlyList<ObjectRule>? nestedObjects = rule.NestedObjects is null
            ? null
            : CanonicalizeObjects(rule.NestedObjects, schemaProperties);
        IReadOnlyList<CollectionRule>? collections = rule.Collections is null
            ? null
            : CanonicalizeCollections(rule.Collections, schemaProperties);
        IReadOnlyList<ExtensionRule>? extensions = rule.Extensions is null
            ? null
            : CanonicalizeExtensions(rule.Extensions, schemaProperties);

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
        JsonObject? schemaProperties
    )
    {
        if (collections is null || collections.Count == 0)
        {
            return collections ?? [];
        }

        CollectionRule[]? rewritten = null;

        for (int i = 0; i < collections.Count; i++)
        {
            CollectionRule canonical = CanonicalizeCollection(
                collections[i],
                CollectionItemProperties(schemaProperties, collections[i].Name)
            );
            if (!ReferenceEquals(canonical, collections[i]))
            {
                rewritten ??= [.. collections];
                rewritten[i] = canonical;
            }
        }

        return rewritten ?? collections;
    }

    private static CollectionRule CanonicalizeCollection(CollectionRule rule, JsonObject? itemProperties)
    {
        IReadOnlyList<ObjectRule>? nestedObjects = rule.NestedObjects is null
            ? null
            : CanonicalizeObjects(rule.NestedObjects, itemProperties);
        IReadOnlyList<CollectionRule>? nestedCollections = rule.NestedCollections is null
            ? null
            : CanonicalizeCollections(rule.NestedCollections, itemProperties);
        IReadOnlyList<ExtensionRule>? extensions = rule.Extensions is null
            ? null
            : CanonicalizeExtensions(rule.Extensions, itemProperties);

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
    /// Rewrites each extension rule name to its canonical schema key — resolved against the
    /// <c>_ext.properties</c> at <paramref name="schemaProperties"/> (this rule's location) —
    /// recurses into the rule's own objects/collections, and drops rules that do not resolve here.
    /// </summary>
    private static IReadOnlyList<ExtensionRule> CanonicalizeExtensions(
        IReadOnlyList<ExtensionRule>? extensions,
        JsonObject? schemaProperties
    )
    {
        if (extensions is null || extensions.Count == 0)
        {
            return extensions ?? [];
        }

        JsonObject? extProperties = ExtensionPropertiesAt(schemaProperties);
        List<ExtensionRule>? rewritten = null;

        for (int i = 0; i < extensions.Count; i++)
        {
            ExtensionRule rule = extensions[i];

            if (!TryResolveExtensionKey(extProperties, rule.Name, out string canonicalName))
            {
                // Unmatched at this location: drop it so no unresolved runtime scope is emitted.
                rewritten ??= [.. extensions.Take(i)];
                continue;
            }

            JsonObject? extChildProperties = MemberProperties(extProperties, canonicalName);
            IReadOnlyList<ObjectRule>? objects = rule.Objects is null
                ? null
                : CanonicalizeObjects(rule.Objects, extChildProperties);
            IReadOnlyList<CollectionRule>? collections = rule.Collections is null
                ? null
                : CanonicalizeCollections(rule.Collections, extChildProperties);

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
