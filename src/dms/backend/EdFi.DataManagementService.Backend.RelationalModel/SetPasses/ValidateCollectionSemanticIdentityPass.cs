// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Naming;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Validates that every persisted multi-item scope has a non-empty compiled semantic identity before
/// downstream constraint, inventory, and plan consumers rely on it.
/// </summary>
public sealed class ValidateCollectionSemanticIdentityPass : IRelationalModelSetPass
{
    /// <summary>
    /// Executes semantic-identity validation across all concrete resources.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var entry in context.ConcreteResourcesInNameOrder)
        {
            ValidateResource(entry.RelationalModel, entry.ResourceKey.Resource);
        }
    }

    /// <summary>
    /// Validates one resource model.
    /// </summary>
    private static void ValidateResource(
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource
    )
    {
        foreach (var table in resourceModel.TablesInDependencyOrder)
        {
            if (
                !IsPersistedMultiItemScope(table)
                || table.IdentityMetadata.SemanticIdentityBindings.Count > 0
            )
            {
                continue;
            }

            var scopeLocalReferenceBindings = resourceModel
                .DocumentReferenceBindings.Where(binding => binding.Table.Equals(table.Table))
                .OrderBy(binding => binding.ReferenceObjectPath.Canonical, StringComparer.Ordinal)
                .ToArray();

            if (!SemanticIdentityCompilationPass.SupportsSemanticIdentityScope(table))
            {
                throw CreateUnsupportedScopeException(table, resource);
            }

            if (scopeLocalReferenceBindings.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Persisted multi-item scope '{table.JsonScope.Canonical}' on resource "
                        + $"'{FormatResource(resource)}' is not reference-backed and did not compile a "
                        + "non-empty semantic identity. Add an applicable arrayUniquenessConstraints "
                        + "entry for this scope."
                );
            }

            var qualifyingReferenceBindings = scopeLocalReferenceBindings
                .Where(binding =>
                    SemanticIdentityCompilationPass.TryCompileReferenceDerivedSemanticIdentity(
                        table,
                        binding,
                        out _
                    )
                )
                .ToArray();

            if (qualifyingReferenceBindings.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Persisted multi-item scope '{table.JsonScope.Canonical}' on resource "
                        + $"'{FormatResource(resource)}' is reference-backed but did not compile a "
                        + "non-empty semantic identity. Expected exactly one qualifying scope-local "
                        + $"document reference binding, found {qualifyingReferenceBindings.Length} from "
                        + $"{scopeLocalReferenceBindings.Length} scope-local binding(s): "
                        + $"{FormatReferenceBindings(scopeLocalReferenceBindings)}."
                );
            }

            throw new InvalidOperationException(
                $"Persisted multi-item scope '{table.JsonScope.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' remained without a non-empty semantic identity "
                    + "after considering arrayUniquenessConstraints and scope-local document reference "
                    + "bindings. This indicates an unsupported semantic identity scenario."
            );
        }
    }

    /// <summary>
    /// Returns whether the table models a persisted multi-item scope.
    /// </summary>
    private static bool IsPersistedMultiItemScope(DbTableModel table)
    {
        return table.Columns.Any(column =>
            column.Kind == ColumnKind.Ordinal
            && column.ColumnName.Equals(RelationalNameConventions.OrdinalColumnName)
        );
    }

    /// <summary>
    /// Creates an unsupported-scope diagnostic.
    /// </summary>
    private static Exception CreateUnsupportedScopeException(
        DbTableModel table,
        QualifiedResourceName resource
    )
    {
        return new InvalidOperationException(
            $"Unsupported persisted multi-item scope '{table.JsonScope.Canonical}' on resource "
                + $"'{FormatResource(resource)}' uses table kind "
                + $"'{table.IdentityMetadata.TableKind}'. Semantic identity validation only supports "
                + $"table kinds '{DbTableKind.Collection}' and '{DbTableKind.ExtensionCollection}' "
                + "after considering arrayUniquenessConstraints and scope-local document reference "
                + "bindings."
        );
    }

    /// <summary>
    /// Formats scope-local document reference bindings for diagnostics.
    /// </summary>
    private static string FormatReferenceBindings(IReadOnlyList<DocumentReferenceBinding> bindings)
    {
        return string.Join(", ", bindings.Select(binding => $"'{binding.ReferenceObjectPath.Canonical}'"));
    }
}
