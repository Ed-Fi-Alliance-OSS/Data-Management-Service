// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

/// <summary>
/// Provides a mean to calculate the order in which resources should be loaded.
/// </summary>
internal class ResourceLoadOrderCalculator(
    IEnumerable<IResourceLoadOrderTransformer> _resourceLoadOrderTransformers,
    IResourceDependencyGraphFactory _resourceDependencyGraphFactory
)
{
    /// <summary>
    /// Calculates the order in which resources should be loaded based on their dependencies.
    /// </summary>
    public IEnumerable<LoadOrder> GetLoadOrder()
    {
        var dependencyGraph = _resourceDependencyGraphFactory.Create();
        var sourceVertices = GetSourceVertices();

        int order = 1;
        List<LoadOrder> loadOrder = [];

        while (sourceVertices.Count > 0)
        {
            foreach (var sourceVertex in sourceVertices)
            {
                dependencyGraph.RemoveVertex(sourceVertex);

                loadOrder.Add(new LoadOrder(sourceVertex.GetEndpointName(), order, ["Create", "Update"]));
            }

            order++;
            sourceVertices = GetSourceVertices();
        }

        // Apply predefined order transformations
        foreach (var orderTransformer in _resourceLoadOrderTransformers)
        {
            orderTransformer.Transform(loadOrder);
        }

        return loadOrder;

        List<ResourceDependencyGraphVertex> GetSourceVertices()
        {
            // A source vertex is a vertex that has no incoming edges (i.e. it's not referenced by any vertex)
            return dependencyGraph
                .Vertices.Where(v => !dependencyGraph.InEdges(v).Any())
                .OrderBy(v => v.GetEndpointName())
                .ToList();
        }
    }
}
