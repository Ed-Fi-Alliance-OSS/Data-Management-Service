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

        var modelBindingsByTable = BuildDocumentReferenceBindingsByTable(readPlan.Model);
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

            if (!modelBindingsByTable.TryGetValue(projectionTablePlan.Table, out var modelBindingsInOrder))
            {
                throw createException(
                    $"reference identity projection table '{projectionTablePlan.Table}' does not match any authoritative DocumentReferenceBindings"
                );
            }

            if (projectionTablePlan.BindingsInOrder.Length != modelBindingsInOrder.Count)
            {
                throw createException(
                    $"reference identity projection table '{projectionTablePlan.Table}' binding count '{projectionTablePlan.BindingsInOrder.Length}' does not match authoritative DocumentReferenceBindings count '{modelBindingsInOrder.Count}' for ordered bindings "
                        + $"{FormatReferenceBindingOrder(modelBindingsInOrder)}"
                );
            }

            for (
                var bindingIndex = 0;
                bindingIndex < projectionTablePlan.BindingsInOrder.Length;
                bindingIndex++
            )
            {
                var binding = projectionTablePlan.BindingsInOrder[bindingIndex];
                var modelBinding = modelBindingsInOrder[bindingIndex];

                ThrowIfOrdinalIsOutOfRange(
                    binding.FkColumnOrdinal,
                    hydrationTablePlan.TableModel.Columns.Count,
                    $"reference identity FK ordinal for table '{projectionTablePlan.Table}'",
                    createException
                );

                var fkColumnModel = hydrationTablePlan.TableModel.Columns[binding.FkColumnOrdinal];

                ValidateReferenceProjectionBindingContractOrThrow(
                    projectionTablePlan.Table,
                    binding,
                    bindingIndex,
                    fkColumnModel,
                    modelBinding,
                    createException
                );

                if (binding.IdentityFieldOrdinalsInOrder.Length != modelBinding.IdentityBindings.Count)
                {
                    throw createException(
                        $"reference identity projection binding '{modelBinding.ReferenceObjectPath.Canonical}' on table '{projectionTablePlan.Table}' field count "
                            + $"'{binding.IdentityFieldOrdinalsInOrder.Length}' does not match model identity binding count '{modelBinding.IdentityBindings.Count}'"
                    );
                }

                for (
                    var fieldIndex = 0;
                    fieldIndex < binding.IdentityFieldOrdinalsInOrder.Length;
                    fieldIndex++
                )
                {
                    var fieldOrdinal = binding.IdentityFieldOrdinalsInOrder[fieldIndex];

                    ThrowIfOrdinalIsOutOfRange(
                        fieldOrdinal.ColumnOrdinal,
                        hydrationTablePlan.TableModel.Columns.Count,
                        $"reference identity field ordinal '{fieldOrdinal.ReferenceJsonPath.Canonical}' for table '{projectionTablePlan.Table}'",
                        createException
                    );

                    var fieldColumnModel = hydrationTablePlan.TableModel.Columns[fieldOrdinal.ColumnOrdinal];
                    var modelIdentityBinding = modelBinding.IdentityBindings[fieldIndex];

                    ValidateReferenceProjectionFieldContractOrThrow(
                        projectionTablePlan.Table,
                        fieldOrdinal,
                        fieldIndex,
                        fieldColumnModel,
                        modelBinding,
                        modelIdentityBinding,
                        createException
                    );
                }
            }
        }

        if (seenProjectionTables.Count == modelBindingsByTable.Count)
        {
            return;
        }

        var missingProjectionTables = modelBindingsByTable
            .Where(pair => !seenProjectionTables.Contains(pair.Key))
            .Select(static pair =>
                $"'{pair.Key}' for ordered bindings {FormatReferenceBindingOrder(pair.Value)}"
            );

        throw createException(
            "reference identity projection is missing authoritative table plan(s): "
                + string.Join(", ", missingProjectionTables)
        );
    }

    private static IReadOnlyDictionary<
        DbTableName,
        List<DocumentReferenceBinding>
    > BuildDocumentReferenceBindingsByTable(RelationalResourceModel model)
    {
        Dictionary<DbTableName, List<DocumentReferenceBinding>> bindingsByTable = [];

        foreach (var binding in model.DocumentReferenceBindings)
        {
            if (bindingsByTable.TryGetValue(binding.Table, out var tableBindings))
            {
                tableBindings.Add(binding);
                continue;
            }

            bindingsByTable.Add(binding.Table, [binding]);
        }

        return bindingsByTable;
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

        var authoritativeSourcesInOrder = readPlan.Model.DescriptorEdgeSources;
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

            for (
                var sourceIndex = 0;
                sourceIndex < descriptorProjectionPlan.SourcesInOrder.Length;
                sourceIndex++
            )
            {
                var source = descriptorProjectionPlan.SourcesInOrder[sourceIndex];
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

                var descriptorColumnModel = hydrationTablePlan.TableModel.Columns[
                    source.DescriptorIdColumnOrdinal
                ];
                var modelSource = ResolveDescriptorEdgeSourceByTableAndColumnOrThrow(
                    readPlan.Model,
                    source.Table,
                    descriptorColumnModel.ColumnName,
                    createException
                );

                ValidateDescriptorProjectionSourceContractOrThrow(
                    source,
                    descriptorColumnModel,
                    modelSource,
                    createException
                );

                if (compiledSourceCount > authoritativeSourcesInOrder.Count)
                {
                    throw createException(
                        $"descriptor projection source at plan index '{planIndex}', source index '{sourceIndex}' resolves to "
                            + $"{FormatDescriptorSource(modelSource)}, but authoritative DescriptorEdgeSources count is only '{authoritativeSourcesInOrder.Count}'"
                    );
                }

                var authoritativeSource = authoritativeSourcesInOrder[compiledSourceCount - 1];

                if (!DescriptorSourcesMatch(modelSource, authoritativeSource))
                {
                    throw createException(
                        $"descriptor projection source at plan index '{planIndex}', source index '{sourceIndex}' resolves to "
                            + $"{FormatDescriptorSource(modelSource)}, but authoritative DescriptorEdgeSources at index '{compiledSourceCount - 1}' requires "
                            + $"{FormatDescriptorSource(authoritativeSource)}"
                    );
                }
            }
        }

        if (compiledSourceCount == authoritativeSourcesInOrder.Count)
        {
            return;
        }

        throw createException(
            $"descriptor projection source count '{compiledSourceCount}' across plan count '{readPlan.DescriptorProjectionPlansInOrder.Length}' does not match DescriptorEdgeSources count '{authoritativeSourcesInOrder.Count}'; "
                + $"missing authoritative DescriptorEdgeSource(s): {string.Join(", ", authoritativeSourcesInOrder.Skip(compiledSourceCount).Select(FormatDescriptorSource))}"
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

    internal static void ValidateDocumentReferenceFkColumnOrThrow(
        DbTableName table,
        DbColumnModel columnModel,
        DocumentReferenceBinding binding,
        Func<string, Exception> createException
    )
    {
        var contextDescription =
            $"document-reference binding '{binding.ReferenceObjectPath.Canonical}' FK column";

        ValidateColumnKindOrThrow(
            columnModel,
            expectedKind: ColumnKind.DocumentFk,
            contextDescription,
            createException
        );
        ValidateStoredColumnOrThrow(columnModel, contextDescription, createException);
        ValidateDocumentReferenceBindingPathOrThrow(table, columnModel, binding, createException);
    }

    internal static void ValidateDocumentReferenceBindingPathOrThrow(
        DbTableName table,
        DbColumnModel columnModel,
        DocumentReferenceBinding binding,
        Func<string, Exception> createException
    )
    {
        ValidateProjectionBindingPathOrThrow(
            table,
            columnModel,
            binding.ReferenceObjectPath,
            $"document-reference binding '{binding.ReferenceObjectPath.Canonical}' FK column",
            $"{nameof(DocumentReferenceBinding)}.{nameof(DocumentReferenceBinding.ReferenceObjectPath)}",
            createException
        );
    }

    internal static void ValidateReferenceIdentityBindingPathOrThrow(
        DbTableName table,
        DbColumnModel columnModel,
        DocumentReferenceBinding binding,
        ReferenceIdentityBinding identityBinding,
        Func<string, Exception> createException
    )
    {
        ValidateProjectionBindingPathOrThrow(
            table,
            columnModel,
            identityBinding.ReferenceJsonPath,
            $"reference-identity binding '{identityBinding.ReferenceJsonPath.Canonical}' for reference '{binding.ReferenceObjectPath.Canonical}' column",
            $"{nameof(ReferenceIdentityBinding)}.{nameof(ReferenceIdentityBinding.ReferenceJsonPath)}",
            createException
        );
    }

    internal static void ValidateDescriptorEdgeSourcePathOrThrow(
        DbTableName table,
        DbColumnModel columnModel,
        DescriptorEdgeSource edgeSource,
        Func<string, Exception> createException
    )
    {
        ValidateProjectionBindingPathOrThrow(
            table,
            columnModel,
            edgeSource.DescriptorValuePath,
            $"descriptor edge source '{edgeSource.DescriptorValuePath.Canonical}' FK column",
            $"{nameof(DescriptorEdgeSource)}.{nameof(DescriptorEdgeSource.DescriptorValuePath)}",
            createException
        );
    }

    private static void ValidateReferenceProjectionBindingContractOrThrow(
        DbTableName table,
        ReferenceIdentityProjectionBinding binding,
        int bindingIndex,
        DbColumnModel fkColumnModel,
        DocumentReferenceBinding modelBinding,
        Func<string, Exception> createException
    )
    {
        if (binding.ReferenceObjectPath.Canonical != modelBinding.ReferenceObjectPath.Canonical)
        {
            throw createException(
                $"reference identity projection binding at index '{bindingIndex}' on table '{table}' has ReferenceObjectPath "
                    + $"'{binding.ReferenceObjectPath.Canonical}', but model binding requires '{modelBinding.ReferenceObjectPath.Canonical}'"
            );
        }

        if (fkColumnModel.ColumnName != modelBinding.FkColumn)
        {
            throw createException(
                $"reference identity projection binding '{modelBinding.ReferenceObjectPath.Canonical}' at index '{bindingIndex}' on table '{table}' targets FK column "
                    + $"'{fkColumnModel.ColumnName.Value}', but model binding requires '{modelBinding.FkColumn.Value}'"
            );
        }

        ValidateDocumentReferenceFkColumnOrThrow(table, fkColumnModel, modelBinding, createException);

        if (binding.TargetResource != modelBinding.TargetResource)
        {
            throw createException(
                $"reference identity projection binding '{modelBinding.ReferenceObjectPath.Canonical}' on table '{table}' targets "
                    + $"'{FormatResource(binding.TargetResource)}', but model binding requires '{FormatResource(modelBinding.TargetResource)}'"
            );
        }

        if (binding.IsIdentityComponent != modelBinding.IsIdentityComponent)
        {
            throw createException(
                $"reference identity projection binding '{modelBinding.ReferenceObjectPath.Canonical}' on table '{table}' has IsIdentityComponent "
                    + $"'{binding.IsIdentityComponent}', but model binding requires '{modelBinding.IsIdentityComponent}'"
            );
        }
    }

    private static void ValidateReferenceProjectionFieldContractOrThrow(
        DbTableName table,
        ReferenceIdentityProjectionFieldOrdinal fieldOrdinal,
        int fieldIndex,
        DbColumnModel fieldColumnModel,
        DocumentReferenceBinding modelBinding,
        ReferenceIdentityBinding modelIdentityBinding,
        Func<string, Exception> createException
    )
    {
        if (fieldOrdinal.ReferenceJsonPath.Canonical != modelIdentityBinding.ReferenceJsonPath.Canonical)
        {
            throw createException(
                $"reference identity projection field at index '{fieldIndex}' for reference '{modelBinding.ReferenceObjectPath.Canonical}' on table '{table}' has ReferenceJsonPath "
                    + $"'{fieldOrdinal.ReferenceJsonPath.Canonical}', but model binding requires '{modelIdentityBinding.ReferenceJsonPath.Canonical}'"
            );
        }

        if (fieldColumnModel.ColumnName != modelIdentityBinding.Column)
        {
            throw createException(
                $"reference identity projection field '{fieldOrdinal.ReferenceJsonPath.Canonical}' at index '{fieldIndex}' on table '{table}' targets column "
                    + $"'{fieldColumnModel.ColumnName.Value}', but model binding requires '{modelIdentityBinding.Column.Value}'"
            );
        }

        ValidateReferenceIdentityBindingPathOrThrow(
            table,
            fieldColumnModel,
            modelBinding,
            modelIdentityBinding,
            createException
        );
    }

    private static void ValidateDescriptorProjectionSourceContractOrThrow(
        DescriptorProjectionSource source,
        DbColumnModel descriptorColumnModel,
        DescriptorEdgeSource modelSource,
        Func<string, Exception> createException
    )
    {
        ValidateDescriptorEdgeSourcePathOrThrow(
            source.Table,
            descriptorColumnModel,
            modelSource,
            createException
        );

        if (source.DescriptorValuePath.Canonical != modelSource.DescriptorValuePath.Canonical)
        {
            throw createException(
                $"descriptor projection source for FK column '{descriptorColumnModel.ColumnName.Value}' on table '{source.Table}' has DescriptorValuePath "
                    + $"'{source.DescriptorValuePath.Canonical}', but model edge source requires '{modelSource.DescriptorValuePath.Canonical}'"
            );
        }

        if (source.DescriptorResource != modelSource.DescriptorResource)
        {
            throw createException(
                $"descriptor projection source '{modelSource.DescriptorValuePath.Canonical}' on table '{source.Table}' targets "
                    + $"'{FormatResource(source.DescriptorResource)}', but model edge source requires '{FormatResource(modelSource.DescriptorResource)}'"
            );
        }
    }

    private static DescriptorEdgeSource ResolveDescriptorEdgeSourceByTableAndColumnOrThrow(
        RelationalResourceModel model,
        DbTableName table,
        DbColumnName fkColumn,
        Func<string, Exception> createException
    )
    {
        DescriptorEdgeSource? resolvedSource = null;

        foreach (var edgeSource in model.DescriptorEdgeSources)
        {
            if (edgeSource.Table != table || edgeSource.FkColumn != fkColumn)
            {
                continue;
            }

            if (resolvedSource is not null)
            {
                throw createException(
                    $"multiple DescriptorEdgeSources match table '{table}' FK column '{fkColumn.Value}'"
                );
            }

            resolvedSource = edgeSource;
        }

        return resolvedSource
            ?? throw createException(
                $"no DescriptorEdgeSource matches table '{table}' FK column '{fkColumn.Value}'"
            );
    }

    private static void ValidateProjectionBindingPathOrThrow(
        DbTableName table,
        DbColumnModel columnModel,
        JsonPathExpression expectedPath,
        string dependencyDescription,
        string expectedPathDescription,
        Func<string, Exception> createException
    )
    {
        if (columnModel.SourceJsonPath?.Canonical == expectedPath.Canonical)
        {
            return;
        }

        throw createException(
            $"{dependencyDescription} '{columnModel.ColumnName.Value}' has DbColumnModel.SourceJsonPath '{columnModel.SourceJsonPath?.Canonical ?? "<null>"}', "
                + $"which does not match {expectedPathDescription} '{expectedPath.Canonical}'"
        );
    }

    private static void ValidateColumnKindOrThrow(
        DbColumnModel columnModel,
        ColumnKind expectedKind,
        string contextDescription,
        Func<string, Exception> createException
    )
    {
        if (columnModel.Kind == expectedKind)
        {
            return;
        }

        throw createException(
            $"{contextDescription} '{columnModel.ColumnName.Value}' has kind '{columnModel.Kind}'. Expected '{expectedKind}'"
        );
    }

    private static void ValidateStoredColumnOrThrow(
        DbColumnModel columnModel,
        string contextDescription,
        Func<string, Exception> createException
    )
    {
        if (columnModel.Storage is ColumnStorage.Stored)
        {
            return;
        }

        throw createException($"{contextDescription} '{columnModel.ColumnName.Value}' is not stored");
    }

    private static string FormatResource(QualifiedResourceName resource)
    {
        return $"{resource.ProjectName}.{resource.ResourceName}";
    }

    private static string FormatReferenceBindingOrder(IEnumerable<DocumentReferenceBinding> bindings)
    {
        return "["
            + string.Join(
                ", ",
                bindings.Select(static binding => $"'{binding.ReferenceObjectPath.Canonical}'")
            )
            + "]";
    }

    private static bool DescriptorSourcesMatch(DescriptorEdgeSource actual, DescriptorEdgeSource expected)
    {
        return actual.Table == expected.Table
            && actual.FkColumn == expected.FkColumn
            && actual.DescriptorValuePath.Canonical == expected.DescriptorValuePath.Canonical
            && actual.DescriptorResource == expected.DescriptorResource;
    }

    private static string FormatDescriptorSource(DescriptorEdgeSource source)
    {
        return $"'{source.DescriptorValuePath.Canonical}' on table '{source.Table}' FK column '{source.FkColumn.Value}'";
    }
}
