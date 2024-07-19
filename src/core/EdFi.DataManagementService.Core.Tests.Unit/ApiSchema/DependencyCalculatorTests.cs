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

        private readonly string _expectedDescriptor =
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
            var dependencies = _dependencyCalculator!.GetDependencies();
            dependencies.Should().NotBeEmpty();
            dependencies.Count.Should().Be(1);

            var expectedDependencies = JsonNode.Parse(_expectedDescriptor)!.AsArray();
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

        private readonly string _expectedDescriptor =
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
            var dependencies = _dependencyCalculator!.GetDependencies();
            dependencies.Should().NotBeEmpty();

            var expectedDependencies = JsonNode.Parse(_expectedDescriptor)!.AsArray();
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

        private readonly string _expectedDescriptor =
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
                  "resource": "/ed-fi/schools",
                  "order": 2,
                  "operations": [
                    "Create",
                    "Update"
                  ]
                },
                {
                  "resource": "/ed-fi/openStaffPositions",
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
            var dependencies = _dependencyCalculator!.GetDependencies();
            dependencies.Should().NotBeEmpty();

            var expectedDependencies = JsonNode.Parse(_expectedDescriptor)!.AsArray();
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
            Action act = () => _dependencyCalculator!.GetDependencies();
            act.Should().Throw<InvalidOperationException>();
        }
    }
}

