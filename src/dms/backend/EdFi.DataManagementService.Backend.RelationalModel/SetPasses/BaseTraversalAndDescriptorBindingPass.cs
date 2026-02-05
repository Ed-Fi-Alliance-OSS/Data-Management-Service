// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Executes base schema traversal and descriptor binding for each concrete, non-extension resource in the
/// effective schema set.
/// </summary>
public sealed class BaseTraversalAndDescriptorBindingPass : IRelationalModelSetPass
{
    private readonly RelationalModelBuilderPipeline _pipeline;

    /// <summary>
    /// Creates a new base traversal pass using the canonical pipeline steps.
    /// </summary>
    public BaseTraversalAndDescriptorBindingPass()
        : this(CreateDefaultPipeline()) { }

    /// <summary>
    /// Creates a new base traversal pass using the supplied pipeline.
    /// </summary>
    /// <param name="pipeline">The per-resource pipeline to execute.</param>
    public BaseTraversalAndDescriptorBindingPass(RelationalModelBuilderPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        _pipeline = pipeline;
    }

    /// <summary>
    /// Executes the base traversal + descriptor binding across all concrete, non-extension resources.
    /// </summary>
    /// <param name="context">The shared set-level builder context.</param>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Dictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint = new(StringComparer.Ordinal);

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            if (IsResourceExtension(resourceContext))
            {
                continue;
            }

            var projectSchema = resourceContext.Project.ProjectSchema;
            var resourceKey = new QualifiedResourceName(
                projectSchema.ProjectName,
                resourceContext.ResourceName
            );
            var descriptorPaths = context.GetDescriptorPathsForResource(resourceKey);
            var apiSchemaRoot = GetApiSchemaRoot(
                apiSchemaRootsByProjectEndpoint,
                projectSchema.ProjectEndpointName,
                resourceContext.Project.EffectiveProject.ProjectSchema,
                cloneProjectSchema: false
            );

            var builderContext = new RelationalModelBuilderContext
            {
                ApiSchemaRoot = apiSchemaRoot,
                ResourceEndpointName = resourceContext.ResourceEndpointName,
                DescriptorPathSource = DescriptorPathSource.Precomputed,
                DescriptorPathsByJsonPath = descriptorPaths,
                OverrideCollisionDetector = context.OverrideCollisionDetector,
            };

            var result = _pipeline.Run(builderContext);
            context.RegisterResourceBuilderContext(resourceKey, builderContext);
            var resourceKeyEntry = context.GetResourceKeyEntry(resourceKey);

            context.ConcreteResourcesInNameOrder.Add(
                new ConcreteResourceModel(
                    resourceKeyEntry,
                    result.ResourceModel.StorageKind,
                    result.ResourceModel
                )
            );
            context.RegisterExtensionSitesForResource(resourceKey, result.ExtensionSites);
        }
    }

    /// <summary>
    /// Creates the canonical per-resource pipeline used to derive base tables, columns, descriptor bindings,
    /// and extension site metadata.
    /// </summary>
    private static RelationalModelBuilderPipeline CreateDefaultPipeline()
    {
        IRelationalModelBuilderStep[] steps =
        [
            new ExtractInputsStep(),
            new ValidateJsonSchemaStep(),
            new DiscoverExtensionSitesStep(),
            new DeriveTableScopesAndKeysStep(),
            new DeriveColumnsAndBindDescriptorEdgesStep(),
            new CanonicalizeOrderingStep(),
        ];

        return new RelationalModelBuilderPipeline(steps);
    }
}
