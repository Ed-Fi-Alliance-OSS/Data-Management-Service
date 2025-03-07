// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using QuickGraph;

namespace EdFi.DataManagementService.Core.ApiSchema.ResourceLoadOrder
{
    internal record struct Edge(Vertex Source, Vertex Target, DocumentPath Reference) : IEdge<Vertex>
    {
        public static IEnumerable<Edge> CreateEdges(
            Vertex source,
            DocumentPath reference,
            Dictionary<ProjectName, ProjectSchema> projectsByName,
            Dictionary<(ProjectName ProjectName, ResourceName ResourceName), Vertex> verticesByName,
            ILookup<ResourceName, Vertex> verticesBySuperClassName)
        {
            source = source.ResourceSchema.IsResourceExtension
                ? verticesByName[(ProjectName.Core, source.ResourceSchema.ResourceName)]
                : source;

            bool referencesAbstractResource = projectsByName[reference.ProjectName]
                .AbstractResources
                .Any(ar => ar.ResourceName == reference.ResourceName);

            if (referencesAbstractResource)
            {
                // Include all subclass types of the referenced resource instead
                foreach (var subclassResource in verticesBySuperClassName[reference.ResourceName])
                {
                    yield return new Edge(
                        new Vertex(subclassResource.ResourceSchema, subclassResource.ProjectSchema), source,
                        reference);
                }
            }
            else
            {
                var referencedResource = verticesByName[(reference.ProjectName, reference.ResourceName)];
                yield return new Edge(
                    new Vertex(referencedResource.ResourceSchema, referencedResource.ProjectSchema), source,
                    reference);
            }
        }
    }
}
