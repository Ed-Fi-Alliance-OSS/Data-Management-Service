// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Maps a foreign-key constraint name surfaced by a failed DELETE to the concrete resource
/// whose table owns the constraint — i.e., the resource that is still holding a reference to
/// the row being deleted. Used to populate
/// <see cref="Core.External.Backend.DeleteResult.DeleteFailureReference"/> with the real
/// referencing resource name instead of a placeholder.
/// </summary>
/// <remarks>
/// The write-side <see cref="IRelationalWriteConstraintResolver"/> scans a single resource's
/// tables because the violated FK is always owned by the resource being written. On the delete
/// path the violated FK is owned by a *different* resource, so the lookup must walk the full
/// <see cref="DerivedRelationalModelSet.ConcreteResourcesInNameOrder"/>. A per-model-set index is
/// built on first use and held in a <see cref="ConditionalWeakTable{TKey,TValue}"/> so it is
/// reused for the lifetime of the mapping set and collected when the set is.
/// </remarks>
public interface IRelationalDeleteConstraintResolver
{
    /// <summary>
    /// Returns the referencing resource that owns <paramref name="constraintName"/>, or
    /// <c>null</c> when the name is not present in the compiled model (drift signal; caller
    /// should surface an empty-names conflict response and log a warning).
    /// </summary>
    QualifiedResourceName? TryResolveReferencingResource(
        DerivedRelationalModelSet modelSet,
        string constraintName
    );
}

internal sealed class RelationalDeleteConstraintResolver : IRelationalDeleteConstraintResolver
{
    private readonly ConditionalWeakTable<
        DerivedRelationalModelSet,
        IReadOnlyDictionary<string, QualifiedResourceName>
    > _indexByModelSet = new();

    public QualifiedResourceName? TryResolveReferencingResource(
        DerivedRelationalModelSet modelSet,
        string constraintName
    )
    {
        ArgumentNullException.ThrowIfNull(modelSet);

        if (string.IsNullOrWhiteSpace(constraintName))
        {
            return null;
        }

        var index = _indexByModelSet.GetValue(modelSet, BuildIndex);

        return index.TryGetValue(constraintName, out var resource) ? resource : null;
    }

    private static IReadOnlyDictionary<string, QualifiedResourceName> BuildIndex(
        DerivedRelationalModelSet modelSet
    )
    {
        Dictionary<string, QualifiedResourceName> byConstraintName = new(StringComparer.Ordinal);

        foreach (var concreteResource in modelSet.ConcreteResourcesInNameOrder)
        {
            var resource = concreteResource.ResourceKey.Resource;

            foreach (var table in concreteResource.RelationalModel.TablesInDependencyOrder)
            {
                foreach (
                    var constraintName in table
                        .Constraints.OfType<TableConstraint.ForeignKey>()
                        .Select(fk => fk.Name)
                )
                {
                    // First-writer-wins. Cross-resource duplicates are legitimate when multiple
                    // resources share a superclass table at the DDL layer (e.g. every concrete
                    // descriptor resource enumerates FK_Descriptor_Document pointing at the shared
                    // dms.Descriptor table). Because at most one physical FK corresponds to each
                    // emitted name, Postgres/MSSQL can only ever surface one owning resource per
                    // violation; keeping the first match preserves correct resolution for uniquely-
                    // owned FKs without crashing the whole model index on the shared-superclass case.
                    byConstraintName.TryAdd(constraintName, resource);
                }
            }
        }

        return byConstraintName;
    }
}
