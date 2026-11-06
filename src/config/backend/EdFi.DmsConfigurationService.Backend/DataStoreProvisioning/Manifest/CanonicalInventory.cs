// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;

/// <summary>
/// The canonical inventory model of the delivered restore contract (F-P8, <c>ConvertTo-CanonicalInventory</c>):
/// schemas sorted ordinally by name, each schema's objects sorted ordinally by lowercased type then name, and
/// principals sorted ordinally, with duplicate schema names, duplicate (type, name) pairs within a schema, and
/// duplicate principals refused. An instance is built from a restore manifest's <c>inventory</c> member or,
/// in later steps, from a live database's catalog, and two inventories are compared only through
/// <see cref="CanonicalInventoryHash"/>; this type defines no structural equality.
/// </summary>
public sealed class CanonicalInventory
{
    private CanonicalInventory(IReadOnlyList<Schema> schemas, IReadOnlyList<string> principals)
    {
        Schemas = schemas;
        Principals = principals;
    }

    /// <summary>The schemas in ordinal name order.</summary>
    public IReadOnlyList<Schema> Schemas { get; }

    /// <summary>The principals in ordinal order; empty when the inventory scope excludes principals.</summary>
    public IReadOnlyList<string> Principals { get; }

    /// <summary>One catalog object. In a canonical inventory <see cref="Type"/> is lowercase.</summary>
    public sealed record Entry(string Name, string Type);

    /// <summary>One schema and its objects. In a canonical inventory the objects are sorted by type then name.</summary>
    public sealed class Schema
    {
        public Schema(string schemaName, IReadOnlyList<Entry> objects)
        {
            ArgumentNullException.ThrowIfNull(schemaName);
            ArgumentNullException.ThrowIfNull(objects);
            SchemaName = schemaName;
            Objects = objects;
        }

        public string SchemaName { get; }

        public IReadOnlyList<Entry> Objects { get; }
    }

    /// <summary>Normalizes raw schemas and principals, in any order and type casing, into the canonical form; throws <see cref="ArgumentException"/> on a defect.</summary>
    public static CanonicalInventory Create(IEnumerable<Schema> schemas, IEnumerable<string> principals)
    {
        if (!TryCreate(schemas, principals, out CanonicalInventory? inventory, out string? defect))
        {
            throw new ArgumentException($"The inventory is not canonical: {defect}");
        }

        return inventory;
    }

    /// <summary>
    /// Normalizes raw schemas and principals into the canonical form. False, with a value-free
    /// <paramref name="defect"/> naming the entry position, when a schema name, object name, object type, or
    /// principal is empty, or when a schema name, a (type, name) pair within a schema, or a principal repeats.
    /// </summary>
    public static bool TryCreate(
        IEnumerable<Schema> schemas,
        IEnumerable<string> principals,
        [NotNullWhen(true)] out CanonicalInventory? inventory,
        [NotNullWhen(false)] out string? defect
    )
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(principals);
        inventory = null;
        defect = null;

        List<Schema> canonicalSchemas = [];
        HashSet<string> seenSchemaNames = new(StringComparer.Ordinal);
        int schemaPosition = 0;
        foreach (Schema schema in schemas)
        {
            schemaPosition++;
            if (string.IsNullOrWhiteSpace(schema.SchemaName))
            {
                defect = $"schema entry {schemaPosition} has an empty name.";
                return false;
            }

            if (!seenSchemaNames.Add(schema.SchemaName))
            {
                defect = $"schema entry {schemaPosition} repeats an earlier schema name.";
                return false;
            }

            if (!TryNormalizeObjects(schema.Objects, schemaPosition, out List<Entry>? objects, out defect))
            {
                return false;
            }

            canonicalSchemas.Add(new Schema(schema.SchemaName, objects));
        }

        canonicalSchemas.Sort((left, right) => string.CompareOrdinal(left.SchemaName, right.SchemaName));

        List<string> canonicalPrincipals = [];
        HashSet<string> seenPrincipals = new(StringComparer.Ordinal);
        int principalPosition = 0;
        foreach (string principal in principals)
        {
            principalPosition++;
            if (string.IsNullOrWhiteSpace(principal))
            {
                defect = $"principal entry {principalPosition} is empty.";
                return false;
            }

            if (!seenPrincipals.Add(principal))
            {
                defect = $"principal entry {principalPosition} repeats an earlier principal.";
                return false;
            }

            canonicalPrincipals.Add(principal);
        }

        canonicalPrincipals.Sort(StringComparer.Ordinal);

        inventory = new CanonicalInventory(canonicalSchemas, canonicalPrincipals);
        return true;
    }

    private static bool TryNormalizeObjects(
        IReadOnlyList<Entry> entries,
        int schemaPosition,
        [NotNullWhen(true)] out List<Entry>? normalized,
        [NotNullWhen(false)] out string? defect
    )
    {
        normalized = null;
        defect = null;
        List<Entry> result = [];
        HashSet<(string Type, string Name)> seen = [];
        for (int index = 0; index < entries.Count; index++)
        {
            Entry entry = entries[index];
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Type))
            {
                defect =
                    $"object entry {index + 1} of schema entry {schemaPosition} has an empty name or type.";
                return false;
            }

            string type = entry.Type.ToLowerInvariant();
            if (!seen.Add((type, entry.Name)))
            {
                defect =
                    $"object entry {index + 1} of schema entry {schemaPosition} repeats an earlier object of the same type and name.";
                return false;
            }

            result.Add(new Entry(entry.Name, type));
        }

        result.Sort(
            (left, right) =>
            {
                int byType = string.CompareOrdinal(left.Type, right.Type);
                return byType != 0 ? byType : string.CompareOrdinal(left.Name, right.Name);
            }
        );

        normalized = result;
        return true;
    }
}
