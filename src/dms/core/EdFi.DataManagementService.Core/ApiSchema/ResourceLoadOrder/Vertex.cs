// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.ApiSchema.ResourceLoadOrder
{
    internal record struct Vertex(ResourceSchema ResourceSchema, ProjectSchema ProjectSchema)
    {
        public FullResourceName FullResourceName { get; } =
            new(ProjectSchema.ProjectName, ResourceSchema.ResourceName);

        public override string ToString()
        {
            return FullResourceName.ToString();
        }
    }
}
