// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
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
public class ExtractDocumentSecurityElementsMiddlewareTests
{
    internal static IPipelineStep BuildMiddleware()
    {
        return new ExtractDocumentSecurityElementsMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_an_assessment_resource_that_has_a_namespace
        : ExtractDocumentSecurityElementsMiddlewareTests
    {
        private RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Assessment")
                .WithNamespaceSecurityElements(["$.namespace"])
                .WithEducationOrganizationSecurityElements([])
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([])
                .WithStaffSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("Namespace", "$.namespace")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "assessments");

            string body = """{"assessmentIdentifier": "123", "namespace": "abc"}""";

            requestInfo = new(
                new(
                    Body: body,
                    Headers: [],
                    QueryParameters: [],
                    Path: "/ed-fi/assessments",
                    TraceId: new TraceId("123")
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema,
                ParsedBody = JsonNode.Parse(body)!,
            };

            await BuildMiddleware().Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_extracted_the_namespace()
        {
            requestInfo.DocumentSecurityElements.Namespace.Should().HaveCount(1);
            requestInfo.DocumentSecurityElements.Namespace[0].Should().Be("abc");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_an_academicWeeks_resource_that_has_a_educationOrganization
        : ExtractDocumentSecurityElementsMiddlewareTests
    {
        private RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithNamespaceSecurityElements([])
                .WithEducationOrganizationSecurityElements([("School", "$.schoolReference.schoolId")])
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([])
                .WithStaffSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("EducationOrganization", "$.schoolReference.schoolId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "academicWeeks");

            string body = """
                {"weekIdentifier": "123",
                    "schoolReference": {
                        "schoolId": 12345
                        }
                }
                """;

            requestInfo = new(
                new(
                    Body: body,
                    Headers: [],
                    QueryParameters: [],
                    Path: "/ed-fi/academicWeeks",
                    TraceId: new TraceId("123")
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema,
                ParsedBody = JsonNode.Parse(body)!,
            };

            await BuildMiddleware().Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_extracted_the_educationOrganization()
        {
            requestInfo.DocumentSecurityElements.EducationOrganization.Should().HaveCount(1);
            requestInfo.DocumentSecurityElements.EducationOrganization[0].Id.Value.Should().Be(12345);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_a_StudentContactAssociations_resource_that_has_studentUniqueId_and_ContactUniqueId
        : ExtractDocumentSecurityElementsMiddlewareTests
    {
        private RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("StudentContactAssociation")
                .WithNamespaceSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithStudentSecurityElements(["$.studentReference.studentUniqueId"])
                .WithContactSecurityElements(["$.contactReference.contactUniqueId"])
                .WithStaffSecurityElements([])
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(
                apiSchemaDocuments,
                "StudentContactAssociations"
            );

            string body = """
                {
                    "studentReference": {
                        "studentUniqueId": "12345"
                    },
                   "contactReference": {
                        "contactUniqueId": "7878"
                    }
                }
                """;

            requestInfo = new(
                new(
                    Body: body,
                    Headers: [],
                    QueryParameters: [],
                    Path: "/ed-fi/academicWeeks",
                    TraceId: new TraceId("123")
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema,
                ParsedBody = JsonNode.Parse(body)!,
            };

            await BuildMiddleware().Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_extracted_studentUniqueId_and_contactUniqueId()
        {
            requestInfo.DocumentSecurityElements.Student.Should().HaveCount(1);
            requestInfo.DocumentSecurityElements.Student[0].Value.Should().Be("12345");
            requestInfo.DocumentSecurityElements.Contact.Should().HaveCount(1);
            requestInfo.DocumentSecurityElements.Contact[0].Value.Should().Be("7878");
        }
    }
}
