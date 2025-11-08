// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ArrayUniquenessValidationMiddlewareTests
{
    internal static ArrayUniquenessValidationMiddleware Middleware()
    {
        return new ArrayUniquenessValidationMiddleware(NullLogger.Instance);
    }

    internal static async Task<RequestInfo> CreateRequestInfoAndExecute(
        ApiSchemaDocuments apiSchema,
        string jsonBody,
        string endpointName
    )
    {
        FrontendRequest frontEndRequest = new(
            Path: $"ed-fi/{endpointName}",
            Body: jsonBody,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("")
        );

        RequestInfo requestInfo = new(frontEndRequest, RequestMethod.POST)
        {
            ApiSchemaDocuments = apiSchema,
            PathComponents = new(
                ProjectEndpointName: new("ed-fi"),
                EndpointName: new(endpointName),
                DocumentUuid: No.DocumentUuid
            ),
        };
        requestInfo.ProjectSchema = requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            new("ed-fi")
        )!;
        requestInfo.ResourceSchema = new ResourceSchema(
            requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new(endpointName))
                ?? new JsonObject()
        );

        requestInfo.ParsedBody = JsonNode.Parse(jsonBody)!;

        await Middleware().Execute(requestInfo, NullNext);
        return requestInfo;
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Document_With_No_Array_Uniqueness_Constraints
        : ArrayUniquenessValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            // Use a schema without array uniqueness constraints
            var noArrayUniquenessDocument = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("SimpleResource")
                .WithStartDocumentPathsMapping()
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                 "simpleProperty": "value"
                }
                """;

            _requestInfo = await CreateRequestInfoAndExecute(
                noArrayUniquenessDocument,
                jsonBody,
                "simpleresources"
            );
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Has_No_Duplicates : ArrayUniquenessValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            // Create schema with gradeLevel descriptor uniqueness constraint
            var apiSchema = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("School")
                .WithStartDocumentPathsMapping()
                .WithDocumentPathDescriptor("GradeLevelDescriptor", "$.gradeLevels[*].gradeLevelDescriptor")
                .WithEndDocumentPathsMapping()
                .WithArrayUniquenessConstraintSimple(["$.gradeLevels[*].gradeLevelDescriptor"])
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                  "schoolId":255901001,
                  "nameOfInstitution":"School Test",
                  "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#First grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Seventh grade"
                      }
                   ],
                   "educationOrganizationCategories":[
                      {
                         "educationOrganizationCategoryDescriptor":"uri://ed-fi.org/educationOrganizationCategoryDescriptor#School"
                      }
                   ]
                }
                """;

            _requestInfo = await CreateRequestInfoAndExecute(apiSchema, jsonBody, "schools");
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Has_Duplicate_Descriptors : ArrayUniquenessValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            // Create schema with gradeLevel descriptor uniqueness constraint
            var apiSchema = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("School")
                .WithStartDocumentPathsMapping()
                .WithDocumentPathDescriptor("GradeLevelDescriptor", "$.gradeLevels[*].gradeLevelDescriptor")
                .WithEndDocumentPathsMapping()
                .WithArrayUniquenessConstraintSimple(["$.gradeLevels[*].gradeLevelDescriptor"])
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                  "schoolId":255901001,
                  "nameOfInstitution":"School Test",
                  "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#First grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Seventh grade"
                      }
                   ],
                   "educationOrganizationCategories":[
                      {
                         "educationOrganizationCategoryDescriptor":"uri://ed-fi.org/educationOrganizationCategoryDescriptor#School"
                      }
                   ]
                }
                """;

            _requestInfo = await CreateRequestInfoAndExecute(apiSchema, jsonBody, "schools");
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_validation_error_with_duplicated_descriptor()
        {
            _requestInfo.FrontendResponse.Body!.ToJsonString().Should().Contain("Data Validation Failed");

            _requestInfo
                .FrontendResponse.Body!.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.gradeLevels":["The 3rd item of the gradeLevels has the same identifying values as another item earlier in the list."]}
                    """
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Has_Multiple_Element_Duplication : ArrayUniquenessValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var apiSchema = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Assessment")
                .WithArrayUniquenessConstraintSimple(
                    [
                        "$.items[*].assessmentItemReference.assessmentIdentifier",
                        "$.items[*].assessmentItemReference.identificationCode",
                        "$.items[*].assessmentItemReference.namespace",
                    ]
                )
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                 "assessmentTitle": "Reading",
                 "items": [
                   {
                     "assessmentResponse": "A",
                     "assessmentItemReference": {
                       "identificationCode": "111111",
                       "assessmentIdentifier": "222222",
                       "namespace": "uri://ed-fi.org/Assessment"
                     }
                   },
                   {
                     "assessmentResponse": "B",
                     "assessmentItemReference": {
                       "identificationCode": "111111",
                       "assessmentIdentifier": "222222",
                       "namespace": "uri://ed-fi.org/Assessment"
                     }
                   }
                 ]
                }
                """;

            _requestInfo = await CreateRequestInfoAndExecute(apiSchema, jsonBody, "assessments");
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_validation_error_with_duplicate_items()
        {
            _requestInfo.FrontendResponse.Body!.ToJsonString().Should().Contain("Data Validation Failed");

            _requestInfo
                .FrontendResponse.Body!.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.items":["The 2nd item of the items has the same identifying values as another item earlier in the list."]}
                    """
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Has_Multiple_Element_Only_Partial_Duplication
        : ArrayUniquenessValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var apiSchema = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Assessment")
                .WithArrayUniquenessConstraintSimple(
                    [
                        "$.items[*].assessmentItemReference.assessmentIdentifier",
                        "$.items[*].assessmentItemReference.identificationCode",
                        "$.items[*].assessmentItemReference.namespace",
                    ]
                )
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                 "assessmentTitle": "Reading",
                 "items": [
                   {
                     "assessmentResponse": "A",
                     "assessmentItemReference": {
                       "identificationCode": "000000",
                       "assessmentIdentifier": "222222",
                       "namespace": "uri://ed-fi.org/Assessment"
                     }
                   },
                   {
                     "assessmentResponse": "B",
                     "assessmentItemReference": {
                       "identificationCode": "111111",
                       "assessmentIdentifier": "222222",
                       "namespace": "uri://ed-fi.org/Assessment"
                     }
                   }
                 ]
                }
                """;

            _requestInfo = await CreateRequestInfoAndExecute(apiSchema, jsonBody, "assessments");
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Has_Two_Levels_And_No_Duplicates : ArrayUniquenessValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            // Create schema with nested array uniqueness constraints
            var apiSchema = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("RequiredImmunization")
                .WithStartDocumentPathsMapping()
                .WithEndDocumentPathsMapping()
                .WithArrayUniquenessConstraint(
                    [
                        new
                        {
                            paths = new[] { "$.requiredImmunizations[*].immunizationTypeDescriptor" },
                            nestedConstraints = new[]
                            {
                                new
                                {
                                    basePath = "$.requiredImmunizations[*]",
                                    paths = new[] { "$.dates[*].immunizationDate" },
                                },
                            },
                        },
                    ]
                )
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                 "requiredImmunizations": [
                    {
                        "dates": [
                            {
                                "immunizationDate": "2007-07-01"
                            }
                        ],
                        "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#MMR"
                    },
                    {
                        "dates": [
                            {
                                "immunizationDate": "2010-04-01"
                            }
                        ],
                        "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#IPV"
                    }
                  ]
                }
                """;

            _requestInfo = await CreateRequestInfoAndExecute(apiSchema, jsonBody, "requiredimmunizations");
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Has_1st_Level_Duplicates : ArrayUniquenessValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            // Create schema with nested array uniqueness constraints
            var apiSchema = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("RequiredImmunization")
                .WithStartDocumentPathsMapping()
                .WithEndDocumentPathsMapping()
                .WithArrayUniquenessConstraint(
                    [
                        new
                        {
                            paths = new[] { "$.requiredImmunizations[*].immunizationTypeDescriptor" },
                            nestedConstraints = new[]
                            {
                                new
                                {
                                    basePath = "$.requiredImmunizations[*]",
                                    paths = new[] { "$.dates[*].immunizationDate" },
                                },
                            },
                        },
                    ]
                )
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                 "requiredImmunizations": [
                    {
                        "dates": [
                            {
                                "immunizationDate": "2007-07-01"
                            }
                        ],
                        "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#IPV"
                    },
                    {
                        "dates": [
                            {
                                "immunizationDate": "2010-04-01"
                            }
                        ],
                        "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#IPV"
                    }
                  ]
                }
                """;

            _requestInfo = await CreateRequestInfoAndExecute(apiSchema, jsonBody, "requiredimmunizations");
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_validation_error_with_duplicate_items()
        {
            _requestInfo.FrontendResponse.Body!.ToJsonString().Should().Contain("Data Validation Failed");

            _requestInfo
                .FrontendResponse.Body!.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.requiredImmunizations":["The 2nd item of the requiredImmunizations has the same identifying values as another item earlier in the list."]}
                    """
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Has_2nd_Level_Duplicates : ArrayUniquenessValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            // Create schema with nested array uniqueness constraints
            var apiSchema = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("RequiredImmunization")
                .WithStartDocumentPathsMapping()
                .WithEndDocumentPathsMapping()
                .WithArrayUniquenessConstraint(
                    [
                        new
                        {
                            paths = new[] { "$.requiredImmunizations[*].immunizationTypeDescriptor" },
                            nestedConstraints = new[]
                            {
                                new
                                {
                                    basePath = "$.requiredImmunizations[*]",
                                    paths = new[] { "$.dates[*].immunizationDate" },
                                },
                            },
                        },
                    ]
                )
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                 "requiredImmunizations": [
                    {
                        "dates": [
                            {
                                "immunizationDate": "2007-07-01"
                            },
                            {
                                "immunizationDate": "2007-07-01"
                            }
                        ],
                        "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#MMR"
                    },
                    {
                        "dates": [
                            {
                                "immunizationDate": "2010-04-01"
                            }
                        ],
                        "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#IPV"
                    }
                  ]
                }
                """;

            _requestInfo = await CreateRequestInfoAndExecute(apiSchema, jsonBody, "requiredimmunizations");
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_validation_error_with_duplicate_items()
        {
            _requestInfo.FrontendResponse.Body!.ToJsonString().Should().Contain("Data Validation Failed");

            _requestInfo
                .FrontendResponse.Body!.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.requiredImmunizations[0].dates":["The 2nd item of the dates has the same identifying values as another item earlier in the list."]}
                    """
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Document_Has_Two_Levels_Of_Duplicates : ArrayUniquenessValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            // Create schema with nested array uniqueness constraints
            var apiSchema = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("RequiredImmunization")
                .WithStartDocumentPathsMapping()
                .WithEndDocumentPathsMapping()
                .WithArrayUniquenessConstraint(
                    [
                        new
                        {
                            paths = new[] { "$.requiredImmunizations[*].immunizationTypeDescriptor" },
                            nestedConstraints = new[]
                            {
                                new
                                {
                                    basePath = "$.requiredImmunizations[*]",
                                    paths = new[] { "$.dates[*].immunizationDate" },
                                },
                            },
                        },
                    ]
                )
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                 "requiredImmunizations": [
                    {
                        "dates": [
                            {
                                "immunizationDate": "2007-07-01"
                            },
                            {
                                "immunizationDate": "2007-07-01"
                            }
                        ],
                        "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#IPV"
                    },
                    {
                        "dates": [
                            {
                                "immunizationDate": "2010-04-01"
                            }
                        ],
                        "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#IPV"
                    }
                  ]
                }
                """;

            _requestInfo = await CreateRequestInfoAndExecute(apiSchema, jsonBody, "requiredimmunizations");
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_validation_error_with_duplicate_items()
        {
            _requestInfo.FrontendResponse.Body!.ToJsonString().Should().Contain("Data Validation Failed");

            var responseBody = _requestInfo.FrontendResponse.Body!.ToJsonString();

            responseBody
                .Should()
                .Contain("$.requiredImmunizations")
                .And.Contain(
                    "The 2nd item of the requiredImmunizations has the same identifying values as another item earlier in the list."
                );

            responseBody
                .Should()
                .Contain("$.requiredImmunizations[0].dates")
                .And.Contain(
                    "The 2nd item of the dates has the same identifying values as another item earlier in the list."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Document_Has_Two_Levels_Of_Duplicates_For_Multiple_Constraints
        : ArrayUniquenessValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            // Create schema with nested array uniqueness constraints
            var apiSchema = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("RequiredImmunization")
                .WithStartDocumentPathsMapping()
                .WithEndDocumentPathsMapping()
                .WithArrayUniquenessConstraint(
                    [
                        new
                        {
                            paths = new[] { "$.requiredImmunizations[*].immunizationTypeDescriptor" },
                            nestedConstraints = new[]
                            {
                                new
                                {
                                    basePath = "$.requiredImmunizations[*]",
                                    paths = new[] { "$.dates[*].immunizationDate" },
                                },
                            },
                        },
                        new
                        {
                            paths = new[] { "$.documentations[*].documentationTypeDescriptor" },
                            nestedConstraints = new[]
                            {
                                new
                                {
                                    basePath = "$.documentations[*]",
                                    paths = new[] { "$.dates[*].documentationDate" },
                                },
                            },
                        },
                    ]
                )
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                "requiredImmunizations": [
                    {
                    "dates": [
                        {
                        "immunizationDate": "2010-04-01"
                        }
                    ],
                    "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#IPV"
                    },
                    {
                    "dates": [
                        {
                        "immunizationDate": "2007-07-01"
                        },
                        {
                        "immunizationDate": "2007-07-01"
                        }
                    ],
                    "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#IPV"
                    }
                ],
                "documentations": [
                    {
                    "dates": [
                        {
                        "documentationDate": "2010-04-01"
                        }
                    ],
                    "documentationTypeDescriptor": "uri://ed-fi.org/documentationTypeDescriptor#Card"
                    },
                    {
                    "dates": [
                        {
                        "documentationDate": "2020-01-01"
                        },
                        {
                        "documentationDate": "2007-01-01"
                        },
                        {
                        "documentationDate": "2020-01-01"
                        }
                    ],
                    "documentationTypeDescriptor": "uri://ed-fi.org/documentationTypeDescriptor#Card"
                    }
                ]
                }
                """;

            _requestInfo = await CreateRequestInfoAndExecute(apiSchema, jsonBody, "requiredimmunizations");
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_validation_error_with_duplicate_items()
        {
            _requestInfo.FrontendResponse.Body!.ToJsonString().Should().Contain("Data Validation Failed");

            var responseBody = _requestInfo.FrontendResponse.Body!.ToJsonString();

            responseBody
                .Should()
                .Contain("$.requiredImmunizations")
                .And.Contain(
                    "The 2nd item of the requiredImmunizations has the same identifying values as another item earlier in the list."
                );

            responseBody
                .Should()
                .Contain("$.requiredImmunizations[1].dates")
                .And.Contain(
                    "The 2nd item of the dates has the same identifying values as another item earlier in the list."
                );

            responseBody
                .Should()
                .Contain("$.documentations")
                .And.Contain(
                    "The 2nd item of the documentations has the same identifying values as another item earlier in the list."
                );

            responseBody
                .Should()
                .Contain("$.documentations[1].dates")
                .And.Contain(
                    "The 3rd item of the dates has the same identifying values as another item earlier in the list."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Document_Has_Duplicate_Dates_But_In_Different_RequiredImmunizations
        : ArrayUniquenessValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            // Create schema with nested array uniqueness constraints
            var apiSchema = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("RequiredImmunization")
                .WithStartDocumentPathsMapping()
                .WithEndDocumentPathsMapping()
                .WithArrayUniquenessConstraint(
                    [
                        new
                        {
                            paths = new[] { "$.requiredImmunizations[*].immunizationTypeDescriptor" },
                            nestedConstraints = new[]
                            {
                                new
                                {
                                    basePath = "$.requiredImmunizations[*]",
                                    paths = new[] { "$.dates[*].immunizationDate" },
                                },
                            },
                        },
                    ]
                )
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                "requiredImmunizations": [
                    {
                        "dates": [
                            {
                                "immunizationDate": "2007-07-01"
                            }
                        ],
                        "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#MMR"
                    },
                    {
                        "dates": [
                            {
                                "immunizationDate": "2007-07-01"
                            }
                        ],
                        "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#IPV"
                    }
                  ]
                }
                """;

            _requestInfo = await CreateRequestInfoAndExecute(apiSchema, jsonBody, "requiredimmunizations");
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Addresses_Differing_Only_In_AddressType : ArrayUniquenessValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            // Create schema with address array uniqueness constraint on multiple fields
            var apiSchema = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("StudentEducationOrganizationAssociation")
                .WithStartDocumentPathsMapping()
                .WithEndDocumentPathsMapping()
                .WithArrayUniquenessConstraintSimple(
                    [
                        "$.addresses[*].addressTypeDescriptor",
                        "$.addresses[*].city",
                        "$.addresses[*].postalCode",
                        "$.addresses[*].streetNumberName",
                    ]
                )
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                    "educationOrganizationReference": {
                        "educationOrganizationId": 255901001
                    },
                    "studentReference": {
                        "studentUniqueId": "604824"
                    },
                    "addresses": [
                        {
                            "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                            "city": "Grand Bend",
                            "postalCode": "78834",
                            "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                            "streetNumberName": "980 Green New Boulevard",
                            "nameOfCounty": "WILLISTON",
                            "periods": []
                        },
                        {
                            "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Home",
                            "city": "Grand Bend",
                            "postalCode": "78834",
                            "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                            "streetNumberName": "980 Green New Boulevard",
                            "nameOfCounty": "WILLISTON",
                            "periods": []
                        }
                    ]
                }
                """;

            _requestInfo = await CreateRequestInfoAndExecute(
                apiSchema,
                jsonBody,
                "studenteducationorganizationassociations"
            );
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_Document_Has_TopLevel_Date_With_Same_Name_As_Array_Date
        : ArrayUniquenessValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            // Create schema with array uniqueness constraint on effectiveDate in events array
            var apiSchema = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Event")
                .WithStartDocumentPathsMapping()
                .WithEndDocumentPathsMapping()
                .WithArrayUniquenessConstraintSimple(["$.events[*].effectiveDate"])
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                    "eventTitle": "Annual Conference",
                    "effectiveDate": "2024-01-01",
                    "events": [
                        {
                            "eventName": "Opening Session",
                            "effectiveDate": "2024-01-01"
                        },
                        {
                            "eventName": "Closing Session",
                            "effectiveDate": "2024-01-02"
                        }
                    ]
                }
                """;

            _requestInfo = await CreateRequestInfoAndExecute(apiSchema, jsonBody, "events");
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_Document_Has_TopLevel_Date_With_Same_Name_As_Array_Date_Which_Has_Duplicates
        : ArrayUniquenessValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            // Create schema with array uniqueness constraint on effectiveDate in events array
            var apiSchema = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Event")
                .WithStartDocumentPathsMapping()
                .WithEndDocumentPathsMapping()
                .WithArrayUniquenessConstraintSimple(["$.events[*].effectiveDate"])
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                    "eventTitle": "Annual Conference",
                    "effectiveDate": "2024-01-01",
                    "events": [
                        {
                            "eventName": "Opening Session",
                            "effectiveDate": "2024-01-01"
                        },
                        {
                            "eventName": "Closing Session",
                            "effectiveDate": "2024-01-01"
                        }
                    ]
                }
                """;

            _requestInfo = await CreateRequestInfoAndExecute(apiSchema, jsonBody, "events");
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_validation_error_with_duplicate_dates()
        {
            _requestInfo.FrontendResponse.Body!.ToJsonString().Should().Contain("Data Validation Failed");

            _requestInfo
                .FrontendResponse.Body!.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.events":["The 2nd item of the events has the same identifying values as another item earlier in the list."]}
                    """
                );
        }
    }
}
