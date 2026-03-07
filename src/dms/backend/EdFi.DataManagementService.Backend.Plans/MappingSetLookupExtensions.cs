// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Lookup helpers for resource read/write plans with actionable failure reasons when a plan was intentionally omitted.
/// </summary>
public static class MappingSetLookupExtensions
{
    private const string DescriptorWriteStoryRef = "E07-S06 (06-descriptor-writes.md)";
    private const string DescriptorReadStoryRef = "E08-S05 (05-descriptor-endpoints.md)";
    private static readonly ConditionalWeakTable<
        MappingSet,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel>
    > ConcreteResourceModelsByResource = new();

    /// <summary>
    /// Gets the compiled write plan for <paramref name="resource" /> or throws a deterministic actionable exception.
    /// </summary>
    public static ResourceWritePlan GetWritePlanOrThrow(
        this MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        if (mappingSet.WritePlansByResource.TryGetValue(resource, out var writePlan))
        {
            return writePlan;
        }

        var concreteResourceModel = GetConcreteResourceModelOrThrow(mappingSet, resource);

        if (concreteResourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            throw new NotSupportedException(
                $"Write plan for resource '{FormatResource(resource)}' was intentionally omitted: "
                    + $"storage kind '{ResourceStorageKind.SharedDescriptorTable}' uses the descriptor write path instead of compiled relational-table write plans. "
                    + $"Next story: {DescriptorWriteStoryRef}."
            );
        }

        if (concreteResourceModel.StorageKind == ResourceStorageKind.RelationalTables)
        {
            throw new InvalidOperationException(
                $"Write plan lookup failed for resource '{FormatResource(resource)}' in mapping set "
                    + $"'{FormatMappingSetKey(mappingSet.Key)}': resource storage kind "
                    + $"'{ResourceStorageKind.RelationalTables}' should always have a compiled relational-table write plan, but no entry "
                    + "was found. This indicates an internal compilation/selection bug."
            );
        }

        throw new InvalidOperationException(
            $"Write plan lookup failed for resource '{FormatResource(resource)}' in mapping set "
                + $"'{FormatMappingSetKey(mappingSet.Key)}': storage kind '{concreteResourceModel.StorageKind}' "
                + "is not recognized."
        );
    }

    /// <summary>
    /// Gets the compiled read plan for <paramref name="resource" /> or throws a deterministic actionable exception.
    /// </summary>
    public static ResourceReadPlan GetReadPlanOrThrow(
        this MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        if (mappingSet.ReadPlansByResource.TryGetValue(resource, out var readPlan))
        {
            ThrowIfReadPlanHasInvalidCompiledProjectionMetadata(mappingSet, resource, readPlan);
            return readPlan;
        }

        var concreteResourceModel = GetConcreteResourceModelOrThrow(mappingSet, resource);

        if (concreteResourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            throw new NotSupportedException(
                $"Read plan for resource '{FormatResource(resource)}' was intentionally omitted: "
                    + $"storage kind '{ResourceStorageKind.SharedDescriptorTable}' uses the descriptor read path instead of compiled relational-table hydration plans. "
                    + $"Next story: {DescriptorReadStoryRef}."
            );
        }

        if (concreteResourceModel.StorageKind == ResourceStorageKind.RelationalTables)
        {
            throw new InvalidOperationException(
                $"Read plan lookup failed for resource '{FormatResource(resource)}' in mapping set "
                    + $"'{FormatMappingSetKey(mappingSet.Key)}': resource storage kind "
                    + $"'{ResourceStorageKind.RelationalTables}' should always have a compiled relational-table read plan, but no entry "
                    + "was found. This indicates an internal compilation/selection bug."
            );
        }

        throw new InvalidOperationException(
            $"Read plan lookup failed for resource '{FormatResource(resource)}' in mapping set "
                + $"'{FormatMappingSetKey(mappingSet.Key)}': storage kind '{concreteResourceModel.StorageKind}' "
                + "is not recognized."
        );
    }

    /// <summary>
    /// Rejects internally inconsistent read plans whose compiled projection metadata is missing or malformed.
    /// </summary>
    private static void ThrowIfReadPlanHasInvalidCompiledProjectionMetadata(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceReadPlan readPlan
    )
    {
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
                _ => throw new InvalidOperationException(
                    $"Read plan projection gating reached an invalid state for resource '{FormatResource(resource)}'."
                ),
            };

            throw CreateProjectionIntegrityException(mappingSet, resource, missingProjectionReason);
        }

        if (
            readPlan.Model.DocumentReferenceBindings.Count == 0
            && !readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.IsEmpty
        )
        {
            throw CreateProjectionIntegrityException(
                mappingSet,
                resource,
                "DocumentReferenceBindings are absent while ReferenceIdentityProjectionPlansInDependencyOrder is populated"
            );
        }

        if (
            readPlan.Model.DescriptorEdgeSources.Count == 0
            && !readPlan.DescriptorProjectionPlansInOrder.IsEmpty
        )
        {
            throw CreateProjectionIntegrityException(
                mappingSet,
                resource,
                "DescriptorEdgeSources are absent while DescriptorProjectionPlansInOrder is populated"
            );
        }

        var hydrationTablePlansByTable = BuildHydrationTablePlansByTableOrThrow(
            mappingSet,
            resource,
            readPlan
        );

        ValidateReferenceProjectionPlanContracts(mappingSet, resource, readPlan, hydrationTablePlansByTable);
        ValidateDescriptorProjectionPlanContracts(mappingSet, resource, readPlan, hydrationTablePlansByTable);
    }

    private static IReadOnlyDictionary<DbTableName, TableReadPlan> BuildHydrationTablePlansByTableOrThrow(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceReadPlan readPlan
    )
    {
        Dictionary<DbTableName, TableReadPlan> hydrationTablePlansByTable = [];

        foreach (var tablePlan in readPlan.TablePlansInDependencyOrder)
        {
            if (hydrationTablePlansByTable.TryAdd(tablePlan.TableModel.Table, tablePlan))
            {
                continue;
            }

            throw CreateProjectionIntegrityException(
                mappingSet,
                resource,
                $"compiled hydration table plans contain duplicate table '{tablePlan.TableModel.Table}'"
            );
        }

        return hydrationTablePlansByTable;
    }

    private static void ValidateReferenceProjectionPlanContracts(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceReadPlan readPlan,
        IReadOnlyDictionary<DbTableName, TableReadPlan> hydrationTablePlansByTable
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
                throw CreateProjectionIntegrityException(
                    mappingSet,
                    resource,
                    $"reference identity projection includes duplicate table '{projectionTablePlan.Table}'"
                );
            }

            if (
                !hydrationTablePlansByTable.TryGetValue(projectionTablePlan.Table, out var hydrationTablePlan)
            )
            {
                throw CreateProjectionIntegrityException(
                    mappingSet,
                    resource,
                    $"reference identity projection table '{projectionTablePlan.Table}' is not present in compiled table plans"
                );
            }

            foreach (var binding in projectionTablePlan.BindingsInOrder)
            {
                compiledBindingCount++;

                ThrowIfOrdinalIsOutOfRange(
                    mappingSet,
                    resource,
                    binding.FkColumnOrdinal,
                    hydrationTablePlan.TableModel.Columns.Count,
                    $"reference identity FK ordinal for table '{projectionTablePlan.Table}'"
                );

                foreach (var fieldOrdinal in binding.IdentityFieldOrdinalsInOrder)
                {
                    ThrowIfOrdinalIsOutOfRange(
                        mappingSet,
                        resource,
                        fieldOrdinal.ColumnOrdinal,
                        hydrationTablePlan.TableModel.Columns.Count,
                        $"reference identity field ordinal '{fieldOrdinal.ReferenceJsonPath.Canonical}' for table '{projectionTablePlan.Table}'"
                    );
                }
            }
        }

        if (compiledBindingCount == readPlan.Model.DocumentReferenceBindings.Count)
        {
            return;
        }

        throw CreateProjectionIntegrityException(
            mappingSet,
            resource,
            $"reference identity projection binding count '{compiledBindingCount}' does not match DocumentReferenceBindings count '{readPlan.Model.DocumentReferenceBindings.Count}'"
        );
    }

    private static void ValidateDescriptorProjectionPlanContracts(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceReadPlan readPlan,
        IReadOnlyDictionary<DbTableName, TableReadPlan> hydrationTablePlansByTable
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
                throw CreateProjectionIntegrityException(
                    mappingSet,
                    resource,
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
                    throw CreateProjectionIntegrityException(
                        mappingSet,
                        resource,
                        $"descriptor projection plan at index '{planIndex}' source '{source.DescriptorValuePath.Canonical}' references table '{source.Table}' that is not present in compiled table plans"
                    );
                }

                ThrowIfOrdinalIsOutOfRange(
                    mappingSet,
                    resource,
                    source.DescriptorIdColumnOrdinal,
                    hydrationTablePlan.TableModel.Columns.Count,
                    $"descriptor projection plan at index '{planIndex}' source ordinal '{source.DescriptorValuePath.Canonical}' for table '{source.Table}'"
                );
            }
        }

        if (compiledSourceCount == readPlan.Model.DescriptorEdgeSources.Count)
        {
            return;
        }

        throw CreateProjectionIntegrityException(
            mappingSet,
            resource,
            $"descriptor projection source count '{compiledSourceCount}' across plan count '{readPlan.DescriptorProjectionPlansInOrder.Length}' does not match DescriptorEdgeSources count '{readPlan.Model.DescriptorEdgeSources.Count}'"
        );
    }

    private static void ThrowIfOrdinalIsOutOfRange(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        int ordinal,
        int count,
        string context
    )
    {
        if ((uint)ordinal < (uint)count)
        {
            return;
        }

        throw CreateProjectionIntegrityException(
            mappingSet,
            resource,
            $"ordinal '{ordinal}' for {context} is out of range for hydration select-list columns (count: {count})"
        );
    }

    private static InvalidOperationException CreateProjectionIntegrityException(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        string reason
    )
    {
        return new InvalidOperationException(
            $"Read plan lookup failed for resource '{FormatResource(resource)}' in mapping set "
                + $"'{FormatMappingSetKey(mappingSet.Key)}': compiled relational-table read plan has invalid projection metadata. "
                + $"{reason}. This indicates an internal compilation/selection bug."
        );
    }

    /// <summary>
    /// Resolves the concrete resource model from the mapping set's canonical resource list or throws a deterministic error.
    /// </summary>
    private static ConcreteResourceModel GetConcreteResourceModelOrThrow(
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        var concreteResourcesByResource = ConcreteResourceModelsByResource.GetValue(
            mappingSet,
            static staticMappingSet =>
            {
                var resourcesByName = new Dictionary<QualifiedResourceName, ConcreteResourceModel>();

                foreach (var concreteResourceModel in staticMappingSet.Model.ConcreteResourcesInNameOrder)
                {
                    var resource = concreteResourceModel.RelationalModel.Resource;

                    if (!resourcesByName.TryAdd(resource, concreteResourceModel))
                    {
                        throw new InvalidOperationException(
                            $"Mapping set '{FormatMappingSetKey(staticMappingSet.Key)}' contains duplicate resource "
                                + $"'{FormatResource(resource)}' in ConcreteResourcesInNameOrder."
                        );
                    }
                }

                return resourcesByName.ToFrozenDictionary();
            }
        );

        if (concreteResourcesByResource.TryGetValue(resource, out var concreteResourceModel))
        {
            return concreteResourceModel;
        }

        throw new KeyNotFoundException(
            $"Mapping set '{FormatMappingSetKey(mappingSet.Key)}' does not contain resource '{FormatResource(resource)}' in ConcreteResourcesInNameOrder."
        );
    }

    /// <summary>
    /// Formats a qualified resource name as <c>{ProjectName}.{ResourceName}</c> for diagnostics.
    /// </summary>
    private static string FormatResource(QualifiedResourceName resource)
    {
        return $"{resource.ProjectName}.{resource.ResourceName}";
    }

    /// <summary>
    /// Formats a mapping set key as <c>{EffectiveSchemaHash}/{Dialect}/{RelationalMappingVersion}</c> for diagnostics.
    /// </summary>
    private static string FormatMappingSetKey(MappingSetKey key)
    {
        return $"{key.EffectiveSchemaHash}/{key.Dialect}/{key.RelationalMappingVersion}";
    }
}
