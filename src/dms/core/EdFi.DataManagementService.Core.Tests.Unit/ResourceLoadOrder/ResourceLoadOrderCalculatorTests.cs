// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ResourceLoadOrder;

public class ResourceLoadOrderCalculatorTests
{
    private ResourceLoadOrderCalculator? _resourceLoadCalculator;

    [TestFixture]
    [Parallelizable]
    public class GivenAnApiSchemaWithReferenceToAbstractResource : ResourceLoadOrderCalculatorTests
    {
        private readonly ApiSchemaDocumentNodes _apiSchemaNodes = new ApiSchemaBuilder()
            .WithStartProject(
                abstractResources: new JsonObject { ["EducationOrganization"] = JsonValue.Create(new { }) }
            )
            .WithStartResource("EducationOrganizationCategoryDescriptor", isDescriptor: true)
            .WithEndResource()
            .WithStartResource("LocalEducationAgency", isSubclass: true)
            .WithSuperclassInformation("domainEntity", "ignored", "EducationOrganization")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("EducationOrganizationCategoryDescriptor", [])
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithStartResource("School", isSubclass: true)
            .WithSuperclassInformation("domainEntity", "ignored", "EducationOrganization")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("EducationOrganizationCategoryDescriptor", [])
            .WithDocumentPathReference("LocalEducationAgency", [])
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithStartResource("OpenStaffPosition")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("EducationOrganization", [])
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .AsApiSchemaNodes();

        private readonly LoadOrder[] _expectedResourceLoadOrder =
        [
            new LoadOrder(
                Resource: "/ed-fi/educationOrganizationCategoryDescriptors",
                Group: 1,
                Operations: ["Create", "Update"]
            ),
            new LoadOrder(
                Resource: "/ed-fi/localEducationAgencys",
                Group: 2,
                Operations: ["Create", "Update"]
            ),
            new LoadOrder(Resource: "/ed-fi/schools", Group: 3, Operations: ["Create", "Update"]),
            new LoadOrder(Resource: "/ed-fi/openStaffPositions", Group: 4, Operations: ["Create", "Update"]),
        ];

        [SetUp]
        public void Setup()
        {
            var apiSchemaProvider = A.Fake<IApiSchemaProvider>();

            A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes()).Returns(_apiSchemaNodes);

            var graphFactory = CreateGraphFactory(apiSchemaProvider);
            _resourceLoadCalculator = new ResourceLoadOrderCalculator([], graphFactory);
        }

        [Test]
        public void It_should_calculate_load_order()
        {
            var loadOrder = _resourceLoadCalculator!.GetLoadOrder().ToList();

            loadOrder.Should().NotBeEmpty();

            loadOrder
                .Should()
                .BeEquivalentTo(
                    _expectedResourceLoadOrder,
                    options => options.WithoutStrictOrdering().IgnoringCyclicReferences()
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class GivenAnApiSchemaWithAuthorizationConcerns : ResourceLoadOrderCalculatorTests
    {
        private readonly ApiSchemaDocumentNodes _apiSchemaNodes = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("Student")
            .WithEndResource()
            .WithStartResource("DisciplineAction")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("Student", [])
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithStartResource("StudentSchoolAssociation")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("Student", [])
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithStartResource("Staff")
            .WithEndResource()
            .WithStartResource("LocalContractedStaff")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("Staff", [])
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithStartResource("StaffEducationOrganizationEmploymentAssociation")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("Staff", [])
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithStartResource("StaffEducationOrganizationAssignmentAssociation")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("Staff", [])
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithStartResource("Contact")
            .WithEndResource()
            .WithStartResource("SurveyResponse")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("Contact", [])
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithStartResource("StudentContactAssociation")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("Contact", [])
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .AsApiSchemaNodes();

        private readonly LoadOrder[] _expectedResourceLoadOrder =
        [
            new LoadOrder(Resource: "/ed-fi/contacts", Group: 1, Operations: ["Create"]),
            new LoadOrder(Resource: "/ed-fi/staffs", Group: 1, Operations: ["Create"]),
            new LoadOrder(Resource: "/ed-fi/students", Group: 1, Operations: ["Create"]),
            new LoadOrder(
                Resource: "/ed-fi/staffEducationOrganizationAssignmentAssociations",
                Group: 2,
                Operations: ["Create", "Update"]
            ),
            new LoadOrder(
                Resource: "/ed-fi/staffEducationOrganizationEmploymentAssociations",
                Group: 2,
                Operations: ["Create", "Update"]
            ),
            new LoadOrder(
                Resource: "/ed-fi/studentContactAssociations",
                Group: 2,
                Operations: ["Create", "Update"]
            ),
            new LoadOrder(
                Resource: "/ed-fi/studentSchoolAssociations",
                Group: 2,
                Operations: ["Create", "Update"]
            ),
            new LoadOrder(Resource: "/ed-fi/contacts", Group: 3, Operations: ["Update"]),
            new LoadOrder(Resource: "/ed-fi/staffs", Group: 3, Operations: ["Update"]),
            new LoadOrder(Resource: "/ed-fi/students", Group: 3, Operations: ["Update"]),
            new LoadOrder(Resource: "/ed-fi/disciplineActions", Group: 3, Operations: ["Create", "Update"]),
            new LoadOrder(
                Resource: "/ed-fi/localContractedStaffs",
                Group: 3,
                Operations: ["Create", "Update"]
            ),
            new LoadOrder(Resource: "/ed-fi/surveyResponses", Group: 3, Operations: ["Create", "Update"]),
        ];

        [SetUp]
        public void Setup()
        {
            var apiSchemaProvider = A.Fake<IApiSchemaProvider>();

            A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes()).Returns(_apiSchemaNodes);

            var graphFactory = CreateGraphFactory(apiSchemaProvider, [
                new PersonAuthorizationDependencyGraphTransformer(
                    apiSchemaProvider,
                    NullLogger<PersonAuthorizationDependencyGraphTransformer>.Instance
                ),
            ]);

            _resourceLoadCalculator = new ResourceLoadOrderCalculator(
                [
                    new PersonAuthorizationLoadOrderTransformer(
                        apiSchemaProvider,
                        NullLogger<PersonAuthorizationLoadOrderTransformer>.Instance
                    ),
                ], graphFactory);
        }

        [Test]
        public void It_should_calculate_load_order()
        {
            var loadOrder = _resourceLoadCalculator!.GetLoadOrder().ToList();

            loadOrder.Should().NotBeEmpty();

            loadOrder
                .Should()
                .BeEquivalentTo(
                    _expectedResourceLoadOrder,
                    options => options.WithoutStrictOrdering().IgnoringCyclicReferences()
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class GivenAnApiSchemaWithExtension : ResourceLoadOrderCalculatorTests
    {
        private readonly ApiSchemaDocumentNodes _apiSchemaNodes = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("Person")
            .WithEndResource()
            .WithEndProject()
            .WithStartProject("TPDM")
            .WithStartResource("Candidate")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("Person", [])
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .AsApiSchemaNodes();

        private readonly LoadOrder[] _expectedResourceLoadOrder =
        [
            new LoadOrder(Resource: "/ed-fi/persons", Group: 1, Operations: ["Create", "Update"]),
            new LoadOrder(Resource: "/tpdm/candidates", Group: 2, Operations: ["Create", "Update"]),
        ];

        [SetUp]
        public void Setup()
        {
            var apiSchemaProvider = A.Fake<IApiSchemaProvider>();

            A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes()).Returns(_apiSchemaNodes);

            var graphFactory = CreateGraphFactory(apiSchemaProvider);
            _resourceLoadCalculator = new ResourceLoadOrderCalculator([], graphFactory);
        }

        [Test]
        public void It_should_calculate_load_order()
        {
            var loadOrder = _resourceLoadCalculator!.GetLoadOrder().ToList();

            loadOrder.Should().NotBeEmpty();

            loadOrder
                .Should()
                .BeEquivalentTo(
                    _expectedResourceLoadOrder,
                    options => options.WithoutStrictOrdering().IgnoringCyclicReferences()
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class GivenAnApiSchemaWithBreakableCycle : ResourceLoadOrderCalculatorTests
    {
        private readonly ApiSchemaDocumentNodes _apiSchemaNodes = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("one")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("two", [], isRequired: false) // Soft dependency
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithStartResource("two")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("one", [], isRequired: true)
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .AsApiSchemaNodes();

        private readonly LoadOrder[] _expectedResourceLoadOrder =
        [
            new LoadOrder(Resource: "/ed-fi/ones", Group: 1, Operations: ["Create", "Update"]),
            new LoadOrder(Resource: "/ed-fi/twos", Group: 2, Operations: ["Create", "Update"]),
        ];

        [SetUp]
        public void Setup()
        {
            var apiSchemaProvider = A.Fake<IApiSchemaProvider>();

            A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes()).Returns(_apiSchemaNodes);

            var graphFactory = CreateGraphFactory(apiSchemaProvider);
            _resourceLoadCalculator = new ResourceLoadOrderCalculator([], graphFactory);
        }

        [Test]
        public void It_should_calculate_load_order()
        {
            var loadOrder = _resourceLoadCalculator!.GetLoadOrder().ToList();

            loadOrder.Should().NotBeEmpty();

            loadOrder
                .Should()
                .BeEquivalentTo(
                    _expectedResourceLoadOrder,
                    options => options.WithoutStrictOrdering().IgnoringCyclicReferences()
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class GivenAnApiSchemaWithSchoolYearType : ResourceLoadOrderCalculatorTests
    {
        private readonly ApiSchemaDocumentNodes _apiSchemaNodes = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("SchoolYearType", isSchoolYearEnumeration: true)
            .WithEndResource()
            .WithStartResource("School")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("SchoolYearType", [])
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .AsApiSchemaNodes();

        private readonly LoadOrder[] _expectedResourceLoadOrder =
        [
            new LoadOrder(Resource: "/ed-fi/schools", Group: 1, Operations: ["Create", "Update"]),
        ];

        [SetUp]
        public void Setup()
        {
            var apiSchemaProvider = A.Fake<IApiSchemaProvider>();

            A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes()).Returns(_apiSchemaNodes);

            var graphFactory = CreateGraphFactory(apiSchemaProvider);
            _resourceLoadCalculator = new ResourceLoadOrderCalculator([], graphFactory);
        }

        [Test]
        public void It_should_ignore_school_year_type()
        {
            var loadOrder = _resourceLoadCalculator!.GetLoadOrder().ToList();

            loadOrder.Should().NotBeEmpty();

            loadOrder
                .Should()
                .BeEquivalentTo(
                    _expectedResourceLoadOrder,
                    options => options.WithoutStrictOrdering().IgnoringCyclicReferences()
                );
        }
    }

    private static ResourceDependencyGraphFactory CreateGraphFactory(IApiSchemaProvider apiSchemaProvider,
        IEnumerable<IResourceDependencyGraphTransformer>? graphTransformers = null)
    {
        var graphFactory = new ResourceDependencyGraphFactory(
            apiSchemaProvider,
            graphTransformers ?? [],
            NullLogger<ResourceLoadOrderCalculator>.Instance);

        return graphFactory;
    }
}
