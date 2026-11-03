// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

internal sealed class RelationalWriteConstraintResolver : IRelationalWriteConstraintResolver
{
    public RelationalWriteConstraintResolution Resolve(RelationalWriteConstraintResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Violation switch
        {
            RelationalWriteExceptionClassification.UniqueConstraintViolation uniqueViolation =>
                ResolveUniqueConstraint(request, uniqueViolation),
            RelationalWriteExceptionClassification.ForeignKeyConstraintViolation foreignKeyViolation =>
                ResolveForeignKeyConstraint(request.WritePlan.Model, foreignKeyViolation.ConstraintName),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Violation,
                "Unsupported relational write constraint violation type."
            ),
        };
    }

    private static RelationalWriteConstraintResolution ResolveUniqueConstraint(
        RelationalWriteConstraintResolutionRequest request,
        RelationalWriteExceptionClassification.UniqueConstraintViolation violation
    )
    {
        var uniqueMatch = FindUniqueConstraint(request.WritePlan.Model, violation.ConstraintName);

        if (uniqueMatch is not null)
        {
            if (!uniqueMatch.Table.Table.Equals(request.WritePlan.Model.Root.Table))
            {
                return new RelationalWriteConstraintResolution.Unresolved(violation.ConstraintName);
            }

            var rootNaturalKeyColumns = GetRootNaturalKeyColumnsOrThrow(request);

            return uniqueMatch.Constraint.Columns.SequenceEqual(rootNaturalKeyColumns)
                ? new RelationalWriteConstraintResolution.RootNaturalKeyUnique(violation.ConstraintName)
                : new RelationalWriteConstraintResolution.Unresolved(violation.ConstraintName);
        }

        // The constraint is not part of the concrete resource model. A unique violation can still be a
        // user-facing identity conflict when it originates from the abstract identity table that a concrete
        // EducationOrganization subclass projects into (e.g. UX_EducationOrganizationIdentity_NK). Resolve
        // those natural-key constraints to the same identity-conflict result used for concrete roots; the
        // mapper reports the concrete request body's identity values.
        if (IsIdentityMaintenancePrimaryKey(violation.ConstraintName))
        {
            return new RelationalWriteConstraintResolution.RootNaturalKeyUnique(violation.ConstraintName);
        }

        return ResolveAbstractIdentityUniqueConstraint(request, violation);
    }

    /// <summary>
    /// The same duplicate identity can also surface from the other identity-maintenance triggers on the
    /// written table, because SQL Server fires multiple AFTER triggers in an undefined order. A duplicate
    /// <c>auth.EducationOrganizationIdToEducationOrganizationId</c> key is only reachable when the education
    /// organization id already exists, since the insert trigger adds the new id's self and ancestor tuples and
    /// the update trigger merges. It is therefore the same identity conflict as the abstract natural key.
    /// </summary>
    private static bool IsIdentityMaintenancePrimaryKey(string constraintName) =>
        string.Equals(
            constraintName,
            AuthObjectDefinitions.AuthEdOrgTable.PrimaryKeyName,
            StringComparison.Ordinal
        );

    private static RelationalWriteConstraintResolution ResolveAbstractIdentityUniqueConstraint(
        RelationalWriteConstraintResolutionRequest request,
        RelationalWriteExceptionClassification.UniqueConstraintViolation violation
    )
    {
        foreach (
            var tableModel in request.ReferenceResolutionRequest.MappingSet.Model.AbstractIdentityTablesInNameOrder.Select(
                abstractIdentityTable => abstractIdentityTable.TableModel
            )
        )
        {
            var match = tableModel
                .Constraints.OfType<TableConstraint.Unique>()
                .SingleOrDefault(constraint =>
                    string.Equals(constraint.Name, violation.ConstraintName, StringComparison.Ordinal)
                );

            if (match is null)
            {
                continue;
            }

            // An abstract identity table carries two unique constraints: the natural-key constraint over the
            // projected identity columns, and the *_RefKey helper that appends the DocumentId primary key.
            // A unique constraint that includes the surrogate DocumentId key column (the *_RefKey helper) is
            // not a user-facing identity conflict and stays unresolved; a unique constraint that does not
            // include it is the natural key and maps to an identity conflict. Keying off the table's own
            // primary-key columns keeps this robust to changes in the projected identity column set.
            var keyColumnNames = tableModel.Key.Columns.Select(keyColumn => keyColumn.ColumnName).ToHashSet();

            return match.Columns.Any(keyColumnNames.Contains)
                ? new RelationalWriteConstraintResolution.Unresolved(violation.ConstraintName)
                : new RelationalWriteConstraintResolution.RootNaturalKeyUnique(violation.ConstraintName);
        }

        return new RelationalWriteConstraintResolution.Unresolved(violation.ConstraintName);
    }

    private static RelationalWriteConstraintResolution ResolveForeignKeyConstraint(
        RelationalResourceModel resourceModel,
        string constraintName
    )
    {
        var foreignKeyMatch = FindForeignKeyConstraint(resourceModel, constraintName);

        if (foreignKeyMatch is null)
        {
            return new RelationalWriteConstraintResolution.Unresolved(constraintName);
        }

        var documentReference = TryResolveDocumentReference(resourceModel, foreignKeyMatch);

        if (documentReference is not null)
        {
            return documentReference;
        }

        var descriptorReference = TryResolveDescriptorReference(resourceModel, foreignKeyMatch);

        if (descriptorReference is not null)
        {
            return descriptorReference;
        }

        return new RelationalWriteConstraintResolution.Unresolved(constraintName);
    }

    // The own-key probe lists the root natural-key constraint columns as emitted, in semantic identity
    // order. Every identity part supplied by the same document reference binds that reference's
    // ..._DocumentId, so duplicates collapse to the first occurrence.
    private static IReadOnlyList<DbColumnName> GetRootNaturalKeyColumnsOrThrow(
        RelationalWriteConstraintResolutionRequest request
    ) =>
        [
            .. RelationalWriteSupport
                .GetOwnNaturalKeyProbeOrThrow(
                    request.ReferenceResolutionRequest.MappingSet,
                    request.WritePlan.Model.Resource
                )
                .KeyColumns.Select(key => key.Column)
                .Distinct(),
        ];

    private static RelationalWriteConstraintResolution.RequestReference? TryResolveDocumentReference(
        RelationalResourceModel resourceModel,
        ConstraintMatch<TableConstraint.ForeignKey> foreignKeyMatch
    )
    {
        var matches = resourceModel
            .DocumentReferenceBindings.Where(binding =>
                binding.Table.Equals(foreignKeyMatch.Table.Table)
                && foreignKeyMatch.Constraint.Columns.Contains(binding.FkColumn)
            )
            .Distinct()
            .ToArray();

        return matches.Length == 1
            ? new RelationalWriteConstraintResolution.RequestReference(
                foreignKeyMatch.Constraint.Name,
                RelationalWriteReferenceKind.Document,
                matches[0].ReferenceObjectPath,
                matches[0].TargetResource
            )
            : null;
    }

    private static RelationalWriteConstraintResolution.RequestReference? TryResolveDescriptorReference(
        RelationalResourceModel resourceModel,
        ConstraintMatch<TableConstraint.ForeignKey> foreignKeyMatch
    )
    {
        var matches = resourceModel
            .DescriptorEdgeSources.Where(source =>
                source.Table.Equals(foreignKeyMatch.Table.Table)
                && foreignKeyMatch.Constraint.Columns.Contains(source.FkColumn)
            )
            .Distinct()
            .ToArray();

        return matches.Length == 1
            ? new RelationalWriteConstraintResolution.RequestReference(
                foreignKeyMatch.Constraint.Name,
                RelationalWriteReferenceKind.Descriptor,
                matches[0].DescriptorValuePath,
                matches[0].DescriptorResource
            )
            : null;
    }

    private static ConstraintMatch<TableConstraint.Unique>? FindUniqueConstraint(
        RelationalResourceModel resourceModel,
        string constraintName
    )
    {
        foreach (var table in resourceModel.TablesInDependencyOrder)
        {
            var uniqueConstraint = table
                .Constraints.OfType<TableConstraint.Unique>()
                .SingleOrDefault(constraint =>
                    string.Equals(constraint.Name, constraintName, StringComparison.Ordinal)
                );

            if (uniqueConstraint is not null)
            {
                return new ConstraintMatch<TableConstraint.Unique>(table, uniqueConstraint);
            }
        }

        return null;
    }

    private static ConstraintMatch<TableConstraint.ForeignKey>? FindForeignKeyConstraint(
        RelationalResourceModel resourceModel,
        string constraintName
    )
    {
        foreach (var table in resourceModel.TablesInDependencyOrder)
        {
            var foreignKeyConstraint = table
                .Constraints.OfType<TableConstraint.ForeignKey>()
                .SingleOrDefault(constraint =>
                    string.Equals(constraint.Name, constraintName, StringComparison.Ordinal)
                );

            if (foreignKeyConstraint is not null)
            {
                return new ConstraintMatch<TableConstraint.ForeignKey>(table, foreignKeyConstraint);
            }
        }

        return null;
    }

    private sealed record ConstraintMatch<TConstraint>(DbTableModel Table, TConstraint Constraint)
        where TConstraint : TableConstraint;
}
