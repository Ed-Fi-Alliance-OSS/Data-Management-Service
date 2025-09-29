// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ResourceLoadOrder;
using NUnit.Framework;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using QuickGraph;

namespace EdFi.DataManagementService.Core.Tests.Unit.ResourceLoadOrder;

[TestFixture]
public class ResourceDependencyGraphMLFactoryTests
{
    /// private static ProjectName EdFi() => new("Ed-Fi");
    private static FullResourceName FRN(string project, string resource) =>
        new(new ProjectName(project), new ResourceName(resource));

    private static ResourceDependencyGraphVertex Vertex(string endpointName, string resourceName)
    {
        var v = A.Fake<ResourceDependencyGraphVertex>();
        A.CallTo(() => v.GetEndpointName()).Returns(endpointName);
        A.CallTo(() => v.FullResourceName).Returns(FRN("Ed-Fi", resourceName));
        return v;
    }

    private static ResourceDependencyGraphEdge Edge(ResourceDependencyGraphVertex source, ResourceDependencyGraphVertex target, bool isRequired)
    {
        /// var reference = A.Fake<DocumentPath>();
        /// A.CallTo(() => reference.ProjectName).Returns(EdFi());
        /// A.CallTo(() => reference.ResourceName).Returns(new ResourceName(target.ResourceName.Value));
        /// A.CallTo(() => reference.IsRequired).Returns(isRequired);

        var e = A.Fake<ResourceDependencyGraphEdge>();
        A.CallTo(() => e.Source).Returns(source);
        A.CallTo(() => e.Target).Returns(target);
        A.CallTo(() => e.IsRequired).Returns(isRequired);
        return e;
    }

    private static BidirectionalGraph<ResourceDependencyGraphVertex, ResourceDependencyGraphEdge> Graph()
        => new();

    [Test]
    public void CreateGraphML_should_expand_person_vertices_with_retry_nodes_and_primary_association_edges()
    {
        // Arrange
        // Person: Student
        var student = Vertex("ed-fi/students", "Student");
        // Primary association: StudentSchoolAssociation
        var ssa = Vertex("ed-fi/studentSchoolAssociations", "StudentSchoolAssociation");
        // Downstream dependency of primary association
        var school = Vertex("ed-fi/schools", "School");

        // Primary association edge: Student -> StudentSchoolAssociation (required)
        var e1 = Edge(student, ssa, isRequired: true);
        // Downstream dependency: StudentSchoolAssociation -> School (optional)
        var e2 = Edge(ssa, school, isRequired: false);

        var resourceGraph = Graph();
        resourceGraph.AddVertexRange(new[] { student, ssa, school });
        resourceGraph.AddEdge(e1);
        resourceGraph.AddEdge(e2);

        var factoryDep = A.Fake<IResourceDependencyGraphFactory>();
        A.CallTo(() => factoryDep.Create()).Returns(resourceGraph);

        var logger = A.Fake<ILogger<ResourceDependencyGraphMLFactory>>();
        var sut = new ResourceDependencyGraphMLFactory(factoryDep, logger);

        // Act
        var graphML = sut.CreateGraphML();

        // Assert
        graphML.Id.Should().Be("EdFi Dependencies");

        // Nodes: student, student#Retry, ssa, school
        var nodeIds = graphML.Nodes.Select(n => n.Id).ToArray();
        nodeIds.Should().Contain(new[]
        {
            "ed-fi/students",
            "ed-fi/students#Retry",
            "ed-fi/studentSchoolAssociations",
            "ed-fi/schools"
        });

        // Edges:
        // 1) Upstream primary association retry dependency:
        //    studentSchoolAssociations -> students#Retry (required: true)
        // 2) Standard association: students -> studentSchoolAssociations (required: true)
        // 3) Downstream dependency relocated to retry:
        //    students#Retry -> schools (required: false)
        var edges = graphML.Edges.Select(e => (e.Source.Id, e.Target.Id, e.IsReferenceRequired)).ToArray();

        edges.Should().Contain(("ed-fi/studentSchoolAssociations", "ed-fi/students#Retry", true));
        edges.Should().Contain(("ed-fi/students", "ed-fi/studentSchoolAssociations", true));
        edges.Should().Contain(("ed-fi/students#Retry", "ed-fi/schools", false));
    }

    [Test]
    public void CreateGraphML_should_project_non_primary_edges_without_retry_redirects()
    {
        // Arrange
        // Person: Student (still expands with retry node)
        var student = Vertex("ed-fi/students", "Student");
        // Non-primary related resource
        var section = Vertex("ed-fi/sections", "Section");

        // Non-primary edge: Student -> Section (optional)
        var e = Edge(student, section, isRequired: false);

        var resourceGraph = Graph();
        resourceGraph.AddVertexRange(new[] { student, section });
        resourceGraph.AddEdge(e);

        var factoryDep = A.Fake<IResourceDependencyGraphFactory>();
        A.CallTo(() => factoryDep.Create()).Returns(resourceGraph);

        var logger = A.Fake<ILogger<ResourceDependencyGraphMLFactory>>();
        var sut = new ResourceDependencyGraphMLFactory(factoryDep, logger);

        // Act
        var graphML = sut.CreateGraphML();

        // Assert
        // Nodes include student & student#Retry due to person expansion, and section
        var nodeIds = graphML.Nodes.Select(n => n.Id).ToArray();
        nodeIds.Should().Contain(new[]
        {
            "ed-fi/students",
            "ed-fi/students#Retry",
            "ed-fi/sections"
        });

        // Only the original edge should appear; no extra edge to/from #Retry for non-primary associations
        var edges = graphML.Edges
            .Select(e2 => (Source: e2.Source.Id, Target: e2.Target.Id, e2.IsReferenceRequired))
            .ToArray();

        edges.Should()
            .ContainSingle(x =>
                x.Source == "ed-fi/students" && x.Target == "ed-fi/sections" && !x.IsReferenceRequired);

        // No retry redirect
        edges.Should().NotContain(x => x.Source == "ed-fi/students#Retry" || x.Target == "ed-fi/students#Retry");
    }

    [Test]
    public void CreateGraphML_should_include_retry_nodes_for_all_person_types()
    {
        // Arrange
        var staff = Vertex("ed-fi/staffs", "Staff");
        var parent = Vertex("ed-fi/parents", "Parent");
        var contact = Vertex("ed-fi/contacts", "Contact");

        var resourceGraph = Graph();
        resourceGraph.AddVertexRange(new[] { staff, parent, contact });

        var factoryDep = A.Fake<IResourceDependencyGraphFactory>();
        A.CallTo(() => factoryDep.Create()).Returns(resourceGraph);

        var logger = A.Fake<ILogger<ResourceDependencyGraphMLFactory>>();
        var sut = new ResourceDependencyGraphMLFactory(factoryDep, logger);

        // Act
        var graphML = sut.CreateGraphML();

        // Assert
        var nodeIds = graphML.Nodes.Select(n => n.Id).ToArray();
        nodeIds.Should().Contain(new[]
        {
            "ed-fi/staffs",
            "ed-fi/staffs#Retry",
            "ed-fi/parents",
            "ed-fi/parents#Retry",
            "ed-fi/contacts",
            "ed-fi/contacts#Retry",
        });
    }
}
