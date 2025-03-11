// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;

namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

/// <summary>
/// A vertex representing a resource in the resource dependency graph.
/// </summary>
internal record ResourceDependencyGraphVertex(ResourceSchema ResourceSchema, ProjectSchema ProjectSchema)
{
    public FullResourceName FullResourceName { get; } =
        new(ProjectSchema.ProjectName, ResourceSchema.ResourceName);

    public override string ToString() => FullResourceName.ToString();
}
