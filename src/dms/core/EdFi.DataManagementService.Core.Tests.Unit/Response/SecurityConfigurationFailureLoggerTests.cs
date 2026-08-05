// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Response;

/// <summary>
/// Request-surface classification is derived from the typed path operation and the resource kind.
/// These fixtures drive the production logger so each classification is proven end to end rather
/// than inferred from the shape of the switch.
/// </summary>
[TestFixture]
[Parallelizable]
public class SecurityConfigurationFailureLoggerTests
{
    private static RequestInfo RequestInfoFor(
        RequestMethod method,
        ResourcePathOperation operation,
        bool isDescriptor
    )
    {
        FrontendRequest frontendRequest = new(
            Body: null,
            Form: null,
            Headers: [],
            Path: "ed-fi/schools",
            QueryParameters: [],
            TraceId: new TraceId("traceId"),
            RouteQualifiers: []
        );

        return new RequestInfo(frontendRequest, method, No.ServiceProvider)
        {
            PathComponents = new PathComponents(
                ProjectEndpointName: new("ed-fi"),
                EndpointName: new("schools"),
                Operation: operation
            ),
            ResourceInfo = new ResourceInfo(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("School"),
                IsDescriptor: isDescriptor,
                ResourceVersion: new SemVer("5.0.0"),
                AllowIdentityUpdates: false
            ),
        };
    }

    private static string RequestSurfaceFrom(
        RequestMethod method,
        ResourcePathOperation operation,
        bool isDescriptor
    )
    {
        RecordingLogger logger = new();

        SecurityConfigurationFailureLogger.Log(
            logger,
            RequestInfoFor(method, operation, isDescriptor),
            ["No authorization strategies were defined"]
        );

        LogRecord record = logger
            .Records.Where(static candidate => candidate.Level == LogLevel.Error)
            .Should()
            .ContainSingle()
            .Subject;

        return (string)record.Properties["RequestSurface"]!;
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Get_Against_A_Resource_Item : SecurityConfigurationFailureLoggerTests
    {
        [Test]
        public void It_classifies_the_request_as_get_by_id_resource()
        {
            RequestSurfaceFrom(
                    RequestMethod.GET,
                    new ResourcePathOperation.ById(
                        new DocumentUuid(Guid.Parse("11111111-1111-1111-1111-111111111111"))
                    ),
                    isDescriptor: false
                )
                .Should()
                .Be("GetByIdResource");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Get_Against_A_Resource_Collection : SecurityConfigurationFailureLoggerTests
    {
        [Test]
        public void It_classifies_the_request_as_get_many_resource()
        {
            RequestSurfaceFrom(
                    RequestMethod.GET,
                    ResourcePathOperation.Collection.Instance,
                    isDescriptor: false
                )
                .Should()
                .Be("GetManyResource");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Get_Against_A_Descriptor_Collection : SecurityConfigurationFailureLoggerTests
    {
        [Test]
        public void It_classifies_the_request_as_get_many_descriptor()
        {
            RequestSurfaceFrom(
                    RequestMethod.GET,
                    ResourcePathOperation.Collection.Instance,
                    isDescriptor: true
                )
                .Should()
                .Be("GetManyDescriptor");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Get_Against_A_Descriptor_Item : SecurityConfigurationFailureLoggerTests
    {
        [Test]
        public void It_classifies_the_request_as_get_by_id_descriptor()
        {
            RequestSurfaceFrom(
                    RequestMethod.GET,
                    new ResourcePathOperation.ById(
                        new DocumentUuid(Guid.Parse("11111111-1111-1111-1111-111111111111"))
                    ),
                    isDescriptor: true
                )
                .Should()
                .Be("GetByIdDescriptor");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Write_Request : SecurityConfigurationFailureLoggerTests
    {
        [Test]
        public void It_classifies_a_post_to_a_collection_as_create()
        {
            RequestSurfaceFrom(
                    RequestMethod.POST,
                    ResourcePathOperation.Collection.Instance,
                    isDescriptor: false
                )
                .Should()
                .Be("CreateResource");
        }

        [TestCase("PUT", "UpdateResource")]
        [TestCase("DELETE", "DeleteResource")]
        public void It_classifies_an_item_write_by_its_method(string methodName, string expectedSurface)
        {
            RequestSurfaceFrom(
                    Enum.Parse<RequestMethod>(methodName),
                    new ResourcePathOperation.ById(
                        new DocumentUuid(Guid.Parse("11111111-1111-1111-1111-111111111111"))
                    ),
                    isDescriptor: false
                )
                .Should()
                .Be(expectedSurface);
        }
    }
}
