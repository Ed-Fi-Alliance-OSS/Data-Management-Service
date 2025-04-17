// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
public class ProvideAuthorizationPathwayMiddlewareTests
{
    // SUT
    private readonly ProvideAuthorizationPathwayMiddleware _provideAuthorizationPathwayMiddleware = new(
        NullLogger<ProvideApiSchemaMiddleware>.Instance
    );

    private readonly PipelineContext _context = No.PipelineContext();

    private readonly ApiSchemaDocuments _apiSchemaDocument = new ApiSchemaBuilder()
        .WithStartProject()
        .WithStartResource("StudentSchoolAssociation")
        .WithAuthorizationPathways(["StudentSchoolAssociationAuthorization"])
        .WithEndResource()
        .WithEndProject()
        .ToApiSchemaDocuments();

    [TestFixture]
    public class Given_A_StudentSchoolAssociation_Post : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _context.ProjectSchema = _apiSchemaDocument.GetCoreProjectSchema();
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("StudentSchoolAssociations"))!
            );

            _context.DocumentSecurityElements = new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new ResourceName("School"),
                        new EducationOrganizationId(123)
                    ),
                ],
                [new StudentUniqueId("987")]
            );
            _context.Method = RequestMethod.POST;
        }

        [Test]
        public async Task Initializes_StudentSchoolAssociationAuthorizationPathway_In_The_Pipeline_Context()
        {
            // Act
            await _provideAuthorizationPathwayMiddleware.Execute(_context, NullNext);

            // Assert
            _context.AuthorizationPathways.Count.Should().Be(1);
            _context
                .AuthorizationPathways.Single()
                .Should()
                .BeOfType<AuthorizationPathway.StudentSchoolAssociation>();

            var pathway = (AuthorizationPathway.StudentSchoolAssociation)
                _context.AuthorizationPathways.Single();
            pathway.StudentUniqueId.Should().Be(new StudentUniqueId("987"));
            pathway.SchoolId.Should().Be(new EducationOrganizationId(123));
        }
    }

    [TestFixture]
    public class Given_A_StudentSchoolAssociation_Get : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _context.ProjectSchema = _apiSchemaDocument.GetCoreProjectSchema();
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("StudentSchoolAssociations"))!
            );

            _context.Method = RequestMethod.GET;
        }

        [Test]
        public async Task Initializes_StudentSchoolAssociationAuthorizationPathway_In_The_Pipeline_Context()
        {
            // Act
            await _provideAuthorizationPathwayMiddleware.Execute(_context, NullNext);

            // Assert
            _context.AuthorizationPathways.Count.Should().Be(1);
            _context
                .AuthorizationPathways.Single()
                .Should()
                .BeOfType<AuthorizationPathway.StudentSchoolAssociation>();

            var pathway = (AuthorizationPathway.StudentSchoolAssociation)
                _context.AuthorizationPathways.Single();
            pathway.StudentUniqueId.Should().Be(default(StudentUniqueId));
            pathway.SchoolId.Should().Be(default(EducationOrganizationId));
        }
    }

    [TestFixture]
    public class Given_A_Resource_With_No_Authorization_Pathway : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            var _invalidAuthorizationPathwayApiSchemaDocument = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Student")
                .WithAuthorizationPathways([])
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            _context.ProjectSchema = _invalidAuthorizationPathwayApiSchemaDocument.GetCoreProjectSchema();
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("Students"))!
            );
            _context.Method = RequestMethod.POST;
        }

        [Test]
        public async Task Does_Not_Initialize_AuthorizationPathways_In_The_Pipeline_Context()
        {
            // Act
            await _provideAuthorizationPathwayMiddleware.Execute(_context, NullNext);

            // Assert
            _context.AuthorizationPathways.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_A_Resource_With_Unrecognized_AuthorizationPathways
        : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            var _invalidAuthorizationPathwayApiSchemaDocument = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("StudentSchoolAssociation")
                .WithAuthorizationPathways(["Invalid"])
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            _context.ProjectSchema = _invalidAuthorizationPathwayApiSchemaDocument.GetCoreProjectSchema();
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("StudentSchoolAssociations"))!
            );

            _context.DocumentSecurityElements = new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new ResourceName("School"),
                        new EducationOrganizationId(123)
                    ),
                ],
                [new StudentUniqueId("987")]
            );
            _context.Method = RequestMethod.POST;
        }

        [Test]
        public void Throws_exception()
        {
            // Act & Assert
            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await _provideAuthorizationPathwayMiddleware.Execute(_context, NullNext)
            );
        }
    }

    [TestFixture]
    public class Given_A_StudentSchoolAssociation_Post_With_No_StudentId
        : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _context.ProjectSchema = _apiSchemaDocument.GetCoreProjectSchema();
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("StudentSchoolAssociations"))!
            );

            _context.DocumentSecurityElements = new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new ResourceName("School"),
                        new EducationOrganizationId(123)
                    ),
                ],
                []
            );
            _context.Method = RequestMethod.POST;
        }

        [Test]
        public void Throws_exception()
        {
            // Act & Assert
            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await _provideAuthorizationPathwayMiddleware.Execute(_context, NullNext)
            );
        }
    }

    [TestFixture]
    public class Given_A_StudentSchoolAssociation_Post_With_No_SchoolId
        : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _context.ProjectSchema = _apiSchemaDocument.GetCoreProjectSchema();
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("StudentSchoolAssociations"))!
            );

            _context.DocumentSecurityElements = new DocumentSecurityElements(
                [],
                [],
                [new StudentUniqueId("987")]
            );
            _context.Method = RequestMethod.POST;
        }

        [Test]
        public void Throws_exception()
        {
            // Act & Assert
            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await _provideAuthorizationPathwayMiddleware.Execute(_context, NullNext)
            );
        }
    }
}
