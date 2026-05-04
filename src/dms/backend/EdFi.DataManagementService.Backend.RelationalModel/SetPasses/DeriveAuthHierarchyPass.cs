// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Build;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Derives the EducationOrganization auth hierarchy from the model set and adds
/// auth hierarchy triggers to the trigger inventory.
/// </summary>
/// <remarks>
/// Must run after <see cref="DeriveTriggerInventoryPass"/> because it reads
/// <c>ConcreteResourcesInNameOrder</c>, <c>AbstractUnionViewsInNameOrder</c>,
/// and <c>AbstractIdentityTablesInNameOrder</c> which must already be populated.
/// </remarks>
public sealed class DeriveAuthHierarchyPass : IRelationalModelSetPass
{
    private const string EducationOrganizationResourceName = "EducationOrganization";

    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var hierarchy = CompileHierarchy(context);
        context.AuthEdOrgHierarchy = hierarchy;

        if (hierarchy is null || hierarchy.EntitiesInNameOrder.Count == 0)
        {
            return;
        }

        // Add auth covering index to the index inventory.
        // (Target) INCLUDE (Source) — used by inverted authorization strategies.
        // Note: The PK on (Source, Target) already covers Source-leading queries.
        context.IndexInventory.Add(
            new DbIndexInfo(
                new DbIndexName($"IX_{AuthNames.EdOrgIdToEdOrgId.Name}_Target"),
                AuthNames.EdOrgIdToEdOrgId,
                KeyColumns: [AuthNames.TargetEdOrgId],
                IsUnique: false,
                Kind: DbIndexKind.Authorization,
                IncludeColumns: [AuthNames.SourceEdOrgId]
            )
        );

        // Add auth triggers to the trigger inventory so they participate in
        // canonical ordering and dialect identifier shortening.
        foreach (var entity in hierarchy.EntitiesInNameOrder)
        {
            bool isLeaf = entity.ParentEdOrgFks.Count == 0;

            // Delete trigger (every entity)
            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName($"TR_{entity.EntityName}_AuthHierarchy_Delete"),
                    entity.Table,
                    KeyColumns: [],
                    IdentityProjectionColumns: [],
                    new TriggerKindParameters.AuthHierarchyMaintenance(
                        entity,
                        AuthHierarchyTriggerEvent.Delete
                    )
                )
            );

            // Insert trigger (every entity)
            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName($"TR_{entity.EntityName}_AuthHierarchy_Insert"),
                    entity.Table,
                    KeyColumns: [],
                    IdentityProjectionColumns: [],
                    new TriggerKindParameters.AuthHierarchyMaintenance(
                        entity,
                        AuthHierarchyTriggerEvent.Insert
                    )
                )
            );

            // Update trigger (hierarchical entities only)
            if (!isLeaf)
            {
                context.TriggerInventory.Add(
                    new DbTriggerInfo(
                        new DbTriggerName($"TR_{entity.EntityName}_AuthHierarchy_Update"),
                        entity.Table,
                        KeyColumns: [],
                        IdentityProjectionColumns: [],
                        new TriggerKindParameters.AuthHierarchyMaintenance(
                            entity,
                            AuthHierarchyTriggerEvent.Update
                        )
                    )
                );
            }
        }
    }

    /// <summary>
    /// Compiles the EducationOrganization hierarchy from the builder context.
    /// Returns <c>null</c> when no abstract EducationOrganization resource exists.
    /// </summary>
    private static AuthEdOrgHierarchy? CompileHierarchy(RelationalModelSetBuilderContext context)
    {
        // Step 1: Find the abstract EducationOrganization union view.
        var edOrgUnionView = context.AbstractUnionViewsInNameOrder.Find(v =>
            v.AbstractResourceKey.Resource.ResourceName == EducationOrganizationResourceName
        );

        if (edOrgUnionView is null)
        {
            return null;
        }

        var abstractEdOrgResource = edOrgUnionView.AbstractResourceKey.Resource;

        // Step 2: Build the set of concrete member resource names.
        var concreteMemberNames = new HashSet<QualifiedResourceName>(
            edOrgUnionView.UnionArmsInOrder.Select(arm => arm.ConcreteMemberResourceKey.Resource)
        );

        // Step 3: Determine the abstract identity column (for the union view projection).
        var abstractIdentityTable =
            context.AbstractIdentityTablesInNameOrder.Find(t =>
                t.AbstractResourceKey.Resource == abstractEdOrgResource
            )
            ?? throw new InvalidOperationException(
                $"No abstract identity table found for '{abstractEdOrgResource.ResourceName}'."
            );

        var abstractIdentityColumn = (
            abstractIdentityTable.TableModel.Columns.FirstOrDefault(c =>
                c.Kind == ColumnKind.Scalar && c.ColumnName.Value != "Discriminator"
            )
            ?? throw new InvalidOperationException(
                $"No scalar identity column found on abstract identity table for '{abstractEdOrgResource.ResourceName}'."
            )
        ).ColumnName;

        // Step 4: Build mapping from concrete member to entity-specific identity column.
        var identityOutputIndex = edOrgUnionView
            .OutputColumnsInSelectOrder.Select((col, idx) => (col, idx))
            .First(x => x.col.ColumnName == abstractIdentityColumn)
            .idx;

        var concreteIdentityColumns = edOrgUnionView.UnionArmsInOrder.ToDictionary(
            arm => arm.ConcreteMemberResourceKey.Resource,
            arm =>
                (
                    (AbstractUnionViewProjectionExpression.SourceColumn)
                        arm.ProjectionExpressionsInSelectOrder[identityOutputIndex]
                ).ColumnName
        );

        // Step 5: Build lookup of concrete resource models by qualified name.
        var concreteResourcesByName = context
            .ConcreteResourcesInNameOrder.Where(cr => concreteMemberNames.Contains(cr.ResourceKey.Resource))
            .ToDictionary(cr => cr.ResourceKey.Resource);

        // Step 6: Build an AuthEdOrgEntity for each concrete member.
        var entities = concreteMemberNames
            .Select(memberName =>
            {
                if (!concreteResourcesByName.TryGetValue(memberName, out var concrete))
                {
                    throw new InvalidOperationException(
                        $"Union view for '{EducationOrganizationResourceName}' references concrete member "
                            + $"'{memberName.ResourceName}' which was not found in ConcreteResourcesInNameOrder."
                    );
                }
                var rootTable = concrete.RelationalModel.Root;
                var entityIdentityColumn = concreteIdentityColumns[memberName];

                // Use DocumentReferenceBindings to find the denormalized parent EdOrg identity
                // columns directly on the root table, avoiding the need for parent table joins.
                var parentFks = concrete
                    .RelationalModel.DocumentReferenceBindings.Where(binding =>
                        binding.Table == rootTable.Table
                        && IsEdOrgFamilyMember(
                            binding.TargetResource,
                            abstractEdOrgResource,
                            concreteMemberNames
                        )
                    )
                    .Select(binding =>
                    {
                        if (binding.IdentityBindings.Count != 1)
                        {
                            throw new InvalidOperationException(
                                $"Expected exactly one identity binding for EdOrg reference on "
                                    + $"'{memberName.ResourceName}', but found {binding.IdentityBindings.Count}."
                            );
                        }
                        return new AuthParentEdOrgFk(binding.IdentityBindings[0].Column);
                    })
                    .OrderBy(fk => fk.DenormalizedParentIdColumn.Value, StringComparer.Ordinal)
                    .ToList();

                return new AuthEdOrgEntity(
                    concrete.ResourceKey.Resource.ResourceName,
                    rootTable.Table,
                    entityIdentityColumn,
                    parentFks
                );
            })
            .OrderBy(e => e.EntityName, StringComparer.Ordinal)
            .ToList();

        return new AuthEdOrgHierarchy(entities);
    }

    private static bool IsEdOrgFamilyMember(
        QualifiedResourceName target,
        QualifiedResourceName abstractEdOrgResource,
        HashSet<QualifiedResourceName> concreteMemberNames
    )
    {
        return target == abstractEdOrgResource || concreteMemberNames.Contains(target);
    }
}
