// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;
using static EdFi.DataManagementService.Core.UtilityService;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ParsePathMiddlewareTests
{
    internal static IPipelineStep Middleware()
    {
        return new ParsePathMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Empty_Path : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Headers: [],
                Path: "",
                QueryParameters: [],
                TraceId: new TraceId("")
            );
            _requestInfo = new(frontendRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_404()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(404);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Invalid_Path : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Headers: [],
                Path: "badpath",
                QueryParameters: [],
                TraceId: new TraceId("")
            );
            _requestInfo = new(frontendRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_404()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(404);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Path_Without_ResourceId : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Headers: [],
                Path: "/ed-fi/endpointName",
                QueryParameters: [],
                TraceId: new TraceId("")
            );
            _requestInfo = new(frontendRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_provides_no_response()
        {
            _requestInfo?.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_provides_correct_path_components()
        {
            _requestInfo?.PathComponents.Should().NotBe(No.PathComponents);

            _requestInfo?.PathComponents.ProjectEndpointName.Value.Should().Be("ed-fi");
            _requestInfo?.PathComponents.EndpointName.Value.Should().Be("endpointName");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Path_With_Valid_ResourceId : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private readonly string documentUuid = "7825fba8-0b3d-4fc9-ae72-5ad8194d3ce2";

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Headers: [],
                Path: $"/ed-fi/endpointName/{documentUuid}",
                QueryParameters: [],
                TraceId: new TraceId("")
            );
            _requestInfo = new(frontendRequest, RequestMethod.PUT);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_provides_no_response()
        {
            _requestInfo?.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_provides_correct_path_components()
        {
            _requestInfo?.PathComponents.Should().NotBe(No.PathComponents);

            _requestInfo?.PathComponents.ProjectEndpointName.Value.Should().Be("ed-fi");
            _requestInfo?.PathComponents.EndpointName.Value.Should().Be("endpointName");
            _requestInfo?.PathComponents.DocumentUuid.Value.Should().Be(documentUuid);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Path_With_Invalid_ResourceId : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Headers: [],
                Path: "/ed-fi/endpointName/invalidId",
                QueryParameters: [],
                TraceId: new TraceId("")
            );
            _requestInfo = new(frontendRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_invalid_Id_message()
        {
            string response = JsonSerializer.Serialize(_requestInfo.FrontendResponse.Body, SerializerOptions);

            response
                .Should()
                .Contain("\"validationErrors\":{\"$.id\":[\"The value 'invalidId' is not valid.\"]}");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_With_ResourceId : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Headers: [],
                Path: $"/ed-fi/endpointName/{Guid.NewGuid()}",
                QueryParameters: [],
                TraceId: new TraceId("")
            );
            _requestInfo = new(frontendRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_405()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(405);
        }

        [Test]
        public void It_returns_method_not_allowed_message()
        {
            string response = JsonSerializer.Serialize(_requestInfo.FrontendResponse.Body, SerializerOptions);

            response.Should().Contain("Method Not Allowed");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Put_With_Missing_ResourceId : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Headers: [],
                Path: "/ed-fi/endpointName/",
                QueryParameters: [],
                TraceId: new TraceId("")
            );
            _requestInfo = new(frontendRequest, RequestMethod.PUT);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_405()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(405);
        }

        [Test]
        public void It_returns_method_not_allowed_message()
        {
            string response = JsonSerializer.Serialize(_requestInfo.FrontendResponse.Body, SerializerOptions);

            response.Should().Contain("Method Not Allowed");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Delete_With_Missing_ResourceId : ParsePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: "{}",
                Headers: [],
                Path: "/ed-fi/endpointName/",
                QueryParameters: [],
                TraceId: new TraceId("")
            );
            _requestInfo = new(frontendRequest, RequestMethod.DELETE);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_405()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(405);
        }

        [Test]
        public void It_returns_method_not_allowed_message()
        {
            string response = JsonSerializer.Serialize(_requestInfo.FrontendResponse.Body, SerializerOptions);

            response.Should().Contain("Method Not Allowed");
        }
    }
}
