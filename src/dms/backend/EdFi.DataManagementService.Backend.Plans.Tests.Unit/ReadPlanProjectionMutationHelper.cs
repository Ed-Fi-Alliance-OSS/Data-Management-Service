// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class ReadPlanProjectionMutationHelper
{
    public static RelationalResourceModel CreateModelWithDocumentReferenceBindingTargetResource(
        RelationalResourceModel model,
        QualifiedResourceName targetResource
    )
    {
        var bindings = model.DocumentReferenceBindings.ToArray();

        bindings[0] = bindings[0] with { TargetResource = targetResource };

        return model with
        {
            DocumentReferenceBindings = [.. bindings],
        };
    }

    public static RelationalResourceModel CreateModelWithDescriptorEdgeSourceDescriptorResource(
        RelationalResourceModel model,
        QualifiedResourceName descriptorResource,
        int edgeSourceIndex = 0
    )
    {
        var edgeSources = model.DescriptorEdgeSources.ToArray();

        edgeSources[edgeSourceIndex] = edgeSources[edgeSourceIndex] with
        {
            DescriptorResource = descriptorResource,
        };

        return model with
        {
            DescriptorEdgeSources = [.. edgeSources],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithReferenceIdentityComponent(
        ResourceReadPlan readPlan,
        bool isIdentityComponent
    )
    {
        var projectionTablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        var bindings = projectionTablePlan.BindingsInOrder.ToArray();

        bindings[0] = bindings[0] with { IsIdentityComponent = isIdentityComponent };

        return readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder = [.. bindings],
                },
            ],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithoutReferenceIdentityProjectionPlans(
        ResourceReadPlan readPlan
    )
    {
        return readPlan with { ReferenceIdentityProjectionPlansInDependencyOrder = [] };
    }

    public static ResourceReadPlan CreateReadPlanWithoutDescriptorProjectionPlans(ResourceReadPlan readPlan)
    {
        return readPlan with { DescriptorProjectionPlansInOrder = [] };
    }

    public static ResourceReadPlan CreateReadPlanWithoutProjectionPlans(ResourceReadPlan readPlan)
    {
        return readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder = [],
            DescriptorProjectionPlansInOrder = [],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithUnexpectedReferenceIdentityProjectionPlans(
        ResourceReadPlan readPlan,
        ResourceReadPlan projectionSourceReadPlan
    )
    {
        return readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
                projectionSourceReadPlan.ReferenceIdentityProjectionPlansInDependencyOrder,
        };
    }

    public static ResourceReadPlan CreateReadPlanWithUnexpectedDescriptorProjectionPlans(
        ResourceReadPlan readPlan,
        ResourceReadPlan projectionSourceReadPlan
    )
    {
        return readPlan with
        {
            DescriptorProjectionPlansInOrder = projectionSourceReadPlan.DescriptorProjectionPlansInOrder,
        };
    }

    public static ResourceReadPlan CreateReadPlanWithReferenceTargetResource(
        ResourceReadPlan readPlan,
        QualifiedResourceName targetResource
    )
    {
        var projectionTablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        var bindings = projectionTablePlan.BindingsInOrder.ToArray();

        bindings[0] = bindings[0] with { TargetResource = targetResource };

        return readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder = [.. bindings],
                },
            ],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithDescriptorProjectionSourceDescriptorResource(
        ResourceReadPlan readPlan,
        QualifiedResourceName descriptorResource,
        int sourceIndex,
        int planIndex = 0
    )
    {
        var descriptorProjectionPlans = readPlan.DescriptorProjectionPlansInOrder.ToArray();
        var descriptorProjectionPlan = descriptorProjectionPlans[planIndex];
        var sources = descriptorProjectionPlan.SourcesInOrder.ToArray();

        sources[sourceIndex] = sources[sourceIndex] with { DescriptorResource = descriptorResource };
        descriptorProjectionPlans[planIndex] = descriptorProjectionPlan with
        {
            SourcesInOrder = [.. sources],
        };

        return readPlan with
        {
            DescriptorProjectionPlansInOrder = [.. descriptorProjectionPlans],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithDescriptorProjectionSourceTable(
        ResourceReadPlan readPlan,
        DbTableName table,
        int sourceIndex = 0,
        int planIndex = 0
    )
    {
        var descriptorProjectionPlans = readPlan.DescriptorProjectionPlansInOrder.ToArray();
        var descriptorProjectionPlan = descriptorProjectionPlans[planIndex];
        var sources = descriptorProjectionPlan.SourcesInOrder.ToArray();

        sources[sourceIndex] = sources[sourceIndex] with { Table = table };
        descriptorProjectionPlans[planIndex] = descriptorProjectionPlan with
        {
            SourcesInOrder = [.. sources],
        };

        return readPlan with
        {
            DescriptorProjectionPlansInOrder = [.. descriptorProjectionPlans],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithDescriptorProjectionSourceOrdinal(
        ResourceReadPlan readPlan,
        int descriptorIdColumnOrdinal,
        int sourceIndex = 0,
        int planIndex = 0
    )
    {
        var descriptorProjectionPlans = readPlan.DescriptorProjectionPlansInOrder.ToArray();
        var descriptorProjectionPlan = descriptorProjectionPlans[planIndex];
        var sources = descriptorProjectionPlan.SourcesInOrder.ToArray();

        sources[sourceIndex] = sources[sourceIndex] with
        {
            DescriptorIdColumnOrdinal = descriptorIdColumnOrdinal,
        };
        descriptorProjectionPlans[planIndex] = descriptorProjectionPlan with
        {
            SourcesInOrder = [.. sources],
        };

        return readPlan with
        {
            DescriptorProjectionPlansInOrder = [.. descriptorProjectionPlans],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithDuplicatedReferenceProjectionBinding(
        ResourceReadPlan readPlan,
        int sourceIndex,
        int targetIndex
    )
    {
        var projectionTablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Single();
        var bindings = projectionTablePlan.BindingsInOrder.ToArray();

        bindings[targetIndex] = bindings[sourceIndex];

        return readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder = [.. bindings],
                },
            ],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithSwappedReferenceProjectionBindings(
        ResourceReadPlan readPlan
    )
    {
        var projectionTablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Single();
        var bindings = projectionTablePlan.BindingsInOrder.ToArray();
        var firstBinding = bindings[0];

        bindings[0] = bindings[1];
        bindings[1] = firstBinding;

        return readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder = [.. bindings],
                },
            ],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithDuplicatedReferenceProjectionField(
        ResourceReadPlan readPlan,
        int sourceIndex,
        int targetIndex
    )
    {
        var projectionTablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Single();
        var binding = projectionTablePlan.BindingsInOrder.Single();
        var identityFieldOrdinals = binding.IdentityFieldOrdinalsInOrder.ToArray();

        identityFieldOrdinals[targetIndex] = identityFieldOrdinals[sourceIndex];

        return readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder =
                    [
                        binding with
                        {
                            IdentityFieldOrdinalsInOrder = [.. identityFieldOrdinals],
                        },
                    ],
                },
            ],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithOmittedReferenceProjectionField(
        ResourceReadPlan readPlan,
        int omittedFieldIndex
    )
    {
        var projectionTablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Single();
        var binding = projectionTablePlan.BindingsInOrder.Single();
        var identityFieldOrdinals = binding
            .IdentityFieldOrdinalsInOrder.Where((_, index) => index != omittedFieldIndex)
            .ToArray();

        return readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder =
                    [
                        binding with
                        {
                            IdentityFieldOrdinalsInOrder = [.. identityFieldOrdinals],
                        },
                    ],
                },
            ],
        };
    }

    public static RelationalResourceModel CreateModelWithNullReferenceIdentityColumnScalarType(
        RelationalResourceModel model
    )
    {
        var binding = model.DocumentReferenceBindings[0];
        var identityColumn = binding.IdentityBindings[0].Column;

        var tables = model
            .TablesInDependencyOrder.Select(table =>
                table.Table.Equals(binding.Table)
                    ? table with
                    {
                        Columns =
                        [
                            .. table.Columns.Select(column =>
                                column.ColumnName.Equals(identityColumn)
                                    ? column with
                                    {
                                        ScalarType = null,
                                    }
                                    : column
                            ),
                        ],
                    }
                    : table
            )
            .ToArray();

        return model with
        {
            Root = tables.Single(table => table.Table.Equals(model.Root.Table)),
            TablesInDependencyOrder = [.. tables],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithReferenceProjectionFieldScalarType(
        ResourceReadPlan readPlan,
        RelationalScalarType scalarType
    )
    {
        var projectionTablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Single();
        var binding = projectionTablePlan.BindingsInOrder.Single();
        var identityFieldOrdinals = binding.IdentityFieldOrdinalsInOrder.ToArray();

        identityFieldOrdinals[0] = identityFieldOrdinals[0] with { ScalarType = scalarType };

        return readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder =
                    [
                        binding with
                        {
                            IdentityFieldOrdinalsInOrder = [.. identityFieldOrdinals],
                        },
                    ],
                },
            ],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithSwappedReferenceProjectionFields(
        ResourceReadPlan readPlan
    )
    {
        var projectionTablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Single();
        var binding = projectionTablePlan.BindingsInOrder.Single();
        var identityFieldOrdinals = binding.IdentityFieldOrdinalsInOrder.ToArray();
        var firstField = identityFieldOrdinals[0];

        identityFieldOrdinals[0] = identityFieldOrdinals[1];
        identityFieldOrdinals[1] = firstField;

        return readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder =
                    [
                        binding with
                        {
                            IdentityFieldOrdinalsInOrder = [.. identityFieldOrdinals],
                        },
                    ],
                },
            ],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithSwappedReferenceProjectionTablePlans(
        ResourceReadPlan readPlan
    )
    {
        var projectionTablePlans = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.ToArray();
        var firstTablePlan = projectionTablePlans[0];

        projectionTablePlans[0] = projectionTablePlans[1];
        projectionTablePlans[1] = firstTablePlan;

        return readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder = [.. projectionTablePlans],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithAppendedReferenceProjectionTablePlan(
        ResourceReadPlan readPlan,
        int sourceIndex
    )
    {
        var projectionTablePlans = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.ToArray();

        return readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                .. projectionTablePlans,
                projectionTablePlans[sourceIndex],
            ],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithDuplicatedDescriptorProjectionSource(
        ResourceReadPlan readPlan,
        int sourceIndex,
        int targetIndex
    )
    {
        var descriptorProjectionPlan = readPlan.DescriptorProjectionPlansInOrder.Single();
        var sources = descriptorProjectionPlan.SourcesInOrder.ToArray();

        sources[targetIndex] = sources[sourceIndex];

        return readPlan with
        {
            DescriptorProjectionPlansInOrder =
            [
                descriptorProjectionPlan with
                {
                    SourcesInOrder = [.. sources],
                },
            ],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithSwappedDescriptorProjectionSources(
        ResourceReadPlan readPlan
    )
    {
        var descriptorProjectionPlan = readPlan.DescriptorProjectionPlansInOrder.Single();
        var sources = descriptorProjectionPlan.SourcesInOrder.ToArray();
        var firstSource = sources[0];

        sources[0] = sources[1];
        sources[1] = firstSource;

        return readPlan with
        {
            DescriptorProjectionPlansInOrder =
            [
                descriptorProjectionPlan with
                {
                    SourcesInOrder = [.. sources],
                },
            ],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithSwappedDescriptorProjectionPlans(
        ResourceReadPlan readPlan
    )
    {
        var descriptorProjectionPlans = readPlan.DescriptorProjectionPlansInOrder.ToArray();
        var firstPlan = descriptorProjectionPlans[0];

        descriptorProjectionPlans[0] = descriptorProjectionPlans[1];
        descriptorProjectionPlans[1] = firstPlan;

        return readPlan with
        {
            DescriptorProjectionPlansInOrder = [.. descriptorProjectionPlans],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithAppendedDescriptorProjectionSource(
        ResourceReadPlan readPlan,
        int sourceIndex,
        int planIndex = 0
    )
    {
        var descriptorProjectionPlans = readPlan.DescriptorProjectionPlansInOrder.ToArray();
        var descriptorProjectionPlan = descriptorProjectionPlans[planIndex];
        var sources = descriptorProjectionPlan.SourcesInOrder.ToArray();

        descriptorProjectionPlans[planIndex] = descriptorProjectionPlan with
        {
            SourcesInOrder = [.. sources, sources[sourceIndex]],
        };

        return readPlan with
        {
            DescriptorProjectionPlansInOrder = [.. descriptorProjectionPlans],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithOmittedDescriptorProjectionSource(
        ResourceReadPlan readPlan,
        int omittedSourceIndex,
        int planIndex = 0
    )
    {
        var descriptorProjectionPlans = readPlan.DescriptorProjectionPlansInOrder.ToArray();
        var descriptorProjectionPlan = descriptorProjectionPlans[planIndex];
        var sources = descriptorProjectionPlan
            .SourcesInOrder.Where((_, index) => index != omittedSourceIndex)
            .ToArray();

        descriptorProjectionPlans[planIndex] = descriptorProjectionPlan with
        {
            SourcesInOrder = [.. sources],
        };

        return readPlan with
        {
            DescriptorProjectionPlansInOrder = [.. descriptorProjectionPlans],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithDescriptorProjectionSourceCount(
        ResourceReadPlan readPlan,
        int sourceCount,
        int planIndex = 0
    )
    {
        var descriptorProjectionPlans = readPlan.DescriptorProjectionPlansInOrder.ToArray();
        var descriptorProjectionPlan = descriptorProjectionPlans[planIndex];

        descriptorProjectionPlans[planIndex] = descriptorProjectionPlan with
        {
            SourcesInOrder = [.. descriptorProjectionPlan.SourcesInOrder.Take(sourceCount)],
        };

        return readPlan with
        {
            DescriptorProjectionPlansInOrder = [.. descriptorProjectionPlans],
        };
    }

    public static ResourceReadPlan CreateReadPlanWithSplitDescriptorProjectionPlans(ResourceReadPlan readPlan)
    {
        var descriptorProjectionPlan = readPlan.DescriptorProjectionPlansInOrder.Single();
        var sources = descriptorProjectionPlan.SourcesInOrder.ToArray();

        return readPlan with
        {
            DescriptorProjectionPlansInOrder =
            [
                descriptorProjectionPlan with
                {
                    SelectByKeysetSql = "SELECT descriptor_plan_0;\n",
                    SourcesInOrder = [sources[0]],
                },
                descriptorProjectionPlan with
                {
                    SelectByKeysetSql = "SELECT descriptor_plan_1;\n",
                    SourcesInOrder = [.. sources[1..]],
                },
            ],
        };
    }
}
