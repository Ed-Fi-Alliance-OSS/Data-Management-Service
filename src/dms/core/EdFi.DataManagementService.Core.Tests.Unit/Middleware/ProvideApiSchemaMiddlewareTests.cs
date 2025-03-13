// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Pipeline;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
public class ProvideApiSchemaMiddlewareTests
{
    // SUT
    private ProvideApiSchemaMiddleware? _provideApiSchemaMiddleware;

    [TestFixture]
    public class Given_An_Api_Schema_With_Resource_Extensions : ProvideApiSchemaMiddlewareTests
    {
        private readonly ApiSchemaNodes _apiSchemaNodes = new ApiSchemaBuilder()
            .WithStartProject("Ed-Fi", "5.0.0")
            .WithStartResource("School")
            .WithEndResource()
            .WithEndProject()
            .WithStartProject("tpdm", "5.0.0")
            .WithStartResource("School", isResourceExtension: true)
            .WithBooleanJsonPaths(["$._ext.tpdm.gradeLevels[*].isSecondary"])
            .WithNumericJsonPaths(["$._ext.tpdm.schoolId"])
            .WithDateTimeJsonPaths(["$._ext.tpdm.beginDate"])
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference(
                "Person",
                [new("$._ext.tpdm.personId", "$._ext.tpdm.personReference.personId")]
            )
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .AsApiSchemaNodes();

        [SetUp]
        public void Setup()
        {
            var fakeApiSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => fakeApiSchemaProvider.GetApiSchemaNodes()).Returns(_apiSchemaNodes);

            _provideApiSchemaMiddleware = new ProvideApiSchemaMiddleware(
                fakeApiSchemaProvider,
                NullLogger<ProvideApiSchemaMiddleware>.Instance
            );
        }

        [Test]
        public async Task Copies_paths_to_core()
        {
            // Act
            var fakePipelineContext = A.Fake<PipelineContext>();
            await _provideApiSchemaMiddleware!.Execute(fakePipelineContext, NullNext);

            // Assert
            fakePipelineContext.ApiSchemaDocuments.Should().NotBeNull();

            var coreSchoolResource = fakePipelineContext
                .ApiSchemaDocuments.GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new ResourceName("School"));

            coreSchoolResource!
                .GetRequiredNode("booleanJsonPaths")
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .ContainSingle("$._ext.tpdm.gradeLevels[*].isSecondary");

            coreSchoolResource!
                .GetRequiredNode("numericJsonPaths")
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .ContainSingle("$._ext.tpdm.schoolId");

            coreSchoolResource!
                .GetRequiredNode("dateTimeJsonPaths")
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .ContainSingle("$._ext.tpdm.beginDate");

            coreSchoolResource!
                .GetRequiredNode("documentPathsMapping")
                .AsObject()
                .GetRequiredNode("Person")
                .GetRequiredNode("referenceJsonPaths")[0]!
                .GetRequiredNode("referenceJsonPath")
                .GetValue<string>()
                .Should()
                .Be("$._ext.tpdm.personReference.personId");
        }
    }
}
