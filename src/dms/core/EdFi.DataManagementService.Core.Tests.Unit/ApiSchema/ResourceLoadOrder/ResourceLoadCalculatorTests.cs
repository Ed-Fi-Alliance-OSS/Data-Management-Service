// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.ResourceLoadOrder;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using QuickGraph;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema.ResourceLoadOrder;

public class ResourceLoadCalculatorTests
{
    private Calculator? _resourceLoadCalculator;

    [TestFixture]
    public class GivenAnApiSchemaWithReferenceToAbstractResource : ResourceLoadCalculatorTests
    {
        private readonly ApiSchemaDocuments _sampleApiSchemaDocuments = new ApiSchemaBuilder()
            .WithStartProject(abstractResources: new JsonObject
            {
                ["EducationOrganization"] = JsonValue.Create(new { }),
            })

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
            .ToApiSchemaDocuments();

        private readonly LoadOrder[] _expectedResourceLoadOrder =
        [
            new LoadOrder
            {
                Resource = "/ed-fi/educationOrganizationCategoryDescriptors",
                Order = 1,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/localEducationAgencys",
                Order = 2,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/schools",
                Order = 3,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/openStaffPositions",
                Order = 4,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            },
        ];

        [SetUp]
        public void Setup()
        {
            _resourceLoadCalculator =
                new Calculator(NullLogger<Calculator>.Instance, [], []);
        }

        [Test]
        public void It_should_calculate_load_order()
        {
            var loadOrder = _resourceLoadCalculator!
                .GetGroupedLoadOrder(_sampleApiSchemaDocuments)
                .ToList();

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
    public class GivenAnApiSchemaWithAuthorizationConcerns : ResourceLoadCalculatorTests
    {
        private readonly ApiSchemaDocuments _sampleApiSchemaDocuments = new ApiSchemaBuilder()
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
            .ToApiSchemaDocuments();

        private readonly LoadOrder[] _expectedResourceLoadOrder =
        [
            new LoadOrder
            {
                Resource = "/ed-fi/contacts",
                Order = 1,
                Operations =
                [
                    "Create"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/staffs",
                Order = 1,
                Operations =
                [
                    "Create"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/students",
                Order = 1,
                Operations =
                [
                    "Create"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/staffEducationOrganizationAssignmentAssociations",
                Order = 2,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/staffEducationOrganizationEmploymentAssociations",
                Order = 2,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/studentContactAssociations",
                Order = 2,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/studentSchoolAssociations",
                Order = 2,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/contacts",
                Order = 3,
                Operations =
                [
                    "Update"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/staffs",
                Order = 3,
                Operations =
                [
                    "Update"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/students",
                Order = 3,
                Operations =
                [
                    "Update"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/disciplineActions",
                Order = 3,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/localContractedStaffs",
                Order = 3,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/surveyResponses",
                Order = 3,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            }
        ];

        [SetUp]
        public void Setup()
        {
            _resourceLoadCalculator =
                new Calculator(NullLogger<Calculator>.Instance,
                    [new PersonAuthorizationGraphTransformer()],
                    [new PersonAuthorizationOrderTransformer()]);
        }

        [Test]
        public void It_should_calculate_load_order()
        {
            var loadOrder = _resourceLoadCalculator!
                .GetGroupedLoadOrder(_sampleApiSchemaDocuments)
                .ToList();

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
    public class GivenAnApiSchemaWithExtension : ResourceLoadCalculatorTests
    {
        private readonly ApiSchemaDocuments _sampleApiSchemaDocuments = new ApiSchemaBuilder()
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

            .ToApiSchemaDocuments();

        private readonly LoadOrder[] _expectedResourceLoadOrder =
        [
            new LoadOrder
            {
                Resource = "/ed-fi/persons",
                Order = 1,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            },
            new LoadOrder
            {
                Resource = "/tpdm/candidates",
                Order = 2,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            }
        ];

        [SetUp]
        public void Setup()
        {
            _resourceLoadCalculator =
                new Calculator(NullLogger<Calculator>.Instance, [], []);
        }

        [Test]
        public void It_should_calculate_load_order()
        {
            var loadOrder = _resourceLoadCalculator!
                .GetGroupedLoadOrder(_sampleApiSchemaDocuments)
                .ToList();

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
    public class GivenAnApiSchemaWithBreakableCycle : ResourceLoadCalculatorTests
    {
        private readonly ApiSchemaDocuments _sampleApiSchemaDocuments = new ApiSchemaBuilder()
            .WithStartProject()

            .WithStartResource("one")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("two", [], isRequired: false)
            .WithEndDocumentPathsMapping()
            .WithEndResource()

            .WithStartResource("two")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("one", [], isRequired: true)
            .WithEndDocumentPathsMapping()
            .WithEndResource()

            .WithEndProject()
            .ToApiSchemaDocuments();

        private readonly LoadOrder[] _expectedResourceLoadOrder =
        [
            new LoadOrder
            {
                Resource = "/ed-fi/ones",
                Order = 1,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            },
            new LoadOrder
            {
                Resource = "/ed-fi/twos",
                Order = 2,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            }
        ];

        [SetUp]
        public void Setup()
        {
            _resourceLoadCalculator =
                new Calculator(NullLogger<Calculator>.Instance, [], []);
        }

        [Test]
        public void It_should_calculate_load_order()
        {
            var loadOrder = _resourceLoadCalculator!
                .GetGroupedLoadOrder(_sampleApiSchemaDocuments)
                .ToList();

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
    public class GivenAnApiSchemaWithUnbreakableCycle : ResourceLoadCalculatorTests
    {
        private readonly ApiSchemaDocuments _sampleApiSchemaDocuments = new ApiSchemaBuilder()
            .WithStartProject()

            .WithStartResource("one")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("two", [], isRequired: true)
            .WithEndDocumentPathsMapping()
            .WithEndResource()

            .WithStartResource("two")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("one", [], isRequired: true)
            .WithEndDocumentPathsMapping()
            .WithEndResource()

            .WithEndProject()
            .ToApiSchemaDocuments();

        [SetUp]
        public void Setup()
        {
            _resourceLoadCalculator =
                new Calculator(NullLogger<Calculator>.Instance, [], []);
        }

        [Test]
        public void It_should_throw_exception()
        {
            Action act = () => _resourceLoadCalculator!
                .GetGroupedLoadOrder(_sampleApiSchemaDocuments);

            act.Should().Throw<NonAcyclicGraphException>();
        }
    }

    [TestFixture]
    public class GivenAnApiSchemaWithSchoolYearType : ResourceLoadCalculatorTests
    {
        private readonly ApiSchemaDocuments _sampleApiSchemaDocuments = new ApiSchemaBuilder()
            .WithStartProject()

            .WithStartResource("SchoolYearType", isSchoolYearEnumeration: true)
            .WithEndResource()

            .WithStartResource("School")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("SchoolYearType", [])
            .WithEndDocumentPathsMapping()
            .WithEndResource()

            .WithEndProject()
            .ToApiSchemaDocuments();

        private readonly LoadOrder[] _expectedResourceLoadOrder =
        [
            new LoadOrder
            {
                Resource = "/ed-fi/schools",
                Order = 1,
                Operations =
                [
                    "Create",
                    "Update"
                ]
            }
        ];

        [SetUp]
        public void Setup()
        {
            _resourceLoadCalculator =
                new Calculator(NullLogger<Calculator>.Instance, [], []);
        }

        [Test]
        public void It_should_ignore_school_year_type()
        {
            var loadOrder = _resourceLoadCalculator!
                .GetGroupedLoadOrder(_sampleApiSchemaDocuments)
                .ToList();

            loadOrder.Should().NotBeEmpty();

            loadOrder
                .Should()
                .BeEquivalentTo(
                    _expectedResourceLoadOrder,
                    options => options.WithoutStrictOrdering().IgnoringCyclicReferences()
                );
        }
    }
}
