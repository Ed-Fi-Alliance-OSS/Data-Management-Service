// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using QuickGraph;

namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

internal interface IResourceDependencyGraphFactory
{
    BidirectionalGraph<ResourceDependencyGraphVertex, ResourceDependencyGraphEdge> Create();
}

internal class ResourceDependencyGraphFactory(
    IApiSchemaProvider _apiSchemaProvider,
    IEnumerable<IResourceDependencyGraphTransformer> _graphTransformers,
    ILogger<ResourceLoadOrderCalculator> _logger
) : IResourceDependencyGraphFactory
{
    /// <summary>
    /// Builds a dependency graph where vertices represent resources and edges represent their references.
    /// </summary>
    public BidirectionalGraph<ResourceDependencyGraphVertex, ResourceDependencyGraphEdge> Create()
    {
        ApiSchemaDocumentNodes apiSchemaNodes = _apiSchemaProvider.GetApiSchemaNodes();

        ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaDocuments(apiSchemaNodes, _logger);

        ProjectSchema[] projectSchemas = apiSchemaDocuments.GetAllProjectSchemas();
        ProjectName coreProjectName = apiSchemaDocuments.GetCoreProjectSchema().ProjectName;

        List<(ProjectSchema projectSchema, ResourceSchema resourceSchema)> allResourceSchemaNodeInfos =
            projectSchemas
                .SelectMany(projectSchema =>
                    projectSchema
                        .GetAllResourceSchemaNodes()
                        .Select(jn => (projectSchema, resourceSchema: new ResourceSchema(jn)))
                )
                .ToList();

        List<ResourceDependencyGraphVertex> allResourceVertices = allResourceSchemaNodeInfos
            .Select(t =>
            {
                return new ResourceDependencyGraphVertex(
                    t.projectSchema.ProjectName,
                    t.projectSchema.ProjectEndpointName,
                    t.resourceSchema.ResourceName,
                    t.resourceSchema.IsResourceExtension
                        ? default
                        : t.projectSchema.GetEndpointNameFromResourceName(t.resourceSchema.ResourceName),
                    t.resourceSchema.IsResourceExtension,
                    t.resourceSchema.IsSubclass,
                    t.resourceSchema.IsSubclass ? t.resourceSchema.SuperclassResourceName : default,
                    t.resourceSchema.IsSchoolYearEnumeration
                );
            })
            .ToList();

        List<ResourceDependencyGraphVertex> nonExtensionResourceVertices = allResourceVertices
            .Where(vertex => !vertex.IsResourceExtension)
            .ToList();

        Dictionary<ProjectName, ProjectSchema> projectByName = projectSchemas.ToDictionary(projectSchema =>
            projectSchema.ProjectName
        );

        Dictionary<FullResourceName, ResourceDependencyGraphVertex> vertexByFullResourceName =
            allResourceVertices.ToDictionary(vertex => new FullResourceName(
                vertex.ProjectName,
                vertex.ResourceName
            ));

        ILookup<ResourceName, ResourceDependencyGraphVertex> extensionVertexBySuperclassName =
            nonExtensionResourceVertices
                .Where(vertex => vertex.IsSubclass)
                .ToLookup(vertex => vertex.SuperclassResourceName);

        IEnumerable<ResourceDependencyGraphEdge> edges = allResourceSchemaNodeInfos
            .SelectMany(t =>
            {
                var vertex = vertexByFullResourceName[
                    new FullResourceName(t.projectSchema.ProjectName, t.resourceSchema.ResourceName)
                ];

                return t
                    .resourceSchema.DocumentPaths.Where(documentPath => documentPath.IsReference)
                    .SelectMany(reference =>
                        BuildVertexEdges(
                            vertex,
                            reference.ProjectName,
                            reference.ResourceName,
                            reference.IsRequired,
                            coreProjectName,
                            projectByName,
                            vertexByFullResourceName,
                            extensionVertexBySuperclassName
                        )
                    );
            })
            .Distinct();

        var graph = new BidirectionalGraph<ResourceDependencyGraphVertex, ResourceDependencyGraphEdge>();
        graph.AddVertexRange(nonExtensionResourceVertices);
        graph.AddEdgeRange(edges);

        foreach (var schoolYearVertex in graph.Vertices.Where(vertex => vertex.IsSchoolYearEnumeration))
        {
            graph.RemoveVertex(schoolYearVertex);
        }

        // Apply predefined graph transformations
        foreach (var graphTransformer in _graphTransformers)
        {
            graphTransformer.Transform(graph);
        }

        graph.BreakCycles(edge => !edge.IsRequired, _logger);
        return graph;
    }

    /// <summary>
    /// Builds the edges that represent the given resource reference.
    /// If the reference points to an abstract resource, an edge is returned pointing to each subclass resource.
    /// </summary>
    private static IEnumerable<ResourceDependencyGraphEdge> BuildVertexEdges(
        ResourceDependencyGraphVertex source,
        ProjectName referenceProjectName,
        ResourceName referenceResourceName,
        bool referenceIsRequired,
        ProjectName coreProjectName,
        Dictionary<ProjectName, ProjectSchema> projectByName,
        Dictionary<FullResourceName, ResourceDependencyGraphVertex> vertexByFullResourceName,
        ILookup<ResourceName, ResourceDependencyGraphVertex> verticesBySuperclassName
    )
    {
        if (source.IsResourceExtension)
        {
            // Change the source vertex to be the Core resource
            var coreResource = new FullResourceName(coreProjectName, source.ResourceName);
            source =
                vertexByFullResourceName.GetValueOrDefault(coreResource)
                ?? throw new InvalidOperationException(
                    $"A vertex representing the resource '{coreResource}' wasn't found in the resource dependency graph."
                );
        }

        bool referencesAbstractResource = projectByName[referenceProjectName]
            .AbstractResources.Any(abstractResource =>
                abstractResource.ResourceName == referenceResourceName
            );

        if (referencesAbstractResource)
        {
            // Include all subclass types of the referenced abstract resource
            foreach (var subclassVertex in verticesBySuperclassName[referenceResourceName])
            {
                yield return new ResourceDependencyGraphEdge(subclassVertex, source, referenceIsRequired);
            }
        }
        else
        {
            var referencedVertex = vertexByFullResourceName[
                new FullResourceName(referenceProjectName, referenceResourceName)
            ];
            yield return new ResourceDependencyGraphEdge(referencedVertex, source, referenceIsRequired);
        }
    }
}
