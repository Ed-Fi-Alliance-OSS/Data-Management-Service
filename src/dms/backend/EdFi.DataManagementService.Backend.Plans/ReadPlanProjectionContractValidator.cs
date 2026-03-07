// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Validates that compiled projection metadata remains consistent with immutable read-plan hydration metadata.
/// </summary>
public static class ReadPlanProjectionContractValidator
{
    /// <summary>
    /// Validates projection metadata or throws the exception created by <paramref name="createException" />.
    /// </summary>
    public static void ValidateOrThrow(ResourceReadPlan readPlan, Func<string, Exception> createException)
    {
        ArgumentNullException.ThrowIfNull(readPlan);
        ArgumentNullException.ThrowIfNull(createException);

        if (readPlan.Model.StorageKind != ResourceStorageKind.RelationalTables)
        {
            return;
        }

        var requiresReferenceIdentityProjection =
            readPlan.Model.DocumentReferenceBindings.Count > 0
            && readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.IsEmpty;
        var requiresDescriptorProjection =
            readPlan.Model.DescriptorEdgeSources.Count > 0
            && readPlan.DescriptorProjectionPlansInOrder.IsEmpty;

        if (requiresReferenceIdentityProjection || requiresDescriptorProjection)
        {
            var missingProjectionReason = (
                requiresReferenceIdentityProjection,
                requiresDescriptorProjection
            ) switch
            {
                (true, true) =>
                    "DocumentReferenceBindings are present while ReferenceIdentityProjectionPlansInDependencyOrder is empty, "
                        + "and DescriptorEdgeSources are present while DescriptorProjectionPlansInOrder is empty",
                (true, false) =>
                    "DocumentReferenceBindings are present while ReferenceIdentityProjectionPlansInDependencyOrder is empty",
                (false, true) =>
                    "DescriptorEdgeSources are present while DescriptorProjectionPlansInOrder is empty",
                _ => throw createException("Projection gating reached an invalid state"),
            };

            throw createException(missingProjectionReason);
        }

        if (
            readPlan.Model.DocumentReferenceBindings.Count == 0
            && !readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.IsEmpty
        )
        {
            throw createException(
                "DocumentReferenceBindings are absent while ReferenceIdentityProjectionPlansInDependencyOrder is populated"
            );
        }

        if (
            readPlan.Model.DescriptorEdgeSources.Count == 0
            && !readPlan.DescriptorProjectionPlansInOrder.IsEmpty
        )
        {
            throw createException(
                "DescriptorEdgeSources are absent while DescriptorProjectionPlansInOrder is populated"
            );
        }

        var hydrationTablePlansByTable = BuildHydrationTablePlansByTableOrThrow(readPlan, createException);

        ValidateReferenceProjectionPlanContracts(readPlan, hydrationTablePlansByTable, createException);
        ValidateDescriptorProjectionPlanContracts(readPlan, hydrationTablePlansByTable, createException);
    }

    private static IReadOnlyDictionary<DbTableName, TableReadPlan> BuildHydrationTablePlansByTableOrThrow(
        ResourceReadPlan readPlan,
        Func<string, Exception> createException
    )
    {
        Dictionary<DbTableName, TableReadPlan> hydrationTablePlansByTable = [];

        foreach (var tablePlan in readPlan.TablePlansInDependencyOrder)
        {
            if (hydrationTablePlansByTable.TryAdd(tablePlan.TableModel.Table, tablePlan))
            {
                continue;
            }

            throw createException(
                $"compiled hydration table plans contain duplicate table '{tablePlan.TableModel.Table}'"
            );
        }

        return hydrationTablePlansByTable;
    }

    private static void ValidateReferenceProjectionPlanContracts(
        ResourceReadPlan readPlan,
        IReadOnlyDictionary<DbTableName, TableReadPlan> hydrationTablePlansByTable,
        Func<string, Exception> createException
    )
    {
        if (readPlan.Model.DocumentReferenceBindings.Count == 0)
        {
            return;
        }

        var compiledBindingCount = 0;
        HashSet<DbTableName> seenProjectionTables = [];

        foreach (var projectionTablePlan in readPlan.ReferenceIdentityProjectionPlansInDependencyOrder)
        {
            if (!seenProjectionTables.Add(projectionTablePlan.Table))
            {
                throw createException(
                    $"reference identity projection includes duplicate table '{projectionTablePlan.Table}'"
                );
            }

            if (
                !hydrationTablePlansByTable.TryGetValue(projectionTablePlan.Table, out var hydrationTablePlan)
            )
            {
                throw createException(
                    $"reference identity projection table '{projectionTablePlan.Table}' is not present in compiled table plans"
                );
            }

            foreach (var binding in projectionTablePlan.BindingsInOrder)
            {
                compiledBindingCount++;

                ThrowIfOrdinalIsOutOfRange(
                    binding.FkColumnOrdinal,
                    hydrationTablePlan.TableModel.Columns.Count,
                    $"reference identity FK ordinal for table '{projectionTablePlan.Table}'",
                    createException
                );

                foreach (var fieldOrdinal in binding.IdentityFieldOrdinalsInOrder)
                {
                    ThrowIfOrdinalIsOutOfRange(
                        fieldOrdinal.ColumnOrdinal,
                        hydrationTablePlan.TableModel.Columns.Count,
                        $"reference identity field ordinal '{fieldOrdinal.ReferenceJsonPath.Canonical}' for table '{projectionTablePlan.Table}'",
                        createException
                    );
                }
            }
        }

        if (compiledBindingCount == readPlan.Model.DocumentReferenceBindings.Count)
        {
            return;
        }

        throw createException(
            $"reference identity projection binding count '{compiledBindingCount}' does not match DocumentReferenceBindings count '{readPlan.Model.DocumentReferenceBindings.Count}'"
        );
    }

    private static void ValidateDescriptorProjectionPlanContracts(
        ResourceReadPlan readPlan,
        IReadOnlyDictionary<DbTableName, TableReadPlan> hydrationTablePlansByTable,
        Func<string, Exception> createException
    )
    {
        if (readPlan.Model.DescriptorEdgeSources.Count == 0)
        {
            return;
        }

        var compiledSourceCount = 0;

        for (var planIndex = 0; planIndex < readPlan.DescriptorProjectionPlansInOrder.Length; planIndex++)
        {
            var descriptorProjectionPlan = readPlan.DescriptorProjectionPlansInOrder[planIndex];

            if (descriptorProjectionPlan.ResultShape is not { DescriptorIdOrdinal: 0, UriOrdinal: 1 })
            {
                throw createException(
                    $"descriptor projection plan at index '{planIndex}' result shape must expose DescriptorId at ordinal '0' and Uri at ordinal '1', "
                        + $"but was DescriptorId='{descriptorProjectionPlan.ResultShape.DescriptorIdOrdinal}', "
                        + $"Uri='{descriptorProjectionPlan.ResultShape.UriOrdinal}'"
                );
            }

            foreach (var source in descriptorProjectionPlan.SourcesInOrder)
            {
                compiledSourceCount++;

                if (!hydrationTablePlansByTable.TryGetValue(source.Table, out var hydrationTablePlan))
                {
                    throw createException(
                        $"descriptor projection plan at index '{planIndex}' source '{source.DescriptorValuePath.Canonical}' references table '{source.Table}' that is not present in compiled table plans"
                    );
                }

                ThrowIfOrdinalIsOutOfRange(
                    source.DescriptorIdColumnOrdinal,
                    hydrationTablePlan.TableModel.Columns.Count,
                    $"descriptor projection plan at index '{planIndex}' source ordinal '{source.DescriptorValuePath.Canonical}' for table '{source.Table}'",
                    createException
                );
            }
        }

        if (compiledSourceCount == readPlan.Model.DescriptorEdgeSources.Count)
        {
            return;
        }

        throw createException(
            $"descriptor projection source count '{compiledSourceCount}' across plan count '{readPlan.DescriptorProjectionPlansInOrder.Length}' does not match DescriptorEdgeSources count '{readPlan.Model.DescriptorEdgeSources.Count}'"
        );
    }

    private static void ThrowIfOrdinalIsOutOfRange(
        int ordinal,
        int count,
        string context,
        Func<string, Exception> createException
    )
    {
        if ((uint)ordinal < (uint)count)
        {
            return;
        }

        throw createException(
            $"ordinal '{ordinal}' for {context} is out of range for hydration select-list columns (count: {count})"
        );
    }
}
