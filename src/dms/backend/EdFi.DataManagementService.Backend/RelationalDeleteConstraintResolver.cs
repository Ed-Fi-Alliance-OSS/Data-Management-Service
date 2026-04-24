// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;
using Microsoft.Extensions.Logging;

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

internal sealed class RelationalDeleteConstraintResolver(ILogger<RelationalDeleteConstraintResolver> logger)
    : IRelationalDeleteConstraintResolver
{
    private readonly ILogger<RelationalDeleteConstraintResolver> _logger = logger;

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

    private IReadOnlyDictionary<string, QualifiedResourceName> BuildIndex(DerivedRelationalModelSet modelSet)
    {
        Dictionary<string, (QualifiedResourceName Resource, DbTableName OwningTable)> entries = new(
            StringComparer.Ordinal
        );

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
                    if (!entries.TryAdd(constraintName, (resource, table.Table)))
                    {
                        var existing = entries[constraintName];

                        // Shared-superclass case: the same physical FK is enumerated once per
                        // concrete resource that shares the superclass table (e.g. every concrete
                        // descriptor resource carries FK_Descriptor_Document on the shared
                        // dms.Descriptor table). First-writer-wins is correct here and stays silent.
                        if (existing.OwningTable == table.Table)
                        {
                            continue;
                        }

                        // Cross-table duplicate — a DDL-level canary. Names are supposed to be
                        // unique per physical FK (see ConstraintNaming + SqlIdentifierShortening),
                        // so two different owning tables sharing one constraint name means either
                        // dialect-limit truncation collapsed two names or the naming scheme
                        // produced a collision. The driver can only surface one name per DELETE
                        // violation, so first-writer-wins still applies, but a Warning lets ops
                        // observe that 409 responses on {ConstraintName} may cite the wrong
                        // referencing resource.
                        _logger.LogWarning(
                            "Delete-constraint index detected a duplicate foreign key name across "
                                + "different owning tables: constraint '{ConstraintName}' is owned by "
                                + "resource '{FirstResource}' on table '{FirstTable}' and resource "
                                + "'{SecondResource}' on table '{SecondTable}'. Keeping the first "
                                + "entry; DELETE 409 responses surfacing this constraint may cite "
                                + "the wrong referencing resource.",
                            constraintName,
                            existing.Resource,
                            existing.OwningTable,
                            resource,
                            table.Table
                        );
                    }
                }
            }
        }

        return entries.ToDictionary(e => e.Key, e => e.Value.Resource, StringComparer.Ordinal);
    }
}
