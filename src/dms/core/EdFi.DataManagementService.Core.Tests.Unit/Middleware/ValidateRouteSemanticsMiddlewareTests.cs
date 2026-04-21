// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
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
public class ValidateRouteSemanticsMiddlewareTests
{
    private static IPipelineStep Middleware()
    {
        return new ValidateRouteSemanticsMiddleware(NullLogger.Instance);
    }

    private static PathComponents PathComponents(bool hasDocumentUuidSegment)
    {
        return new(
            ProjectEndpointName: new("ed-fi"),
            EndpointName: new("schools"),
            DocumentUuid: hasDocumentUuidSegment ? new(Guid.NewGuid()) : No.DocumentUuid,
            HasDocumentUuidSegment: hasDocumentUuidSegment
        );
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_To_An_Item_Route : ValidateRouteSemanticsMiddlewareTests
    {
        private readonly RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.Method = RequestMethod.POST;
            _requestInfo.PathComponents = PathComponents(hasDocumentUuidSegment: true);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_405()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(405);
        }

        [Test]
        public void It_returns_the_existing_method_not_allowed_message()
        {
            string response = JsonSerializer.Serialize(_requestInfo.FrontendResponse.Body, SerializerOptions);

            response
                .Should()
                .Contain(
                    "Resource items can only be updated using PUT. To 'upsert' an item in the resource collection using POST, remove the 'id' from the route."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Put_Request_To_A_Collection_Route : ValidateRouteSemanticsMiddlewareTests
    {
        private readonly RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.Method = RequestMethod.PUT;
            _requestInfo.PathComponents = PathComponents(hasDocumentUuidSegment: false);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_405()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(405);
        }

        [Test]
        public void It_returns_the_existing_method_not_allowed_message()
        {
            string response = JsonSerializer.Serialize(_requestInfo.FrontendResponse.Body, SerializerOptions);

            response
                .Should()
                .Contain(
                    "Resource collections cannot be replaced. To 'upsert' an item in the collection, use POST. To update a specific item, use PUT and include the 'id' in the route."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Delete_Request_To_A_Collection_Route : ValidateRouteSemanticsMiddlewareTests
    {
        private readonly RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.Method = RequestMethod.DELETE;
            _requestInfo.PathComponents = PathComponents(hasDocumentUuidSegment: false);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_405()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(405);
        }

        [Test]
        public void It_returns_the_existing_method_not_allowed_message()
        {
            string response = JsonSerializer.Serialize(_requestInfo.FrontendResponse.Body, SerializerOptions);

            response
                .Should()
                .Contain(
                    "Resource collections cannot be deleted. To delete a specific item, use DELETE and include the 'id' in the route."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Write_Request_With_Valid_Route_Semantics : ValidateRouteSemanticsMiddlewareTests
    {
        [Test]
        public async Task It_allows_post_to_collection_routes()
        {
            RequestInfo requestInfo = No.RequestInfo();
            requestInfo.Method = RequestMethod.POST;
            requestInfo.PathComponents = PathComponents(hasDocumentUuidSegment: false);

            await Middleware().Execute(requestInfo, NullNext);

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public async Task It_allows_put_to_item_routes()
        {
            RequestInfo requestInfo = No.RequestInfo();
            requestInfo.Method = RequestMethod.PUT;
            requestInfo.PathComponents = PathComponents(hasDocumentUuidSegment: true);

            await Middleware().Execute(requestInfo, NullNext);

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public async Task It_allows_delete_to_item_routes()
        {
            RequestInfo requestInfo = No.RequestInfo();
            requestInfo.Method = RequestMethod.DELETE;
            requestInfo.PathComponents = PathComponents(hasDocumentUuidSegment: true);

            await Middleware().Execute(requestInfo, NullNext);

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }
}
