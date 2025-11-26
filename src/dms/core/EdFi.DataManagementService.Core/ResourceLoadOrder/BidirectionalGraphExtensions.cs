// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;
using QuickGraph;

namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

internal static class BidirectionalGraphExtensions
{
    /// <summary>
    /// Attempts to break cycles in the graph by removing optional edges, as defined
    /// by the supplied <see cref="isRemovable"/> predicate function.
    /// </summary>
    /// <param name="graph">The bidirectional graph to be processed.</param>
    /// <param name="isRemovable">A function indicating whether a particular edge can be removed (i.e. is a "soft" dependency).</param>
    /// <param name="logger"></param>
    /// <typeparam name="TVertex">The <see cref="Type" /> of the vertices of the graph.</typeparam>
    /// <typeparam name="TEdge">The <see cref="Type" /> of the edges of the graph.</typeparam>
    /// <returns>The list of edges that were removed to break the cycle(s).</returns>
    /// <exception cref="NonAcyclicGraphException">Occurs if one or more of the cycles present in the graph cannot be broken by removing one of its edges.</exception>
    public static IReadOnlyList<TEdge> BreakCycles<TVertex, TEdge>(
        this BidirectionalGraph<TVertex, TEdge> graph,
        Func<TEdge, bool> isRemovable,
        ILogger logger
    )
        where TEdge : IEdge<TVertex>
        where TVertex : notnull
    {
        // Get cyclical dependencies found in the graph
        var cycles = graph.GetCycles(logger);

        // Break circular dependencies
        var removedEdges = cycles
            .SelectMany(cycle =>
            {
                // Last element of Path repeats first element (so ignore duplicate)
                var distinctPathVertices = cycle.Path.Take(cycle.Path.Count - 1).ToArray();

                var sacrificialDependency = distinctPathVertices
                    .Select(
                        (_, i) =>
                        {
                            // Get the next entity in the path (circling around to the first entity on the last item)
                            var dependentVertex = distinctPathVertices[(i + 1) % distinctPathVertices.Length];

                            return new
                            {
                                DependentVertex = dependentVertex,
                                CycleEdges = graph.InEdges(dependentVertex).Where(IsCycleEdge),
                            };
                        }
                    )
                    .Reverse()
                    .FirstOrDefault(x => x.CycleEdges.All(isRemovable));

                if (sacrificialDependency == null)
                {
                    graph.ValidateAcyclicGraph(logger);

                    // Should never get here in this situation, but throw an exception to satisfy code analysis warnings
                    throw new NonAcyclicGraphException();
                }

                // Remove the chosen graph edge(s) to break the cyclical dependency
                var sacrificialDependencyEdges = sacrificialDependency.CycleEdges.ToArray();
                foreach (TEdge edge in sacrificialDependencyEdges)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(
                            "Edge '{Edge}' removed to prevent the following cycle: {Cycle}",
                            edge,
                            string.Join(" --> ", cycle.Path.Select(x => x.ToString()))
                        );
                    }

                    graph.RemoveEdge(edge);
                }

                return sacrificialDependencyEdges;

                bool IsCycleEdge(TEdge edge) => distinctPathVertices.Contains(edge.Source);
            })
            .ToList();

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                @"The following edges were removed from the graph to prevent cycles:
{Edges}",
                string.Join(Environment.NewLine, removedEdges.Select(x => x.ToString()))
            );
        }

        return removedEdges;
    }

    /// <summary>
    /// Validates that the graph is free of cycles, and throws a <see cref="NonAcyclicGraphException"/> otherwise.
    ///
    /// </summary>
    /// <typeparam name="TVertex">The <see cref="Type" /> of the vertices of the graph.</typeparam>
    /// <typeparam name="TEdge">The <see cref="Type" /> of the edges of the graph.</typeparam>
    /// <param name="graph">The bidirectional graph to be validated.</param>
    /// <param name="logger"></param>
    /// <exception cref="NonAcyclicGraphException">Occurs if there are cycles in the graph.</exception>
    public static void ValidateAcyclicGraph<TVertex, TEdge>(
        this BidirectionalGraph<TVertex, TEdge> graph,
        ILogger logger
    )
        where TEdge : IEdge<TVertex>
        where TVertex : notnull
    {
        var cycles = graph.GetCycles(logger);

        if (cycles.Count == 0)
        {
            return;
        }

        string cycleExplanations = string.Join(
            Environment.NewLine + Environment.NewLine,
            cycles.Select(cycle =>
                $"{string.Join(Environment.NewLine + "    is used by ", cycle.Path.Select(x => x.ToString()))}"
            )
        );

        string dependencyPluralization = cycles.Count == 1 ? "dependency" : "dependencies";

        throw new NonAcyclicGraphException(
            $"Circular {dependencyPluralization} found:{Environment.NewLine}{cycleExplanations}"
        );
    }

    /// <summary>
    /// Returns the list of cycles present in the graph.
    /// </summary>
    /// <typeparam name="TVertex">The <see cref="Type" /> of the vertices of the graph.</typeparam>
    /// <typeparam name="TEdge">The <see cref="Type" /> of the edges of the graph.</typeparam>
    /// <param name="graph">The bidirectional graph to be processed.</param>
    /// <param name="logger"></param>
    private static IReadOnlyList<ResourceDependencyGraphCycle<TVertex>> GetCycles<TVertex, TEdge>(
        this BidirectionalGraph<TVertex, TEdge> graph,
        ILogger logger
    )
        where TEdge : IEdge<TVertex>
        where TVertex : notnull
    {
        // Initialize vertex, and current stack tracking
        var visited = new HashSet<TVertex>();
        var stack = new List<TVertex>();
        var cycles = new List<ResourceDependencyGraphCycle<TVertex>>();

        // Call the recursive helper function to detect cycle in different DFS trees
        foreach (var vertex in graph.Vertices)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Probing node '{Vertex}' for cyclical dependencies...", vertex);
            }

            graph.FindCycles(vertex, visited, stack, cycles, logger);
        }

        return cycles;
    }

    /// <summary>
    /// Recursive function that uses Depth-first search (DFS) to find cycles in the graph.
    /// </summary>
    private static void FindCycles<TVertex, TEdge>(
        this BidirectionalGraph<TVertex, TEdge> executionGraph,
        TVertex vertex,
        HashSet<TVertex> visited,
        List<TVertex> stack,
        List<ResourceDependencyGraphCycle<TVertex>> cycles,
        ILogger logger
    )
        where TEdge : IEdge<TVertex>
        where TVertex : notnull
    {
        // Do we have a circular dependency?
        if (stack.Contains(vertex))
        {
            var cycle = new ResourceDependencyGraphCycle<TVertex>(
                Vertex: $"{vertex}",
                Path: stack.SkipWhile(x => !x.Equals(vertex)).Concat([vertex]).ToList()
            );

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Cycle found for vertex '{Vertex}': {Cycle}",
                    cycle.Vertex,
                    string.Join(" --> ", cycle.Path.Select(x => x.ToString()))
                );
            }

            cycles.Add(cycle);
            visited.Add(vertex);
            return;
        }

        // If we've already evaluated this vertex, stop now.
        if (visited.Contains(vertex))
        {
            return;
        }

        // Mark the current node as visited and part of recursion stack
        visited.Add(vertex);
        stack.Add(vertex);

        try
        {
            var children = executionGraph.OutEdges(vertex).Select(x => x.Target).ToList();

            if (logger.IsEnabled(LogLevel.Debug) && children.Count > 0)
            {
                logger.LogDebug(
                    "Children of {Vertex}: {Children}",
                    vertex,
                    string.Join(" => ", children.Select(x => x.ToString()))
                );
            }

            foreach (var child in children)
            {
                executionGraph.FindCycles(child, visited, stack, cycles, logger);
            }
        }
        finally
        {
            stack.Remove(vertex);
        }
    }
}
