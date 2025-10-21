// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;
using ProjectEndpointName = EdFi.DataManagementService.Core.Model.ProjectEndpointName;

namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

/// <summary>
/// A vertex representing a resource in the resource dependency graph.
/// </summary>
internal record ResourceDependencyGraphVertex(
    ProjectName ProjectName,
    ProjectEndpointName ProjectEndpointName,
    ResourceName ResourceName,
    EndpointName ResourceEndpointName,
    bool IsResourceExtension,
    bool IsSubclass,
    ResourceName SuperclassResourceName,
    bool IsSchoolYearEnumeration
) : IComparable<ResourceDependencyGraphVertex>
{
    public FullResourceName FullResourceName { get; } = new(ProjectName, ResourceName);

    public int CompareTo(ResourceDependencyGraphVertex? other)
    {
        return FullResourceName.CompareTo(other?.FullResourceName);
    }

    public virtual bool Equals(ResourceDependencyGraphVertex? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return FullResourceName.Equals(other.FullResourceName);
    }

    public override int GetHashCode()
    {
        return FullResourceName.GetHashCode();
    }

    public override string ToString() => FullResourceName.ToString();
}
