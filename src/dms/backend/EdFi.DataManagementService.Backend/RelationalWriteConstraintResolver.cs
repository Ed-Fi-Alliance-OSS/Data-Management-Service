// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

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

            // The root table carries a second unique constraint besides the natural key: the mirrored
            // DocumentUuid. Recognize it by its COLUMN SET, never by name — ApplyDialectIdentifierShortening
            // hash-truncates long identifiers, so the "_DocumentUuid" token is not reliably present.
            if (IsDocumentUuidColumnSet(uniqueMatch.Constraint.Columns))
            {
                return new RelationalWriteConstraintResolution.DocumentUuidUnique(violation.ConstraintName);
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
        // mapper reports the concrete request body's identity values. Keep the resolution type distinct:
        // unlike a concrete root collision, an abstract identity collision does not prove that the target
        // guarded by If-None-Match: * now exists.
        return ResolveAbstractIdentityUniqueConstraint(request, violation);
    }

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
                : new RelationalWriteConstraintResolution.AbstractIdentityNaturalKeyUnique(
                    violation.ConstraintName
                );
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

    /// <summary>
    /// Reports whether a root-table unique constraint covers exactly the mirrored <c>DocumentUuid</c> column.
    /// </summary>
    private static bool IsDocumentUuidColumnSet(IReadOnlyList<DbColumnName> columns) =>
        columns.Count == 1 && columns[0].Equals(RelationalNameConventions.DocumentUuidColumnName);

    /// <summary>
    /// Returns the root natural-key column list a <c>UX_&lt;R&gt;_NK</c> violation must match to be
    /// recognized as a user-facing identity conflict.
    /// </summary>
    /// <remarks>
    /// Reads the compiled <see cref="OwnNaturalKeyProbe"/>. It used to re-derive this list at runtime from
    /// the <c>ReferentialIdentity</c> trigger's identity-element block — metadata that disappeared with the
    /// trigger, which would have silently broken 409 classification and If-None-Match handling.
    /// The compile-time probe reproduces <c>RootIdentityConstraintPass.BuildRootIdentityColumns</c>
    /// directly from the model, so this comparison stays exact.
    /// </remarks>
    private static IReadOnlyList<DbColumnName> GetRootNaturalKeyColumnsOrThrow(
        RelationalWriteConstraintResolutionRequest request
    )
    {
        var ownNaturalKeyProbe = RelationalWriteSupport.GetOwnNaturalKeyProbeOrThrow(
            request.ReferenceResolutionRequest.MappingSet,
            request.WritePlan.Model.Resource,
            request.WritePlan.Model.Root.Table
        );

        if (ownNaturalKeyProbe.Columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Resource '{RelationalWriteSupport.FormatResource(request.WritePlan.Model.Resource)}' did not resolve any root natural-key columns from compiled natural-key probe metadata."
            );
        }

        return ownNaturalKeyProbe.Columns.Select(column => column.ColumnName).ToArray();
    }

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
