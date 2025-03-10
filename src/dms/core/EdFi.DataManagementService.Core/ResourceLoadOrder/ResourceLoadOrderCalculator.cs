// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using QuickGraph;

namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

/// <summary>
/// Provides a mean to calculate the order in which resources should be loaded.
/// </summary>
internal class ResourceLoadOrderCalculator(
    IApiSchemaProvider apiSchemaProvider,
    IEnumerable<IResourceDependencyGraphTransformer> graphTransformers,
    IEnumerable<IResourceLoadOrderTransformer> orderTransformers,
    ILogger<ResourceLoadOrderCalculator> logger
)
{
    /// <summary>
    /// Calculates the order in which resources should be loaded based on their dependencies.
    /// </summary>
    public IEnumerable<LoadOrder> GetLoadOrder()
    {
        var dependencyGraph = BuildDependencyGraph();
        var sourceVertices = GetSourceVertices();

        int order = 1;
        List<LoadOrder> loadOrder = [];

        while (sourceVertices.Count > 0)
        {
            foreach (var sourceVertex in sourceVertices)
            {
                dependencyGraph.RemoveVertex(sourceVertex);

                loadOrder.Add(new LoadOrder(GetEndpointName(sourceVertex), order, ["Create", "Update"]));
            }

            order++;
            sourceVertices = GetSourceVertices();
        }

        // Apply predefined order transformations
        foreach (var orderTransformer in orderTransformers)
        {
            orderTransformer.Transform(loadOrder);
        }

        return loadOrder;

        List<ResourceDependencyGraphVertex> GetSourceVertices()
        {
            // A source vertex is a vertex that has no incoming edges (i.e. it's not referenced by any vertex)

            return dependencyGraph
                .Vertices.Where(v => !dependencyGraph.InEdges(v).Any())
                .OrderBy(GetEndpointName)
                .ToList();
        }

        string GetEndpointName(ResourceDependencyGraphVertex vertex) =>
            $"/{vertex.ProjectSchema.ProjectEndpointName.Value}/{vertex.ProjectSchema
                .GetEndpointNameFromResourceName(vertex.ResourceSchema.ResourceName).Value}";
    }

    /// <summary>
    /// Builds a dependency graph where vertices represent resources and edges represent their references.
    /// </summary>
    private BidirectionalGraph<
        ResourceDependencyGraphVertex,
        ResourceDependencyGraphEdge
    > BuildDependencyGraph()
    {
        ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaDocuments(
            apiSchemaProvider.GetApiSchemaNodes(),
            logger
        );

        ProjectSchema[] projectSchemas = apiSchemaDocuments.GetAllProjectSchemas();
        ProjectName coreProjectName = apiSchemaDocuments.GetCoreProjectSchema().ProjectName;

        List<ResourceDependencyGraphVertex> resources = projectSchemas
            .SelectMany(projectSchema =>
                projectSchema
                    .GetAllResourceSchemaNodes()
                    .Select(resourceSchemaNode => new ResourceDependencyGraphVertex(
                        new ResourceSchema(resourceSchemaNode),
                        projectSchema
                    ))
            )
            .ToList();

        List<ResourceDependencyGraphVertex> vertices = resources
            .Where(vertex => !vertex.ResourceSchema.IsResourceExtension)
            .ToList();

        Dictionary<ProjectName, ProjectSchema> projectByName = projectSchemas.ToDictionary(projectSchema =>
            projectSchema.ProjectName
        );

        Dictionary<FullResourceName, ResourceDependencyGraphVertex> vertexByFullResourceName =
            resources.ToDictionary(vertex => new FullResourceName(
                vertex.ProjectSchema.ProjectName,
                vertex.ResourceSchema.ResourceName
            ));

        ILookup<ResourceName, ResourceDependencyGraphVertex> verticesBySuperclassName = vertices
            .Where(vertex => vertex.ResourceSchema.IsSubclass)
            .ToLookup(vertex => vertex.ResourceSchema.SuperclassResourceName);

        IEnumerable<ResourceDependencyGraphEdge> edges = resources
            .SelectMany(vertex =>
                vertex
                    .ResourceSchema.DocumentPaths.Where(documentPath => documentPath.IsReference)
                    .SelectMany(reference =>
                        BuildVertexEdges(
                            vertex,
                            reference,
                            coreProjectName,
                            projectByName,
                            vertexByFullResourceName,
                            verticesBySuperclassName
                        )
                    )
            )
            .Distinct();

        var graph = new BidirectionalGraph<ResourceDependencyGraphVertex, ResourceDependencyGraphEdge>();
        graph.AddVertexRange(vertices);
        graph.AddEdgeRange(edges);

        foreach (
            var schoolYearVertex in graph.Vertices.Where(vertex =>
                vertex.ResourceSchema.IsSchoolYearEnumeration
            )
        )
        {
            graph.RemoveVertex(schoolYearVertex);
        }

        // Apply predefined graph transformations
        foreach (var graphTransformer in graphTransformers)
        {
            graphTransformer.Transform(graph);
        }

        graph.BreakCycles(edge => !edge.Reference.IsRequired, logger);
        return graph;
    }

    /// <summary>
    /// Builds the edges that represent the given resource reference.
    /// If the reference points to an abstract resource, an edge is returned pointing to each subclass resource.
    /// </summary>
    private static IEnumerable<ResourceDependencyGraphEdge> BuildVertexEdges(
        ResourceDependencyGraphVertex source,
        DocumentPath reference,
        ProjectName coreProjectName,
        Dictionary<ProjectName, ProjectSchema> projectByName,
        Dictionary<FullResourceName, ResourceDependencyGraphVertex> vertexByFullResourceName,
        ILookup<ResourceName, ResourceDependencyGraphVertex> verticesBySuperclassName
    )
    {
        if (source.ResourceSchema.IsResourceExtension)
        {
            // Change the source vertex to be the Core resource
            var coreResource = new FullResourceName(coreProjectName, source.ResourceSchema.ResourceName);
            source =
                vertexByFullResourceName.GetValueOrDefault(coreResource)
                ?? throw new InvalidOperationException(
                    $"A vertex representing the resource '{coreResource}' wasn't found in the resource dependency graph."
                );
        }

        bool referencesAbstractResource = projectByName[reference.ProjectName]
            .AbstractResources.Any(abstractResource =>
                abstractResource.ResourceName == reference.ResourceName
            );

        if (referencesAbstractResource)
        {
            // Include all subclass types of the referenced abstract resource
            foreach (var subclassVertex in verticesBySuperclassName[reference.ResourceName])
            {
                yield return new ResourceDependencyGraphEdge(subclassVertex, source, reference);
            }
        }
        else
        {
            var referencedVertex = vertexByFullResourceName[
                new FullResourceName(reference.ProjectName, reference.ResourceName)
            ];
            yield return new ResourceDependencyGraphEdge(referencedVertex, source, reference);
        }
    }
}
