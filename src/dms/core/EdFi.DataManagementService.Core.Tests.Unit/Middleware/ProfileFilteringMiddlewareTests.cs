// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ProfileFilteringMiddlewareTests
{
    private static ProfileFilteringMiddleware CreateMiddleware(IProfileResponseFilter? filter = null)
    {
        return new ProfileFilteringMiddleware(
            filter ?? new ProfileResponseFilter(),
            NullLogger<ProfileFilteringMiddleware>.Instance
        );
    }

    private static RequestInfo CreateRequestInfo(
        RequestMethod method = RequestMethod.GET,
        string resourceName = "Student"
    )
    {
        var frontendRequest = new FrontendRequest(
            Path: $"/ed-fi/{resourceName.ToLowerInvariant()}s",
            Body: "{}",
            Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            QueryParameters: [],
            TraceId: new TraceId("test-trace-id"),
            RouteQualifiers: []
        );

        var resourceSchema = new ResourceSchema(
            new JsonObject
            {
                ["resourceName"] = resourceName,
                ["identityJsonPaths"] = new JsonArray { "$.studentUniqueId" },
            }
        );

        return new RequestInfo(frontendRequest, method) { ResourceSchema = resourceSchema };
    }

    private static ProfileContext CreateProfileContext(
        MemberSelection memberSelection = MemberSelection.IncludeOnly,
        IReadOnlyList<PropertyRule>? properties = null
    )
    {
        var contentType = new ContentTypeDefinition(
            memberSelection,
            properties ?? [new PropertyRule("firstName")],
            [],
            [],
            []
        );

        var resourceProfile = new ResourceProfile(
            ResourceName: "Student",
            LogicalSchema: null,
            ReadContentType: contentType,
            WriteContentType: null
        );

        return new ProfileContext(
            ProfileName: "TestProfile",
            ContentType: ProfileContentType.Read,
            ResourceProfile: resourceProfile,
            WasExplicitlySpecified: true
        );
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Profile_Context : ProfileFilteringMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;
        private JsonNode? _originalBody;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo();
            _originalBody = new JsonObject
            {
                ["id"] = "12345",
                ["studentUniqueId"] = "STU001",
                ["firstName"] = "John",
                ["lastName"] = "Doe",
            };

            // Simulate handler setting the response
            _requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 200,
                Body: _originalBody,
                Headers: []
            );

            _nextCalled = false;
            var middleware = CreateMiddleware();

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_does_not_modify_response_body()
        {
            _requestInfo.FrontendResponse.Body!["lastName"]?.GetValue<string>().Should().Be("Doe");
            _requestInfo.FrontendResponse.Body["firstName"]?.GetValue<string>().Should().Be("John");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Non_200_Status_Code : ProfileFilteringMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;
        private JsonNode? _originalBody;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo();
            _requestInfo.ProfileContext = CreateProfileContext();
            _originalBody = new JsonObject { ["error"] = "Not found" };

            // Simulate handler setting a 404 response
            _requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 404,
                Body: _originalBody,
                Headers: []
            );

            _nextCalled = false;
            var middleware = CreateMiddleware();

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_does_not_modify_response_body()
        {
            _requestInfo.FrontendResponse.Body!["error"]?.GetValue<string>().Should().Be("Not found");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Profile_Context_With_ReadContentType : ProfileFilteringMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo();
            _requestInfo.ProfileContext = CreateProfileContext(
                MemberSelection.IncludeOnly,
                [new PropertyRule("firstName")]
            );

            var sourceBody = new JsonObject
            {
                ["id"] = "12345",
                ["studentUniqueId"] = "STU001",
                ["firstName"] = "John",
                ["lastName"] = "Doe",
                ["birthDate"] = "2000-01-01",
            };

            // Simulate handler setting the response
            _requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 200,
                Body: sourceBody,
                Headers: []
            );

            _nextCalled = false;
            var middleware = CreateMiddleware();

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_filters_single_document_response()
        {
            _requestInfo.FrontendResponse.Body!["firstName"]?.GetValue<string>().Should().Be("John");
            _requestInfo.FrontendResponse.Body["lastName"].Should().BeNull();
            _requestInfo.FrontendResponse.Body["birthDate"].Should().BeNull();
        }

        [Test]
        public void It_preserves_identity_fields()
        {
            _requestInfo.FrontendResponse.Body!["id"]?.GetValue<string>().Should().Be("12345");
            _requestInfo.FrontendResponse.Body["studentUniqueId"]?.GetValue<string>().Should().Be("STU001");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Array_Response : ProfileFilteringMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo();
            _requestInfo.ProfileContext = CreateProfileContext(
                MemberSelection.IncludeOnly,
                [new PropertyRule("firstName")]
            );

            var sourceBody = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "12345",
                    ["studentUniqueId"] = "STU001",
                    ["firstName"] = "John",
                    ["lastName"] = "Doe",
                },
                new JsonObject
                {
                    ["id"] = "67890",
                    ["studentUniqueId"] = "STU002",
                    ["firstName"] = "Jane",
                    ["lastName"] = "Smith",
                },
            };

            // Simulate handler setting the response
            _requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 200,
                Body: sourceBody,
                Headers: new Dictionary<string, string> { ["Total-Count"] = "2" }
            );

            _nextCalled = false;
            var middleware = CreateMiddleware();

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_filters_all_documents_in_array()
        {
            var resultArray = _requestInfo.FrontendResponse.Body as JsonArray;
            resultArray.Should().NotBeNull();
            resultArray!.Count.Should().Be(2);

            var firstDoc = resultArray[0] as JsonObject;
            firstDoc!["firstName"]?.GetValue<string>().Should().Be("John");
            firstDoc["lastName"].Should().BeNull();

            var secondDoc = resultArray[1] as JsonObject;
            secondDoc!["firstName"]?.GetValue<string>().Should().Be("Jane");
            secondDoc["lastName"].Should().BeNull();
        }

        [Test]
        public void It_preserves_identity_fields_in_all_documents()
        {
            var resultArray = _requestInfo.FrontendResponse.Body as JsonArray;

            var firstDoc = resultArray![0] as JsonObject;
            firstDoc!["id"]?.GetValue<string>().Should().Be("12345");
            firstDoc["studentUniqueId"]?.GetValue<string>().Should().Be("STU001");

            var secondDoc = resultArray[1] as JsonObject;
            secondDoc!["id"]?.GetValue<string>().Should().Be("67890");
            secondDoc["studentUniqueId"]?.GetValue<string>().Should().Be("STU002");
        }

        [Test]
        public void It_preserves_response_headers()
        {
            _requestInfo.FrontendResponse.Headers["Total-Count"].Should().Be("2");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Null_Response_Body : ProfileFilteringMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo();
            _requestInfo.ProfileContext = CreateProfileContext();

            // Simulate handler setting a response with null body
            _requestInfo.FrontendResponse = new FrontendResponse(StatusCode: 200, Body: null, Headers: []);

            _nextCalled = false;
            var middleware = CreateMiddleware();

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_does_not_modify_null_body()
        {
            _requestInfo.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Profile_Context_With_Null_ReadContentType : ProfileFilteringMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;
        private JsonNode? _originalBody;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo();

            // Create profile context with null ReadContentType (write-only profile)
            var resourceProfile = new ResourceProfile(
                ResourceName: "Student",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], [])
            );

            _requestInfo.ProfileContext = new ProfileContext(
                ProfileName: "WriteOnlyProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            _originalBody = new JsonObject
            {
                ["id"] = "12345",
                ["studentUniqueId"] = "STU001",
                ["firstName"] = "John",
                ["lastName"] = "Doe",
            };

            _requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 200,
                Body: _originalBody,
                Headers: []
            );

            _nextCalled = false;
            var middleware = CreateMiddleware();

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_does_not_filter_response()
        {
            _requestInfo.FrontendResponse.Body!["lastName"]?.GetValue<string>().Should().Be("Doe");
        }
    }
}
