// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

public class DependencyCalculatorTests
{
    private DependencyCalculator? _dependencyCalculator;

    [TestFixture]
    public class Given_A_Sample_ApiSchema() : DependencyCalculatorTests
    {
        private readonly string _sampleSchema =
            """
            {
              "projectNameMapping": {
                "Ed-Fi": "ed-fi"
              },
              "projectSchemas": {
                "ed-fi": {
                  "resourceNameMapping": {
                    "AbsenceEventCategory": "absenceEventCategoryDescriptors"
                  },
                  "resourceSchemas": {
                    "absenceEventCategoryDescriptors": {
                      "documentPathsMapping": {
                      },
                      "isSchoolYearEnumeration": false,
                      "resourceName": "AbsenceEventCategoryDescriptor"
                    }
                  }
                }
              }
            }
            """;

        private readonly string _expectedDependencies =
            """
            [
                {
                  "resource": "/ed-fi/absenceEventCategoryDescriptors",
                  "order": 1,
                  "operations": [
                    "Create",
                    "Update"
                  ]
                }
            ]
            """;

        [SetUp]
        public void Setup()
        {
            var logger = NullLogger<ApiSchemaSchemaProvider>.Instance;
            _dependencyCalculator = new DependencyCalculator(JsonNode.Parse(_sampleSchema)!, logger);
        }

        [Test]
        public void It_should_calculate_dependencies()
        {
            var dependencies = _dependencyCalculator!.GetDependenciesFromResourceSchema();
            dependencies.Should().NotBeEmpty();
            dependencies.Count.Should().Be(1);

            var expectedDependencies = JsonNode.Parse(_expectedDependencies)!.AsArray();
            dependencies!.Should().BeEquivalentTo(expectedDependencies!, options => options
                .WithoutStrictOrdering()
                .IgnoringCyclicReferences());
        }
    }

    [TestFixture]
    public class Given_A_Sample_ApiSchema_With_Subclass_Resources() : DependencyCalculatorTests
    {
        private readonly string _sampleSchema =
            """
            {
                "projectNameMapping": {
                  "Ed-Fi": "ed-fi"
                },
                "projectSchemas": {
                  "ed-fi": {
                    "resourceNameMapping": {
                      "EducationOrganizationCategory": "educationOrganizationCategoryDescriptors",
                      "LocalEducationAgency": "localEducationAgencies",
                      "School": "schools"
                    },
                    "resourceSchemas": {
                      "educationOrganizationCategoryDescriptors": {
                      "documentPathsMapping": {
                      },
                      "isDescriptor": true,
                      "isSchoolYearEnumeration": false,          
                      "isSubclass": false,         
                      "resourceName": "EducationOrganizationCategoryDescriptor"
                    },
                    "localEducationAgencies": {
                      "documentPathsMapping": {
                        "EducationOrganizationCategoryDescriptor": {
                          "isDescriptor": true,
                          "isReference": true,
                          "projectName": "Ed-Fi",
                          "resourceName": "EducationOrganizationCategoryDescriptor"
                        },
                        "ParentLocalEducationAgency": {
                          "isReference": true,
                          "projectName": "Ed-Fi",
                          "resourceName": "LocalEducationAgency"
                        }
                      },         
                      "isSubclass": true,
                      "isSchoolYearEnumeration": false,       
                      "resourceName": "LocalEducationAgency",
                      "subclassType": "domainEntity",
                      "superclassProjectName": "Ed-Fi",
                      "superclassResourceName": "EducationOrganization"
                    },
                      "schools": {
                      "documentPathsMapping": {
                        "EducationOrganizationCategoryDescriptor": {
                          "isDescriptor": true,
                          "isReference": true,
                          "projectName": "Ed-Fi",
                          "resourceName": "EducationOrganizationCategoryDescriptor"
                        },
                        "LocalEducationAgency": {
                          "isReference": true,
                          "projectName": "Ed-Fi",
                          "resourceName": "LocalEducationAgency"
                        },
                        "WebSite": {
                          "isReference": false,
                          "path": "$.webSite"
                        }
                      },
                      "isSubclass": true,
                      "isSchoolYearEnumeration": false,
                      "resourceName": "School",
                      "subclassType": "domainEntity",
                      "superclassProjectName": "Ed-Fi",
                      "superclassResourceName": "EducationOrganization"
                    }
                  }
                }
              }
            }
            """;

        private readonly string _expectedDependencies =
            """
            [
                {
                  "resource": "/ed-fi/educationOrganizationCategoryDescriptors",
                  "order": 1,
                  "operations": [
                    "Create",
                    "Update"
                  ]
                },
                {
                  "resource": "/ed-fi/localEducationAgencies",
                  "order": 2,
                  "operations": [
                    "Create",
                    "Update"
                  ]
                },
                {
                  "resource": "/ed-fi/schools",
                  "order": 3,
                  "operations": [
                    "Create",
                    "Update"
                  ]
                }
            ]
            """;

        [SetUp]
        public void Setup()
        {
            var logger = NullLogger<ApiSchemaSchemaProvider>.Instance;
            _dependencyCalculator = new DependencyCalculator(JsonNode.Parse(_sampleSchema)!, logger);
        }

        [Test]
        public void It_should_calculate_dependencies()
        {
            var dependencies = _dependencyCalculator!.GetDependenciesFromResourceSchema();
            dependencies.Should().NotBeEmpty();

            var expectedDependencies = JsonNode.Parse(_expectedDependencies)!.AsArray();
            dependencies!.Should().BeEquivalentTo(expectedDependencies!, options => options
                .WithoutStrictOrdering()
                .IgnoringCyclicReferences());
        }
    }


    [TestFixture]
    public class Given_A_Sample_ApiSchema_With_Superclass_Reference() : DependencyCalculatorTests
    {
        private readonly string _sampleSchema =
            """
            {
                "projectNameMapping": {
                  "Ed-Fi": "ed-fi"
                },
                "projectSchemas": {
                  "ed-fi": {
                    "resourceNameMapping": {
                      "EducationOrganizationCategory": "educationOrganizationCategoryDescriptors",
                      "LocalEducationAgency": "localEducationAgencies",
                      "OpenStaffPosition": "openStaffPositions",
                      "School": "schools"
                    },
                    "resourceSchemas": {
                      "educationOrganizationCategoryDescriptors": {
                      "documentPathsMapping": {
                      },
                      "isDescriptor": true,   
                      "isSchoolYearEnumeration": false,       
                      "isSubclass": false,         
                      "resourceName": "EducationOrganizationCategoryDescriptor"
                    },
                    "openStaffPositions": {
                      "allowIdentityUpdates": false,
                      "documentPathsMapping": {            
                        "EducationOrganization": {
                          "isDescriptor": false,
                          "isReference": true,
                          "projectName": "Ed-Fi",
                          "resourceName": "EducationOrganization"
                        }            
                      },
                      "isSubclass": false,
                      "isSchoolYearEnumeration": false,
                      "resourceName": "OpenStaffPosition"
                    },
                    "localEducationAgencies": {
                      "documentPathsMapping": {
                        "EducationOrganizationCategoryDescriptor": {
                          "isDescriptor": true,
                          "isReference": true,
                          "projectName": "Ed-Fi",
                          "resourceName": "EducationOrganizationCategoryDescriptor"
                        },
                        "ParentLocalEducationAgency": {
                          "isReference": true,
                          "projectName": "Ed-Fi",
                          "resourceName": "LocalEducationAgency"
                        }
                      },         
                      "isSubclass": true,
                      "isSchoolYearEnumeration": false,       
                      "resourceName": "LocalEducationAgency",
                      "subclassType": "domainEntity",
                      "superclassProjectName": "Ed-Fi",
                      "superclassResourceName": "EducationOrganization"
                    },
                      "schools": {
                      "documentPathsMapping": {
                        "EducationOrganizationCategoryDescriptor": {
                          "isDescriptor": true,
                          "isReference": true,
                          "projectName": "Ed-Fi",
                          "resourceName": "EducationOrganizationCategoryDescriptor"
                        },
                        "LocalEducationAgency": {
                          "isReference": true,
                          "projectName": "Ed-Fi",
                          "resourceName": "LocalEducationAgency"
                        }
                      },
                      "isSubclass": true,
                      "isSchoolYearEnumeration": false,
                      "resourceName": "School"
                    }
                  }
                }
              }
            }
            """;

        private readonly string _expectedDescriptors =
            """
            [
                {
                  "resource": "/ed-fi/educationOrganizationCategoryDescriptors",
                  "order": 1,
                  "operations": [
                    "Create",
                    "Update"
                  ]
                },
                {
                  "resource": "/ed-fi/localEducationAgencies",
                  "order": 2,
                  "operations": [
                    "Create",
                    "Update"
                  ]
                },
                {
                  "resource": "/ed-fi/schools",
                  "order": 3,
                  "operations": [
                    "Create",
                    "Update"
                  ]
                },
                {
                  "resource": "/ed-fi/openStaffPositions",
                  "order": 4,
                  "operations": [
                    "Create",
                    "Update"
                  ]
                }
            ]
            """;

        [SetUp]
        public void Setup()
        {
            var logger = NullLogger<ApiSchemaSchemaProvider>.Instance;
            _dependencyCalculator = new DependencyCalculator(JsonNode.Parse(_sampleSchema)!, logger);
        }

        [Test]
        public void It_should_calculate_dependencies()
        {
            var dependencies = _dependencyCalculator!.GetDependenciesFromResourceSchema();
            dependencies.Should().NotBeEmpty();

            var expectedDependencies = JsonNode.Parse(_expectedDescriptors)!.AsArray();
            dependencies!.Should().BeEquivalentTo(expectedDependencies!, options => options
                .WithoutStrictOrdering()
                .IgnoringCyclicReferences());
        }
    }

    [TestFixture]
    public class Given_A_Sample_ApiSchema_Missing_ProjectSchemas() : DependencyCalculatorTests
    {
        private readonly string _sampleSchema =
            """
            {
                "projectNameMapping": {
                  "Ed-Fi": "ed-fi"
                }
            }
            """;

        [SetUp]
        public void Setup()
        {
            var logger = NullLogger<ApiSchemaSchemaProvider>.Instance;
            _dependencyCalculator = new DependencyCalculator(JsonNode.Parse(_sampleSchema)!, logger);
        }

        [Test]
        public void It_should_throw_invalid_operation()
        {
            Action act = () => _dependencyCalculator!.GetDependenciesFromResourceSchema();
            act.Should().Throw<InvalidOperationException>();
        }
    }

    [TestFixture]
    public class Given_A_Dependency_Calculator() : DependencyCalculatorTests
    {
        [Test]
        public void It_should_return_proper_ordered_dependencies1()
        {
            Dictionary<string, List<string>> resources = new Dictionary<string, List<string>>
            {
                { "A", ["B"] },
                { "B", [] },
                { "C", ["B"] },
            };

            var dependencies = DependencyCalculator.GetDependencies(resources);

            dependencies["A"].Should().Be(2);
            dependencies["B"].Should().Be(1);
            dependencies["C"].Should().Be(2);
        }

        [Test]
        public void It_should_return_proper_ordered_dependencies2()
        {
            Dictionary<string, List<string>> resources = new Dictionary<string, List<string>>
            {
                { "A", ["B"] },
                { "B", ["C", "D"] },
                { "C", [] },
                { "D", [] }
            };

            var dependencies = DependencyCalculator.GetDependencies(resources);

            dependencies["A"].Should().Be(3);
            dependencies["B"].Should().Be(2);
            dependencies["C"].Should().Be(1);
            dependencies["D"].Should().Be(1);
        }

        [Test]
        public void It_should_handle_circular_dependencies()
        {
            Dictionary<string, List<string>> resources = new Dictionary<string, List<string>>
            {
                { "EOCD", [] },
                { "OSP", ["S"] },
                { "LEA", ["EOCD", "LEA"] },
                { "S", ["EOCD", "LEA"] }
            };

            var dependencies = DependencyCalculator.GetDependencies(resources);

            dependencies["EOCD"].Should().Be(1);
            dependencies["LEA"].Should().Be(2);
            dependencies["S"].Should().Be(3);
            dependencies["OSP"].Should().Be(4);
        }
    }
}

