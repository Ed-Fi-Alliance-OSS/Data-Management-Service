// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;

/// <summary>
/// The canonical inventory JSON of the delivered restore contract (<c>ConvertTo-CanonicalInventoryJson</c>):
/// <c>{"schemas":[{"schemaName":…,"objects":[{"name":…,"type":…}]}],"principals":[…]}</c> on one line with
/// fixed key order, entries in canonical order, and deterministic string escaping: <c>"</c> and <c>\</c> are
/// backslash-escaped and every UTF-16 code unit below U+0020 or above U+007E is written as a lowercase
/// <c>\uXXXX</c> escape, so the output is ASCII and never depends on a JSON library's encoder. The producer
/// and the consumer hash exactly this text. <see cref="Read"/> is the inverse for a manifest's <c>inventory</c>
/// member, applying the delivered input rules plus the strict unknown-member rule.
/// </summary>
public static class CanonicalInventoryJson
{
    private static readonly HashSet<string> _inventoryMembers = new(StringComparer.Ordinal)
    {
        "schemas",
        "principals",
    };

    private static readonly HashSet<string> _schemaMembers = new(StringComparer.Ordinal)
    {
        "schemaName",
        "objects",
    };

    private static readonly HashSet<string> _objectMembers = new(StringComparer.Ordinal) { "name", "type" };

    public static string Serialize(CanonicalInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        StringBuilder builder = new();
        builder.Append("{\"schemas\":[");
        for (int schemaIndex = 0; schemaIndex < inventory.Schemas.Count; schemaIndex++)
        {
            if (schemaIndex > 0)
            {
                builder.Append(',');
            }

            CanonicalInventory.Schema schema = inventory.Schemas[schemaIndex];
            builder.Append("{\"schemaName\":");
            AppendStringLiteral(builder, schema.SchemaName);
            builder.Append(",\"objects\":[");
            for (int objectIndex = 0; objectIndex < schema.Objects.Count; objectIndex++)
            {
                if (objectIndex > 0)
                {
                    builder.Append(',');
                }

                CanonicalInventory.Entry entry = schema.Objects[objectIndex];
                builder.Append("{\"name\":");
                AppendStringLiteral(builder, entry.Name);
                builder.Append(",\"type\":");
                AppendStringLiteral(builder, entry.Type);
                builder.Append('}');
            }

            builder.Append("]}");
        }

        builder.Append("],\"principals\":[");
        for (int principalIndex = 0; principalIndex < inventory.Principals.Count; principalIndex++)
        {
            if (principalIndex > 0)
            {
                builder.Append(',');
            }

            AppendStringLiteral(builder, inventory.Principals[principalIndex]);
        }

        builder.Append("]}");
        return builder.ToString();
    }

    /// <summary>
    /// Reads a manifest's <c>inventory</c> member: an object with a <c>schemas</c> array (each entry an object
    /// with a non-empty string <c>schemaName</c> and an <c>objects</c> array of objects with non-empty string
    /// <c>name</c> and <c>type</c>; an empty schema declares an empty array) and an optional <c>principals</c>
    /// array of non-empty strings (absent or null means none). Any other member, a scalar or singleton object
    /// where an array is required, a null entry, or an entry that is not canonical (<see cref="CanonicalInventory.TryCreate"/>)
    /// throws <see cref="RestoreManifestException"/> naming the entry position only.
    /// </summary>
    internal static CanonicalInventory Read(JsonElement inventory)
    {
        if (inventory.ValueKind != JsonValueKind.Object)
        {
            throw new RestoreManifestException(
                "The restore manifest field 'inventory' must be a JSON object."
            );
        }

        RestoreManifestShapeValidator.RejectUnknownMembers(
            inventory,
            _inventoryMembers,
            "The restore manifest inventory"
        );

        if (
            !inventory.TryGetProperty("schemas", out JsonElement schemasElement)
            || schemasElement.ValueKind == JsonValueKind.Null
        )
        {
            throw new RestoreManifestException(
                "The restore manifest inventory is missing the required 'schemas' array."
            );
        }

        if (schemasElement.ValueKind != JsonValueKind.Array)
        {
            throw new RestoreManifestException(
                "The restore manifest inventory field 'schemas' must be a JSON array, not a single object or scalar."
            );
        }

        List<CanonicalInventory.Schema> schemas = [];
        int schemaPosition = 0;
        foreach (JsonElement schemaElement in schemasElement.EnumerateArray())
        {
            schemaPosition++;
            schemas.Add(ReadSchema(schemaElement, schemaPosition));
        }

        List<string> principals = [];
        if (
            inventory.TryGetProperty("principals", out JsonElement principalsElement)
            && principalsElement.ValueKind != JsonValueKind.Null
        )
        {
            if (principalsElement.ValueKind != JsonValueKind.Array)
            {
                throw new RestoreManifestException(
                    "The restore manifest inventory field 'principals' must be a JSON array when present, not a single value."
                );
            }

            int principalPosition = 0;
            foreach (JsonElement principalElement in principalsElement.EnumerateArray())
            {
                principalPosition++;
                if (
                    principalElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(principalElement.GetString())
                )
                {
                    throw new RestoreManifestException(
                        $"The restore manifest inventory principal entry {principalPosition} must be a non-empty JSON string."
                    );
                }

                principals.Add(principalElement.GetString()!);
            }
        }

        if (
            !CanonicalInventory.TryCreate(
                schemas,
                principals,
                out CanonicalInventory? canonical,
                out string? defect
            )
        )
        {
            throw new RestoreManifestException($"The restore manifest inventory is not canonical: {defect}");
        }

        return canonical;
    }

    private static CanonicalInventory.Schema ReadSchema(JsonElement schemaElement, int schemaPosition)
    {
        if (schemaElement.ValueKind != JsonValueKind.Object)
        {
            throw new RestoreManifestException(
                $"The restore manifest inventory schema entry {schemaPosition} must be a JSON object."
            );
        }

        RestoreManifestShapeValidator.RejectUnknownMembers(
            schemaElement,
            _schemaMembers,
            $"The restore manifest inventory schema entry {schemaPosition}"
        );

        if (
            !TrustContract.TryGetString(schemaElement, "schemaName", out string? schemaName)
            || string.IsNullOrWhiteSpace(schemaName)
        )
        {
            throw new RestoreManifestException(
                $"The restore manifest inventory schema entry {schemaPosition} must declare a non-empty 'schemaName' string."
            );
        }

        if (
            !schemaElement.TryGetProperty("objects", out JsonElement objectsElement)
            || objectsElement.ValueKind == JsonValueKind.Null
        )
        {
            throw new RestoreManifestException(
                $"The restore manifest inventory schema entry {schemaPosition} is missing its 'objects' array (an empty schema declares an empty array)."
            );
        }

        if (objectsElement.ValueKind != JsonValueKind.Array)
        {
            throw new RestoreManifestException(
                $"The restore manifest inventory schema entry {schemaPosition} field 'objects' must be a JSON array, not a single object or scalar."
            );
        }

        List<CanonicalInventory.Entry> objects = [];
        int objectPosition = 0;
        foreach (JsonElement objectElement in objectsElement.EnumerateArray())
        {
            objectPosition++;
            objects.Add(ReadEntry(objectElement, schemaPosition, objectPosition));
        }

        return new CanonicalInventory.Schema(schemaName, objects);
    }

    private static CanonicalInventory.Entry ReadEntry(
        JsonElement objectElement,
        int schemaPosition,
        int objectPosition
    )
    {
        string subject =
            $"The restore manifest inventory object entry {objectPosition} of schema entry {schemaPosition}";
        if (objectElement.ValueKind != JsonValueKind.Object)
        {
            throw new RestoreManifestException($"{subject} must be a JSON object.");
        }

        RestoreManifestShapeValidator.RejectUnknownMembers(objectElement, _objectMembers, subject);

        if (
            !TrustContract.TryGetString(objectElement, "name", out string? name)
            || string.IsNullOrWhiteSpace(name)
            || !TrustContract.TryGetString(objectElement, "type", out string? type)
            || string.IsNullOrWhiteSpace(type)
        )
        {
            throw new RestoreManifestException(
                $"{subject} must declare both a non-empty 'name' and a non-empty 'type' string."
            );
        }

        return new CanonicalInventory.Entry(name, type);
    }

    private static void AppendStringLiteral(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (char character in value)
        {
            if (character == '"')
            {
                builder.Append("\\\"");
            }
            else if (character == '\\')
            {
                builder.Append("\\\\");
            }
            else if (character < ' ' || character > '~')
            {
                builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(character);
            }
        }

        builder.Append('"');
    }
}
