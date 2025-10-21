// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using QuickGraph;
using EndpointName = EdFi.DataManagementService.Core.ApiSchema.Model.EndpointName;

namespace EdFi.DataManagementService.Core.Tests.Unit.ResourceLoadOrder;

[TestFixture]
public class ResourceDependencyGraphMLFactoryTests
{
    private IResourceDependencyGraphFactory _graphFactory = null!;
    private ICoreProjectNameProvider _projectNameProvider = null!;
    private ILogger<ResourceDependencyGraphMLFactory> _logger = null!;
    private ResourceDependencyGraphMLFactory _graphMLFactory = null!;
    private ProjectName _edFiProject;

    [SetUp]
    public void SetUp()
    {
        _graphFactory = A.Fake<IResourceDependencyGraphFactory>();
        _projectNameProvider = A.Fake<ICoreProjectNameProvider>();
        _logger = A.Fake<ILogger<ResourceDependencyGraphMLFactory>>();

        _edFiProject = new ProjectName("EdFi");
        A.CallTo(() => _projectNameProvider.GetCoreProjectName()).Returns(_edFiProject);

        _graphMLFactory = new ResourceDependencyGraphMLFactory(_graphFactory, _projectNameProvider, _logger);
    }

    [Test]
    public void CreateGraphML_WhenNoVerticesOrEdges_ReturnsEmptyGraphML()
    {
        // Arrange
        var empty = new BidirectionalGraph<ResourceDependencyGraphVertex, ResourceDependencyGraphEdge>();
        A.CallTo(() => _graphFactory.Create()).Returns(empty);

        // Act
        var result = _graphMLFactory.CreateGraphML();

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be("EdFi Dependencies");
        result.Nodes.Should().BeEmpty();
        result.Edges.Should().BeEmpty();
    }

    [Test]
    public void CreateGraphML_AddsRetryNodesForPersonTypes()
    {
        // Arrange
        var student = Vertex("Student");
        var staff = Vertex("Staff");
        var parent = Vertex("Parent");
        var contact = Vertex("Contact");
        var notPerson = Vertex("School");

        var g = new BidirectionalGraph<ResourceDependencyGraphVertex, ResourceDependencyGraphEdge>();

        g.AddVertexRange([student, staff, parent, contact, notPerson]);

        A.CallTo(() => _graphFactory.Create()).Returns(g);

        // Act
        var graphML = _graphMLFactory.CreateGraphML();

        // Assert
        var nodeIds = graphML.Nodes.Select(n => n.Id).ToArray();

        nodeIds
            .Should()
            .Contain(
                [
                    "/ed-fi/students",
                    "/ed-fi/students#Retry",
                    "/ed-fi/staffs",
                    "/ed-fi/staffs#Retry",
                    "/ed-fi/parents",
                    "/ed-fi/parents#Retry",
                    "/ed-fi/contacts",
                    "/ed-fi/contacts#Retry",
                    "/ed-fi/schools",
                ]
            );

        // Only person types have retry nodes
        nodeIds.Count(id => id.EndsWith("#Retry", StringComparison.Ordinal)).Should().Be(4);
    }

    [Test]
    public void CreateGraphML_UpstreamPrimaryAssociation_AddsRetryDependencyAndOriginalEdge()
    {
        // Arrange
        // Student -> StudentSchoolAssociation (primary association)
        var student = Vertex("Student");
        var ssa = Vertex("StudentSchoolAssociation");

        var g = new BidirectionalGraph<ResourceDependencyGraphVertex, ResourceDependencyGraphEdge>();
        g.AddVertexRange([student, ssa]);

        var e = Edge(student, ssa, isRequired: true);
        g.AddEdge(e);

        A.CallTo(() => _graphFactory.Create()).Returns(g);

        // Act
        GraphML graphML = _graphMLFactory.CreateGraphML();

        // Assert
        var ids = graphML.Nodes.Select(n => n.Id).ToArray();

        ids.Should()
            .Contain(["/ed-fi/students", "/ed-fi/students#Retry", "/ed-fi/studentSchoolAssociations"]);

        var edges = graphML
            .Edges.Select(x => (Source: x.Source.Id, Target: x.Target.Id, x.IsReferenceRequired))
            .ToArray();

        edges
            .Should()
            .Contain(
                (
                    Source: "/ed-fi/studentSchoolAssociations",
                    Target: "/ed-fi/students#Retry",
                    IsReferenceRequired: true
                ),
                "primary association must point to #Retry as required"
            );

        edges
            .Should()
            .Contain(
                (
                    Source: "/ed-fi/students",
                    Target: "/ed-fi/studentSchoolAssociations",
                    IsReferenceRequired: true
                ),
                "original association edge is kept"
            );
    }

    [Test]
    public void CreateGraphML_DownstreamOfPrimaryAssociation_IsRedirectedToRetryNode()
    {
        // Arrange
        // Graph:
        // Student -> StudentSchoolAssociation (primary)
        // StudentSchoolAssociation -> SectionAssociation (downstream)
        var student = Vertex("Student");
        var studentSchoolAssociation = Vertex("StudentSchoolAssociation");
        var studentSectionAssociation = Vertex("StudentSectionAssociation"); // any downstream target

        var g = new BidirectionalGraph<ResourceDependencyGraphVertex, ResourceDependencyGraphEdge>();

        g.AddVertexRange([student, studentSchoolAssociation, studentSectionAssociation]);
        g.AddEdge(Edge(student, studentSchoolAssociation, isRequired: true)); // upstream primary
        g.AddEdge(Edge(studentSchoolAssociation, studentSectionAssociation, isRequired: false)); // downstream of primary

        A.CallTo(() => _graphFactory.Create()).Returns(g);

        // Act
        GraphML graphML = _graphMLFactory.CreateGraphML();

        // Assert
        // Expect edge from students#Retry -> studentSectionAssociations (not from ssa)
        var tupleEdges = graphML
            .Edges.Select(x => (Source: x.Source.Id, Target: x.Target.Id, x.IsReferenceRequired))
            .ToArray();

        tupleEdges
            .Should()
            .Contain(
                (
                    Source: "/ed-fi/students#Retry",
                    Target: "/ed-fi/studentSectionAssociations",
                    IsReferenceRequired: false
                )
            );

        tupleEdges
            .Should()
            .NotContain(
                (
                    Source: "/ed-fi/studentSchoolAssociations",
                    Target: "/ed-fi/studentSectionAssociations",
                    IsReferenceRequired: false
                )
            );
    }

    [Test]
    public void CreateGraphML_NonPrimaryAssociation_RemainsUnchanged()
    {
        // Arrange
        // School -> CalendarDate (non-primary)
        var school = Vertex("School");
        var calendarDate = Vertex("CalendarDate");

        var g = new BidirectionalGraph<ResourceDependencyGraphVertex, ResourceDependencyGraphEdge>();
        g.AddVertexRange([school, calendarDate]);
        g.AddEdge(Edge(school, calendarDate, isRequired: false));

        A.CallTo(() => _graphFactory.Create()).Returns(g);

        // Act
        GraphML graphML = _graphMLFactory.CreateGraphML();

        // Assert
        var edges = graphML
            .Edges.Select(e => (Source: e.Source.Id, Target: e.Target.Id, e.IsReferenceRequired))
            .ToArray();

        edges
            .Should()
            .Contain((Source: "/ed-fi/schools", Target: "/ed-fi/calendarDates", IsReferenceRequired: false));
    }

    [Test]
    public void CreateGraphML_RequiredFlagsPropagateCorrectly()
    {
        // Arrange
        // Staff -> StaffEducationOrganizationEmploymentAssociation (primary upstream, required=false)
        // Downstream: EmploymentAssociation -> Contact (required=true) should redirect to staffs#Retry with required=true
        var staff = Vertex("Staff");
        var employment = Vertex("StaffEducationOrganizationEmploymentAssociation");
        var contact = Vertex("Contact");

        var g = new BidirectionalGraph<ResourceDependencyGraphVertex, ResourceDependencyGraphEdge>();
        g.AddVertexRange([staff, employment, contact]);
        g.AddEdge(Edge(staff, employment, isRequired: false)); // upstream primary (not required)
        g.AddEdge(Edge(employment, contact, isRequired: true)); // downstream (required)

        A.CallTo(() => _graphFactory.Create()).Returns(g);

        // Act
        GraphML graphML = _graphMLFactory.CreateGraphML();

        // Assert
        var edges = graphML
            .Edges.Select(e => (Source: e.Source.Id, Target: e.Target.Id, e.IsReferenceRequired))
            .ToArray();

        // upstream adds required=true edge: primary -> staffs#Retry (always required)
        edges
            .Should()
            .Contain(
                (
                    Source: "/ed-fi/staffEducationOrganizationEmploymentAssociations",
                    Target: "/ed-fi/staffs#Retry",
                    IsReferenceRequired: true
                )
            );

        // redirected downstream keeps its original required flag (true)
        edges
            .Should()
            .Contain((Source: "/ed-fi/staffs#Retry", Target: "/ed-fi/contacts", IsReferenceRequired: true));
    }

    [Test]
    public void CreateGraphML_HandlesBothStaffPrimaryAssociations()
    {
        // Arrange
        // Staff -> Assignment (primary)
        // Staff -> Employment (primary)
        var staff = Vertex("Staff");
        var assignment = Vertex("StaffEducationOrganizationAssignmentAssociation");
        var employment = Vertex("StaffEducationOrganizationEmploymentAssociation");
        var downstream1 = Vertex("Section");
        var downstream2 = Vertex("EducationOrganization");

        var g = new BidirectionalGraph<ResourceDependencyGraphVertex, ResourceDependencyGraphEdge>();

        g.AddVertexRange([staff, assignment, employment, downstream1, downstream2]);
        g.AddEdge(Edge(staff, assignment, true));
        g.AddEdge(Edge(staff, employment, true));
        g.AddEdge(Edge(assignment, downstream1, false));
        g.AddEdge(Edge(employment, downstream2, false));

        A.CallTo(() => _graphFactory.Create()).Returns(g);

        // Act
        GraphML graphML = _graphMLFactory.CreateGraphML();

        // Assert
        var edges = graphML.Edges.Select(e => (e.Source.Id, e.Target.Id)).ToArray();

        edges
            .Should()
            .Contain(("/ed-fi/staffEducationOrganizationAssignmentAssociations", "/ed-fi/staffs#Retry"));
        edges
            .Should()
            .Contain(("/ed-fi/staffEducationOrganizationEmploymentAssociations", "/ed-fi/staffs#Retry"));

        edges.Should().Contain(("/ed-fi/staffs#Retry", "/ed-fi/sections"));
        edges.Should().Contain(("/ed-fi/staffs#Retry", "/ed-fi/educationOrganizations"));

        // No edges from the primary association nodes to their downstream targets anymore
        edges
            .Should()
            .NotContain(("/ed-fi/staffEducationOrganizationAssignmentAssociations", "/ed-fi/sections"));

        edges
            .Should()
            .NotContain(
                ("/ed-fi/staffEducationOrganizationEmploymentAssociations", "/ed-fi/educationOrganizations")
            );
    }

    [Test]
    public void CreateGraphML_DistinctAndSortedEdges_NormalizesVertices()
    {
        // Arrange
        // Build a graph that can produce duplicate projected edges and check the set is distinct and sorted
        var school = Vertex("School");
        var calendarDate = Vertex("CalendarDate");
        var studentSchoolAttendanceEvent = Vertex("StudentSchoolAttendanceEvent");
        var assessment = Vertex("Assessment");
        var assessmentItem = Vertex("AssessmentItem");

        var g = new BidirectionalGraph<ResourceDependencyGraphVertex, ResourceDependencyGraphEdge>();

        g.AddVertexRange([studentSchoolAttendanceEvent, school, calendarDate, assessmentItem, assessment]);

        // Add an edge out of alphabetical order
        g.AddEdge(Edge(school, studentSchoolAttendanceEvent, false));

        // add the same edge twice
        g.AddEdge(Edge(school, calendarDate, false));
        g.AddEdge(Edge(school, calendarDate, false));

        // Add another edge with a source that is alphabetically before the previously added edges
        g.AddEdge(Edge(assessment, assessmentItem, false));

        A.CallTo(() => _graphFactory.Create()).Returns(g);

        // Act
        GraphML graphML = _graphMLFactory.CreateGraphML();

        // Assert
        var edges = graphML.Edges.Select(e => (e.Source.Id, e.Target.Id)).ToArray();

        // Verify the edges are sorted by source and then target
        edges
            .Should()
            .Equal(
                ("/ed-fi/assessments", "/ed-fi/assessmentItems"),
                ("/ed-fi/schools", "/ed-fi/calendarDates"),
                ("/ed-fi/schools", "/ed-fi/studentSchoolAttendanceEvents")
            );

        // Verify the duplicate edges are made distinct
        var schoolCalendarDateEdges = graphML
            .Edges.Where(e => e.Source.Id == "/ed-fi/schools" && e.Target.Id == "/ed-fi/calendarDates")
            .ToList();
        schoolCalendarDateEdges.Count.Should().Be(1);

        // Verify the edges use actual vertex instances in the graph
        var schoolNode = graphML.Nodes.Single(n => n.Id == "/ed-fi/schools");
        var calendarDates = graphML.Nodes.Single(n => n.Id == "/ed-fi/calendarDates");
        schoolCalendarDateEdges.Single().Source.Should().BeSameAs(schoolNode);
        schoolCalendarDateEdges.Single().Target.Should().BeSameAs(calendarDates);
    }

    // Helpers

    private static ResourceDependencyGraphVertex Vertex(string resourceName)
    {
        var projectName = new ProjectName("EdFi");
        var projectEndpoint = new ProjectEndpointName("ed-fi"); // sensible default
        var resName = new ResourceName(resourceName);
        var endpointName = new EndpointName(ToPluralLowerKebab(resourceName));
        bool isExtension = false;
        bool isSubclass = false;
        ResourceName superclass = default; // unused when isSubclass == false
        bool isSchoolYearEnum = false;

        return new ResourceDependencyGraphVertex(
            projectName,
            projectEndpoint,
            resName,
            endpointName,
            isExtension,
            isSubclass,
            superclass,
            isSchoolYearEnum
        );
    }

    private static string ToPluralLowerKebab(string singularPascal)
    {
        // Minimal pluralization and formatting matching GetEndpointName() used by the logic under test.
        // Rules needed for covered tests:
        // - Student -> students
        // - Staff -> staffs
        // - Parent -> parents
        // - Contact -> contacts
        // - School -> schools
        // - CalendarDate -> calendarDates
        // - StudentSchoolAssociation -> studentSchoolAssociations
        // - StudentSectionAssociation -> studentSectionAssociations
        // - StaffEducationOrganizationAssignmentAssociation -> staffEducationOrganizationAssignmentAssociations
        // - StaffEducationOrganizationEmploymentAssociation -> staffEducationOrganizationEmploymentAssociations
        // - Section -> sections
        // - EducationOrganization -> educationOrganizations
        // Convert first char to lower, keep others as-is, then append 's'
        if (string.IsNullOrWhiteSpace(singularPascal))
        {
            return singularPascal;
        }

        string camel = $"{char.ToLowerInvariant(singularPascal[0])}{singularPascal[1..]}";
        return $"{camel}s";
    }

    private static ResourceDependencyGraphEdge Edge(
        ResourceDependencyGraphVertex src,
        ResourceDependencyGraphVertex dst,
        bool isRequired
    ) => new(src, dst, isRequired);
}
