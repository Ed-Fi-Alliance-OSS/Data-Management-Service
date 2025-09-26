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

        List<ResourceDependencyGraphVertex> allResourceVertices = projectSchemas
            .SelectMany(projectSchema =>
                projectSchema
                    .GetAllResourceSchemaNodes()
                    .Select(resourceSchemaNode => new ResourceDependencyGraphVertex(
                        new ResourceSchema(resourceSchemaNode),
                        projectSchema
                    ))
            )
            .ToList();

        List<ResourceDependencyGraphVertex> nonExtensionResourceVertices = allResourceVertices
            .Where(vertex => !vertex.ResourceSchema.IsResourceExtension)
            .ToList();

        Dictionary<ProjectName, ProjectSchema> projectByName = projectSchemas.ToDictionary(projectSchema =>
            projectSchema.ProjectName
        );

        Dictionary<FullResourceName, ResourceDependencyGraphVertex> vertexByFullResourceName =
            allResourceVertices.ToDictionary(vertex => new FullResourceName(
                vertex.ProjectSchema.ProjectName,
                vertex.ResourceSchema.ResourceName
            ));

        ILookup<ResourceName, ResourceDependencyGraphVertex> extensionVertexBySuperclassName =
            nonExtensionResourceVertices
                .Where(vertex => vertex.ResourceSchema.IsSubclass)
                .ToLookup(vertex => vertex.ResourceSchema.SuperclassResourceName);

        IEnumerable<ResourceDependencyGraphEdge> edges = allResourceVertices
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
                            extensionVertexBySuperclassName
                        )
                    )
            )
            .Distinct();

        var graph = new BidirectionalGraph<ResourceDependencyGraphVertex, ResourceDependencyGraphEdge>();
        graph.AddVertexRange(nonExtensionResourceVertices);
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
        foreach (var graphTransformer in _graphTransformers)
        {
            graphTransformer.Transform(graph);
        }

        graph.BreakCycles(edge => !edge.Reference.IsRequired, _logger);
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
