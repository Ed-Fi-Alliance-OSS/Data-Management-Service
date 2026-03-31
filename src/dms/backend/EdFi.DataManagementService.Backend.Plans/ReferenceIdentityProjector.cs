// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// A single projected reference identity field: the JSON path under the reference object
/// and its CLR value from the hydrated row buffer.
/// </summary>
/// <param name="ReferenceJsonPath">The identity field path within the reference object.</param>
/// <param name="Value">The CLR value read from the hydrated row buffer.</param>
public sealed record ProjectedReferenceField(JsonPathExpression ReferenceJsonPath, object Value);

/// <summary>
/// The projection result for a single reference binding on a hydrated row.
/// </summary>
public abstract record ReferenceProjectionResult
{
    private ReferenceProjectionResult() { }

    /// <summary>
    /// The reference FK column was null — the reference is absent and should not be emitted.
    /// </summary>
    public sealed record Absent : ReferenceProjectionResult
    {
        public static readonly Absent Instance = new();
    }

    /// <summary>
    /// The reference is present. Carries the reference object path, target resource,
    /// identity participation flag, and the ordered identity field values.
    /// </summary>
    /// <param name="ReferenceObjectPath">JSON path where the reference object is emitted.</param>
    /// <param name="TargetResource">The referenced resource type.</param>
    /// <param name="IsIdentityComponent">Whether the reference participates in resource identity.</param>
    /// <param name="FieldsInOrder">Ordered identity field projections.</param>
    public sealed record Present(
        JsonPathExpression ReferenceObjectPath,
        QualifiedResourceName TargetResource,
        bool IsIdentityComponent,
        IReadOnlyList<ProjectedReferenceField> FieldsInOrder
    ) : ReferenceProjectionResult;
}

/// <summary>
/// Projects reference identity values from hydrated row buffers using compiled
/// <see cref="ReferenceIdentityProjectionBinding"/> metadata.
/// </summary>
public static class ReferenceIdentityProjector
{
    /// <summary>
    /// Projects a single reference binding against a hydrated row buffer.
    /// </summary>
    /// <remarks>
    /// The row buffer values are normalized by <see cref="HydrationReader"/> —
    /// SQL NULL is represented as CLR <see langword="null"/>, not <see cref="DBNull.Value"/>.
    /// </remarks>
    /// <param name="row">The hydrated row buffer aligned to table column ordinals.</param>
    /// <param name="binding">The compiled reference identity projection binding.</param>
    /// <returns>
    /// A <see cref="ReferenceProjectionResult.Present"/> when the FK column is non-null,
    /// or <see cref="ReferenceProjectionResult.Absent"/> when the reference is not populated.
    /// </returns>
    public static ReferenceProjectionResult Project(object?[] row, ReferenceIdentityProjectionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(binding);

        if (row[binding.FkColumnOrdinal] is null)
        {
            return ReferenceProjectionResult.Absent.Instance;
        }

        var fields = new ProjectedReferenceField[binding.IdentityFieldOrdinalsInOrder.Length];

        for (var i = 0; i < binding.IdentityFieldOrdinalsInOrder.Length; i++)
        {
            var fieldOrdinal = binding.IdentityFieldOrdinalsInOrder[i];
            var value =
                row[fieldOrdinal.ColumnOrdinal]
                ?? throw new InvalidOperationException(
                    $"Identity field at ordinal {fieldOrdinal.ColumnOrdinal} "
                        + $"(path '{fieldOrdinal.ReferenceJsonPath.Canonical}') is null "
                        + "but FK column is non-null — CHECK constraint may be violated."
                );
            fields[i] = new ProjectedReferenceField(fieldOrdinal.ReferenceJsonPath, value);
        }

        return new ReferenceProjectionResult.Present(
            binding.ReferenceObjectPath,
            binding.TargetResource,
            binding.IsIdentityComponent,
            fields
        );
    }

    /// <summary>
    /// Projects all reference bindings for a table's hydrated rows, grouped by root document scope.
    /// </summary>
    /// <remarks>
    /// The root document scope is determined by <see cref="DbTableIdentityMetadata.RootScopeLocatorColumns"/>
    /// from the table's identity metadata. Each root scope locator resolves to a <c>DocumentId</c> (<see langword="long"/>)
    /// that identifies the owning root document.
    /// </remarks>
    /// <param name="hydratedRows">Hydrated rows for the table.</param>
    /// <param name="projectionPlan">The compiled reference identity projection plan for the table.</param>
    /// <returns>
    /// Present projection results grouped by root document <c>DocumentId</c>.
    /// Absent references (null FK) are omitted.
    /// </returns>
    public static IReadOnlyDictionary<long, IReadOnlyList<ReferenceProjectionResult.Present>> ProjectTable(
        HydratedTableRows hydratedRows,
        ReferenceIdentityProjectionTablePlan projectionPlan
    )
    {
        ArgumentNullException.ThrowIfNull(hydratedRows);
        ArgumentNullException.ThrowIfNull(projectionPlan);

        if (!projectionPlan.Table.Equals(hydratedRows.TableModel.Table))
        {
            throw new InvalidOperationException(
                $"Cannot project references: projection plan table '{projectionPlan.Table}' "
                    + $"does not match hydrated rows table '{hydratedRows.TableModel.Table}'."
            );
        }

        var rootScopeLocatorColumn =
            RelationalResourceModelCompileValidator.ResolveRootScopeLocatorColumnOrThrow(
                hydratedRows.TableModel,
                "reference identity projection"
            );

        var rootScopeOrdinal = ProjectionMetadataResolver.ResolveTableColumnOrdinalOrThrow(
            hydratedRows.TableModel,
            rootScopeLocatorColumn,
            missingColumn => new InvalidOperationException(
                $"Cannot project references for table '{hydratedRows.TableModel.Table}': "
                    + $"root scope locator column '{missingColumn.Value}' not found in table columns."
            )
        );

        var resultsByDocumentId = new Dictionary<long, List<ReferenceProjectionResult.Present>>();

        foreach (var row in hydratedRows.Rows)
        {
            var documentId = Convert.ToInt64(row[rootScopeOrdinal]);

            foreach (var binding in projectionPlan.BindingsInOrder)
            {
                if (Project(row, binding) is ReferenceProjectionResult.Present present)
                {
                    if (!resultsByDocumentId.TryGetValue(documentId, out var list))
                    {
                        list = [];
                        resultsByDocumentId[documentId] = list;
                    }

                    list.Add(present);
                }
            }
        }

        return resultsByDocumentId.ToDictionary(
            static kvp => kvp.Key,
            static kvp => (IReadOnlyList<ReferenceProjectionResult.Present>)kvp.Value
        );
    }
}
