// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Backend;
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
public class ProvideAuthorizationSecurableInfoMiddlewareTests
{
    internal static IPipelineStep BuildMiddleware()
    {
        return new ProvideAuthorizationSecurableInfoMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_a_document_with_a_StudentUniqueId : ProvideAuthorizationSecurableInfoMiddlewareTests
    {
        private PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Student")
                .WithStudentSecurityElements(["$.studentUniqueId"]) // This indicates that the StudentUniqueId should be extracted for authorization
                .WithContactSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithStafftSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("StudentUniqueId", "$.studentUniqueId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "students");

            string body = """{"studentUniqueId": "12345"}""";

            context = new(
                new(
                    Body: body,
                    Header: [],
                    QueryParameters: [],
                    Path: "/ed-fi/students",
                    TraceId: new TraceId("123"),
                    ClientAuthorizations: new ClientAuthorizations(
                        TokenId: "",
                        ClaimSetName: "",
                        EducationOrganizationIds: [],
                        NamespacePrefixes: []
                    )
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema,
                ParsedBody = JsonNode.Parse(body)!,
            };

            await BuildMiddleware().Execute(context, NullNext);
        }

        [Test]
        public void It_has_StudentUniqueId_as_securable_key()
        {
            context
                .AuthorizationSecurableInfo[0]
                .SecurableKey.Should()
                .Be(SecurityElementNameConstants.StudentUniqueId);
        }
    }

    [TestFixture]
    public class Given_a_document_without_StudentAuthorizationSecurablePaths
        : ProvideAuthorizationSecurableInfoMiddlewareTests
    {
        private PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Student")
                .WithStudentSecurityElements([]) // No paths specified for Student Securable elements
                .WithContactSecurityElements([])
                .WithStafftSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("StudentUniqueId", "$.studentUniqueId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "students");

            string body = """{"studentUniqueId": "12345"}""";

            context = new(
                new(
                    Body: body,
                    Header: [],
                    QueryParameters: [],
                    Path: "/ed-fi/students",
                    TraceId: new TraceId("123"),
                    ClientAuthorizations: new ClientAuthorizations(
                        TokenId: "",
                        ClaimSetName: "",
                        EducationOrganizationIds: [],
                        NamespacePrefixes: []
                    )
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema,
                ParsedBody = JsonNode.Parse(body)!,
            };

            await BuildMiddleware().Execute(context, NullNext);
        }

        [Test]
        public void It_does_not_have_securable_key()
        {
            context.AuthorizationSecurableInfo.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_document_with_ContactReference_and_StudentUniqueId
        : ProvideAuthorizationSecurableInfoMiddlewareTests
    {
        private PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            // StudentUniqueId and ContactUniqueId should be extracted for authorization
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("StudentContactAssociation")
                .WithStudentSecurityElements(["$.studentUniqueId"])
                .WithContactSecurityElements(["$.contactReference.contactUniqueId"])
                .WithStafftSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("StudentUniqueId", "$.studentUniqueId")
                .WithDocumentPathReference(
                    "Contact",
                    [new("$.contactUniqueId", "$.contactReference.contactUniqueId")]
                )
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(
                apiSchemaDocuments,
                "StudentContactAssociations"
            );

            string body = """
                {
                 "studentUniqueId": "12345",
                 "contactReference":
                    {
                      "contactUniqueId": "contact123"
                    }
                }
                """;

            context = new(
                new(
                    Body: body,
                    Header: [],
                    QueryParameters: [],
                    Path: "/ed-fi/StudentContactAssociations",
                    TraceId: new TraceId("123"),
                    ClientAuthorizations: new ClientAuthorizations(
                        TokenId: "",
                        ClaimSetName: "",
                        EducationOrganizationIds: [],
                        NamespacePrefixes: []
                    )
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema,
                ParsedBody = JsonNode.Parse(body)!,
            };

            await BuildMiddleware().Execute(context, NullNext);
        }

        [Test]
        public void It_has_StudentUniqueId_as_securable_key()
        {
            context.AuthorizationSecurableInfo.Length.Should().Be(2);
            context
                .AuthorizationSecurableInfo[0]
                .SecurableKey.Should()
                .Be(SecurityElementNameConstants.StudentUniqueId);
            context
                .AuthorizationSecurableInfo[1]
                .SecurableKey.Should()
                .Be(SecurityElementNameConstants.ContactUniqueId);
        }
    }

    [TestFixture]
    public class Given_a_document_with_a_ContactUniqueId : ProvideAuthorizationSecurableInfoMiddlewareTests
    {
        private PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Contact")
                .WithStudentSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithContactSecurityElements(["$.contactUniqueId"]) // This indicates that the ContactUniqueId should be extracted for authorization
                .WithStafftSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("ContactUniqueId", "$.contactUniqueId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "contacts");

            string body = """{"contactUniqueId": "12345"}""";

            context = new(
                new(
                    Body: body,
                    Header: [],
                    QueryParameters: [],
                    Path: "/ed-fi/contacts",
                    TraceId: new TraceId("123"),
                    ClientAuthorizations: new ClientAuthorizations(
                        TokenId: "",
                        ClaimSetName: "",
                        EducationOrganizationIds: [],
                        NamespacePrefixes: []
                    )
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema,
                ParsedBody = JsonNode.Parse(body)!,
            };

            await BuildMiddleware().Execute(context, NullNext);
        }

        [Test]
        public void It_has_ContactUniqueId_as_securable_key()
        {
            context
                .AuthorizationSecurableInfo[0]
                .SecurableKey.Should()
                .Be(SecurityElementNameConstants.ContactUniqueId);
        }
    }

    [TestFixture]
    public class Given_a_document_without_ContactAuthorizationSecurablePaths
        : ProvideAuthorizationSecurableInfoMiddlewareTests
    {
        private PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Contact")
                .WithEducationOrganizationSecurityElements([])
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([]) // No paths specified for Student Securable elements
                .WithStafftSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("ContactUniqueId", "$.contactUniqueId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "Contacts");

            string body = """{"contactUniqueId": "12345"}""";

            context = new(
                new(
                    Body: body,
                    Header: [],
                    QueryParameters: [],
                    Path: "/ed-fi/Contacts",
                    TraceId: new TraceId("123"),
                    ClientAuthorizations: new ClientAuthorizations(
                        TokenId: "",
                        ClaimSetName: "",
                        EducationOrganizationIds: [],
                        NamespacePrefixes: []
                    )
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema,
                ParsedBody = JsonNode.Parse(body)!,
            };

            await BuildMiddleware().Execute(context, NullNext);
        }

        [Test]
        public void It_does_not_have_securable_key()
        {
            context.AuthorizationSecurableInfo.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_document_with_a_StaffUniqueId : ProvideAuthorizationSecurableInfoMiddlewareTests
    {
        private PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Staff")
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithStafftSecurityElements(["$.staffUniqueId"])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("StaffUniqueId", "$.staffUniqueId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "staffs");

            string body = """{"staffUniqueId": "S12345"}""";

            context = new(
                new(
                    Body: body,
                    Header: [],
                    QueryParameters: [],
                    Path: "/ed-fi/staffs",
                    TraceId: new TraceId("123"),
                    ClientAuthorizations: new ClientAuthorizations(
                        TokenId: "",
                        ClaimSetName: "",
                        EducationOrganizationIds: [],
                        NamespacePrefixes: []
                    )
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema,
                ParsedBody = JsonNode.Parse(body)!,
            };

            await BuildMiddleware().Execute(context, NullNext);
        }

        [Test]
        public void It_has_StaffUniqueId_as_securable_key()
        {
            context
                .AuthorizationSecurableInfo[0]
                .SecurableKey.Should()
                .Be(SecurityElementNameConstants.StaffUniqueId);
        }
    }

    [TestFixture]
    public class Given_a_document_without_StaffAuthorizationSecurablePaths
        : ProvideAuthorizationSecurableInfoMiddlewareTests
    {
        private PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Staff")
                .WithStudentSecurityElements([])
                .WithContactSecurityElements([])
                .WithEducationOrganizationSecurityElements([])
                .WithStafftSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("StaffUniqueId", "$.staffUniqueId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "staffs");

            string body = """{"staffUniqueId": "S12345"}""";

            context = new(
                new(
                    Body: body,
                    Header: [],
                    QueryParameters: [],
                    Path: "/ed-fi/staffs",
                    TraceId: new TraceId("123"),
                    ClientAuthorizations: new ClientAuthorizations(
                        TokenId: "",
                        ClaimSetName: "",
                        EducationOrganizationIds: [],
                        NamespacePrefixes: []
                    )
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema,
                ParsedBody = JsonNode.Parse(body)!,
            };

            await BuildMiddleware().Execute(context, NullNext);
        }

        [Test]
        public void It_does_not_have_securable_key()
        {
            context.AuthorizationSecurableInfo.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_document_with_StaffReference_and_StudentUniqueId
        : ProvideAuthorizationSecurableInfoMiddlewareTests
    {
        private PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("StudentStaffAssociation")
                .WithStudentSecurityElements(["$.studentUniqueId"])
                .WithContactSecurityElements([])
                .WithStafftSecurityElements(["$.staffReference.staffUniqueId"])
                .WithEducationOrganizationSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("StudentUniqueId", "$.studentUniqueId")
                .WithDocumentPathReference(
                    "Staff",
                    [new("$.staffUniqueId", "$.staffReference.staffUniqueId")]
                )
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(
                apiSchemaDocuments,
                "StudentStaffAssociations"
            );

            string body = """
                {
                 "studentUniqueId": "12345",
                 "staffReference":
                    {
                      "staffUniqueId": "staff123"
                    }
                }
                """;

            context = new(
                new(
                    Body: body,
                    Header: [],
                    QueryParameters: [],
                    Path: "/ed-fi/StudentStaffAssociations",
                    TraceId: new TraceId("123"),
                    ClientAuthorizations: new ClientAuthorizations(
                        TokenId: "",
                        ClaimSetName: "",
                        EducationOrganizationIds: [],
                        NamespacePrefixes: []
                    )
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema,
                ParsedBody = JsonNode.Parse(body)!,
            };

            await BuildMiddleware().Execute(context, NullNext);
        }

        [Test]
        public void It_has_StudentUniqueId_and_StaffUniqueId_as_securable_keys()
        {
            context.AuthorizationSecurableInfo.Length.Should().Be(2);
            context
                .AuthorizationSecurableInfo[0]
                .SecurableKey.Should()
                .Be(SecurityElementNameConstants.StudentUniqueId);
            context
                .AuthorizationSecurableInfo[1]
                .SecurableKey.Should()
                .Be(SecurityElementNameConstants.StaffUniqueId);
        }
    }
}
