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

        if (uniqueMatch is null || !uniqueMatch.Table.Table.Equals(request.WritePlan.Model.Root.Table))
        {
            return new RelationalWriteConstraintResolution.Unresolved(violation.ConstraintName);
        }

        var rootNaturalKeyColumns = GetRootNaturalKeyColumnsOrThrow(request);

        return uniqueMatch.Constraint.Columns.SequenceEqual(rootNaturalKeyColumns)
            ? new RelationalWriteConstraintResolution.RootNaturalKeyUnique(violation.ConstraintName)
            : new RelationalWriteConstraintResolution.Unresolved(violation.ConstraintName);
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

    private static IReadOnlyList<DbColumnName> GetRootNaturalKeyColumnsOrThrow(
        RelationalWriteConstraintResolutionRequest request
    )
    {
        var rootTable = request.WritePlan.Model.Root;
        var referentialIdentityTrigger =
            request.ReferenceResolutionRequest.MappingSet.Model.TriggersInCreateOrder.SingleOrDefault(
                trigger =>
                    trigger.Table.Equals(rootTable.Table)
                    && trigger.Parameters is TriggerKindParameters.ReferentialIdentityMaintenance parameters
                    && string.Equals(
                        parameters.ProjectName,
                        request.WritePlan.Model.Resource.ProjectName,
                        StringComparison.Ordinal
                    )
                    && string.Equals(
                        parameters.ResourceName,
                        request.WritePlan.Model.Resource.ResourceName,
                        StringComparison.Ordinal
                    )
            );

        if (
            referentialIdentityTrigger?.Parameters
            is not TriggerKindParameters.ReferentialIdentityMaintenance referentialIdentityParameters
        )
        {
            throw new InvalidOperationException(
                $"Mapping set '{RelationalWriteSupport.FormatMappingSetKey(request.ReferenceResolutionRequest.MappingSet.Key)}' "
                    + $"is missing referential-identity trigger metadata for resource '{RelationalWriteSupport.FormatResource(request.WritePlan.Model.Resource)}'."
            );
        }

        Dictionary<string, DocumentReferenceBinding> identityBindingsByPath = new(StringComparer.Ordinal);

        foreach (
            var binding in request.WritePlan.Model.DocumentReferenceBindings.Where(binding =>
                binding.IsIdentityComponent && binding.Table.Equals(rootTable.Table)
            )
        )
        {
            foreach (
                var canonicalReferencePath in binding.IdentityBindings.Select(referencePath =>
                    referencePath.ReferenceJsonPath.Canonical
                )
            )
            {
                if (
                    !identityBindingsByPath.TryAdd(canonicalReferencePath, binding)
                    && identityBindingsByPath[canonicalReferencePath].ReferenceObjectPath
                        != binding.ReferenceObjectPath
                )
                {
                    throw new InvalidOperationException(
                        $"Resource '{RelationalWriteSupport.FormatResource(request.WritePlan.Model.Resource)}' contains ambiguous identity reference bindings for path "
                            + $"'{canonicalReferencePath}'."
                    );
                }
            }
        }

        HashSet<string> seenColumns = new(StringComparer.Ordinal);
        List<DbColumnName> rootNaturalKeyColumns = [];

        foreach (var identityElement in referentialIdentityParameters.IdentityElements)
        {
            var constraintColumn = identityBindingsByPath.TryGetValue(
                identityElement.IdentityJsonPath,
                out var identityBinding
            )
                ? identityBinding.FkColumn
                : identityElement.Column;

            if (seenColumns.Add(constraintColumn.Value))
            {
                rootNaturalKeyColumns.Add(constraintColumn);
            }
        }

        if (rootNaturalKeyColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Resource '{RelationalWriteSupport.FormatResource(request.WritePlan.Model.Resource)}' did not resolve any root natural-key columns from referential-identity metadata."
            );
        }

        return rootNaturalKeyColumns;
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
