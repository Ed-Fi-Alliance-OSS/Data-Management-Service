// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using QuickGraph;

namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

/// <summary>
/// An edge representing a resource's reference in the resource dependency graph.
/// </summary>
/// <param name="Source">The source vertex.</param>
/// <param name="Target">The target vertex.</param>
/// <param name="IsRequired">Indicates whether the associated reference is required.</param>
internal record ResourceDependencyGraphEdge(
    ResourceDependencyGraphVertex Source,
    ResourceDependencyGraphVertex Target,
    bool IsRequired
) : IEdge<ResourceDependencyGraphVertex>
{
    public virtual bool Equals(ResourceDependencyGraphEdge? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Source.Equals(other.Source) && Target.Equals(other.Target);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return (Source.GetHashCode() * 397) ^ Target.GetHashCode();
        }
    }

    public override string ToString() => $"({Source.FullResourceName}) --> ({Target.FullResourceName})";
}
