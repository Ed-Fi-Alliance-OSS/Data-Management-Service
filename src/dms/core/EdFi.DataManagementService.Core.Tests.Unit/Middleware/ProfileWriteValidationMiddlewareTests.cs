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
public class ProfileWriteValidationMiddlewareTests
{
    private static ProfileWriteValidationMiddleware CreateMiddleware(IProfileResponseFilter? filter = null)
    {
        return new ProfileWriteValidationMiddleware(
            filter ?? new ProfileResponseFilter(),
            NullLogger<ProfileWriteValidationMiddleware>.Instance
        );
    }

    private static RequestInfo CreateRequestInfo(
        RequestMethod method = RequestMethod.POST,
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

    private static ProfileContext CreateWriteProfileContext(
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
            ReadContentType: null,
            WriteContentType: contentType
        );

        return new ProfileContext(
            ProfileName: "TestWriteProfile",
            ContentType: ProfileContentType.Write,
            ResourceProfile: resourceProfile,
            WasExplicitlySpecified: true
        );
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Profile_Context : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo();
            _requestInfo.ParsedBody = new JsonObject
            {
                ["studentUniqueId"] = "STU001",
                ["firstName"] = "John",
                ["lastName"] = "Doe",
            };

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
        public void It_does_not_modify_request_body()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["lastName"]?.GetValue<string>().Should().Be("Doe");
            body["firstName"]?.GetValue<string>().Should().Be("John");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Profile_Context_With_Null_WriteContentType : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo();

            // Create profile context with null WriteContentType (read-only profile)
            var resourceProfile = new ResourceProfile(
                ResourceName: "Student",
                LogicalSchema: null,
                ReadContentType: new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], []),
                WriteContentType: null
            );

            _requestInfo.ProfileContext = new ProfileContext(
                ProfileName: "ReadOnlyProfile",
                ContentType: ProfileContentType.Read,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["studentUniqueId"] = "STU001",
                ["firstName"] = "John",
                ["lastName"] = "Doe",
            };

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
        public void It_does_not_filter_request_body()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["lastName"]?.GetValue<string>().Should().Be("Doe");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Null_Parsed_Body : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo();
            _requestInfo.ProfileContext = CreateWriteProfileContext();
            // ParsedBody is null by default (No.JsonNode)

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
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Profile_Context_With_WriteContentType_IncludeOnly
        : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo();
            _requestInfo.ProfileContext = CreateWriteProfileContext(
                MemberSelection.IncludeOnly,
                [new PropertyRule("firstName")]
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["studentUniqueId"] = "STU001",
                ["firstName"] = "John",
                ["lastName"] = "Doe",
                ["birthDate"] = "2000-01-01",
            };

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
        public void It_strips_excluded_fields()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["lastName"].Should().BeNull();
            body["birthDate"].Should().BeNull();
        }

        [Test]
        public void It_preserves_included_fields()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["firstName"]?.GetValue<string>().Should().Be("John");
        }

        [Test]
        public void It_preserves_identity_fields()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["studentUniqueId"]?.GetValue<string>().Should().Be("STU001");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Profile_Context_With_WriteContentType_ExcludeOnly
        : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo();
            _requestInfo.ProfileContext = CreateWriteProfileContext(
                MemberSelection.ExcludeOnly,
                [new PropertyRule("birthDate"), new PropertyRule("lastName")]
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["studentUniqueId"] = "STU001",
                ["firstName"] = "John",
                ["lastName"] = "Doe",
                ["birthDate"] = "2000-01-01",
            };

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
        public void It_strips_explicitly_excluded_fields()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["lastName"].Should().BeNull();
            body["birthDate"].Should().BeNull();
        }

        [Test]
        public void It_preserves_non_excluded_fields()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["firstName"]?.GetValue<string>().Should().Be("John");
        }

        [Test]
        public void It_preserves_identity_fields()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["studentUniqueId"]?.GetValue<string>().Should().Be("STU001");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Profile_Context_With_WriteContentType_IncludeAll
        : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo();
            _requestInfo.ProfileContext = CreateWriteProfileContext(MemberSelection.IncludeAll, []);

            _requestInfo.ParsedBody = new JsonObject
            {
                ["studentUniqueId"] = "STU001",
                ["firstName"] = "John",
                ["lastName"] = "Doe",
                ["birthDate"] = "2000-01-01",
            };

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
        public void It_preserves_all_fields()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["firstName"]?.GetValue<string>().Should().Be("John");
            body["lastName"]?.GetValue<string>().Should().Be("Doe");
            body["birthDate"]?.GetValue<string>().Should().Be("2000-01-01");
            body["studentUniqueId"]?.GetValue<string>().Should().Be("STU001");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Profile_With_Collection_Item_Filter : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(resourceName: "School");

            // Set up ResourceSchema for School
            _requestInfo.ResourceSchema = new ResourceSchema(
                new JsonObject
                {
                    ["resourceName"] = "School",
                    ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
                }
            );

            // Create a profile with a collection filter on gradeLevels
            var collectionRule = new CollectionRule(
                Name: "gradeLevels",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                NestedCollections: null,
                Extensions: null,
                ItemFilter: new CollectionItemFilter(
                    PropertyName: "gradeLevelDescriptor",
                    FilterMode: FilterMode.IncludeOnly,
                    Values: ["uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"]
                )
            );

            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [],
                [collectionRule],
                []
            );

            var resourceProfile = new ResourceProfile(
                ResourceName: "School",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: contentType
            );

            _requestInfo.ProfileContext = new ProfileContext(
                ProfileName: "GradeLevelFilterProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["schoolId"] = 99000101,
                ["nameOfInstitution"] = "Test School",
                ["gradeLevels"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["gradeLevelDescriptor"] = "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade",
                    },
                    new JsonObject
                    {
                        ["gradeLevelDescriptor"] = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade",
                    },
                    new JsonObject
                    {
                        ["gradeLevelDescriptor"] = "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade",
                    },
                },
            };

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
        public void It_strips_non_matching_collection_items()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            var gradeLevels = body!["gradeLevels"] as JsonArray;

            gradeLevels.Should().NotBeNull();
            gradeLevels!.Count.Should().Be(1);

            var remainingItem = gradeLevels[0] as JsonObject;
            remainingItem!
                ["gradeLevelDescriptor"]
                ?.GetValue<string>()
                .Should()
                .Be("uri://ed-fi.org/GradeLevelDescriptor#Ninth grade");
        }

        [Test]
        public void It_preserves_identity_fields()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["schoolId"]?.GetValue<int>().Should().Be(99000101);
        }

        [Test]
        public void It_preserves_non_collection_fields()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["nameOfInstitution"]?.GetValue<string>().Should().Be("Test School");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_PUT_Request_With_WriteContentType : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(method: RequestMethod.PUT);
            _requestInfo.ProfileContext = CreateWriteProfileContext(
                MemberSelection.IncludeOnly,
                [new PropertyRule("firstName")]
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["id"] = "12345678-1234-1234-1234-123456789012",
                ["studentUniqueId"] = "STU001",
                ["firstName"] = "John",
                ["lastName"] = "Doe",
            };

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
        public void It_strips_excluded_fields_from_put_request()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["lastName"].Should().BeNull();
        }

        [Test]
        public void It_preserves_id_field()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["id"]?.GetValue<string>().Should().Be("12345678-1234-1234-1234-123456789012");
        }
    }
}
