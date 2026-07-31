// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Test affordance for fixtures that compile ONE hand-built resource model in isolation. The
/// document-reference auxiliary lookup resolves each binding's <c>TargetResource</c> through a
/// cross-resource target map that only whole-schema compilation
/// (<see cref="MappingSetCompiler"/>) can build, so an isolated fixture has to declare its own.
/// </summary>
/// <remarks>
/// The derived map treats every target as a concrete resource whose root table is named after the
/// resource inside the compiling model's own physical schema — the shape hand-built fixtures use.
/// Fixtures that need an abstract target (a <c>{Abstract}Identity</c> join plus the stored
/// <c>Discriminator</c> column) declare it explicitly instead of using this helper, and fixtures
/// driven by a real derived model set use
/// <see cref="MappingSetCompiler.BuildDocumentReferenceLookupTargets"/>.
/// </remarks>
internal static class DocumentReferenceLookupTargetTestMap
{
    /// <summary>
    /// Derives the isolated-resource target map from a model's own document-reference bindings.
    /// </summary>
    public static IReadOnlyDictionary<
        QualifiedResourceName,
        DocumentReferenceLookupTarget
    > ForIsolatedResource(RelationalResourceModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var targetsByResource = new Dictionary<QualifiedResourceName, DocumentReferenceLookupTarget>();

        foreach (var binding in model.DocumentReferenceBindings)
        {
            targetsByResource.TryAdd(
                binding.TargetResource,
                new DocumentReferenceLookupTarget(
                    LookupTable: new DbTableName(model.PhysicalSchema, binding.TargetResource.ResourceName),
                    DiscriminatorLiteral: $"{binding.TargetResource.ProjectName}:{binding.TargetResource.ResourceName}"
                )
            );
        }

        return targetsByResource;
    }

    /// <summary>
    /// Compiles a read plan for one isolated resource model, supplying
    /// <see cref="ForIsolatedResource"/> as the document-reference lookup target map.
    /// </summary>
    public static ResourceReadPlan CompileReadPlan(SqlDialect dialect, RelationalResourceModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new ReadPlanCompiler(dialect, ForIsolatedResource(model)).Compile(model);
    }

    /// <summary>
    /// Creates a compiler pre-loaded with <paramref name="model"/>'s isolated target map, for
    /// fixtures that need the compiler instance itself (e.g., <c>TryCompile</c> coverage).
    /// </summary>
    public static ReadPlanCompiler CreateCompiler(SqlDialect dialect, RelationalResourceModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new ReadPlanCompiler(dialect, ForIsolatedResource(model));
    }
}
