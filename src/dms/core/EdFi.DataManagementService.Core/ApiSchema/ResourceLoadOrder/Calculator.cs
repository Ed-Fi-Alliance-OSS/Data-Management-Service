// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using QuickGraph;

namespace EdFi.DataManagementService.Core.ApiSchema.ResourceLoadOrder
{
    internal class Calculator(
        ILogger<Calculator> logger,
        IEnumerable<IGraphTransformer> graphTransformers,
        IEnumerable<IOrderTransformer> orderTransformers
    )
    {
        public IEnumerable<LoadOrder> GetGroupedLoadOrder(ApiSchemaDocuments apiSchemaDocuments)
        {
            int groupNumber = 1;
            var executionGraph = CreateResourceLoadGraph(apiSchemaDocuments);
            var loadableResources = GetLoadableResources();
            var loadOrder = new List<LoadOrder>();

            while (loadableResources.Count > 0)
            {
                foreach (var loadableResource in loadableResources)
                {
                    executionGraph.RemoveVertex(loadableResource);

                    loadOrder.Add(new LoadOrder
                    {
                        Resource = GetEndpointName(loadableResource),
                        Order = groupNumber
                    });
                }

                groupNumber++;
                loadableResources = GetLoadableResources();
            }

            // Apply predefined order transformations
            foreach (var orderTransformer in orderTransformers)
            {
                orderTransformer.Transform(loadOrder);
            }

            return loadOrder;

            List<Vertex> GetLoadableResources()
            {
                return executionGraph.Vertices.Where(v => !executionGraph.InEdges(v).Any())
                    .OrderBy(GetEndpointName)
                    .ToList();
            }

            #region Remove this workaround after DMS-543 gets closed

            string GetEndpointNameWorkaround(Vertex vertex) =>
                vertex.ResourceSchema.ResourceName == new ResourceName("GradingPeriod")
                    ? "gradingPeriods"
                    : vertex.ProjectSchema
                        .GetEndpointNameFromResourceName(RemoveDescriptorSuffix(vertex.ResourceSchema)).Value;

            ResourceName RemoveDescriptorSuffix(ResourceSchema resourceSchema) => resourceSchema.IsDescriptor
                ? new ResourceName(resourceSchema.ResourceName.Value[..^10])
                : resourceSchema.ResourceName;

            #endregion

            string GetEndpointName(Vertex vertex)
                => $"/{vertex.ProjectSchema.ProjectEndpointName.Value}/{GetEndpointNameWorkaround(vertex)}";
        }

        private BidirectionalGraph<Vertex, Edge> CreateResourceLoadGraph(
            ApiSchemaDocuments apiSchemaDocuments)
        {
            var projects = apiSchemaDocuments.GetAllProjectSchemas();

            var resources = projects
                .SelectMany(
                    ps => ps.GetAllResourceSchemaNodes()
                        .Select(rsn => new Vertex(new ResourceSchema(rsn), ps)))
                .ToList();

            var vertices = resources
                .Where(v => !v.ResourceSchema.IsResourceExtension)
                .ToList();

            var projectsByName = projects
                .ToDictionary(ps => ps.ProjectName);

            var verticesByName = resources
                .ToDictionary(v => (v.ProjectSchema.ProjectName, v.ResourceSchema.ResourceName));

            var verticesBySuperClassName = vertices
                .Where(v => v.ResourceSchema.IsSubclass)
                .ToLookup(v => v.ResourceSchema.SuperclassResourceName);

            var edges = resources
                .SelectMany(v =>
                    v.ResourceSchema.DocumentPaths
                        .Where(dp => dp.IsReference)
                        .SelectMany(dp => Edge.CreateEdges(v, dp, projectsByName, verticesByName,
                            verticesBySuperClassName)))
                .Distinct();

            var graph = new BidirectionalGraph<Vertex, Edge>();
            graph.AddVertexRange(vertices);
            graph.AddEdgeRange(edges);

            foreach (var schoolYearResource in graph.Vertices.Where(v =>
                         v.ResourceSchema.IsSchoolYearEnumeration))
            {
                graph.RemoveVertex(schoolYearResource);
            }

            // Apply predefined graph transformations
            foreach (var graphTransformer in graphTransformers)
            {
                graphTransformer.Transform(graph);
            }

            graph.BreakCycles(e => !e.Reference.IsRequired, logger);
            return graph;
        }
    }
}
