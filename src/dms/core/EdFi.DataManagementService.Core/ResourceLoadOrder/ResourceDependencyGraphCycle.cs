// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

/// <summary>
/// Represents a cycle in the resource dependency graph.
/// A cycle can arise, for example, when a resource X references resource Y,
/// which in turns references resource X.
/// </summary>
/// <typeparam name="TVertex">The Type of the vertices in the graph.</typeparam>
/// <param name="Vertex">The string representation of the initial vertex being probed when the cycle was found.</param>
/// <param name="Path">The list of vertices found that form the cycle, with the first vertex also appearing as the last vertex.</param>
internal record ResourceDependencyGraphCycle<TVertex>(string Vertex, List<TVertex> Path)
    where TVertex : notnull;
