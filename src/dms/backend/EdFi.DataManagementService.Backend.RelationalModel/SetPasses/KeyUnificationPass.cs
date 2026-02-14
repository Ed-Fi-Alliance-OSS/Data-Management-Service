// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.Constraints.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Applies ApiSchema equality constraints as table-local key-unification classes.
/// </summary>
public sealed class KeyUnificationPass : IRelationalModelSetPass
{
    /// <summary>
    /// Executes key unification for all concrete resources and extensions.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var baseResourcesByName = BuildBaseResourceLookup(
            context.ConcreteResourcesInNameOrder,
            static (index, model) => new BaseResourceEntry(index, model)
        );
        var resourceIndexByKey = context
            .ConcreteResourcesInNameOrder.Select(
                (resource, index) => new { resource.ResourceKey.Resource, Index = index }
            )
            .ToDictionary(entry => entry.Resource, entry => entry.Index);
        Dictionary<int, List<EqualityConstraintInput>> constraintsByResourceIndex = [];

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );
            var equalityConstraints = ReadEqualityConstraints(resourceContext.ResourceSchema);

            if (equalityConstraints.Count == 0)
            {
                continue;
            }

            var concreteResourceIndex = ResolveConcreteResourceIndex(
                resourceContext,
                resource,
                baseResourcesByName,
                resourceIndexByKey
            );

            if (!constraintsByResourceIndex.TryGetValue(concreteResourceIndex, out var combinedConstraints))
            {
                combinedConstraints = [];
                constraintsByResourceIndex.Add(concreteResourceIndex, combinedConstraints);
            }

            combinedConstraints.AddRange(equalityConstraints);
        }

        foreach (var entry in constraintsByResourceIndex.OrderBy(item => item.Key))
        {
            var concreteResource = context.ConcreteResourcesInNameOrder[entry.Key];
            var updatedModel = ApplyKeyUnification(
                concreteResource.RelationalModel,
                concreteResource.ResourceKey.Resource,
                entry.Value
            );

            context.ConcreteResourcesInNameOrder[entry.Key] = concreteResource with
            {
                RelationalModel = updatedModel,
            };
        }
    }

    /// <summary>
    /// Resolves the concrete-resource index for a schema context (including extension-to-base mapping).
    /// </summary>
    private static int ResolveConcreteResourceIndex(
        ConcreteResourceSchemaContext resourceContext,
        QualifiedResourceName resource,
        IReadOnlyDictionary<string, List<BaseResourceEntry>> baseResourcesByName,
        IReadOnlyDictionary<QualifiedResourceName, int> resourceIndexByKey
    )
    {
        if (IsResourceExtension(resourceContext))
        {
            var baseResource = ResolveBaseResourceForExtension(
                resourceContext.ResourceName,
                resource,
                baseResourcesByName,
                static entry => entry.Model.ResourceKey.Resource
            );

            return baseResource.Index;
        }

        if (resourceIndexByKey.TryGetValue(resource, out var index))
        {
            return index;
        }

        throw new InvalidOperationException(
            $"Concrete resource '{FormatResource(resource)}' was not found for key unification."
        );
    }

    /// <summary>
    /// Parses equality constraints from a resource schema.
    /// </summary>
    private static IReadOnlyList<EqualityConstraintInput> ReadEqualityConstraints(JsonObject resourceSchema)
    {
        if (!resourceSchema.TryGetPropertyValue("equalityConstraints", out var equalityConstraintsNode))
        {
            return Array.Empty<EqualityConstraintInput>();
        }

        if (equalityConstraintsNode is null)
        {
            return Array.Empty<EqualityConstraintInput>();
        }

        if (equalityConstraintsNode is not JsonArray equalityConstraintsArray)
        {
            throw new InvalidOperationException(
                "Expected equalityConstraints to be an array, invalid ApiSchema."
            );
        }

        List<EqualityConstraintInput> constraints = new(equalityConstraintsArray.Count);

        foreach (var constraintNode in equalityConstraintsArray)
        {
            if (constraintNode is null)
            {
                throw new InvalidOperationException(
                    "Expected equalityConstraints entries to be non-null, invalid ApiSchema."
                );
            }

            if (constraintNode is not JsonObject constraintObject)
            {
                throw new InvalidOperationException(
                    "Expected equalityConstraints entries to be objects, invalid ApiSchema."
                );
            }

            var sourcePath = JsonPathExpressionCompiler.Compile(
                RequireString(constraintObject, "sourceJsonPath")
            );
            var targetPath = JsonPathExpressionCompiler.Compile(
                RequireString(constraintObject, "targetJsonPath")
            );

            constraints.Add(new EqualityConstraintInput(sourcePath, targetPath));
        }

        return constraints;
    }

    /// <summary>
    /// Applies key unification to one concrete resource model.
    /// </summary>
    private static RelationalResourceModel ApplyKeyUnification(
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource,
        IReadOnlyList<EqualityConstraintInput> constraints
    )
    {
        if (constraints.Count == 0)
        {
            return resourceModel;
        }

        var tables = resourceModel.TablesInDependencyOrder.ToArray();
        var bindingsByPath = BuildSourcePathBindingsByTable(tables);
        Dictionary<int, List<(TableBoundColumn Left, TableBoundColumn Right)>> appliedEdgesByTable = [];

        foreach (var constraint in constraints)
        {
            var left = ResolveEndpointBinding(bindingsByPath, constraint.SourcePath, resource);
            var right = ResolveEndpointBinding(bindingsByPath, constraint.TargetPath, resource);

            if (left.TableIndex != right.TableIndex)
            {
                continue;
            }

            if (left.Column.ColumnName.Equals(right.Column.ColumnName))
            {
                continue;
            }

            if (!appliedEdgesByTable.TryGetValue(left.TableIndex, out var edges))
            {
                edges = [];
                appliedEdgesByTable[left.TableIndex] = edges;
            }

            edges.Add((left, right));
        }

        if (appliedEdgesByTable.Count == 0)
        {
            return resourceModel;
        }

        var changed = false;

        foreach (var tableEntry in appliedEdgesByTable)
        {
            var table = tables[tableEntry.Key];
            var updatedTable = ApplyKeyUnificationToTable(
                table,
                resource,
                resourceModel.DocumentReferenceBindings,
                tableEntry.Value
            );

            if (ReferenceEquals(updatedTable, table))
            {
                continue;
            }

            tables[tableEntry.Key] = updatedTable;
            changed = true;
        }

        if (!changed)
        {
            return resourceModel;
        }

        var updatedRoot = tables.Single(table => table.Table.Equals(resourceModel.Root.Table));

        return resourceModel with
        {
            Root = updatedRoot,
            TablesInDependencyOrder = tables,
        };
    }

    /// <summary>
    /// Builds a lookup from source JSON path to all bound columns.
    /// </summary>
    private static IReadOnlyDictionary<string, List<TableBoundColumn>> BuildSourcePathBindingsByTable(
        IReadOnlyList<DbTableModel> tables
    )
    {
        Dictionary<string, List<TableBoundColumn>> lookup = new(StringComparer.Ordinal);

        for (var tableIndex = 0; tableIndex < tables.Count; tableIndex++)
        {
            var table = tables[tableIndex];

            foreach (var column in table.Columns)
            {
                if (column.SourceJsonPath is not { } sourcePath)
                {
                    continue;
                }

                if (!lookup.TryGetValue(sourcePath.Canonical, out var boundColumns))
                {
                    boundColumns = [];
                    lookup[sourcePath.Canonical] = boundColumns;
                }

                boundColumns.Add(new TableBoundColumn(tableIndex, table, column));
            }
        }

        return lookup;
    }

    /// <summary>
    /// Resolves one source-path endpoint to a unique physical table/column binding.
    /// </summary>
    private static TableBoundColumn ResolveEndpointBinding(
        IReadOnlyDictionary<string, List<TableBoundColumn>> bindingsByPath,
        JsonPathExpression endpointPath,
        QualifiedResourceName resource
    )
    {
        if (!bindingsByPath.TryGetValue(endpointPath.Canonical, out var candidates))
        {
            throw new InvalidOperationException(
                $"Equality constraint endpoint '{endpointPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' was not bound to any column."
            );
        }

        var distinctCandidates = candidates
            .GroupBy(candidate =>
                (
                    candidate.TableIndex,
                    candidate.Table.Table,
                    candidate.Column.ColumnName,
                    candidate.Table.JsonScope.Canonical
                )
            )
            .Select(group => group.First())
            .ToArray();

        if (distinctCandidates.Length == 1)
        {
            return distinctCandidates[0];
        }

        var details = string.Join(
            ", ",
            distinctCandidates
                .OrderBy(candidate => candidate.Table.Table.Schema.Value, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Table.Table.Name, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Table.JsonScope.Canonical, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Column.ColumnName.Value, StringComparer.Ordinal)
                .Select(candidate =>
                    $"{candidate.Table.Table.Schema.Value}.{candidate.Table.Table.Name}"
                    + $"[{candidate.Table.JsonScope.Canonical}].{candidate.Column.ColumnName.Value}"
                )
        );

        throw new InvalidOperationException(
            $"Equality constraint endpoint '{endpointPath.Canonical}' on resource "
                + $"'{FormatResource(resource)}' resolved to multiple distinct bindings: {details}."
        );
    }

    /// <summary>
    /// Applies key-unification class derivation to a single table.
    /// </summary>
    private static DbTableModel ApplyKeyUnificationToTable(
        DbTableModel table,
        QualifiedResourceName resource,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings,
        IReadOnlyList<(TableBoundColumn Left, TableBoundColumn Right)> appliedEdges
    )
    {
        var componentColumnNames = BuildConnectedComponents(appliedEdges);

        if (componentColumnNames.Count == 0)
        {
            return table;
        }

        var localReferenceBindings = documentReferenceBindings
            .Where(binding => binding.Table.Equals(table.Table))
            .ToArray();
        var referenceBindingByIdentityPath = BuildReferenceIdentityBindings(localReferenceBindings, resource);
        List<DbColumnModel> updatedColumns = new(table.Columns);
        Dictionary<string, int> columnIndexByName = updatedColumns
            .Select((column, index) => new { column.ColumnName.Value, Index = index })
            .ToDictionary(entry => entry.Value, entry => entry.Index, StringComparer.Ordinal);
        HashSet<string> existingColumnNames = updatedColumns
            .Select(column => column.ColumnName.Value)
            .ToHashSet(StringComparer.Ordinal);
        List<KeyUnificationClass> keyUnificationClasses = [];

        foreach (
            var component in componentColumnNames.OrderBy(
                group => string.Join("|", group.OrderBy(name => name, StringComparer.Ordinal)),
                StringComparer.Ordinal
            )
        )
        {
            var memberColumns = component
                .Select(columnName =>
                {
                    if (!columnIndexByName.TryGetValue(columnName, out var columnIndex))
                    {
                        throw new InvalidOperationException(
                            $"Key-unification member column '{columnName}' was not found on table "
                                + $"'{table.Table}' for resource '{FormatResource(resource)}'."
                        );
                    }

                    return updatedColumns[columnIndex];
                })
                .DistinctBy(column => column.ColumnName)
                .OrderBy(
                    column => GetRequiredSourcePath(column, resource, table).Canonical,
                    StringComparer.Ordinal
                )
                .ThenBy(column => column.ColumnName.Value, StringComparer.Ordinal)
                .ToArray();

            if (memberColumns.Length < 2)
            {
                continue;
            }

            ValidateUnificationMembers(memberColumns, resource, table);

            var baseName = ResolveCanonicalBaseName(
                memberColumns,
                table,
                referenceBindingByIdentityPath,
                resource
            );
            var canonicalColumnName = AllocateCanonicalColumnName(
                baseName,
                memberColumns,
                existingColumnNames
            );
            var firstMember = memberColumns[0];
            var canonicalColumn = new DbColumnModel(
                canonicalColumnName,
                firstMember.Kind,
                firstMember.ScalarType,
                memberColumns.All(column => column.IsNullable),
                SourceJsonPath: null,
                firstMember.TargetResource
            );

            updatedColumns.Add(canonicalColumn);
            columnIndexByName[canonicalColumnName.Value] = updatedColumns.Count - 1;
            existingColumnNames.Add(canonicalColumnName.Value);

            foreach (var memberColumn in memberColumns)
            {
                var sourcePath = GetRequiredSourcePath(memberColumn, resource, table);
                var presenceColumn = ResolvePresenceColumn(
                    memberColumn,
                    sourcePath,
                    referenceBindingByIdentityPath,
                    existingColumnNames,
                    updatedColumns,
                    columnIndexByName
                );
                var memberColumnIndex = columnIndexByName[memberColumn.ColumnName.Value];
                var updatedMemberColumn = updatedColumns[memberColumnIndex] with
                {
                    Storage = new ColumnStorage.UnifiedAlias(canonicalColumnName, presenceColumn),
                };

                updatedColumns[memberColumnIndex] = updatedMemberColumn;
            }

            keyUnificationClasses.Add(
                new KeyUnificationClass(
                    canonicalColumnName,
                    memberColumns.Select(column => column.ColumnName).ToArray()
                )
            );
        }

        if (keyUnificationClasses.Count == 0)
        {
            return table;
        }

        return table with
        {
            Columns = updatedColumns.ToArray(),
            KeyUnificationClasses = keyUnificationClasses
                .OrderBy(@class => @class.CanonicalColumn.Value, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    /// <summary>
    /// Builds connected components for table-local equality edges.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> BuildConnectedComponents(
        IReadOnlyList<(TableBoundColumn Left, TableBoundColumn Right)> edges
    )
    {
        Dictionary<string, string> parent = new(StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            var left = edge.Left.Column.ColumnName.Value;
            var right = edge.Right.Column.ColumnName.Value;

            EnsureSet(parent, left);
            EnsureSet(parent, right);
            Union(parent, left, right);
        }

        return parent
            .Keys.GroupBy(columnName => Find(parent, columnName), StringComparer.Ordinal)
            .Select(group =>
                (IReadOnlyList<string>)
                    group.OrderBy(columnName => columnName, StringComparer.Ordinal).ToArray()
            )
            .ToArray();
    }

    /// <summary>
    /// Adds a set entry when a node is first encountered.
    /// </summary>
    private static void EnsureSet(IDictionary<string, string> parent, string key)
    {
        if (!parent.ContainsKey(key))
        {
            parent[key] = key;
        }
    }

    /// <summary>
    /// Finds the representative for a disjoint-set node.
    /// </summary>
    private static string Find(IDictionary<string, string> parent, string key)
    {
        var current = parent[key];

        while (!string.Equals(current, parent[current], StringComparison.Ordinal))
        {
            current = parent[current];
        }

        var root = current;
        current = key;

        while (!string.Equals(current, parent[current], StringComparison.Ordinal))
        {
            var next = parent[current];
            parent[current] = root;
            current = next;
        }

        return root;
    }

    /// <summary>
    /// Unions two disjoint-set nodes.
    /// </summary>
    private static void Union(IDictionary<string, string> parent, string left, string right)
    {
        var leftRoot = Find(parent, left);
        var rightRoot = Find(parent, right);

        if (string.Equals(leftRoot, rightRoot, StringComparison.Ordinal))
        {
            return;
        }

        var winner = string.CompareOrdinal(leftRoot, rightRoot) <= 0 ? leftRoot : rightRoot;
        var loser = string.Equals(winner, leftRoot, StringComparison.Ordinal) ? rightRoot : leftRoot;
        parent[loser] = winner;
    }

    /// <summary>
    /// Validates that all members in a class have compatible kinds and types.
    /// </summary>
    private static void ValidateUnificationMembers(
        IReadOnlyList<DbColumnModel> members,
        QualifiedResourceName resource,
        DbTableModel table
    )
    {
        var first = members[0];

        if (first.Kind is not ColumnKind.Scalar and not ColumnKind.DescriptorFk)
        {
            throw new InvalidOperationException(
                $"Key unification only supports scalar/descriptor columns, but '{first.ColumnName.Value}' "
                    + $"on resource '{FormatResource(resource)}' table '{table.Table}' has kind "
                    + $"'{first.Kind}'."
            );
        }

        foreach (var member in members)
        {
            if (member.Kind is not ColumnKind.Scalar and not ColumnKind.DescriptorFk)
            {
                throw new InvalidOperationException(
                    $"Key unification only supports scalar/descriptor columns, but '{member.ColumnName.Value}' "
                        + $"on resource '{FormatResource(resource)}' table '{table.Table}' has kind "
                        + $"'{member.Kind}'."
                );
            }

            if (member.Kind != first.Kind)
            {
                throw new InvalidOperationException(
                    $"Key unification class on resource '{FormatResource(resource)}' table '{table.Table}' "
                        + "cannot mix scalar and descriptor members."
                );
            }

            if (member.Kind == ColumnKind.Scalar && !Equals(member.ScalarType, first.ScalarType))
            {
                throw new InvalidOperationException(
                    $"Key unification scalar type mismatch on resource '{FormatResource(resource)}' table "
                        + $"'{table.Table}': '{member.ColumnName.Value}' does not match "
                        + $"'{first.ColumnName.Value}'."
                );
            }

            if (
                member.Kind == ColumnKind.DescriptorFk
                && !Equals(member.TargetResource, first.TargetResource)
            )
            {
                throw new InvalidOperationException(
                    $"Key unification descriptor target mismatch on resource '{FormatResource(resource)}' "
                        + $"table '{table.Table}': '{member.ColumnName.Value}' does not match "
                        + $"'{first.ColumnName.Value}'."
                );
            }
        }
    }

    /// <summary>
    /// Resolves the canonical base token from member source paths.
    /// </summary>
    private static string ResolveCanonicalBaseName(
        IReadOnlyList<DbColumnModel> members,
        DbTableModel table,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingByIdentityPath,
        QualifiedResourceName resource
    )
    {
        var memberTokens = members
            .Select(member => BuildMemberBaseToken(member, table, referenceBindingByIdentityPath, resource))
            .ToArray();

        var firstToken = memberTokens[0];

        return memberTokens.All(token => string.Equals(token, firstToken, StringComparison.Ordinal))
            ? firstToken
            : firstToken;
    }

    /// <summary>
    /// Derives one member base token using JsonPath semantics, independent of physical column names.
    /// </summary>
    private static string BuildMemberBaseToken(
        DbColumnModel member,
        DbTableModel table,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingByIdentityPath,
        QualifiedResourceName resource
    )
    {
        var sourcePath = GetRequiredSourcePath(member, resource, table);
        var prefixSegments = referenceBindingByIdentityPath.TryGetValue(sourcePath.Canonical, out var binding)
            ? binding.ReferenceObjectPath.Segments
            : table.JsonScope.Segments;

        if (!IsPrefixOf(prefixSegments, sourcePath.Segments))
        {
            throw new InvalidOperationException(
                $"Key-unification member path '{sourcePath.Canonical}' does not match expected prefix on "
                    + $"resource '{FormatResource(resource)}' table '{table.Table}'."
            );
        }

        var relativeSegments = sourcePath.Segments.Skip(prefixSegments.Count).ToArray();

        if (relativeSegments.Any(segment => segment is JsonPathSegment.AnyArrayElement))
        {
            throw new InvalidOperationException(
                $"Key-unification member path '{sourcePath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' contains unsupported wildcard segments."
            );
        }

        var relativeProperties = relativeSegments.OfType<JsonPathSegment.Property>().ToArray();

        if (relativeProperties.Length > 0)
        {
            return string.Concat(
                relativeProperties.Select(property => RelationalNameConventions.ToPascalCase(property.Name))
            );
        }

        for (var index = prefixSegments.Count - 1; index >= 0; index--)
        {
            if (prefixSegments[index] is not JsonPathSegment.Property property)
            {
                continue;
            }

            var singular = RelationalNameConventions.SingularizeCollectionSegment(property.Name);
            return RelationalNameConventions.ToPascalCase(singular);
        }

        throw new InvalidOperationException(
            $"Unable to derive key-unification base token for path '{sourcePath.Canonical}' on resource "
                + $"'{FormatResource(resource)}'."
        );
    }

    /// <summary>
    /// Allocates a unique canonical column name for a unification class.
    /// </summary>
    private static DbColumnName AllocateCanonicalColumnName(
        string baseName,
        IReadOnlyList<DbColumnModel> members,
        ISet<string> existingColumnNames
    )
    {
        var firstMember = members[0];
        var suffix = firstMember.Kind == ColumnKind.DescriptorFk ? "_Unified_DescriptorId" : "_Unified";
        var initialName = $"{baseName}{suffix}";

        if (existingColumnNames.Add(initialName))
        {
            return new DbColumnName(initialName);
        }

        var signature = string.Join(
            "\n",
            members
                .Select(member => member.SourceJsonPath!.Value.Canonical)
                .OrderBy(path => path, StringComparer.Ordinal)
        );
        var hash = ComputeHash8($"key-unification-canonical-name:v1\n{signature}");
        var disambiguatedName = $"{baseName}_U{hash}{suffix}";

        if (existingColumnNames.Add(disambiguatedName))
        {
            return new DbColumnName(disambiguatedName);
        }

        for (var suffixIndex = 2; ; suffixIndex++)
        {
            var fallbackName =
                firstMember.Kind == ColumnKind.DescriptorFk
                    ? $"{baseName}_U{hash}_{suffixIndex}_Unified_DescriptorId"
                    : $"{baseName}_U{hash}_{suffixIndex}_Unified";

            if (!existingColumnNames.Add(fallbackName))
            {
                continue;
            }

            return new DbColumnName(fallbackName);
        }
    }

    /// <summary>
    /// Resolves reference or synthetic presence gating for one member alias.
    /// </summary>
    private static DbColumnName? ResolvePresenceColumn(
        DbColumnModel memberColumn,
        JsonPathExpression sourcePath,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingByIdentityPath,
        ISet<string> existingColumnNames,
        ICollection<DbColumnModel> updatedColumns,
        IDictionary<string, int> columnIndexByName
    )
    {
        if (referenceBindingByIdentityPath.TryGetValue(sourcePath.Canonical, out var binding))
        {
            return binding.FkColumn;
        }

        if (!memberColumn.IsNullable)
        {
            return null;
        }

        var presenceColumnName = AllocatePresenceColumnName(
            memberColumn.ColumnName,
            sourcePath.Canonical,
            existingColumnNames
        );
        var presenceColumn = new DbColumnModel(
            presenceColumnName,
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Boolean),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: null
        );

        updatedColumns.Add(presenceColumn);
        columnIndexByName[presenceColumnName.Value] = updatedColumns.Count - 1;
        return presenceColumnName;
    }

    /// <summary>
    /// Allocates a deterministic synthetic presence-column name for an optional non-reference member.
    /// </summary>
    private static DbColumnName AllocatePresenceColumnName(
        DbColumnName memberColumnName,
        string sourcePathCanonical,
        ISet<string> existingColumnNames
    )
    {
        var initialName = $"{memberColumnName.Value}_Present";

        if (existingColumnNames.Add(initialName))
        {
            return new DbColumnName(initialName);
        }

        var hash = ComputeHash8($"key-unification-presence-name:v1\n{sourcePathCanonical}");
        var disambiguatedName = $"{memberColumnName.Value}_U{hash}_Present";

        if (existingColumnNames.Add(disambiguatedName))
        {
            return new DbColumnName(disambiguatedName);
        }

        for (var suffixIndex = 2; ; suffixIndex++)
        {
            var fallbackName = $"{memberColumnName.Value}_U{hash}_{suffixIndex}_Present";

            if (!existingColumnNames.Add(fallbackName))
            {
                continue;
            }

            return new DbColumnName(fallbackName);
        }
    }

    /// <summary>
    /// Returns a required source path for a key-unification member.
    /// </summary>
    private static JsonPathExpression GetRequiredSourcePath(
        DbColumnModel member,
        QualifiedResourceName resource,
        DbTableModel table
    )
    {
        if (member.SourceJsonPath is { } sourcePath)
        {
            return sourcePath;
        }

        throw new InvalidOperationException(
            $"Key-unification member '{member.ColumnName.Value}' on resource '{FormatResource(resource)}' "
                + $"table '{table.Table}' must have SourceJsonPath."
        );
    }

    /// <summary>
    /// Computes the first 8 hexadecimal characters of SHA-256 for deterministic naming.
    /// </summary>
    private static string ComputeHash8(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    /// <summary>
    /// Base resource entry used when resolving extension schemas.
    /// </summary>
    private sealed record BaseResourceEntry(int Index, ConcreteResourceModel Model);

    /// <summary>
    /// Parsed equality-constraint endpoints.
    /// </summary>
    private sealed record EqualityConstraintInput(
        JsonPathExpression SourcePath,
        JsonPathExpression TargetPath
    );

    /// <summary>
    /// One source-path column binding with table context.
    /// </summary>
    private sealed record TableBoundColumn(int TableIndex, DbTableModel Table, DbColumnModel Column);
}
