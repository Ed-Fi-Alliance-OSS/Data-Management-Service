// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Shared schema navigation and extension-key resolution used by both
/// <see cref="ProfileDataValidator"/> (which reports unknown/duplicate extensions) and
/// <see cref="ProfileExtensionCanonicalizer"/> (which rewrites/drops them). Both must resolve
/// the same canonical key at the same schema location for every rule, or the validator could
/// pass a profile while the canonicalizer drops a different rule — silently re-creating the
/// unresolved-scope failure this logic exists to prevent. Keeping the navigation in one place
/// makes that invariant hold by construction rather than by copy-paste discipline.
/// </summary>
internal static class ProfileExtensionSchemaResolver
{
    /// <summary>The <c>_ext.properties</c> object at the current schema location, or null.</summary>
    public static JsonObject? ExtensionPropertiesAt(JsonObject? schemaProperties) =>
        (schemaProperties?["_ext"] as JsonObject)?["properties"] as JsonObject;

    /// <summary>The <c>properties</c> object of a named member's schema node, or null.</summary>
    public static JsonObject? MemberProperties(JsonObject? schemaProperties, string memberName) =>
        (schemaProperties?[memberName] as JsonObject)?["properties"] as JsonObject;

    /// <summary>The <c>items.properties</c> object of a named collection's schema node, or null.</summary>
    public static JsonObject? CollectionItemProperties(JsonObject? schemaProperties, string collectionName) =>
        ((schemaProperties?[collectionName] as JsonObject)?["items"] as JsonObject)?["properties"]
        as JsonObject;

    /// <summary>
    /// Resolves the canonical schema extension key for <paramref name="name"/> from the supplied
    /// <c>_ext.properties</c>, matching case-insensitively and preferring an exact ordinal match.
    /// Returns true and sets <paramref name="canonicalKey"/> to the schema's casing when matched;
    /// returns false (leaving <paramref name="canonicalKey"/> as <paramref name="name"/>) otherwise.
    /// </summary>
    public static bool TryResolveExtensionKey(JsonObject? extProperties, string name, out string canonicalKey)
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
}
