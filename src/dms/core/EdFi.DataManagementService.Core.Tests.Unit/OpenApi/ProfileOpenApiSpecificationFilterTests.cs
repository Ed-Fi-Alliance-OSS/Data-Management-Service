// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.OpenApi;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

[TestFixture]
public class ProfileOpenApiSpecificationFilterTests
{
    private static readonly string _baseOpenApiSpec = """
        {
          "openapi": "3.0.0",
          "info": {
            "title": "Ed-Fi Resources API",
            "version": "1.0.0"
          },
          "servers": [],
          "tags": [
            { "name": "students", "description": "Student resources" },
            { "name": "schools", "description": "School resources" },
            { "name": "staff", "description": "Staff resources" }
          ],
          "paths": {
            "/ed-fi/students": {
              "get": {
                "tags": ["students"],
                "responses": {
                  "200": {
                    "content": {
                      "application/json": {
                        "schema": { "$ref": "#/components/schemas/EdFi_Student" }
                      }
                    }
                  }
                }
              },
              "post": {
                "tags": ["students"],
                "requestBody": {
                  "content": {
                    "application/json": {
                      "schema": { "$ref": "#/components/schemas/EdFi_Student" }
                    }
                  }
                },
                "responses": {
                  "201": { "description": "Created" }
                }
              }
            },
            "/ed-fi/students/{id}": {
              "get": {
                "tags": ["students"],
                "responses": {
                  "200": {
                    "content": {
                      "application/json": {
                        "schema": { "$ref": "#/components/schemas/EdFi_Student" }
                      }
                    }
                  }
                }
              },
              "put": {
                "tags": ["students"],
                "requestBody": {
                  "content": {
                    "application/json": {
                      "schema": { "$ref": "#/components/schemas/EdFi_Student" }
                    }
                  }
                },
                "responses": {
                  "204": { "description": "Updated" }
                }
              },
              "delete": {
                "tags": ["students"],
                "responses": {
                  "204": { "description": "Deleted" }
                }
              }
            },
            "/ed-fi/schools": {
              "get": {
                "tags": ["schools"],
                "responses": {
                  "200": {
                    "content": {
                      "application/json": {
                        "schema": { "$ref": "#/components/schemas/EdFi_School" }
                      }
                    }
                  }
                }
              },
              "post": {
                "tags": ["schools"],
                "requestBody": {
                  "content": {
                    "application/json": {
                      "schema": { "$ref": "#/components/schemas/EdFi_School" }
                    }
                  }
                },
                "responses": {
                  "201": { "description": "Created" }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "EdFi_Student": {
                "type": "object",
                "required": ["studentUniqueId", "firstName", "lastName", "birthDate"],
                "properties": {
                  "id": { "type": "string" },
                  "studentUniqueId": { "type": "string" },
                  "firstName": { "type": "string" },
                  "lastName": { "type": "string" },
                  "birthDate": { "type": "string", "format": "date" },
                  "middleName": { "type": "string" },
                  "addresses": { "type": "array" },
                  "_etag": { "type": "string" },
                  "_lastModifiedDate": { "type": "string", "format": "date-time" },
                  "link": { "type": "object" }
                }
              },
              "EdFi_School": {
                "type": "object",
                "required": ["schoolId", "nameOfInstitution"],
                "properties": {
                  "id": { "type": "string" },
                  "schoolId": { "type": "integer" },
                  "nameOfInstitution": { "type": "string" },
                  "webSite": { "type": "string" },
                  "_etag": { "type": "string" },
                  "_lastModifiedDate": { "type": "string", "format": "date-time" }
                }
              }
            }
          }
        }
        """;

    private static ProfileOpenApiSpecificationFilter CreateFilter()
    {
        return new ProfileOpenApiSpecificationFilter(NullLogger.Instance);
    }

    private static JsonNode GetBaseSpec()
    {
        return JsonNode.Parse(_baseOpenApiSpec)!;
    }

    [TestFixture]
    public class Given_Profile_Covers_Single_Resource : ProfileOpenApiSpecificationFilterTests
    {
        private static ProfileDefinition CreateStudentProfile()
        {
            return new ProfileDefinition(
                "StudentProfile",
                [
                    new ResourceProfile(
                        "Student",
                        null,
                        new ContentTypeDefinition(
                            MemberSelection.IncludeOnly,
                            [new PropertyRule("firstName"), new PropertyRule("lastName")],
                            [],
                            [],
                            []
                        ),
                        new ContentTypeDefinition(
                            MemberSelection.IncludeOnly,
                            [new PropertyRule("firstName"), new PropertyRule("lastName")],
                            [],
                            [],
                            []
                        )
                    ),
                ]
            );
        }

        [Test]
        public void It_updates_info_title_with_profile_name()
        {
            var filter = CreateFilter();
            var profile = CreateStudentProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            result["info"]!["title"]!.GetValue<string>().Should().Be("StudentProfile Resources");
        }

        [Test]
        public void It_removes_paths_for_resources_not_in_profile()
        {
            var filter = CreateFilter();
            var profile = CreateStudentProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var paths = result["paths"] as JsonObject;
            paths.Should().NotBeNull();
            paths!.ContainsKey("/ed-fi/students").Should().BeTrue();
            paths.ContainsKey("/ed-fi/students/{id}").Should().BeTrue();
            paths.ContainsKey("/ed-fi/schools").Should().BeFalse();
        }

        [Test]
        public void It_removes_unused_tags()
        {
            var filter = CreateFilter();
            var profile = CreateStudentProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var tags = result["tags"] as JsonArray;
            tags.Should().NotBeNull();

            var tagNames = tags!.Select(t => t!["name"]!.GetValue<string>()).ToList();
            tagNames.Should().Contain("students");
            tagNames.Should().NotContain("schools");
            tagNames.Should().NotContain("staff");
        }

        [Test]
        public void It_updates_GET_response_content_type_to_readable()
        {
            var filter = CreateFilter();
            var profile = CreateStudentProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var getOperation = result["paths"]!["/ed-fi/students"]!["get"];
            var responseContent = getOperation!["responses"]!["200"]!["content"] as JsonObject;

            responseContent.Should().NotBeNull();
            responseContent!.ContainsKey("application/json").Should().BeFalse();
            responseContent
                .ContainsKey("application/vnd.ed-fi.student.studentprofile.readable+json")
                .Should()
                .BeTrue();
        }

        [Test]
        public void It_updates_POST_request_content_type_to_writable()
        {
            var filter = CreateFilter();
            var profile = CreateStudentProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var postOperation = result["paths"]!["/ed-fi/students"]!["post"];
            var requestContent = postOperation!["requestBody"]!["content"] as JsonObject;

            requestContent.Should().NotBeNull();
            requestContent!.ContainsKey("application/json").Should().BeFalse();
            requestContent
                .ContainsKey("application/vnd.ed-fi.student.studentprofile.writable+json")
                .Should()
                .BeTrue();
        }

        [Test]
        public void It_updates_PUT_request_content_type_to_writable()
        {
            var filter = CreateFilter();
            var profile = CreateStudentProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var putOperation = result["paths"]!["/ed-fi/students/{id}"]!["put"];
            var requestContent = putOperation!["requestBody"]!["content"] as JsonObject;

            requestContent.Should().NotBeNull();
            requestContent!.ContainsKey("application/json").Should().BeFalse();
            requestContent
                .ContainsKey("application/vnd.ed-fi.student.studentprofile.writable+json")
                .Should()
                .BeTrue();
        }

        [Test]
        public void It_preserves_DELETE_operations()
        {
            var filter = CreateFilter();
            var profile = CreateStudentProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var deleteOperation = result["paths"]!["/ed-fi/students/{id}"]!["delete"];
            deleteOperation.Should().NotBeNull();
        }
    }

    [TestFixture]
    public class Given_Profile_With_ReadOnly_Resource : ProfileOpenApiSpecificationFilterTests
    {
        private static ProfileDefinition CreateReadOnlyStudentProfile()
        {
            return new ProfileDefinition(
                "ReadOnlyStudentProfile",
                [
                    new ResourceProfile(
                        "Student",
                        null,
                        new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], []),
                        null // No write content type
                    ),
                ]
            );
        }

        [Test]
        public void It_removes_POST_operation()
        {
            var filter = CreateFilter();
            var profile = CreateReadOnlyStudentProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var studentsPath = result["paths"]!["/ed-fi/students"] as JsonObject;
            studentsPath.Should().NotBeNull();
            studentsPath!.ContainsKey("post").Should().BeFalse();
            studentsPath.ContainsKey("get").Should().BeTrue();
        }

        [Test]
        public void It_removes_PUT_operation()
        {
            var filter = CreateFilter();
            var profile = CreateReadOnlyStudentProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var studentByIdPath = result["paths"]!["/ed-fi/students/{id}"] as JsonObject;
            studentByIdPath.Should().NotBeNull();
            studentByIdPath!.ContainsKey("put").Should().BeFalse();
            studentByIdPath.ContainsKey("get").Should().BeTrue();
        }

        [Test]
        public void It_removes_DELETE_operation()
        {
            var filter = CreateFilter();
            var profile = CreateReadOnlyStudentProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var studentByIdPath = result["paths"]!["/ed-fi/students/{id}"] as JsonObject;
            studentByIdPath.Should().NotBeNull();
            studentByIdPath!.ContainsKey("delete").Should().BeFalse();
            studentByIdPath.ContainsKey("get").Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_Profile_With_WriteOnly_Resource : ProfileOpenApiSpecificationFilterTests
    {
        private static ProfileDefinition CreateWriteOnlyStudentProfile()
        {
            return new ProfileDefinition(
                "WriteOnlyStudentProfile",
                [
                    new ResourceProfile(
                        "Student",
                        null,
                        null, // No read content type
                        new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], [])
                    ),
                ]
            );
        }

        [Test]
        public void It_removes_GET_operations()
        {
            var filter = CreateFilter();
            var profile = CreateWriteOnlyStudentProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var studentsPath = result["paths"]!["/ed-fi/students"] as JsonObject;
            studentsPath.Should().NotBeNull();
            studentsPath!.ContainsKey("get").Should().BeFalse();
            studentsPath.ContainsKey("post").Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_Profile_With_IncludeOnly_Properties : ProfileOpenApiSpecificationFilterTests
    {
        private static ProfileDefinition CreateIncludeOnlyProfile()
        {
            return new ProfileDefinition(
                "NameOnlyProfile",
                [
                    new ResourceProfile(
                        "Student",
                        null,
                        new ContentTypeDefinition(
                            MemberSelection.IncludeOnly,
                            [new PropertyRule("firstName"), new PropertyRule("lastName")],
                            [],
                            [],
                            []
                        ),
                        null
                    ),
                ]
            );
        }

        [Test]
        public void It_removes_properties_not_in_include_list()
        {
            var filter = CreateFilter();
            var profile = CreateIncludeOnlyProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            // Check _readable schema (since profile only has ReadContentType)
            var studentSchema =
                result["components"]!["schemas"]!["EdFi_Student_readable"]!["properties"] as JsonObject;
            studentSchema.Should().NotBeNull();

            // firstName and lastName should be kept
            studentSchema!.ContainsKey("firstName").Should().BeTrue();
            studentSchema.ContainsKey("lastName").Should().BeTrue();

            // Identifying properties should be preserved
            studentSchema.ContainsKey("id").Should().BeTrue();
            studentSchema.ContainsKey("studentUniqueId").Should().BeTrue();

            // birthDate and middleName should be removed (not in include list)
            studentSchema.ContainsKey("birthDate").Should().BeFalse();
            studentSchema.ContainsKey("middleName").Should().BeFalse();
        }

        [Test]
        public void It_updates_required_array_to_remove_filtered_properties()
        {
            var filter = CreateFilter();
            var profile = CreateIncludeOnlyProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            // Check _readable schema (since profile only has ReadContentType)
            var studentSchema = result["components"]!["schemas"]!["EdFi_Student_readable"];
            var required = studentSchema!["required"] as JsonArray;
            required.Should().NotBeNull();

            var requiredProps = required!.Select(r => r!.GetValue<string>()).ToList();
            requiredProps.Should().Contain("firstName");
            requiredProps.Should().Contain("lastName");
            requiredProps.Should().Contain("studentUniqueId"); // Identifying - kept
            requiredProps.Should().NotContain("birthDate"); // Removed
        }
    }

    [TestFixture]
    public class Given_Profile_With_ExcludeOnly_Properties : ProfileOpenApiSpecificationFilterTests
    {
        private static ProfileDefinition CreateExcludeOnlyProfile()
        {
            return new ProfileDefinition(
                "ExcludeBirthDateProfile",
                [
                    new ResourceProfile(
                        "Student",
                        null,
                        new ContentTypeDefinition(
                            MemberSelection.ExcludeOnly,
                            [new PropertyRule("birthDate"), new PropertyRule("middleName")],
                            [],
                            [],
                            []
                        ),
                        null
                    ),
                ]
            );
        }

        [Test]
        public void It_removes_only_excluded_properties()
        {
            var filter = CreateFilter();
            var profile = CreateExcludeOnlyProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            // Check _readable schema (since profile only has ReadContentType)
            var studentSchema =
                result["components"]!["schemas"]!["EdFi_Student_readable"]!["properties"] as JsonObject;
            studentSchema.Should().NotBeNull();

            // Properties not in exclude list should be kept
            studentSchema!.ContainsKey("firstName").Should().BeTrue();
            studentSchema.ContainsKey("lastName").Should().BeTrue();
            studentSchema.ContainsKey("id").Should().BeTrue();
            studentSchema.ContainsKey("studentUniqueId").Should().BeTrue();

            // Excluded properties should be removed
            studentSchema.ContainsKey("birthDate").Should().BeFalse();
            studentSchema.ContainsKey("middleName").Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_Profile_Covers_Multiple_Resources : ProfileOpenApiSpecificationFilterTests
    {
        private static ProfileDefinition CreateMultiResourceProfile()
        {
            return new ProfileDefinition(
                "StudentSchoolProfile",
                [
                    new ResourceProfile(
                        "Student",
                        null,
                        new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], []),
                        new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], [])
                    ),
                    new ResourceProfile(
                        "School",
                        null,
                        new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], []),
                        null // Read-only
                    ),
                ]
            );
        }

        [Test]
        public void It_includes_paths_for_all_covered_resources()
        {
            var filter = CreateFilter();
            var profile = CreateMultiResourceProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var paths = result["paths"] as JsonObject;
            paths.Should().NotBeNull();
            paths!.ContainsKey("/ed-fi/students").Should().BeTrue();
            paths.ContainsKey("/ed-fi/schools").Should().BeTrue();
        }

        [Test]
        public void It_applies_different_operations_per_resource()
        {
            var filter = CreateFilter();
            var profile = CreateMultiResourceProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            // Students should have both GET and POST
            var studentsPath = result["paths"]!["/ed-fi/students"] as JsonObject;
            studentsPath!.ContainsKey("get").Should().BeTrue();
            studentsPath.ContainsKey("post").Should().BeTrue();

            // Schools should only have GET (read-only in profile)
            var schoolsPath = result["paths"]!["/ed-fi/schools"] as JsonObject;
            schoolsPath!.ContainsKey("get").Should().BeTrue();
            schoolsPath.ContainsKey("post").Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_Base_Spec_Is_Modified : ProfileOpenApiSpecificationFilterTests
    {
        [Test]
        public void It_does_not_modify_original_base_specification()
        {
            var filter = CreateFilter();
            var baseSpec = GetBaseSpec();
            var originalJson = baseSpec.ToJsonString();

            var profile = new ProfileDefinition(
                "TestProfile",
                [
                    new ResourceProfile(
                        "Student",
                        null,
                        new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], []),
                        null
                    ),
                ]
            );

            _ = filter.CreateProfileSpecification(baseSpec, profile);

            // Original should not be modified
            baseSpec.ToJsonString().Should().Be(originalJson);
        }
    }

    [TestFixture]
    public class Given_Profile_Creates_Readable_And_Writable_Schemas : ProfileOpenApiSpecificationFilterTests
    {
        private static ProfileDefinition CreateReadWriteProfile()
        {
            return new ProfileDefinition(
                "StudentProfile",
                [
                    new ResourceProfile(
                        "Student",
                        null,
                        new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], []),
                        new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], [])
                    ),
                ]
            );
        }

        [Test]
        public void It_creates_readable_schema_suffix()
        {
            var filter = CreateFilter();
            var profile = CreateReadWriteProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var schemas = result["components"]!["schemas"] as JsonObject;
            schemas.Should().NotBeNull();
            schemas!.ContainsKey("EdFi_Student_readable").Should().BeTrue();
        }

        [Test]
        public void It_creates_writable_schema_suffix()
        {
            var filter = CreateFilter();
            var profile = CreateReadWriteProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var schemas = result["components"]!["schemas"] as JsonObject;
            schemas.Should().NotBeNull();
            schemas!.ContainsKey("EdFi_Student_writable").Should().BeTrue();
        }

        [Test]
        public void It_updates_GET_response_ref_to_readable_schema()
        {
            var filter = CreateFilter();
            var profile = CreateReadWriteProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var getOperation = result["paths"]!["/ed-fi/students/{id}"]!["get"];
            var responseContent = getOperation!["responses"]!["200"]!["content"] as JsonObject;
            responseContent.Should().NotBeNull();

            // Find the profile content type
            string contentKey = responseContent!.Select(kvp => kvp.Key).First(k => k.Contains("readable"));

            var contentNode = responseContent![contentKey] as JsonObject;
            contentNode.Should().NotBeNull();
            var schema = contentNode!["schema"] as JsonObject;
            schema.Should().NotBeNull();
            var refValue = schema!["$ref"]?.GetValue<string>();
            refValue.Should().EndWith("_readable");
        }

        [Test]
        public void It_updates_POST_request_ref_to_writable_schema()
        {
            var filter = CreateFilter();
            var profile = CreateReadWriteProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var postOperation = result["paths"]!["/ed-fi/students"]!["post"];
            var requestContent = postOperation!["requestBody"]!["content"] as JsonObject;
            requestContent.Should().NotBeNull();

            // Find the profile content type
            string contentKey = requestContent!.Select(kvp => kvp.Key).First(k => k.Contains("writable"));

            var contentNode = requestContent![contentKey] as JsonObject;
            contentNode.Should().NotBeNull();
            var schema = contentNode!["schema"] as JsonObject;
            schema.Should().NotBeNull();
            var refValue = schema!["$ref"]?.GetValue<string>();
            refValue.Should().EndWith("_writable");
        }

        [Test]
        public void It_updates_PUT_request_ref_to_writable_schema()
        {
            var filter = CreateFilter();
            var profile = CreateReadWriteProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var putOperation = result["paths"]!["/ed-fi/students/{id}"]!["put"];
            var requestContent = putOperation!["requestBody"]!["content"] as JsonObject;
            requestContent.Should().NotBeNull();

            // Find the profile content type
            string contentKey = requestContent!.Select(kvp => kvp.Key).First(k => k.Contains("writable"));

            var contentNode = requestContent![contentKey] as JsonObject;
            contentNode.Should().NotBeNull();
            var schema = contentNode!["schema"] as JsonObject;
            schema.Should().NotBeNull();
            var refValue = schema!["$ref"]?.GetValue<string>();
            refValue.Should().EndWith("_writable");
        }
    }

    [TestFixture]
    public class Given_Transformation_Schema_Rules : ProfileOpenApiSpecificationFilterTests
    {
        private static ProfileDefinition CreateFullReadWriteProfile()
        {
            return new ProfileDefinition(
                "FullProfile",
                [
                    new ResourceProfile(
                        "Student",
                        null,
                        new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], []),
                        new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], [])
                    ),
                ]
            );
        }

        [Test]
        public void Writable_schema_must_exclude_server_generated_id()
        {
            var filter = CreateFilter();
            var profile = CreateFullReadWriteProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var writableSchema =
                result["components"]!["schemas"]!["EdFi_Student_writable"]!["properties"] as JsonObject;
            writableSchema.Should().NotBeNull();
            writableSchema!
                .ContainsKey("id")
                .Should()
                .BeFalse("writable schemas MUST NOT include server-generated id");
        }

        [Test]
        public void Writable_schema_must_exclude_etag()
        {
            var filter = CreateFilter();
            var profile = CreateFullReadWriteProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var writableSchema =
                result["components"]!["schemas"]!["EdFi_Student_writable"]!["properties"] as JsonObject;
            writableSchema.Should().NotBeNull();
            writableSchema!.ContainsKey("_etag").Should().BeFalse("writable schemas MUST exclude _etag");
        }

        [Test]
        public void Writable_schema_must_exclude_lastModifiedDate()
        {
            var filter = CreateFilter();
            var profile = CreateFullReadWriteProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var writableSchema =
                result["components"]!["schemas"]!["EdFi_Student_writable"]!["properties"] as JsonObject;
            writableSchema.Should().NotBeNull();
            writableSchema!
                .ContainsKey("_lastModifiedDate")
                .Should()
                .BeFalse("writable schemas MUST exclude _lastModifiedDate");
        }

        [Test]
        public void Writable_schema_must_exclude_link()
        {
            var filter = CreateFilter();
            var profile = CreateFullReadWriteProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var writableSchema =
                result["components"]!["schemas"]!["EdFi_Student_writable"]!["properties"] as JsonObject;
            writableSchema.Should().NotBeNull();
            writableSchema!.ContainsKey("link").Should().BeFalse("writable schemas MUST exclude link");
        }

        [Test]
        public void Writable_schema_must_include_natural_identity_fields()
        {
            var filter = CreateFilter();
            var profile = CreateFullReadWriteProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var writableSchema =
                result["components"]!["schemas"]!["EdFi_Student_writable"]!["properties"] as JsonObject;
            writableSchema.Should().NotBeNull();
            writableSchema!
                .ContainsKey("studentUniqueId")
                .Should()
                .BeTrue("writable schemas MUST include natural identity (UniqueId) fields");
        }

        [Test]
        public void Readable_schema_must_include_server_generated_id()
        {
            var filter = CreateFilter();
            var profile = CreateFullReadWriteProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var readableSchema =
                result["components"]!["schemas"]!["EdFi_Student_readable"]!["properties"] as JsonObject;
            readableSchema.Should().NotBeNull();
            readableSchema!
                .ContainsKey("id")
                .Should()
                .BeTrue("readable schemas MUST include server-generated id");
        }

        [Test]
        public void Readable_schema_must_include_etag()
        {
            var filter = CreateFilter();
            var profile = CreateFullReadWriteProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var readableSchema =
                result["components"]!["schemas"]!["EdFi_Student_readable"]!["properties"] as JsonObject;
            readableSchema.Should().NotBeNull();
            readableSchema!.ContainsKey("_etag").Should().BeTrue("readable schemas MUST include _etag");
        }

        [Test]
        public void Readable_schema_must_include_lastModifiedDate()
        {
            var filter = CreateFilter();
            var profile = CreateFullReadWriteProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var readableSchema =
                result["components"]!["schemas"]!["EdFi_Student_readable"]!["properties"] as JsonObject;
            readableSchema.Should().NotBeNull();
            readableSchema!
                .ContainsKey("_lastModifiedDate")
                .Should()
                .BeTrue("readable schemas MUST include _lastModifiedDate");
        }

        [Test]
        public void Readable_schema_must_include_link()
        {
            var filter = CreateFilter();
            var profile = CreateFullReadWriteProfile();

            var result = filter.CreateProfileSpecification(GetBaseSpec(), profile);

            var readableSchema =
                result["components"]!["schemas"]!["EdFi_Student_readable"]!["properties"] as JsonObject;
            readableSchema.Should().NotBeNull();
            readableSchema!.ContainsKey("link").Should().BeTrue("readable schemas MUST include link");
        }
    }
}
