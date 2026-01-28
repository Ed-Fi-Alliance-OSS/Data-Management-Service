// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Executes base schema traversal and descriptor binding for each concrete, non-extension resource in the
/// effective schema set.
/// </summary>
public sealed class BaseTraversalAndDescriptorBindingRelationalModelSetPass : IRelationalModelSetPass
{
    private readonly RelationalModelBuilderPipeline _pipeline;

    /// <summary>
    /// Creates a new base traversal pass using the canonical pipeline steps.
    /// </summary>
    public BaseTraversalAndDescriptorBindingRelationalModelSetPass()
        : this(CreateDefaultPipeline()) { }

    /// <summary>
    /// Creates a new base traversal pass using the supplied pipeline.
    /// </summary>
    /// <param name="pipeline">The per-resource pipeline to execute.</param>
    public BaseTraversalAndDescriptorBindingRelationalModelSetPass(RelationalModelBuilderPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        _pipeline = pipeline;
    }

    /// <summary>
    /// The explicit order for the base traversal + descriptor binding pass.
    /// </summary>
    public int Order { get; } = 0;

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
                resourceContext.Project.EffectiveProject.ProjectSchema
            );

            var builderContext = new RelationalModelBuilderContext
            {
                ApiSchemaRoot = apiSchemaRoot,
                ResourceEndpointName = resourceContext.ResourceEndpointName,
                DescriptorPathSource = DescriptorPathSource.Precomputed,
                DescriptorPathsByJsonPath = descriptorPaths,
            };

            var result = _pipeline.Run(builderContext);
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
    /// Reads the <c>isResourceExtension</c> flag to determine whether the schema entry represents a
    /// resource-extension document rather than a concrete base resource.
    /// </summary>
    private static bool IsResourceExtension(ConcreteResourceSchemaContext resourceContext)
    {
        if (
            !resourceContext.ResourceSchema.TryGetPropertyValue(
                "isResourceExtension",
                out var resourceExtensionNode
            ) || resourceExtensionNode is null
        )
        {
            throw new InvalidOperationException(
                $"Expected isResourceExtension to be on ResourceSchema for resource "
                    + $"'{resourceContext.Project.ProjectSchema.ProjectName}:{resourceContext.ResourceName}', "
                    + "invalid ApiSchema."
            );
        }

        return resourceExtensionNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<bool>(),
            _ => throw new InvalidOperationException(
                $"Expected isResourceExtension to be a boolean for resource "
                    + $"'{resourceContext.Project.ProjectSchema.ProjectName}:{resourceContext.ResourceName}', "
                    + "invalid ApiSchema."
            ),
        };
    }

    /// <summary>
    /// Builds (and caches) a minimal <c>ApiSchema.json</c>-shaped root node for the per-resource pipeline.
    /// </summary>
    /// <param name="apiSchemaRootsByProjectEndpoint">Cache of root nodes by project endpoint name.</param>
    /// <param name="projectEndpointName">The project endpoint name for the resource.</param>
    /// <param name="projectSchema">The project schema node.</param>
    /// <returns>A root object containing the <c>projectSchema</c> property.</returns>
    private static JsonObject GetApiSchemaRoot(
        IDictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint,
        string projectEndpointName,
        JsonObject projectSchema
    )
    {
        if (apiSchemaRootsByProjectEndpoint.TryGetValue(projectEndpointName, out var apiSchemaRoot))
        {
            return apiSchemaRoot;
        }

        apiSchemaRoot = new JsonObject { ["projectSchema"] = projectSchema };

        apiSchemaRootsByProjectEndpoint[projectEndpointName] = apiSchemaRoot;

        return apiSchemaRoot;
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
