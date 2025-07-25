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

public class ApiSchemaValidatorTests
{
    private ApiSchemaValidator? _validator;

    [SetUp]
    public void Setup()
    {
        _validator = new ApiSchemaValidator(NullLogger<ApiSchemaValidator>.Instance);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Empty_Schema : ApiSchemaValidatorTests
    {
        [Test]
        public void It_has_validation_errors()
        {
            var response = _validator!.Validate(new JsonObject());
            response.Should().NotBeNull();
            response.Count.Should().Be(1);
            response[0].Should().NotBeNull();

            response[0].FailureMessages.Count.Should().Be(1);
            response[0].FailureMessages[0].Should().Contain("Required properties");
            response[0].FailureMessages[0].Should().Contain("apiSchemaVersion");
            response[0].FailureMessages[0].Should().Contain("projectSchema");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_ProjectSchema_With_Missing_Required_Properties : ApiSchemaValidatorTests
    {
        private readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                """
                {
                  "apiSchemaVersion": "1.0.0",
                  "projectSchema": {
                    "description": "The Ed-Fi Data Standard v5.0",
                    "isExtensionProject": false,
                    "projectName": "ed-fi",
                    "projectEndpointName": "ed-fi",
                    "projectVersion": "5.0.0",
                    "resourceNameMapping": {},
                    "resourceSchemas": {}
                  }
                }
                """
            ) ?? new JsonObject();

        [Test]
        public void It_has_validation_errors()
        {
            var response = _validator!.Validate(_apiSchemaRootNode);
            response.Should().NotBeNull();
            response.Count.Should().Be(1);
            response[0].Should().NotBeNull();

            response[0].FailureMessages.Count.Should().Be(1);
            response[0].FailureMessages[0].Should().Contain("Required properties");
            response[0].FailureMessages[0].Should().Contain("abstractResources");
            response[0].FailureMessages[0].Should().Contain("caseInsensitiveEndpointNameMapping");
            response[0].FailureMessages[0].Should().Contain("educationOrganizationHierarchy");
            response[0].FailureMessages[0].Should().Contain("educationOrganizationTypes");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_ProjectSchema_With_Missing_OpenApi_Core_Properties : ApiSchemaValidatorTests
    {
        private readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                """
                {
                  "apiSchemaVersion": "1.0.0",
                  "projectSchema": {
                    "caseInsensitiveEndpointNameMapping": {},
                    "abstractResources": {},
                    "description": "The Ed-Fi Data Standard v5.0",
                    "educationOrganizationHierarchy": {},
                    "educationOrganizationTypes": [],
                    "isExtensionProject": false,
                    "projectName": "ed-fi",
                    "projectEndpointName": "ed-fi",
                    "projectVersion": "5.0.0",
                    "resourceNameMapping": {},
                    "resourceSchemas": {}
                  }
                }
                """
            ) ?? new JsonObject();

        [Test]
        public void It_has_no_validation_errors()
        {
            var response = _validator!.Validate(_apiSchemaRootNode);
            response.Should().NotBeNull();
            response.Count.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Invalid_Identity_Json_Path_On_AbstractResource : ApiSchemaValidatorTests
    {
        private readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                """
                {
                  "apiSchemaVersion": "1.0.0",
                  "projectSchema": {
                    "caseInsensitiveEndpointNameMapping": {},
                    "abstractResources": {
                      "educationOrg": {
                        "identityJsonPaths": [
                          "educationOrganizationId"
                        ],
                        "openApiFragment": {}
                      }
                    },
                    "description": "The Ed-Fi Data Standard v5.0",
                    "educationOrganizationHierarchy": {},
                    "educationOrganizationTypes": [],
                    "isExtensionProject": false,
                    "openApiBaseDocuments": {
                      "resources": { "components": {}, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] },
                      "descriptors": { "components": {}, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] }
                    },
                    "projectName": "ed-fi",
                    "projectEndpointName": "ed-fi",
                    "projectVersion": "5.0.0",
                    "resourceNameMapping": {},
                    "resourceSchemas": {}
                  }
                }
                """
            ) ?? new JsonObject();

        [Test]
        public void It_has_validation_errors()
        {
            var response = _validator!.Validate(_apiSchemaRootNode);
            response.Should().NotBeNull();
            response.Count.Should().Be(1);
            response[0].Should().NotBeNull();

            response[0].FailureMessages.Count.Should().Be(1);
            response[0].FailurePath.Value.Should().Contain("educationOrg.identityJsonPaths");
            response[0]
                .FailureMessages[0]
                .Should()
                .Contain("The string value is not a match for the indicated regular expression");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_ResourceSchema_With_Missing_Required_Properties : ApiSchemaValidatorTests
    {
        private readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                """
                {
                  "apiSchemaVersion": "1.0.0",
                  "projectSchema": {
                    "caseInsensitiveEndpointNameMapping": {},
                    "abstractResources": {},
                    "description": "The Ed-Fi Data Standard v5.0",
                    "educationOrganizationHierarchy": {},
                    "educationOrganizationTypes": [],
                    "isExtensionProject": false,
                    "openApiBaseDocuments": {
                      "resources": { "components": {}, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] },
                      "descriptors": { "components": {}, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] }
                    },
                    "projectName": "ed-fi",
                    "projectEndpointName": "ed-fi",
                    "projectVersion": "5.0.0",
                    "resourceNameMapping": {},
                    "resourceSchemas": {
                      "Students": {
                        "allowIdentityUpdates": false,
                        "documentPathsMapping": {},
                        "identityJsonPaths": [],
                        "isDescriptor": false,
                        "jsonSchemaForInsert": {},
                        "resourceName": "Student"
                      }
                    }
                  }
                }
                """
            ) ?? new JsonObject();

        [Test]
        public void It_has_validation_errors()
        {
            var response = _validator!.Validate(_apiSchemaRootNode);
            response.Should().NotBeNull();
            response.Count.Should().Be(1);
            response[0].Should().NotBeNull();

            response[0].FailureMessages.Count.Should().Be(1);
            response[0].FailurePath.Value.Should().Contain("resourceSchemas.Students");
            response[0].FailureMessages[0].Should().Contain("Required properties");
            response[0].FailureMessages[0].Should().Contain("isSchoolYearEnumeration");
            response[0].FailureMessages[0].Should().Contain("equalityConstraints");
            response[0].FailureMessages[0].Should().Contain("isSubclass");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_ResourceSchema_With_Invalid_DocumentPathsMapping : ApiSchemaValidatorTests
    {
        private readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                """
                {
                  "apiSchemaVersion": "1.0.0",
                  "projectSchema": {
                    "caseInsensitiveEndpointNameMapping": {},
                    "abstractResources": {},
                    "description": "The Ed-Fi Data Standard v5.0",
                    "educationOrganizationHierarchy": {},
                    "educationOrganizationTypes": [],
                    "isExtensionProject": false,
                    "openApiBaseDocuments": {
                      "resources": { "components": {}, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] },
                      "descriptors": { "components": {}, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] }
                    },
                    "projectName": "ed-fi",
                    "projectEndpointName": "ed-fi",
                    "projectVersion": "5.0.0",
                    "resourceNameMapping": {},
                    "resourceSchemas": {
                      "Students": {
                        "allowIdentityUpdates": false,
                        "documentPathsMapping": {
                          "begindate": {}
                        },
                        "identityJsonPaths": [],
                        "isSchoolYearEnumeration": false,
                        "isSubclass": false,
                        "equalityConstraints": [],
                        "isDescriptor": false,
                        "jsonSchemaForInsert": {},
                        "resourceName": "Student"
                      }
                    }
                  }
                }
                """
            ) ?? new JsonObject();

        [Test]
        public void It_has_validation_errors()
        {
            var response = _validator!.Validate(_apiSchemaRootNode);
            response.Should().NotBeNull();
            response.Count.Should().Be(1);
            response[0].Should().NotBeNull();

            response[0].FailureMessages.Count.Should().Be(1);
            response[0]
                .FailurePath.Value.Should()
                .Contain("resourceSchemas.Students.documentPathsMapping.begindate");
            response[0].FailureMessages[0].Should().Contain("Required properties");
            response[0].FailureMessages[0].Should().Contain("isReference");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_ResourceSchema_With_Missing_New_Properties : ApiSchemaValidatorTests
    {
        private readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                """
                {
                  "apiSchemaVersion": "1.0.0",
                  "projectSchema": {
                    "caseInsensitiveEndpointNameMapping": {},
                    "abstractResources": {},
                    "description": "The Ed-Fi Data Standard v5.0",
                    "educationOrganizationHierarchy": {},
                    "educationOrganizationTypes": [],
                    "isExtensionProject": false,
                    "openApiBaseDocuments": {
                      "resources": { "components": {}, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] },
                      "descriptors": { "components": {}, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] }
                    },
                    "projectName": "ed-fi",
                    "projectEndpointName": "ed-fi",
                    "projectVersion": "5.0.0",
                    "resourceNameMapping": {},
                    "resourceSchemas": {
                      "Students": {
                        "allowIdentityUpdates": false,
                        "documentPathsMapping": {},
                        "identityJsonPaths": [],
                        "isDescriptor": false,
                        "isSchoolYearEnumeration": false,
                        "isSubclass": false,
                        "equalityConstraints": [],
                        "jsonSchemaForInsert": {},
                        "resourceName": "Student",
                        "invalidProperty": "should not be allowed"
                      }
                    }
                  }
                }
                """
            ) ?? new JsonObject();

        [Test]
        public void It_has_validation_errors()
        {
            var response = _validator!.Validate(_apiSchemaRootNode);
            response.Should().NotBeNull();
            response.Count.Should().Be(1);
            response[0].Should().NotBeNull();

            response[0].FailureMessages.Count.Should().Be(1);
            response[0].FailurePath.Value.Should().Contain("resourceSchemas.Students.invalidProperty");
            response[0].FailureMessages[0].Should().Contain("All values fail against the false schema");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Api_Schema : ApiSchemaValidatorTests
    {
        private readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                """
                {
                  "apiSchemaVersion": "1.0.0",
                  "projectSchema": {
                    "caseInsensitiveEndpointNameMapping": {},
                    "abstractResources": {},
                    "description": "The Ed-Fi Data Standard v5.0",
                    "educationOrganizationHierarchy": {},
                    "educationOrganizationTypes": [],
                    "isExtensionProject": false,
                    "openApiBaseDocuments": {
                      "resources": { "components": {}, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] },
                      "descriptors": { "components": {}, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] }
                    },
                    "projectName": "ed-fi",
                    "projectEndpointName": "ed-fi",
                    "projectVersion": "5.0.0",
                    "resourceNameMapping": {},
                    "resourceSchemas": {
                      "Students": {
                        "allowIdentityUpdates": false,
                        "arrayUniquenessConstraints": [],
                        "authorizationPathways": [],
                        "booleanJsonPaths": [],
                        "dateJsonPaths": [],
                        "dateTimeJsonPaths": [],
                        "decimalPropertyValidationInfos": [],
                        "documentPathsMapping": {
                          "begindate": {
                            "isReference": false
                          }
                        },
                        "identityJsonPaths": [],
                        "isSchoolYearEnumeration": false,
                        "isSubclass": false,
                        "isDescriptor": false,
                        "isResourceExtension": false,
                        "equalityConstraints": [],
                        "jsonSchemaForInsert": {},
                        "numericJsonPaths": [],
                        "queryFieldMapping": {},
                        "resourceName": "Student",
                        "securableElements": {
                          "Namespace": [],
                          "EducationOrganization": [],
                          "Student": [],
                          "Contact": [],
                          "Staff": []
                        }
                      }
                    }
                  }
                }
                """
            ) ?? new JsonObject();

        [Test]
        public void It_has_no_validation_errors()
        {
            _validator!.Validate(_apiSchemaRootNode).Count.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Extension_Project_Schema : ApiSchemaValidatorTests
    {
        private readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                """
                {
                  "apiSchemaVersion": "1.0.0",
                  "projectSchema": {
                    "caseInsensitiveEndpointNameMapping": {},
                    "abstractResources": {},
                    "description": "Sample Extension",
                    "educationOrganizationHierarchy": {},
                    "educationOrganizationTypes": [],
                    "isExtensionProject": true,
                    "openApiBaseDocuments": {
                      "resources": { "components": {}, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] },
                      "descriptors": { "components": {}, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] }
                    },
                    "projectName": "sample-extension",
                    "projectEndpointName": "sample-extension",
                    "projectVersion": "1.0.0",
                    "resourceNameMapping": {},
                    "resourceSchemas": {}
                  }
                }
                """
            ) ?? new JsonObject();

        [Test]
        public void It_has_no_validation_errors()
        {
            _validator!.Validate(_apiSchemaRootNode).Count.Should().Be(0);
        }
    }
}
