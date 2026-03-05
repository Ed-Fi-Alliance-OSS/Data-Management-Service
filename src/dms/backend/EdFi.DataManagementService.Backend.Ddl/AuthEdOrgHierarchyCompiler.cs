// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Extracts an <see cref="AuthEdOrgHierarchy"/> from a <see cref="DerivedRelationalModelSet"/>
/// by identifying concrete EducationOrganization members and classifying their
/// <see cref="ColumnKind.DocumentFk"/> columns as parent EdOrg references.
/// </summary>
/// <remarks>
/// Model-driven: no hardcoded EdOrg list. Extensions that add new concrete
/// EducationOrganization subclasses are automatically discovered via the
/// abstract union view arms.
/// </remarks>
public static class AuthEdOrgHierarchyCompiler
{
    private const string EducationOrganizationResourceName = "EducationOrganization";

    /// <summary>
    /// Compiles the EducationOrganization hierarchy from the derived relational model set.
    /// Returns an empty hierarchy when no abstract EducationOrganization resource exists.
    /// </summary>
    public static AuthEdOrgHierarchy Compile(DerivedRelationalModelSet modelSet)
    {
        ArgumentNullException.ThrowIfNull(modelSet);

        // Step 1: Find the abstract EducationOrganization union view.
        // Its union arms enumerate all concrete members (including extension-added ones).
        var edOrgUnionView = modelSet.AbstractUnionViewsInNameOrder.FirstOrDefault(v =>
            v.AbstractResourceKey.Resource.ResourceName == EducationOrganizationResourceName
        );

        if (edOrgUnionView is null)
        {
            return new AuthEdOrgHierarchy([]);
        }

        var abstractEdOrgResource = edOrgUnionView.AbstractResourceKey.Resource;

        // Step 2: Build the set of concrete member resource names.
        var concreteMemberNames = new HashSet<QualifiedResourceName>(
            edOrgUnionView.UnionArmsInOrder.Select(arm => arm.ConcreteMemberResourceKey.Resource)
        );

        // Step 3: Find the abstract identity table for parent FK resolution
        // on abstract references (e.g., OrganizationDepartment.ParentEducationOrganization).
        var abstractIdentityTable =
            modelSet.AbstractIdentityTablesInNameOrder.FirstOrDefault(t =>
                t.AbstractResourceKey.Resource == abstractEdOrgResource
            )
            ?? throw new InvalidOperationException(
                $"No abstract identity table found for '{abstractEdOrgResource.ResourceName}'."
            );

        // Step 4: Determine the abstract identity column from the abstract identity table.
        // This is the scalar column that is NOT Discriminator (DocumentId columns are
        // implicitly excluded by their non-Scalar ColumnKind).
        var abstractIdentityColumn = (
            abstractIdentityTable.TableModel.Columns.FirstOrDefault(c =>
                c.Kind == ColumnKind.Scalar && c.ColumnName.Value != "Discriminator"
            )
            ?? throw new InvalidOperationException(
                $"No scalar identity column found on abstract identity table for '{abstractEdOrgResource.ResourceName}'."
            )
        ).ColumnName;

        // Step 5: Build a mapping from concrete member resource to its entity-specific
        // identity column using the union view arm projections. Concrete EdOrg tables use
        // entity-specific column names (e.g., SchoolId, LocalEducationAgencyId) while the
        // abstract identity table uses the abstract name (EducationOrganizationId).
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

        // Step 6: Build a lookup of concrete resource models by qualified name.
        var concreteResourcesByName = modelSet
            .ConcreteResourcesInNameOrder.Where(cr => concreteMemberNames.Contains(cr.ResourceKey.Resource))
            .ToDictionary(cr => cr.ResourceKey.Resource);

        // Step 7: Build an AuthEdOrgEntity for each concrete member.
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

                var parentFks = rootTable
                    .Columns.Where(col =>
                        col.Kind == ColumnKind.DocumentFk
                        && col.Storage is ColumnStorage.Stored
                        && col.TargetResource.HasValue
                        && IsEdOrgFamilyMember(
                            col.TargetResource.Value,
                            abstractEdOrgResource,
                            concreteMemberNames
                        )
                    )
                    .Select(col => new AuthParentEdOrgFk(
                        col.ColumnName,
                        ResolveParentTable(
                            col.TargetResource!.Value,
                            abstractEdOrgResource,
                            abstractIdentityTable,
                            concreteResourcesByName
                        ),
                        ResolveParentIdentityColumn(
                            col.TargetResource!.Value,
                            abstractEdOrgResource,
                            abstractIdentityColumn,
                            concreteIdentityColumns
                        )
                    ))
                    .OrderBy(fk => fk.FkColumn.Value, StringComparer.Ordinal)
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

    /// <summary>
    /// Returns <c>true</c> when the target resource is the abstract EducationOrganization
    /// or one of its concrete members.
    /// </summary>
    private static bool IsEdOrgFamilyMember(
        QualifiedResourceName target,
        QualifiedResourceName abstractEdOrgResource,
        HashSet<QualifiedResourceName> concreteMemberNames
    )
    {
        return target == abstractEdOrgResource || concreteMemberNames.Contains(target);
    }

    /// <summary>
    /// Resolves the parent identity column for a DocumentFk targeting an EdOrg family member.
    /// Abstract references use the abstract identity column; concrete references use the
    /// entity-specific identity column (e.g., SchoolId, LocalEducationAgencyId).
    /// </summary>
    private static DbColumnName ResolveParentIdentityColumn(
        QualifiedResourceName targetResource,
        QualifiedResourceName abstractEdOrgResource,
        DbColumnName abstractIdentityColumn,
        Dictionary<QualifiedResourceName, DbColumnName> concreteIdentityColumns
    )
    {
        if (targetResource == abstractEdOrgResource)
        {
            return abstractIdentityColumn;
        }

        if (!concreteIdentityColumns.TryGetValue(targetResource, out var concreteColumn))
        {
            throw new InvalidOperationException(
                $"Parent FK targets concrete EdOrg '{targetResource.ResourceName}' "
                    + "which was not found in the union view arms."
            );
        }

        return concreteColumn;
    }

    /// <summary>
    /// Resolves the parent table for a DocumentFk targeting an EdOrg family member.
    /// Abstract references resolve to the abstract identity table; concrete references
    /// resolve to the concrete member's root table.
    /// </summary>
    private static DbTableName ResolveParentTable(
        QualifiedResourceName targetResource,
        QualifiedResourceName abstractEdOrgResource,
        AbstractIdentityTableInfo abstractIdentityTable,
        Dictionary<QualifiedResourceName, ConcreteResourceModel> concreteResourcesByName
    )
    {
        if (targetResource == abstractEdOrgResource)
        {
            return abstractIdentityTable.TableModel.Table;
        }

        if (!concreteResourcesByName.TryGetValue(targetResource, out var concreteResource))
        {
            throw new InvalidOperationException(
                $"Parent FK targets concrete EdOrg '{targetResource.ResourceName}' "
                    + "which was not found in ConcreteResourcesInNameOrder."
            );
        }

        return concreteResource.RelationalModel.Root.Table;
    }
}
