// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using QuickGraph;

namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

/// <summary>
/// An edge representing a resource's reference in the resource dependency graph.
/// </summary>
/// <param name="Source">The source vertex.</param>
/// <param name="Target">The target vertex.</param>
/// <param name="Reference">The <see cref="DocumentPath"/> that represents the reference.</param>
internal record ResourceDependencyGraphEdge(
    ResourceDependencyGraphVertex Source,
    ResourceDependencyGraphVertex Target,
    DocumentPath Reference
) : IEdge<ResourceDependencyGraphVertex>
{
    public override string ToString() => $"({Source.FullResourceName}) --> ({Target.FullResourceName})";
}
