// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using QuickGraph;

// ReSharper disable InconsistentNaming
namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

internal interface IResourceDependencyGraphMLFactory
{
    GraphML CreateGraphML();
}

internal class ResourceDependencyGraphMLFactory(
    IResourceDependencyGraphFactory _resourceDependencyGraphFactory,
    ILogger<ResourceDependencyGraphMLFactory> _logger
) : IResourceDependencyGraphMLFactory
{
    private const string RetrySuffix = "#Retry";
    private static readonly ProjectName _edFiProjectName = new("Ed-Fi");
    private static readonly FullResourceName _studentFullName = new(_edFiProjectName, new ResourceName("Student"));
    private static readonly FullResourceName _staffFullName = new(_edFiProjectName, new ResourceName("Staff"));
    private static readonly FullResourceName _parentFullName = new(_edFiProjectName, new ResourceName("Parent"));
    private static readonly FullResourceName _contactFullName = new(_edFiProjectName, new ResourceName("Contact"));

    private static readonly FullResourceName _studentSchoolAssociationFullName =
        new(_edFiProjectName, new ResourceName("StudentSchoolAssociation"));

    private static readonly FullResourceName _staffEdOrgAssignmentAssociationFullName =
        new(_edFiProjectName, new ResourceName("StaffEducationOrganizationAssignmentAssociation"));

    private static readonly FullResourceName _staffEdOrgEmploymentAssociationFullName =
        new(_edFiProjectName, new ResourceName("StaffEducationOrganizationEmploymentAssociation"));

    private static readonly FullResourceName _studentParentAssociationFullName =
        new(_edFiProjectName, new ResourceName("StudentParentAssociation"));

    private static readonly FullResourceName _studentContactAssociationFullName =
        new(_edFiProjectName, new ResourceName("StudentContactAssociation"));

    public GraphML CreateGraphML()
    {
        var retryNodeIdByPrimaryAssociationNodeId = new Dictionary<string, string>();

        var resourceGraph = _resourceDependencyGraphFactory.Create();

        var vertices = resourceGraph.Vertices
            .SelectMany(ApplyStandardSecurityVertexExpansions)
            .OrderBy(n => n.Id)
            .ToList();

        var edges =
            resourceGraph.Edges.SelectMany(ApplyUpstreamPrimaryAssociationEdgeExpansions)
            .Concat(resourceGraph.Edges.SelectMany(ApplyDownstreamPrimaryAssociationEdgeExpansions))
            .Concat(resourceGraph.Edges.Where(e => !IsUpstreamPrimaryAssociationEdge(e) && !IsDownstreamPrimaryAssociationEdge(e)).Select(ProjectNonPrimaryAssociationEdge))
            .Distinct()
            .GroupBy(x => x.Source.Id)
            .OrderBy(g => g.Key)
            .SelectMany(g => g.OrderBy(x => x.Target.Id))
            .Select(e => new GraphMLEdge
            {
                Source = vertices.Single(v => v.Id == e.Source.Id),
                Target = vertices.Single(v => v.Id == e.Target.Id),
                IsReferenceRequired = e.IsReferenceRequired
            })
            .ToList();

        var graphMLExecutionGraph = new BidirectionalGraph<GraphMLNode, GraphMLEdge>();
        graphMLExecutionGraph.AddVertexRange(vertices);
        graphMLExecutionGraph.AddEdgeRange(edges);

        // Remove any cycles resulting from the application of standard security
        graphMLExecutionGraph.BreakCycles(edge => !edge.IsReferenceRequired, _logger);

        return new GraphML
        {
            Id = "EdFi Dependencies",
            Nodes = graphMLExecutionGraph.Vertices.ToArray(),
            Edges = graphMLExecutionGraph.Edges.ToArray(),
        };

        string GetNodeId(ResourceDependencyGraphVertex vertex)
        {
            return vertex.GetEndpointName();
        }

        IEnumerable<GraphMLNode> ApplyStandardSecurityVertexExpansions(ResourceDependencyGraphVertex resource)
        {
            yield return new GraphMLNode { Id = resource.GetEndpointName() };

            // Yield "retry" nodes for person types
            if (resource.FullResourceName == _studentFullName
                || resource.FullResourceName == _staffFullName
                || resource.FullResourceName == _parentFullName
                || resource.FullResourceName == _contactFullName)
            {
                yield return new GraphMLNode() { Id = $"{resource.GetEndpointName()}{RetrySuffix}" };
            }
        }

        IEnumerable<GraphMLEdge> ApplyUpstreamPrimaryAssociationEdgeExpansions(ResourceDependencyGraphEdge edge)
        {
            // Add a dependency for the #Retry node of edges with person types as the source
            if (IsUpstreamPrimaryAssociationEdge(edge))
            {
                string primaryAssociationNodeId = GetNodeId(edge.Target);
                string retryNodeId = $"{GetNodeId(edge.Source)}{RetrySuffix}";

                // Capture the Retry node's relationship with the primary association for redirecting the dependencies
                retryNodeIdByPrimaryAssociationNodeId[primaryAssociationNodeId] = retryNodeId;

                // Yield a new edge for the primary association as a dependency of the retry node
                yield return new GraphMLEdge
                {
                    Source = new GraphMLNode { Id = primaryAssociationNodeId },
                    Target = new GraphMLNode { Id = retryNodeId },
                    IsReferenceRequired = true // Upstream "Retry" edges are always required
                };

                // Yield the standard association edge
                yield return new GraphMLEdge
                {
                    Source = new GraphMLNode { Id = GetNodeId(edge.Source) },
                    Target = new GraphMLNode { Id = GetNodeId(edge.Target) },
                    IsReferenceRequired = edge.Reference.IsRequired
                };
            }
        }

        IEnumerable<GraphMLEdge> ApplyDownstreamPrimaryAssociationEdgeExpansions(ResourceDependencyGraphEdge edge)
        {
            // Add copies of the downstream dependencies of the primary associations with the #Retry node
            if (IsDownstreamPrimaryAssociationEdge(edge))
            {
                // Yield an association edge relocated to the retry node instead
                yield return new GraphMLEdge
                {
                    Source = new GraphMLNode { Id = retryNodeIdByPrimaryAssociationNodeId[GetNodeId(edge.Source)] },
                    Target = new GraphMLNode { Id = GetNodeId(edge.Target) },
                    IsReferenceRequired = edge.Reference.IsRequired
                };
            }
        }

        GraphMLEdge ProjectNonPrimaryAssociationEdge(ResourceDependencyGraphEdge edge)
        {
            // Yield the standard association edge
            return new GraphMLEdge
            {
                Source = new GraphMLNode { Id = GetNodeId(edge.Source) },
                Target = new GraphMLNode { Id = GetNodeId(edge.Target) },
                IsReferenceRequired = edge.Reference.IsRequired
            };
        }
    }

    private static bool IsUpstreamPrimaryAssociationEdge(ResourceDependencyGraphEdge edge)
    {
        return (edge.Source.FullResourceName == _studentFullName && edge.Target.FullResourceName == _studentSchoolAssociationFullName)
            || (edge.Source.FullResourceName == _staffFullName && (edge.Target.FullResourceName == _staffEdOrgAssignmentAssociationFullName || edge.Target.FullResourceName == _staffEdOrgEmploymentAssociationFullName))
            || (edge.Source.FullResourceName == _parentFullName && edge.Target.FullResourceName == _studentParentAssociationFullName)
            || (edge.Source.FullResourceName == _contactFullName && edge.Target.FullResourceName == _studentContactAssociationFullName);
    }

    private static bool IsDownstreamPrimaryAssociationEdge(ResourceDependencyGraphEdge edge)
    {
        return edge.Source.FullResourceName == _studentSchoolAssociationFullName
            || edge.Source.FullResourceName == _staffEdOrgAssignmentAssociationFullName
            || edge.Source.FullResourceName == _staffEdOrgEmploymentAssociationFullName
            || edge.Source.FullResourceName == _studentContactAssociationFullName
            || edge.Source.FullResourceName == _studentParentAssociationFullName;
    }
}
