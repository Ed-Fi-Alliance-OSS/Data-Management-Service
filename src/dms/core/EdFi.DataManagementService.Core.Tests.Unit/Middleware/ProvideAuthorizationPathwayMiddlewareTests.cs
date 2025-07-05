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
[Parallelizable]
public class ProvideAuthorizationPathwayMiddlewareTests
{
    // SUT
    private readonly ProvideAuthorizationPathwayMiddleware _provideAuthorizationPathwayMiddleware = new(
        NullLogger<ProvideApiSchemaMiddleware>.Instance
    );

    private readonly RequestInfo _requestInfo = No.RequestInfo();

    private readonly ApiSchemaDocuments _studentSchoolAssociationApiSchma = new ApiSchemaBuilder()
        .WithStartProject()
        .WithStartResource("StudentSchoolAssociation")
        .WithAuthorizationPathways(["StudentSchoolAssociationAuthorization"])
        .WithEndResource()
        .WithEndProject()
        .ToApiSchemaDocuments();

    private readonly ApiSchemaDocuments _studentContactAssociationApiSchma = new ApiSchemaBuilder()
        .WithStartProject()
        .WithStartResource("StudentContactAssociation")
        .WithAuthorizationPathways(["ContactStudentSchoolAuthorization"])
        .WithEndResource()
        .WithEndProject()
        .ToApiSchemaDocuments();

    private readonly ApiSchemaDocuments _staffEducationOrganizationApiSchema = new ApiSchemaBuilder()
        .WithStartProject()
        .WithStartResource("StaffEducationOrganizationAssignmentAssociation")
        .WithAuthorizationPathways(["StaffEducationOrganizationAuthorization"])
        .WithEndResource()
        .WithEndProject()
        .ToApiSchemaDocuments();

    [TestFixture]
    [Parallelizable]
    public class Given_A_StudentSchoolAssociation_Post : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _requestInfo.ProjectSchema = _studentSchoolAssociationApiSchma.GetCoreProjectSchema();
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(
                    new("StudentSchoolAssociations")
                )!
            );

            _requestInfo.DocumentSecurityElements = new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new MetaEdPropertyFullName("SchoolId"),
                        new EducationOrganizationId(123)
                    ),
                ],
                [new StudentUniqueId("987")],
                [],
                []
            );
            _requestInfo.Method = RequestMethod.POST;
        }

        [Test]
        public async Task Initializes_StudentSchoolAssociationAuthorizationPathway_In_The_Pipeline_Context()
        {
            // Act
            await _provideAuthorizationPathwayMiddleware.Execute(_requestInfo, NullNext);

            // Assert
            _requestInfo.AuthorizationPathways.Count.Should().Be(1);
            _requestInfo
                .AuthorizationPathways.Single()
                .Should()
                .BeOfType<AuthorizationPathway.StudentSchoolAssociation>();

            var pathway = (AuthorizationPathway.StudentSchoolAssociation)
                _requestInfo.AuthorizationPathways.Single();
            pathway.StudentUniqueId.Should().Be(new StudentUniqueId("987"));
            pathway.SchoolId.Should().Be(new EducationOrganizationId(123));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_StudentSchoolAssociation_Get : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _requestInfo.ProjectSchema = _studentSchoolAssociationApiSchma.GetCoreProjectSchema();
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(
                    new("StudentSchoolAssociations")
                )!
            );

            _requestInfo.Method = RequestMethod.GET;
        }

        [Test]
        public async Task Initializes_StudentSchoolAssociationAuthorizationPathway_In_The_Pipeline_Context()
        {
            // Act
            await _provideAuthorizationPathwayMiddleware.Execute(_requestInfo, NullNext);

            // Assert
            _requestInfo.AuthorizationPathways.Count.Should().Be(1);
            _requestInfo
                .AuthorizationPathways.Single()
                .Should()
                .BeOfType<AuthorizationPathway.StudentSchoolAssociation>();

            var pathway = (AuthorizationPathway.StudentSchoolAssociation)
                _requestInfo.AuthorizationPathways.Single();
            pathway.StudentUniqueId.Should().Be(default(StudentUniqueId));
            pathway.SchoolId.Should().Be(default(EducationOrganizationId));
        }
    }

    [TestFixture]
    [Parallelizable]
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

            _requestInfo.ProjectSchema = _invalidAuthorizationPathwayApiSchemaDocument.GetCoreProjectSchema();
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("Students"))!
            );
            _requestInfo.Method = RequestMethod.POST;
        }

        [Test]
        public async Task Does_Not_Initialize_AuthorizationPathways_In_The_Pipeline_Context()
        {
            // Act
            await _provideAuthorizationPathwayMiddleware.Execute(_requestInfo, NullNext);

            // Assert
            _requestInfo.AuthorizationPathways.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
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

            _requestInfo.ProjectSchema = _invalidAuthorizationPathwayApiSchemaDocument.GetCoreProjectSchema();
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(
                    new("StudentSchoolAssociations")
                )!
            );

            _requestInfo.DocumentSecurityElements = new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new MetaEdPropertyFullName("SchoolId"),
                        new EducationOrganizationId(123)
                    ),
                ],
                [new StudentUniqueId("987")],
                [],
                []
            );
            _requestInfo.Method = RequestMethod.POST;
        }

        [Test]
        public void Throws_exception()
        {
            // Act & Assert
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _provideAuthorizationPathwayMiddleware.Execute(_requestInfo, NullNext)
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_StudentSchoolAssociation_Post_With_No_StudentId
        : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _requestInfo.ProjectSchema = _studentSchoolAssociationApiSchma.GetCoreProjectSchema();
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(
                    new("StudentSchoolAssociations")
                )!
            );

            _requestInfo.DocumentSecurityElements = new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new MetaEdPropertyFullName("SchoolId"),
                        new EducationOrganizationId(123)
                    ),
                ],
                [],
                [],
                []
            );
            _requestInfo.Method = RequestMethod.POST;
        }

        [Test]
        public void Throws_exception()
        {
            // Act & Assert
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _provideAuthorizationPathwayMiddleware.Execute(_requestInfo, NullNext)
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_StudentSchoolAssociation_Post_With_No_SchoolId
        : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _requestInfo.ProjectSchema = _studentSchoolAssociationApiSchma.GetCoreProjectSchema();
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(
                    new("StudentSchoolAssociations")
                )!
            );

            _requestInfo.DocumentSecurityElements = new DocumentSecurityElements(
                [],
                [],
                [new StudentUniqueId("987")],
                [],
                []
            );
            _requestInfo.Method = RequestMethod.POST;
        }

        [Test]
        public void Throws_exception()
        {
            // Act & Assert
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _provideAuthorizationPathwayMiddleware.Execute(_requestInfo, NullNext)
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_StudentContactAssociation_Post : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _requestInfo.ProjectSchema = _studentContactAssociationApiSchma.GetCoreProjectSchema();
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(
                    new("StudentContactAssociations")
                )!
            );

            _requestInfo.DocumentSecurityElements = new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new MetaEdPropertyFullName("SchoolId"),
                        new EducationOrganizationId(123)
                    ),
                ],
                [new StudentUniqueId("987")],
                [new ContactUniqueId("898")],
                []
            );
            _requestInfo.Method = RequestMethod.POST;
        }

        [Test]
        public async Task Initializes_StudentContactAssociationAuthorizationPathway_In_The_Pipeline_Context()
        {
            // Act
            await _provideAuthorizationPathwayMiddleware.Execute(_requestInfo, NullNext);

            // Assert
            _requestInfo.AuthorizationPathways.Count.Should().Be(1);
            _requestInfo
                .AuthorizationPathways.Single()
                .Should()
                .BeOfType<AuthorizationPathway.StudentContactAssociation>();

            var pathway = (AuthorizationPathway.StudentContactAssociation)
                _requestInfo.AuthorizationPathways.Single();
            pathway.StudentUniqueId.Should().Be(new StudentUniqueId("987"));
            pathway.ContactUniqueId.Should().Be(new ContactUniqueId("898"));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_StudentContactAssociation_Get : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _requestInfo.ProjectSchema = _studentContactAssociationApiSchma.GetCoreProjectSchema();
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(
                    new("StudentContactAssociations")
                )!
            );

            _requestInfo.Method = RequestMethod.GET;
        }

        [Test]
        public async Task Initializes_StudentContactAssociationAuthorizationPathway_In_The_Pipeline_Context()
        {
            // Act
            await _provideAuthorizationPathwayMiddleware.Execute(_requestInfo, NullNext);

            // Assert
            _requestInfo.AuthorizationPathways.Count.Should().Be(1);
            _requestInfo
                .AuthorizationPathways.Single()
                .Should()
                .BeOfType<AuthorizationPathway.StudentContactAssociation>();

            var pathway = (AuthorizationPathway.StudentContactAssociation)
                _requestInfo.AuthorizationPathways.Single();
            pathway.StudentUniqueId.Should().Be(default(StudentUniqueId));
            pathway.ContactUniqueId.Should().Be(default(ContactUniqueId));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_StudentContactAssociation_Post_With_No_StudentId
        : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _requestInfo.ProjectSchema = _studentContactAssociationApiSchma.GetCoreProjectSchema();
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(
                    new("StudentContactAssociations")
                )!
            );

            _requestInfo.DocumentSecurityElements = new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new MetaEdPropertyFullName("SchoolId"),
                        new EducationOrganizationId(123)
                    ),
                ],
                [],
                [new ContactUniqueId("898")],
                []
            );
            _requestInfo.Method = RequestMethod.POST;
        }

        [Test]
        public void Throws_exception()
        {
            // Act & Assert
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _provideAuthorizationPathwayMiddleware.Execute(_requestInfo, NullNext)
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_StudentContactAssociation_Post_With_No_ContactUniqueId
        : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _requestInfo.ProjectSchema = _studentContactAssociationApiSchma.GetCoreProjectSchema();
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(
                    new("StudentContactAssociations")
                )!
            );

            _requestInfo.DocumentSecurityElements = new DocumentSecurityElements(
                [],
                [],
                [new StudentUniqueId("987")],
                [],
                []
            );
            _requestInfo.Method = RequestMethod.POST;
        }

        [Test]
        public void Throws_exception()
        {
            // Act & Assert
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _provideAuthorizationPathwayMiddleware.Execute(_requestInfo, NullNext)
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_StaffEducationOrganizationAssociation_Post
        : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _requestInfo.ProjectSchema = _staffEducationOrganizationApiSchema.GetCoreProjectSchema();
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(
                    new("StaffEducationOrganizationAssignmentAssociations")
                )!
            );

            _requestInfo.DocumentSecurityElements = new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new MetaEdPropertyFullName("EducationOrganizationId"),
                        new EducationOrganizationId(456)
                    ),
                ],
                [],
                [],
                [new StaffUniqueId("S123")]
            );
            _requestInfo.Method = RequestMethod.POST;
        }

        [Test]
        public async Task Initializes_StaffEducationOrganizationAssociationAuthorizationPathway_In_The_Pipeline_Context()
        {
            // Act
            await _provideAuthorizationPathwayMiddleware.Execute(_requestInfo, NullNext);

            // Assert
            _requestInfo.AuthorizationPathways.Count.Should().Be(1);
            _requestInfo
                .AuthorizationPathways.Single()
                .Should()
                .BeOfType<AuthorizationPathway.StaffEducationOrganizationAssociation>();

            var pathway = (AuthorizationPathway.StaffEducationOrganizationAssociation)
                _requestInfo.AuthorizationPathways.Single();
            pathway.StaffUniqueId.Should().Be(new StaffUniqueId("S123"));
            pathway.EducationOrganizationId.Should().Be(new EducationOrganizationId(456));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_StaffEducationOrganizationAssociation_Get
        : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _requestInfo.ProjectSchema = _staffEducationOrganizationApiSchema.GetCoreProjectSchema();
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(
                    new("StaffEducationOrganizationAssignmentAssociations")
                )!
            );

            _requestInfo.Method = RequestMethod.GET;
        }

        [Test]
        public async Task Initializes_StaffEducationOrganizationAssociationAuthorizationPathway_In_The_Pipeline_Context()
        {
            // Act
            await _provideAuthorizationPathwayMiddleware.Execute(_requestInfo, NullNext);

            // Assert
            _requestInfo.AuthorizationPathways.Count.Should().Be(1);
            _requestInfo
                .AuthorizationPathways.Single()
                .Should()
                .BeOfType<AuthorizationPathway.StaffEducationOrganizationAssociation>();

            var pathway = (AuthorizationPathway.StaffEducationOrganizationAssociation)
                _requestInfo.AuthorizationPathways.Single();
            pathway.StaffUniqueId.Should().Be(default(StaffUniqueId));
            pathway.EducationOrganizationId.Should().Be(default(EducationOrganizationId));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_StaffEducationOrganizationAssociation_Post_With_No_StaffId
        : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _requestInfo.ProjectSchema = _staffEducationOrganizationApiSchema.GetCoreProjectSchema();
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(
                    new("StaffEducationOrganizationAssignmentAssociations")
                )!
            );

            _requestInfo.DocumentSecurityElements = new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new MetaEdPropertyFullName("EducationOrganizationId"),
                        new EducationOrganizationId(456)
                    ),
                ],
                [],
                [],
                []
            );
            _requestInfo.Method = RequestMethod.POST;
        }

        [Test]
        public void Throws_exception()
        {
            // Act & Assert
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _provideAuthorizationPathwayMiddleware.Execute(_requestInfo, NullNext)
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_StaffEducationOrganizationAssociation_Post_With_No_EducationOrganizationId
        : ProvideAuthorizationPathwayMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
            _requestInfo.ProjectSchema = _staffEducationOrganizationApiSchema.GetCoreProjectSchema();
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(
                    new("StaffEducationOrganizationAssignmentAssociations")
                )!
            );

            _requestInfo.DocumentSecurityElements = new DocumentSecurityElements(
                [],
                [],
                [],
                [],
                [new StaffUniqueId("S123")]
            );
            _requestInfo.Method = RequestMethod.POST;
        }

        [Test]
        public void Throws_exception()
        {
            // Act & Assert
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _provideAuthorizationPathwayMiddleware.Execute(_requestInfo, NullNext)
            );
        }
    }
}
