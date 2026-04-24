// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.Constraints.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Compiles persisted collection semantic identity into table metadata before downstream consumers derive
/// constraints, indexes, manifests, or triggers from it.
/// </summary>
public sealed class SemanticIdentityCompilationPass : IRelationalModelSetPass
{
    /// <summary>
    /// Executes semantic-identity compilation across all concrete resources and resource extensions.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ApplyArrayUniquenessSemanticIdentity(context);
        ApplyReferenceFallbackSemanticIdentity(context);
    }

    /// <summary>
    /// Applies semantic identity compiled from array uniqueness metadata across all contributing base and
    /// extension resource contexts before any reference-derived fallback is considered.
    /// </summary>
    private static void ApplyArrayUniquenessSemanticIdentity(RelationalModelSetBuilderContext context)
    {
        SetPassHelpers.ExecuteContributingResourceMutationPass(
            context,
            "semantic identity compilation",
            static builderContext => builderContext.ArrayUniquenessConstraints.Count > 0,
            static (mutation, resourceModel, builderContext, resource) =>
            {
                ArrayUniquenessConstraintPass.ApplyArrayUniquenessConstraintsForResource(
                    mutation,
                    resourceModel,
                    builderContext,
                    resource,
                    emitUniqueConstraints: false
                );
            }
        );
    }

    /// <summary>
    /// Applies reference-derived fallback semantic identity only after all applicable array uniqueness metadata
    /// has been compiled across contributing base and extension resource contexts.
    /// </summary>
    private static void ApplyReferenceFallbackSemanticIdentity(RelationalModelSetBuilderContext context)
    {
        SetPassHelpers.ExecuteContributingResourceMutationPass(
            context,
            "semantic identity compilation",
            static builderContext => builderContext.DocumentReferenceMappings.Count > 0,
            static (mutation, resourceModel, _, resource) =>
            {
                CompileReferenceDerivedSemanticIdentityForResource(mutation, resourceModel, resource);
            }
        );
    }

    /// <summary>
    /// Compiles semantic identity from a single qualifying scope-local document reference binding when a
    /// persisted collection scope still has no compiled identity after array uniqueness processing.
    /// </summary>
    private static void CompileReferenceDerivedSemanticIdentityForResource(
        ResourceMutation mutation,
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource
    )
    {
        foreach (var table in resourceModel.TablesInDependencyOrder)
        {
            if (!SupportsSemanticIdentityScope(table))
            {
                continue;
            }

            var tableAccumulator = mutation.GetTableAccumulator(table, resource);

            if (tableAccumulator.IdentityMetadata.SemanticIdentityBindings.Count > 0)
            {
                continue;
            }

            var candidateBindings = resourceModel
                .DocumentReferenceBindings.Select(binding =>
                    TryCompileReferenceDerivedSemanticIdentity(table, binding, out var bindings)
                        ? new ReferenceSemanticIdentityCandidate(binding, bindings)
                        : null
                )
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                .OrderBy(candidate => candidate.Binding.ReferenceObjectPath.Canonical, StringComparer.Ordinal)
                .ToArray();

            if (candidateBindings.Length != 1)
            {
                continue;
            }

            ArrayUniquenessConstraintPass.ApplySemanticIdentityBindings(
                mutation,
                table,
                candidateBindings[0].Bindings,
                CollectionSemanticIdentitySource.ReferenceFallback,
                resource
            );
        }
    }

    /// <summary>
    /// Returns whether a table can consume reference-derived semantic identity.
    /// </summary>
    internal static bool SupportsSemanticIdentityScope(DbTableModel table)
    {
        var supportsTableKind =
            table.IdentityMetadata.TableKind is DbTableKind.Collection or DbTableKind.ExtensionCollection;

        return supportsTableKind && SetPassHelpers.HasPersistedScopeOrdinalColumn(table);
    }

    /// <summary>
    /// Attempts to compile semantic identity for a table from one scope-local document reference binding.
    /// Invalid scope-relative identity paths fail fast with the shared semantic-identity normalization
    /// diagnostic.
    /// </summary>
    internal static bool TryCompileReferenceDerivedSemanticIdentity(
        DbTableModel table,
        DocumentReferenceBinding binding,
        out IReadOnlyList<CollectionSemanticIdentityBinding> bindings
    )
    {
        bindings = [];

        if (!binding.Table.Equals(table.Table) || binding.IdentityBindings.Count == 0)
        {
            return false;
        }

        List<CollectionSemanticIdentityBinding> compiledBindings = new(binding.IdentityBindings.Count);

        foreach (var identityBinding in binding.IdentityBindings)
        {
            compiledBindings.Add(
                new CollectionSemanticIdentityBinding(
                    DeriveScopeRelativeSemanticIdentityPath(
                        table.JsonScope,
                        identityBinding.ReferenceJsonPath
                    ),
                    identityBinding.Column
                )
            );
        }

        bindings = compiledBindings.ToArray();
        return true;
    }

    /// <summary>
    /// Captures one qualifying reference binding and its compiled semantic-identity bindings.
    /// </summary>
    private sealed record ReferenceSemanticIdentityCandidate(
        DocumentReferenceBinding Binding,
        IReadOnlyList<CollectionSemanticIdentityBinding> Bindings
    );
}
