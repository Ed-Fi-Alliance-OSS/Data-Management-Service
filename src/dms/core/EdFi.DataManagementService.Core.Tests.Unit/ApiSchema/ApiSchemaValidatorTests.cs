// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.OpenApi;
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
                      "resources": { "components": { "schemas": {}, "parameters": { "limit": { "schema": {} }, "pageToken": { "name": "pageToken", "schema": {} }, "pageSize": { "name": "pageSize", "schema": {} }, "numberOfPartitions": { "schema": {} } } }, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] },
                      "descriptors": { "components": { "schemas": {}, "parameters": { "limit": { "schema": {} }, "pageToken": { "name": "pageToken", "schema": {} }, "pageSize": { "name": "pageSize", "schema": {} }, "numberOfPartitions": { "schema": {} } } }, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] }
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
    public class Given_A_ProjectSchema_With_ChangeQueries_OpenApi_Base_Document : ApiSchemaValidatorTests
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
                      "resources": { "components": { "schemas": {}, "parameters": { "limit": { "schema": {} }, "pageToken": { "name": "pageToken", "schema": {} }, "pageSize": { "name": "pageSize", "schema": {} }, "numberOfPartitions": { "schema": {} } } }, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] },
                      "descriptors": { "components": { "schemas": {}, "parameters": { "limit": { "schema": {} }, "pageToken": { "name": "pageToken", "schema": {} }, "pageSize": { "name": "pageSize", "schema": {} }, "numberOfPartitions": { "schema": {} } } }, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] },
                      "changeQueries": { "components": {}, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] }
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
        public void It_has_no_validation_errors()
        {
            var response = _validator!.Validate(_apiSchemaRootNode);
            response.Should().NotBeNull();
            response.Count.Should().Be(0);
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
                      "resources": { "components": { "schemas": {}, "parameters": { "limit": { "schema": {} }, "pageToken": { "name": "pageToken", "schema": {} }, "pageSize": { "name": "pageSize", "schema": {} }, "numberOfPartitions": { "schema": {} } } }, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] },
                      "descriptors": { "components": { "schemas": {}, "parameters": { "limit": { "schema": {} }, "pageToken": { "name": "pageToken", "schema": {} }, "pageSize": { "name": "pageSize", "schema": {} }, "numberOfPartitions": { "schema": {} } } }, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] }
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
                      "resources": { "components": { "schemas": {}, "parameters": { "limit": { "schema": {} }, "pageToken": { "name": "pageToken", "schema": {} }, "pageSize": { "name": "pageSize", "schema": {} }, "numberOfPartitions": { "schema": {} } } }, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] },
                      "descriptors": { "components": { "schemas": {}, "parameters": { "limit": { "schema": {} }, "pageToken": { "name": "pageToken", "schema": {} }, "pageSize": { "name": "pageSize", "schema": {} }, "numberOfPartitions": { "schema": {} } } }, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] }
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
                      "resources": { "components": { "schemas": {}, "parameters": { "limit": { "schema": {} }, "pageToken": { "name": "pageToken", "schema": {} }, "pageSize": { "name": "pageSize", "schema": {} }, "numberOfPartitions": { "schema": {} } } }, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] },
                      "descriptors": { "components": { "schemas": {}, "parameters": { "limit": { "schema": {} }, "pageToken": { "name": "pageToken", "schema": {} }, "pageSize": { "name": "pageSize", "schema": {} }, "numberOfPartitions": { "schema": {} } } }, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] }
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
                      "resources": { "components": { "schemas": {}, "parameters": { "limit": { "schema": {} }, "pageToken": { "name": "pageToken", "schema": {} }, "pageSize": { "name": "pageSize", "schema": {} }, "numberOfPartitions": { "schema": {} } } }, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] },
                      "descriptors": { "components": { "schemas": {}, "parameters": { "limit": { "schema": {} }, "pageToken": { "name": "pageToken", "schema": {} }, "pageSize": { "name": "pageSize", "schema": {} }, "numberOfPartitions": { "schema": {} } } }, "info": {}, "openapi": "3.0.0", "paths": {}, "servers": [], "tags": [] }
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

    /// <summary>
    /// Builds an ApiSchema whose resource and descriptor base documents declare the cursor-paging
    /// parameter components, optionally omitting one component or one component's schema so a single
    /// precondition can be tested at a time.
    /// </summary>
    private static JsonNode ApiSchemaWithCursorComponents(
        string? omittedComponent = null,
        string? componentWithoutSchema = null,
        string? componentWithoutName = null,
        string? componentWithBlankName = null,
        bool omitPaths = false,
        bool omitComponentSchemas = false
    )
    {
        JsonObject BuildBaseDocument()
        {
            JsonObject parameters = new();

            foreach (string componentName in CursorParameterComponentNames)
            {
                if (componentName == omittedComponent)
                {
                    continue;
                }

                JsonObject component = new() { ["in"] = "query" };

                if (componentName != componentWithoutName)
                {
                    component["name"] = componentName == componentWithBlankName ? "   " : componentName;
                }

                if (componentName != componentWithoutSchema)
                {
                    component["schema"] = new JsonObject { ["type"] = "string" };
                }

                parameters[componentName] = component;
            }

            JsonObject components = new() { ["parameters"] = parameters };

            if (!omitComponentSchemas)
            {
                components["schemas"] = new JsonObject();
            }

            JsonObject baseDocument = new()
            {
                ["openapi"] = "3.0.1",
                ["info"] = new JsonObject(),
                ["components"] = components,
            };

            if (!omitPaths)
            {
                baseDocument["paths"] = new JsonObject();
            }

            return baseDocument;
        }

        return new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = new JsonObject
            {
                ["abstractResources"] = new JsonObject(),
                ["caseInsensitiveEndpointNameMapping"] = new JsonObject(),
                ["description"] = "The Ed-Fi Data Standard v5.0",
                ["educationOrganizationHierarchy"] = new JsonObject(),
                ["educationOrganizationTypes"] = new JsonArray(),
                ["isExtensionProject"] = false,
                ["openApiBaseDocuments"] = new JsonObject
                {
                    ["resources"] = BuildBaseDocument(),
                    ["descriptors"] = BuildBaseDocument(),
                },
                ["projectName"] = "ed-fi",
                ["projectEndpointName"] = "ed-fi",
                ["projectVersion"] = "5.0.0",
                ["resourceNameMapping"] = new JsonObject(),
                ["resourceSchemas"] = new JsonObject(),
            },
        };
    }

    private static readonly string[] CursorParameterComponentNames =
    [
        "limit",
        "pageToken",
        "pageSize",
        "numberOfPartitions",
    ];

    /// <summary>
    /// OpenAPI assembly cannot publish the cursor-paging contract without these parameter components, and
    /// the assembled metadata documents are built lazily on first metadata request. Rejecting the omission
    /// here is what makes it a startup failure rather than a permanent metadata outage discovered later.
    /// </summary>
    [TestFixture("limit")]
    [TestFixture("pageToken")]
    [TestFixture("pageSize")]
    [TestFixture("numberOfPartitions")]
    [Parallelizable]
    public class Given_A_Base_Document_Omitting_A_Cursor_Parameter_Component(string _omittedComponent)
        : ApiSchemaValidatorTests
    {
        private List<SchemaValidationFailure> _failures = [];

        [SetUp]
        public void Arrange()
        {
            _failures = _validator!.Validate(ApiSchemaWithCursorComponents(_omittedComponent));
        }

        [Test]
        public void It_has_validation_errors()
        {
            _failures.Should().NotBeEmpty();
        }

        [Test]
        public void It_names_the_resource_parameters_location()
        {
            _failures
                .Select(failure => failure.FailurePath.Value)
                .Should()
                .Contain("$.projectSchema.openApiBaseDocuments.resources.components.parameters");
        }

        [Test]
        public void It_names_the_descriptor_parameters_location()
        {
            _failures
                .Select(failure => failure.FailurePath.Value)
                .Should()
                .Contain("$.projectSchema.openApiBaseDocuments.descriptors.components.parameters");
        }

        [Test]
        public void It_names_the_omitted_component()
        {
            _failures
                .SelectMany(failure => failure.FailureMessages)
                .Should()
                .Contain(message => message.Contains(_omittedComponent));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Base_Document_Whose_Cursor_Component_Has_No_Schema : ApiSchemaValidatorTests
    {
        private List<SchemaValidationFailure> _failures = [];

        [SetUp]
        public void Arrange()
        {
            _failures = _validator!.Validate(
                ApiSchemaWithCursorComponents(componentWithoutSchema: "pageSize")
            );
        }

        [Test]
        public void It_has_validation_errors()
        {
            _failures.Should().NotBeEmpty();
        }

        [Test]
        public void It_names_the_component_missing_its_schema()
        {
            _failures
                .Select(failure => failure.FailurePath.Value)
                .Should()
                .Contain("$.projectSchema.openApiBaseDocuments.resources.components.parameters.pageSize");
        }

        [Test]
        public void It_reports_the_missing_schema()
        {
            _failures
                .SelectMany(failure => failure.FailureMessages)
                .Should()
                .Contain(message => message.Contains("schema"));
        }
    }

    /// <summary>
    /// Cursor assembly reads the merged document's paths before it considers whether any collection is
    /// eligible, so a core base document without them fails assembly on first metadata access. It also
    /// silently discards every fragment's paths, because the fragment merge is guarded on the target
    /// already having a paths object.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Core_Base_Document_Without_Paths : ApiSchemaValidatorTests
    {
        private List<SchemaValidationFailure> _failures = [];

        [SetUp]
        public void Arrange()
        {
            _failures = _validator!.Validate(ApiSchemaWithCursorComponents(omitPaths: true));
        }

        [Test]
        public void It_has_validation_errors()
        {
            _failures.Should().NotBeEmpty();
        }

        [Test]
        public void It_names_both_base_documents()
        {
            _failures
                .Select(failure => failure.FailurePath.Value)
                .Should()
                .Contain("$.projectSchema.openApiBaseDocuments.resources")
                .And.Contain("$.projectSchema.openApiBaseDocuments.descriptors");
        }

        [Test]
        public void It_reports_the_missing_paths()
        {
            _failures
                .SelectMany(failure => failure.FailureMessages)
                .Should()
                .Contain(message => message.Contains("paths"));
        }
    }

    /// <summary>
    /// Cursor assembly reads the merged component schemas unconditionally. A fragment merge creates that
    /// section when some merged fragment carries one, so whether an accepted document assembles would
    /// otherwise depend on unrelated content rather than on the document itself.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Core_Base_Document_Without_Component_Schemas : ApiSchemaValidatorTests
    {
        private List<SchemaValidationFailure> _failures = [];

        [SetUp]
        public void Arrange()
        {
            _failures = _validator!.Validate(ApiSchemaWithCursorComponents(omitComponentSchemas: true));
        }

        [Test]
        public void It_has_validation_errors()
        {
            _failures.Should().NotBeEmpty();
        }

        [Test]
        public void It_names_both_component_sections()
        {
            _failures
                .Select(failure => failure.FailurePath.Value)
                .Should()
                .Contain("$.projectSchema.openApiBaseDocuments.resources.components")
                .And.Contain("$.projectSchema.openApiBaseDocuments.descriptors.components");
        }

        [Test]
        public void It_reports_the_missing_schemas()
        {
            _failures
                .SelectMany(failure => failure.FailureMessages)
                .Should()
                .Contain(message => message.Contains("schemas"));
        }
    }

    /// <summary>
    /// The published query name of pageToken and pageSize is read from the component rather than from a
    /// second copy of the spelling, so assembly cannot proceed without it.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Core_Base_Document_Whose_PageToken_Has_No_Name : ApiSchemaValidatorTests
    {
        private List<SchemaValidationFailure> _failures = [];

        [SetUp]
        public void Arrange()
        {
            _failures = _validator!.Validate(
                ApiSchemaWithCursorComponents(componentWithoutName: "pageToken")
            );
        }

        [Test]
        public void It_has_validation_errors()
        {
            _failures.Should().NotBeEmpty();
        }

        [Test]
        public void It_names_the_component_missing_its_name()
        {
            _failures
                .Select(failure => failure.FailurePath.Value)
                .Should()
                .Contain("$.projectSchema.openApiBaseDocuments.resources.components.parameters.pageToken");
        }

        [Test]
        public void It_reports_the_missing_name()
        {
            _failures
                .SelectMany(failure => failure.FailureMessages)
                .Should()
                .Contain(message => message.Contains("name"));
        }
    }

    /// <summary>
    /// A whitespace-only name is rejected by the production check, so a length bound alone would leave
    /// this shape accepted by validation and failing at assembly.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Core_Base_Document_Whose_PageSize_Name_Is_Blank : ApiSchemaValidatorTests
    {
        private List<SchemaValidationFailure> _failures = [];

        [SetUp]
        public void Arrange()
        {
            _failures = _validator!.Validate(
                ApiSchemaWithCursorComponents(componentWithBlankName: "pageSize")
            );
        }

        [Test]
        public void It_has_validation_errors()
        {
            _failures.Should().NotBeEmpty();
        }

        [Test]
        public void It_names_the_blank_name()
        {
            _failures
                .Select(failure => failure.FailurePath.Value)
                .Should()
                .Contain(
                    "$.projectSchema.openApiBaseDocuments.resources.components.parameters.pageSize.name"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Base_Document_Declaring_Every_Cursor_Parameter_Component : ApiSchemaValidatorTests
    {
        [Test]
        public void It_has_no_validation_errors()
        {
            _validator!.Validate(ApiSchemaWithCursorComponents()).Count.Should().Be(0);
        }
    }

    /// <summary>
    /// The pairing that keeps validation and assembly from disagreeing: the smallest core ApiSchema this
    /// validator accepts must also assemble. Assembly's unconditional prologue runs before any collection
    /// is considered, so this minimum — no resources, no paths, no schemas to fall back on — is the shape
    /// that exposes any requirement the schema fails to state.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Minimum_Accepted_Core_ApiSchema : ApiSchemaValidatorTests
    {
        private readonly JsonNode _apiSchemaRootNode = ApiSchemaWithCursorComponents();

        [Test]
        public void It_is_accepted_by_validation()
        {
            _validator!.Validate(_apiSchemaRootNode).Count.Should().Be(0);
        }

        [Test]
        public void It_assembles_the_resource_document()
        {
            Action assemble = () => CreateDocument(OpenApiDocument.OpenApiDocumentType.Resource);

            assemble.Should().NotThrow();
        }

        [Test]
        public void It_assembles_the_descriptor_document()
        {
            Action assemble = () => CreateDocument(OpenApiDocument.OpenApiDocumentType.Descriptor);

            assemble.Should().NotThrow();
        }

        /// <summary>
        /// Assembly mutates the nodes it is given, so each pass reads its own copy of the input.
        /// </summary>
        private void CreateDocument(OpenApiDocument.OpenApiDocumentType documentType)
        {
            OpenApiDocument openApiDocument = new(
                NullLogger.Instance,
                pagingSettings: OpenApiPagingSettings.Default
            );

            openApiDocument.CreateDocument(
                new ApiSchemaDocumentNodes(_apiSchemaRootNode.DeepClone(), []),
                documentType
            );
        }
    }

    /// <summary>
    /// Only the core project's resource and descriptor base documents are assembled; an extension reaches
    /// assembly through its fragments, and its own base documents are never read. Holding an extension to
    /// the cursor-paging requirements would refuse startup over content nothing consumes.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_Project_Whose_Base_Documents_Omit_Components : ApiSchemaValidatorTests
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
                      "resources": { "openapi": "3.0.0", "paths": {} },
                      "descriptors": { "openapi": "3.0.0", "paths": {} }
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

    /// <summary>
    /// Excluding the condition selector's own result must not turn an extension into an unvalidated
    /// document: every assertion outside that selector still applies to it.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_Project_With_An_Invalid_Identity_Json_Path : ApiSchemaValidatorTests
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
                    "description": "Sample Extension",
                    "educationOrganizationHierarchy": {},
                    "educationOrganizationTypes": [],
                    "isExtensionProject": true,
                    "openApiBaseDocuments": {
                      "resources": { "openapi": "3.0.0", "paths": {} },
                      "descriptors": { "openapi": "3.0.0", "paths": {} }
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
        public void It_has_validation_errors()
        {
            var response = _validator!.Validate(_apiSchemaRootNode);
            response.Count.Should().Be(1);
            response[0].FailurePath.Value.Should().Contain("educationOrg.identityJsonPaths");
        }
    }

    /// <summary>
    /// The optional Change-Queries document is outside the cursor-paging contract, so it keeps the
    /// generic base-document shape and is never required to declare cursor parameter components.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_ChangeQueries_Document_Without_Cursor_Parameter_Components : ApiSchemaValidatorTests
    {
        [Test]
        public void It_has_no_validation_errors()
        {
            JsonNode apiSchemaRootNode = ApiSchemaWithCursorComponents();
            apiSchemaRootNode["projectSchema"]!["openApiBaseDocuments"]!["changeQueries"] = new JsonObject
            {
                ["openapi"] = "3.0.1",
                ["info"] = new JsonObject(),
                ["paths"] = new JsonObject(),
                ["components"] = new JsonObject(),
            };

            _validator!.Validate(apiSchemaRootNode).Count.Should().Be(0);
        }
    }
}
