// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.


using EdFi.DataManagementService.Core.External.Model;
using QuickGraph;

namespace EdFi.DataManagementService.Core.ApiSchema.ResourceLoadOrder
{
    internal class PersonAuthorizationGraphTransformer : IGraphTransformer
    {
        public void Transform(BidirectionalGraph<Vertex, Edge> resourceGraph)
        {
            ApplyStudentTransformation(resourceGraph);
            ApplyStaffTransformation(resourceGraph);
            ApplyContactTransformation(resourceGraph);
        }

        private void ApplyStudentTransformation(BidirectionalGraph<Vertex, Edge> resourceGraph)
        {
            var resources = resourceGraph.Vertices.ToList();

            var studentResource =
                resources.Find(x =>
                    x.FullResourceName ==
                    new FullResourceName(ProjectName.Core, new ResourceName("Student")));

            var studentSchoolAssociationResource = resources.Find(
                x => x.FullResourceName == new FullResourceName(ProjectName.Core,
                    new ResourceName("StudentSchoolAssociation")));

            // No student entity in the graph, nothing to do.
            if (studentResource == default)
            {
                return;
            }

            if (studentSchoolAssociationResource == default)
            {
                string message =
                    "Unable to transform resource load graph as StudentSchoolAssociation was not found in the graph.";

                throw new InvalidOperationException(message);
            }

            // Get direct student dependencies
            var studentDependencies = resourceGraph.OutEdges(studentResource)
                .Where(e => e.Target != studentSchoolAssociationResource)
                .ToList();

            // Add dependency on primaryRelationship path
            foreach (var directStudentDependency in studentDependencies)
            {
                // Re-point the edge to the primary relationships
                resourceGraph.RemoveEdge(directStudentDependency);

                resourceGraph.AddEdge(
                    directStudentDependency with { Source = studentSchoolAssociationResource });
            }
        }

        private void ApplyStaffTransformation(BidirectionalGraph<Vertex, Edge> resourceGraph)
        {
            var resources = resourceGraph.Vertices.ToList();

            var staffResource =
                resources.Find(x =>
                    x.FullResourceName == new FullResourceName(ProjectName.Core, new ResourceName("Staff")));

            var staffEdOrgEmployAssoc = resources.Find(
                x => x.FullResourceName
                     == new FullResourceName(ProjectName.Core,
                         new ResourceName("StaffEducationOrganizationEmploymentAssociation")));

            var staffEdOrgAssignAssoc = resources.Find(
                x => x.FullResourceName
                     == new FullResourceName(ProjectName.Core,
                         new ResourceName("StaffEducationOrganizationAssignmentAssociation")));

            // No staff entity in the graph, nothing to do.
            if (staffResource == default)
            {
                return;
            }

            if (staffEdOrgEmployAssoc == default && staffEdOrgAssignAssoc == default)
            {
                string message =
                    "Unable to transform resource load graph since StaffEducationOrganizationAssignmentAssociation and StaffEducationOrganizationEmploymentAssociation were not found in the graph.";

                throw new InvalidOperationException(message);
            }

            // Get direct staff dependencies
            var directStaffDependencies = resourceGraph.OutEdges(staffResource)
                .Where(e => e.Target != staffEdOrgEmployAssoc && e.Target != staffEdOrgAssignAssoc)
                .ToList();

            // Add dependency on primaryRelationship path
            foreach (var directStaffDependency in directStaffDependencies)
            {
                // Re-point the edge to the primary relationships
                resourceGraph.RemoveEdge(directStaffDependency);

                resourceGraph.AddEdge(
                    directStaffDependency with { Source = staffEdOrgAssignAssoc });

                resourceGraph.AddEdge(
                    directStaffDependency with { Source = staffEdOrgEmployAssoc });
            }
        }

        private void ApplyContactTransformation(BidirectionalGraph<Vertex, Edge> resourceGraph)
        {
            var resources = resourceGraph.Vertices.ToList();

            var contactResource = resources.Find(
                x => x.FullResourceName == new FullResourceName(ProjectName.Core, new ResourceName("Contact"))
                     || x.FullResourceName ==
                     new FullResourceName(ProjectName.Core, new ResourceName("Parent")));

            // No entity named Parent or Contact in the graph, nothing to do.
            if (contactResource == default)
            {
                return;
            }

            string contactStudentAssociationName = contactResource.FullResourceName.ResourceName.Value switch
            {
                "Contact" => "StudentContactAssociation",
                "Parent" => "StudentParentAssociation",
                _ => LogAndThrowException()
            };

            var studentContactAssociationResource = resources.Find(
                x => x.FullResourceName == new FullResourceName(ProjectName.Core,
                    new ResourceName(contactStudentAssociationName)));

            if (studentContactAssociationResource == default)
            {
                string message =
                    $"Unable to transform resource load graph as {contactStudentAssociationName} was not found in the graph.";

                throw new InvalidOperationException(message);
            }

            // Get direct contact dependencies
            var directContactDependencies = resourceGraph.OutEdges(contactResource)
                .Where(e => e.Target != studentContactAssociationResource)
                .ToList();

            // Add dependency on primaryRelationship path
            foreach (var directContactDependency in directContactDependencies)
            {
                // Re-point the edge to the primary relationships
                resourceGraph.RemoveEdge(directContactDependency);

                resourceGraph.AddEdge(
                    directContactDependency with { Source = studentContactAssociationResource });
            }

            string LogAndThrowException()
            {
                string message =
                    $"Unable to transform resource load graph as a student association for {contactResource.FullResourceName} is not defined.";

                throw new InvalidOperationException(message);
            }
        }
    }
}
