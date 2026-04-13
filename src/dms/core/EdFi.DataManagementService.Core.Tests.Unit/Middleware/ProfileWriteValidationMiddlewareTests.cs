// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Validation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using CoreApiSchemaModel = EdFi.DataManagementService.Core.ApiSchema.Model;
using RelationalWriteSeamFixture = EdFi.DataManagementService.Core.Tests.Unit.Handler.RelationalWriteSeamFixture;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ProfileWriteValidationMiddlewareTests
{
    private static ProfileWriteValidationMiddleware CreateMiddleware(
        IProfileResponseFilter? filter = null,
        IProfileCreatabilityValidator? creatabilityValidator = null,
        ICompiledSchemaCache? schemaCache = null
    )
    {
        return new ProfileWriteValidationMiddleware(
            Options.Create(new AppSettings { AllowIdentityUpdateOverrides = "" }),
            filter ?? new ProfileResponseFilter(),
            creatabilityValidator ?? new ProfileCreatabilityValidator(),
            schemaCache ?? new CompiledSchemaCache(),
            NullLogger<ProfileWriteValidationMiddleware>.Instance
        );
    }

    private static IServiceProvider BuildScopedServiceProvider(
        IDocumentStoreRepository? documentStoreRepository = null
    )
    {
        var services = new ServiceCollection();

        if (documentStoreRepository is not null)
        {
            services.AddSingleton(documentStoreRepository);
        }
        else
        {
            services.AddSingleton<IDocumentStoreRepository>(new StubDocumentStoreRepository());
        }

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Stub repository that returns not found for all get requests.
    /// Used for POST tests where the merge logic won't be triggered.
    /// </summary>
    private sealed class StubDocumentStoreRepository : IDocumentStoreRepository
    {
        public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest) =>
            throw new NotImplementedException();

        public Task<GetResult> GetDocumentById(IGetRequest getRequest) =>
            Task.FromResult<GetResult>(new GetResult.GetFailureNotExists());

        public Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest) =>
            throw new NotImplementedException();

        public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest) =>
            throw new NotImplementedException();
    }

    private static RequestInfo CreateRequestInfo(
        RequestMethod method = RequestMethod.POST,
        string resourceName = "Student",
        string[]? requiredFields = null,
        IServiceProvider? scopedServiceProvider = null
    )
    {
        var frontendRequest = new FrontendRequest(
            Path: $"/ed-fi/{resourceName.ToLowerInvariant()}s",
            Body: "{}",
            Form: null,
            Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            QueryParameters: [],
            TraceId: new TraceId("test-trace-id"),
            RouteQualifiers: []
        );

        // Build jsonSchemaForInsert with required array
        var jsonSchemaForInsert = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

        if (requiredFields != null && requiredFields.Length > 0)
        {
            var requiredArray = new JsonArray();
            foreach (var field in requiredFields)
            {
                requiredArray.Add(field);
            }
            jsonSchemaForInsert["required"] = requiredArray;
        }

        var resourceSchema = new ResourceSchema(
            new JsonObject
            {
                ["resourceName"] = resourceName,
                ["isDescriptor"] = false,
                ["identityJsonPaths"] = new JsonArray { "$.studentUniqueId" },
                ["jsonSchemaForInsert"] = jsonSchemaForInsert,
            }
        );

        var projectSchema = new ProjectSchema(
            new JsonObject { ["projectName"] = "Ed-Fi" },
            NullLogger<ProjectSchema>.Instance
        );

        return new RequestInfo(frontendRequest, method, scopedServiceProvider ?? BuildScopedServiceProvider())
        {
            ProjectSchema = projectSchema,
            ResourceSchema = resourceSchema,
        };
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
                    ["isDescriptor"] = false,
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

    [TestFixture]
    [Parallelizable]
    public class Given_POST_With_Profile_Excluding_Required_Field : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            // Create request with required fields including "lastName"
            _requestInfo = CreateRequestInfo(
                method: RequestMethod.POST,
                resourceName: "Student",
                requiredFields: ["studentUniqueId", "firstName", "lastName"]
            );

            // Profile that excludes lastName (a required field)
            _requestInfo.ProfileContext = CreateWriteProfileContext(
                MemberSelection.IncludeOnly,
                [new PropertyRule("firstName")]
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
        public void It_does_not_call_next()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_400_status()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_data_policy_enforced_error_type()
        {
            var body = _requestInfo.FrontendResponse.Body as JsonObject;
            body!["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:data-policy-enforced");
        }

        [Test]
        public void It_returns_error_message_with_profile_name()
        {
            var body = _requestInfo.FrontendResponse.Body as JsonObject;
            var errors = body!["errors"] as JsonArray;
            errors.Should().NotBeNull();
            errors!.Count.Should().Be(1);
            errors[0]?.GetValue<string>().Should().Contain("TestWriteProfile");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_PUT_With_Profile_Excluding_Required_Field : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            // Create request with required fields including "lastName"
            _requestInfo = CreateRequestInfo(
                method: RequestMethod.PUT,
                resourceName: "Student",
                requiredFields: ["studentUniqueId", "firstName", "lastName"]
            );

            // Profile that excludes lastName (a required field)
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
            // PUT requests skip the creatability check
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_strips_excluded_fields()
        {
            // Normal filtering still applies
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["lastName"].Should().BeNull();
        }

        [Test]
        public void It_preserves_allowed_fields()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["firstName"]?.GetValue<string>().Should().Be("John");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_POST_With_NonCreatable_Child_Collection_Item : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(method: RequestMethod.POST, resourceName: "School");

            var collectionRule = new CollectionRule(
                Name: "internationalAddresses",
                MemberSelection: MemberSelection.ExcludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("addressLine1")],
                NestedObjects: null,
                NestedCollections: null,
                Extensions: null,
                ItemFilter: null
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
                ProfileName: "NonCreatableCollectionProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["schoolId"] = 99000101,
                ["nameOfInstitution"] = "Non-creatable collection test",
                ["internationalAddresses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                        ["countryDescriptor"] = "uri://ed-fi.org/CountryDescriptor#US",
                        ["addressLine1"] = "Blocked Address Line",
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
        public void It_returns_400_and_blocks_the_request()
        {
            _nextCalled.Should().BeFalse();
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_POST_With_NonCreatable_Embedded_Object : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(method: RequestMethod.POST, resourceName: "Assessment");

            var objectRule = new ObjectRule(
                Name: "contentStandard",
                MemberSelection: MemberSelection.ExcludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("title")],
                NestedObjects: null,
                Collections: null,
                Extensions: null
            );

            var contentType = new ContentTypeDefinition(MemberSelection.IncludeAll, [], [objectRule], [], []);

            var resourceProfile = new ResourceProfile(
                ResourceName: "Assessment",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: contentType
            );

            _requestInfo.ProfileContext = new ProfileContext(
                ProfileName: "NonCreatableObjectProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["assessmentIdentifier"] = "ASSESSMENT-001",
                ["namespace"] = "uri://ed-fi.org/Assessment/Assessment.xml",
                ["assessmentCategoryDescriptor"] =
                    "uri://ed-fi.org/AssessmentCategoryDescriptor#Benchmark test",
                ["assessmentTitle"] = "Non-creatable object test",
                ["contentStandard"] = new JsonObject { ["title"] = "Blocked title" },
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
        public void It_returns_400_and_blocks_the_request()
        {
            _nextCalled.Should().BeFalse();
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_PUT_With_NonCreatable_Embedded_Object_And_No_Existing_Object
        : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        private sealed class AssessmentWithoutContentStandardRepository : IDocumentStoreRepository
        {
            public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest) =>
                throw new NotImplementedException();

            public Task<GetResult> GetDocumentById(IGetRequest getRequest) =>
                Task.FromResult<GetResult>(
                    new GetResult.GetSuccess(
                        DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012")),
                        EdfiDoc: new JsonObject
                        {
                            ["id"] = "12345678-1234-1234-1234-123456789012",
                            ["assessmentIdentifier"] = "ASSESSMENT-003",
                            ["namespace"] = "uri://ed-fi.org/Assessment/Assessment.xml",
                            ["assessmentCategoryDescriptor"] =
                                "uri://ed-fi.org/AssessmentCategoryDescriptor#Benchmark test",
                            ["assessmentTitle"] = "Existing Assessment",
                        },
                        LastModifiedDate: DateTime.UtcNow,
                        LastModifiedTraceId: "existing-trace-id"
                    )
                );

            public Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest) =>
                throw new NotImplementedException();

            public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest) =>
                throw new NotImplementedException();
        }

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(method: RequestMethod.PUT, resourceName: "Assessment");
            _requestInfo.PathComponents = new PathComponents(
                ProjectEndpointName: new CoreApiSchemaModel.ProjectEndpointName("ed-fi"),
                EndpointName: new CoreApiSchemaModel.EndpointName("assessments"),
                DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012"))
            );

            var objectRule = new ObjectRule(
                Name: "contentStandard",
                MemberSelection: MemberSelection.ExcludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("title")],
                NestedObjects: null,
                Collections: null,
                Extensions: null
            );

            var contentType = new ContentTypeDefinition(MemberSelection.IncludeAll, [], [objectRule], [], []);

            var resourceProfile = new ResourceProfile(
                ResourceName: "Assessment",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: contentType
            );

            _requestInfo.ProfileContext = new ProfileContext(
                ProfileName: "NonCreatableObjectProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["id"] = "12345678-1234-1234-1234-123456789012",
                ["assessmentIdentifier"] = "ASSESSMENT-003",
                ["namespace"] = "uri://ed-fi.org/Assessment/Assessment.xml",
                ["assessmentCategoryDescriptor"] =
                    "uri://ed-fi.org/AssessmentCategoryDescriptor#Benchmark test",
                ["assessmentTitle"] = "Updated Assessment",
                ["contentStandard"] = new JsonObject { ["title"] = "Blocked title" },
            };

            _nextCalled = false;
            _requestInfo.ScopedServiceProvider = BuildScopedServiceProvider(
                new AssessmentWithoutContentStandardRepository()
            );
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
        public void It_returns_400_and_blocks_the_request()
        {
            _nextCalled.Should().BeFalse();
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_PUT_With_NonCreatable_Embedded_Object_And_Existing_Object
        : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        private sealed class AssessmentWithContentStandardRepository : IDocumentStoreRepository
        {
            public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest) =>
                throw new NotImplementedException();

            public Task<GetResult> GetDocumentById(IGetRequest getRequest) =>
                Task.FromResult<GetResult>(
                    new GetResult.GetSuccess(
                        DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012")),
                        EdfiDoc: new JsonObject
                        {
                            ["id"] = "12345678-1234-1234-1234-123456789012",
                            ["assessmentIdentifier"] = "ASSESSMENT-004",
                            ["namespace"] = "uri://ed-fi.org/Assessment/Assessment.xml",
                            ["assessmentCategoryDescriptor"] =
                                "uri://ed-fi.org/AssessmentCategoryDescriptor#Benchmark test",
                            ["assessmentTitle"] = "Existing Assessment",
                            ["contentStandard"] = new JsonObject { ["title"] = "Existing title" },
                        },
                        LastModifiedDate: DateTime.UtcNow,
                        LastModifiedTraceId: "existing-trace-id"
                    )
                );

            public Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest) =>
                throw new NotImplementedException();

            public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest) =>
                throw new NotImplementedException();
        }

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(method: RequestMethod.PUT, resourceName: "Assessment");
            _requestInfo.PathComponents = new PathComponents(
                ProjectEndpointName: new CoreApiSchemaModel.ProjectEndpointName("ed-fi"),
                EndpointName: new CoreApiSchemaModel.EndpointName("assessments"),
                DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012"))
            );

            var objectRule = new ObjectRule(
                Name: "contentStandard",
                MemberSelection: MemberSelection.ExcludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("title")],
                NestedObjects: null,
                Collections: null,
                Extensions: null
            );

            var contentType = new ContentTypeDefinition(MemberSelection.IncludeAll, [], [objectRule], [], []);

            var resourceProfile = new ResourceProfile(
                ResourceName: "Assessment",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: contentType
            );

            _requestInfo.ProfileContext = new ProfileContext(
                ProfileName: "NonCreatableObjectProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["id"] = "12345678-1234-1234-1234-123456789012",
                ["assessmentIdentifier"] = "ASSESSMENT-004",
                ["namespace"] = "uri://ed-fi.org/Assessment/Assessment.xml",
                ["assessmentCategoryDescriptor"] =
                    "uri://ed-fi.org/AssessmentCategoryDescriptor#Benchmark test",
                ["assessmentTitle"] = "Updated Assessment",
                ["contentStandard"] = new JsonObject { ["title"] = "Blocked title" },
            };

            _nextCalled = false;
            _requestInfo.ScopedServiceProvider = BuildScopedServiceProvider(
                new AssessmentWithContentStandardRepository()
            );
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
        public void It_preserves_the_existing_embedded_object_value()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            var contentStandard = body!["contentStandard"] as JsonObject;
            contentStandard!["title"]?.GetValue<string>().Should().Be("Existing title");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_PUT_With_NonCreatable_Child_Collection_Item_And_New_Item
        : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        private sealed class SchoolWithoutInternationalAddressRepository : IDocumentStoreRepository
        {
            public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest) =>
                throw new NotImplementedException();

            public Task<GetResult> GetDocumentById(IGetRequest getRequest) =>
                Task.FromResult<GetResult>(
                    new GetResult.GetSuccess(
                        DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012")),
                        EdfiDoc: new JsonObject
                        {
                            ["id"] = "12345678-1234-1234-1234-123456789012",
                            ["schoolId"] = 99000103,
                            ["nameOfInstitution"] = "Existing School",
                        },
                        LastModifiedDate: DateTime.UtcNow,
                        LastModifiedTraceId: "existing-trace-id"
                    )
                );

            public Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest) =>
                throw new NotImplementedException();

            public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest) =>
                throw new NotImplementedException();
        }

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(method: RequestMethod.PUT, resourceName: "School");
            _requestInfo.PathComponents = new PathComponents(
                ProjectEndpointName: new CoreApiSchemaModel.ProjectEndpointName("ed-fi"),
                EndpointName: new CoreApiSchemaModel.EndpointName("schools"),
                DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012"))
            );

            var collectionRule = new CollectionRule(
                Name: "internationalAddresses",
                MemberSelection: MemberSelection.ExcludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("addressLine1")],
                NestedObjects: null,
                NestedCollections: null,
                Extensions: null,
                ItemFilter: null
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
                ProfileName: "NonCreatableCollectionProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["id"] = "12345678-1234-1234-1234-123456789012",
                ["schoolId"] = 99000103,
                ["nameOfInstitution"] = "Updated School",
                ["internationalAddresses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                        ["countryDescriptor"] = "uri://ed-fi.org/CountryDescriptor#AD",
                        ["addressLine1"] = "Blocked Address Line",
                    },
                },
            };

            _nextCalled = false;
            _requestInfo.ScopedServiceProvider = BuildScopedServiceProvider(
                new SchoolWithoutInternationalAddressRepository()
            );
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
        public void It_returns_400_and_blocks_the_request()
        {
            _nextCalled.Should().BeFalse();
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_PUT_With_NonCreatable_Child_Collection_Item_And_Existing_Item
        : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        private sealed class SchoolWithInternationalAddressRepository : IDocumentStoreRepository
        {
            public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest) =>
                throw new NotImplementedException();

            public Task<GetResult> GetDocumentById(IGetRequest getRequest) =>
                Task.FromResult<GetResult>(
                    new GetResult.GetSuccess(
                        DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012")),
                        EdfiDoc: new JsonObject
                        {
                            ["id"] = "12345678-1234-1234-1234-123456789012",
                            ["schoolId"] = 99000104,
                            ["nameOfInstitution"] = "Existing School",
                            ["internationalAddresses"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["addressTypeDescriptor"] =
                                        "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                                    ["countryDescriptor"] = "uri://ed-fi.org/CountryDescriptor#AD",
                                    ["addressLine1"] = "Existing Address Line",
                                },
                            },
                        },
                        LastModifiedDate: DateTime.UtcNow,
                        LastModifiedTraceId: "existing-trace-id"
                    )
                );

            public Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest) =>
                throw new NotImplementedException();

            public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest) =>
                throw new NotImplementedException();
        }

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(method: RequestMethod.PUT, resourceName: "School");
            _requestInfo.PathComponents = new PathComponents(
                ProjectEndpointName: new CoreApiSchemaModel.ProjectEndpointName("ed-fi"),
                EndpointName: new CoreApiSchemaModel.EndpointName("schools"),
                DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012"))
            );

            var collectionRule = new CollectionRule(
                Name: "internationalAddresses",
                MemberSelection: MemberSelection.ExcludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("addressLine1")],
                NestedObjects: null,
                NestedCollections: null,
                Extensions: null,
                ItemFilter: null
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
                ProfileName: "NonCreatableCollectionProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["id"] = "12345678-1234-1234-1234-123456789012",
                ["schoolId"] = 99000104,
                ["nameOfInstitution"] = "Updated School",
                ["internationalAddresses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                        ["countryDescriptor"] = "uri://ed-fi.org/CountryDescriptor#AD",
                        ["addressLine1"] = "Blocked Address Line",
                    },
                },
            };

            _nextCalled = false;
            _requestInfo.ScopedServiceProvider = BuildScopedServiceProvider(
                new SchoolWithInternationalAddressRepository()
            );
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
        public void It_preserves_the_existing_collection_item_value()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            var internationalAddresses = body!["internationalAddresses"] as JsonArray;
            var address = internationalAddresses![0] as JsonObject;
            address!["addressLine1"]?.GetValue<string>().Should().Be("Existing Address Line");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_POST_With_NonCreatable_Child_Collection_Item_Using_Legacy_Casing
        : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(method: RequestMethod.POST, resourceName: "School");

            var collectionRule = new CollectionRule(
                Name: "InternationalAddresses",
                MemberSelection: MemberSelection.ExcludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("AddressLine1")],
                NestedObjects: null,
                NestedCollections: null,
                Extensions: null,
                ItemFilter: null
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
                ProfileName: "NonCreatableCollectionLegacyCaseProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["schoolId"] = 99000102,
                ["nameOfInstitution"] = "Legacy casing collection test",
                ["internationalAddresses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                        ["countryDescriptor"] = "uri://ed-fi.org/CountryDescriptor#US",
                        ["addressLine1"] = "Blocked Address Line",
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
        public void It_returns_400_and_blocks_the_request()
        {
            _nextCalled.Should().BeFalse();
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_POST_With_NonCreatable_Embedded_Object_Using_Legacy_Casing
        : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(method: RequestMethod.POST, resourceName: "Assessment");

            var objectRule = new ObjectRule(
                Name: "ContentStandard",
                MemberSelection: MemberSelection.ExcludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("Title")],
                NestedObjects: null,
                Collections: null,
                Extensions: null
            );

            var contentType = new ContentTypeDefinition(MemberSelection.IncludeAll, [], [objectRule], [], []);

            var resourceProfile = new ResourceProfile(
                ResourceName: "Assessment",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: contentType
            );

            _requestInfo.ProfileContext = new ProfileContext(
                ProfileName: "NonCreatableObjectLegacyCaseProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["assessmentIdentifier"] = "ASSESSMENT-002",
                ["namespace"] = "uri://ed-fi.org/Assessment/Assessment.xml",
                ["assessmentCategoryDescriptor"] =
                    "uri://ed-fi.org/AssessmentCategoryDescriptor#Benchmark test",
                ["assessmentTitle"] = "Legacy casing object test",
                ["contentStandard"] = new JsonObject { ["title"] = "Blocked title" },
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
        public void It_returns_400_and_blocks_the_request()
        {
            _nextCalled.Should().BeFalse();
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_PUT_With_Nested_Object_Profile_Excluding_Property
        : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        /// <summary>
        /// Repository that returns an existing document with an address object
        /// containing both street and city properties.
        /// </summary>
        private sealed class AddressRepository : IDocumentStoreRepository
        {
            public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest) =>
                throw new NotImplementedException();

            public Task<GetResult> GetDocumentById(IGetRequest getRequest) =>
                Task.FromResult<GetResult>(
                    new GetResult.GetSuccess(
                        DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012")),
                        EdfiDoc: new JsonObject
                        {
                            ["id"] = "12345678-1234-1234-1234-123456789012",
                            ["studentUniqueId"] = "STU001",
                            ["address"] = new JsonObject
                            {
                                ["streetAddress"] = "123 Main St",
                                ["city"] = "Springfield",
                            },
                        },
                        LastModifiedDate: DateTime.UtcNow,
                        LastModifiedTraceId: "existing-trace-id"
                    )
                );

            public Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest) =>
                throw new NotImplementedException();

            public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest) =>
                throw new NotImplementedException();
        }

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(method: RequestMethod.PUT);
            _requestInfo.PathComponents = new PathComponents(
                ProjectEndpointName: new CoreApiSchemaModel.ProjectEndpointName("ed-fi"),
                EndpointName: new CoreApiSchemaModel.EndpointName("students"),
                DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012"))
            );

            // Profile with nested object rule that only includes streetAddress (excludes city)
            var addressObjectRule = new ObjectRule(
                Name: "address",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("streetAddress")],
                NestedObjects: null,
                Collections: null,
                Extensions: null
            );

            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [addressObjectRule],
                [],
                []
            );

            var resourceProfile = new ResourceProfile(
                ResourceName: "Student",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: contentType
            );

            _requestInfo.ProfileContext = new ProfileContext(
                ProfileName: "AddressStreetOnlyProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["id"] = "12345678-1234-1234-1234-123456789012",
                ["studentUniqueId"] = "STU001",
                ["address"] = new JsonObject { ["streetAddress"] = "456 Oak Ave", ["city"] = "New City" },
            };

            _nextCalled = false;
            _requestInfo.ScopedServiceProvider = BuildScopedServiceProvider(new AddressRepository());
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
        public void It_preserves_included_nested_property()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            var address = body!["address"] as JsonObject;
            address!["streetAddress"]?.GetValue<string>().Should().Be("456 Oak Ave");
        }

        [Test]
        public void It_merges_excluded_nested_property_from_existing_document()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            var address = body!["address"] as JsonObject;
            // City was excluded by profile, so should be merged from existing doc
            address!["city"]?.GetValue<string>().Should().Be("Springfield");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_PUT_With_Collection_Profile_Excluding_Item_Property
        : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        /// <summary>
        /// Repository that returns an existing document with telephone numbers
        /// containing both telephoneNumber and textMessageCapabilityIndicator properties.
        /// </summary>
        private sealed class TelephoneRepository : IDocumentStoreRepository
        {
            public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest) =>
                throw new NotImplementedException();

            public Task<GetResult> GetDocumentById(IGetRequest getRequest) =>
                Task.FromResult<GetResult>(
                    new GetResult.GetSuccess(
                        DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012")),
                        EdfiDoc: new JsonObject
                        {
                            ["id"] = "12345678-1234-1234-1234-123456789012",
                            ["studentUniqueId"] = "STU001",
                            ["telephones"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["telephoneNumber"] = "555-1234",
                                    ["telephoneNumberTypeDescriptor"] =
                                        "uri://ed-fi.org/TelephoneNumberTypeDescriptor#Home",
                                    ["textMessageCapabilityIndicator"] = true,
                                },
                            },
                        },
                        LastModifiedDate: DateTime.UtcNow,
                        LastModifiedTraceId: "existing-trace-id"
                    )
                );

            public Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest) =>
                throw new NotImplementedException();

            public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest) =>
                throw new NotImplementedException();
        }

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(method: RequestMethod.PUT);
            _requestInfo.PathComponents = new PathComponents(
                ProjectEndpointName: new CoreApiSchemaModel.ProjectEndpointName("ed-fi"),
                EndpointName: new CoreApiSchemaModel.EndpointName("students"),
                DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012"))
            );

            // Profile with collection rule that only includes telephoneNumber
            var telephonesCollectionRule = new CollectionRule(
                Name: "telephones",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties:
                [
                    new PropertyRule("telephoneNumber"),
                    new PropertyRule("telephoneNumberTypeDescriptor"),
                ],
                NestedObjects: null,
                NestedCollections: null,
                Extensions: null,
                ItemFilter: null
            );

            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [],
                [telephonesCollectionRule],
                []
            );

            var resourceProfile = new ResourceProfile(
                ResourceName: "Student",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: contentType
            );

            _requestInfo.ProfileContext = new ProfileContext(
                ProfileName: "TelephoneNumberOnlyProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["id"] = "12345678-1234-1234-1234-123456789012",
                ["studentUniqueId"] = "STU001",
                ["telephones"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["telephoneNumber"] = "555-9999",
                        ["telephoneNumberTypeDescriptor"] =
                            "uri://ed-fi.org/TelephoneNumberTypeDescriptor#Home",
                        ["textMessageCapabilityIndicator"] = false,
                    },
                },
            };

            _nextCalled = false;
            _requestInfo.ScopedServiceProvider = BuildScopedServiceProvider(new TelephoneRepository());
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
        public void It_preserves_included_collection_item_property()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            var telephones = body!["telephones"] as JsonArray;
            var phone = telephones![0] as JsonObject;
            phone!["telephoneNumber"]?.GetValue<string>().Should().Be("555-9999");
        }

        [Test]
        public void It_merges_excluded_collection_item_property_from_existing_document()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            var telephones = body!["telephones"] as JsonArray;
            var phone = telephones![0] as JsonObject;
            // textMessageCapabilityIndicator was excluded by profile, so should be merged from existing doc
            phone!["textMessageCapabilityIndicator"]?.GetValue<bool>().Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_PUT_With_Collection_ItemFilter_Preserves_Filtered_Items
        : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        /// <summary>
        /// Repository that returns an existing document with multiple grade levels,
        /// some of which won't pass the ItemFilter.
        /// </summary>
        private sealed class GradeLevelRepository : IDocumentStoreRepository
        {
            public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest) =>
                throw new NotImplementedException();

            public Task<GetResult> GetDocumentById(IGetRequest getRequest) =>
                Task.FromResult<GetResult>(
                    new GetResult.GetSuccess(
                        DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012")),
                        EdfiDoc: new JsonObject
                        {
                            ["id"] = "12345678-1234-1234-1234-123456789012",
                            ["schoolId"] = 99000101,
                            ["nameOfInstitution"] = "Test School",
                            ["gradeLevels"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["gradeLevelDescriptor"] =
                                        "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade",
                                },
                                new JsonObject
                                {
                                    ["gradeLevelDescriptor"] =
                                        "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade",
                                },
                                new JsonObject
                                {
                                    ["gradeLevelDescriptor"] =
                                        "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade",
                                },
                            },
                        },
                        LastModifiedDate: DateTime.UtcNow,
                        LastModifiedTraceId: "existing-trace-id"
                    )
                );

            public Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest) =>
                throw new NotImplementedException();

            public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest) =>
                throw new NotImplementedException();
        }

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(method: RequestMethod.PUT, resourceName: "School");
            _requestInfo.PathComponents = new PathComponents(
                ProjectEndpointName: new CoreApiSchemaModel.ProjectEndpointName("ed-fi"),
                EndpointName: new CoreApiSchemaModel.EndpointName("schools"),
                DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012"))
            );

            _requestInfo.ResourceSchema = new ResourceSchema(
                new JsonObject
                {
                    ["resourceName"] = "School",
                    ["isDescriptor"] = false,
                    ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
                }
            );

            // Profile with ItemFilter that only allows modifying 9th grade
            var gradeLevelsCollectionRule = new CollectionRule(
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
                [gradeLevelsCollectionRule],
                []
            );

            var resourceProfile = new ResourceProfile(
                ResourceName: "School",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: contentType
            );

            _requestInfo.ProfileContext = new ProfileContext(
                ProfileName: "NinthGradeOnlyProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            // Client sends only 9th grade (the only one they can modify)
            _requestInfo.ParsedBody = new JsonObject
            {
                ["id"] = "12345678-1234-1234-1234-123456789012",
                ["schoolId"] = 99000101,
                ["nameOfInstitution"] = "Test School",
                ["gradeLevels"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["gradeLevelDescriptor"] = "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade",
                    },
                },
            };

            _nextCalled = false;
            _requestInfo.ScopedServiceProvider = BuildScopedServiceProvider(new GradeLevelRepository());
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
        public void It_preserves_items_that_client_can_modify()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            var gradeLevels = body!["gradeLevels"] as JsonArray;
            gradeLevels!
                .Any(g =>
                    (g as JsonObject)?["gradeLevelDescriptor"]?.GetValue<string>()
                    == "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                )
                .Should()
                .BeTrue();
        }

        [Test]
        public void It_merges_back_items_filtered_out_by_ItemFilter()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            var gradeLevels = body!["gradeLevels"] as JsonArray;

            // Should have 3 items: 9th (from request) + 10th and 11th (merged from existing)
            gradeLevels!.Count.Should().Be(3);
        }

        [Test]
        public void It_includes_tenth_grade_from_existing_document()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            var gradeLevels = body!["gradeLevels"] as JsonArray;
            gradeLevels!
                .Any(g =>
                    (g as JsonObject)?["gradeLevelDescriptor"]?.GetValue<string>()
                    == "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                )
                .Should()
                .BeTrue();
        }

        [Test]
        public void It_includes_eleventh_grade_from_existing_document()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            var gradeLevels = body!["gradeLevels"] as JsonArray;
            gradeLevels!
                .Any(g =>
                    (g as JsonObject)?["gradeLevelDescriptor"]?.GetValue<string>()
                    == "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade"
                )
                .Should()
                .BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_PUT_With_Deeply_Nested_Merging : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        /// <summary>
        /// Repository that returns an existing document with deeply nested structure:
        /// addresses collection → periods collection → nested object with excluded properties
        /// </summary>
        private sealed class DeepNestedRepository : IDocumentStoreRepository
        {
            public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest) =>
                throw new NotImplementedException();

            public Task<GetResult> GetDocumentById(IGetRequest getRequest) =>
                Task.FromResult<GetResult>(
                    new GetResult.GetSuccess(
                        DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012")),
                        EdfiDoc: new JsonObject
                        {
                            ["id"] = "12345678-1234-1234-1234-123456789012",
                            ["studentUniqueId"] = "STU001",
                            ["addresses"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Home",
                                    ["streetAddress"] = "123 Main St",
                                    ["city"] = "Springfield",
                                    ["periods"] = new JsonArray
                                    {
                                        new JsonObject
                                        {
                                            ["beginDate"] = "2024-01-01",
                                            ["endDate"] = "2024-12-31",
                                            ["secretData"] = "should-be-preserved",
                                        },
                                    },
                                },
                            },
                        },
                        LastModifiedDate: DateTime.UtcNow,
                        LastModifiedTraceId: "existing-trace-id"
                    )
                );

            public Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest) =>
                throw new NotImplementedException();

            public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest) =>
                throw new NotImplementedException();
        }

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(method: RequestMethod.PUT);
            _requestInfo.PathComponents = new PathComponents(
                ProjectEndpointName: new CoreApiSchemaModel.ProjectEndpointName("ed-fi"),
                EndpointName: new CoreApiSchemaModel.EndpointName("students"),
                DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012"))
            );

            // Profile with nested collection → nested collection → excluded property
            var periodsCollectionRule = new CollectionRule(
                Name: "periods",
                MemberSelection: MemberSelection.IncludeOnly,
                LogicalSchema: null,
                Properties: [new PropertyRule("beginDate"), new PropertyRule("endDate")],
                NestedObjects: null,
                NestedCollections: null,
                Extensions: null,
                ItemFilter: null
            );

            var addressesCollectionRule = new CollectionRule(
                Name: "addresses",
                MemberSelection: MemberSelection.IncludeAll,
                LogicalSchema: null,
                Properties: null,
                NestedObjects: null,
                NestedCollections: [periodsCollectionRule],
                Extensions: null,
                ItemFilter: null
            );

            var contentType = new ContentTypeDefinition(
                MemberSelection.IncludeAll,
                [],
                [],
                [addressesCollectionRule],
                []
            );

            var resourceProfile = new ResourceProfile(
                ResourceName: "Student",
                LogicalSchema: null,
                ReadContentType: null,
                WriteContentType: contentType
            );

            _requestInfo.ProfileContext = new ProfileContext(
                ProfileName: "DeepNestedProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: resourceProfile,
                WasExplicitlySpecified: true
            );

            _requestInfo.ParsedBody = new JsonObject
            {
                ["id"] = "12345678-1234-1234-1234-123456789012",
                ["studentUniqueId"] = "STU001",
                ["addresses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = "uri://ed-fi.org/AddressTypeDescriptor#Home",
                        ["streetAddress"] = "456 New St",
                        ["city"] = "New City",
                        ["periods"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["beginDate"] = "2024-01-01",
                                ["endDate"] = "2025-06-30",
                                ["secretData"] = "this-should-be-stripped",
                            },
                        },
                    },
                },
            };

            _nextCalled = false;
            _requestInfo.ScopedServiceProvider = BuildScopedServiceProvider(new DeepNestedRepository());
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
        public void It_preserves_included_properties_at_deep_level()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            var addresses = body!["addresses"] as JsonArray;
            var address = addresses![0] as JsonObject;
            var periods = address!["periods"] as JsonArray;
            var period = periods![0] as JsonObject;

            period!["beginDate"]?.GetValue<string>().Should().Be("2024-01-01");
            period["endDate"]?.GetValue<string>().Should().Be("2025-06-30");
        }

        [Test]
        public void It_merges_excluded_property_at_deep_level_from_existing_document()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            var addresses = body!["addresses"] as JsonArray;
            var address = addresses![0] as JsonObject;
            var periods = address!["periods"] as JsonArray;
            var period = periods![0] as JsonObject;

            // secretData was excluded by profile, so should be merged from existing doc
            period!["secretData"]?.GetValue<string>().Should().Be("should-be-preserved");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_POST_With_Profile_Including_All_Required_Fields : ProfileWriteValidationMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            // Create request with required fields
            _requestInfo = CreateRequestInfo(
                method: RequestMethod.POST,
                resourceName: "Student",
                requiredFields: ["studentUniqueId", "firstName"]
            );

            // Profile that includes all required fields
            _requestInfo.ProfileContext = CreateWriteProfileContext(
                MemberSelection.IncludeOnly,
                [new PropertyRule("firstName")]
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
        public void It_performs_normal_filtering()
        {
            var body = _requestInfo.ParsedBody as JsonObject;
            body!["lastName"].Should().BeNull();
            body["firstName"]?.GetValue<string>().Should().Be("John");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_PUT_Merge_Loading_The_Existing_Document : ProfileWriteValidationMiddlewareTests
    {
        private sealed class CapturingRepository : IDocumentStoreRepository
        {
            public IRelationalGetRequest? CapturedRequest { get; private set; }

            public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest) =>
                throw new NotImplementedException();

            public Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                CapturedRequest = getRequest as IRelationalGetRequest;

                return Task.FromResult<GetResult>(
                    new GetResult.GetSuccess(
                        DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012")),
                        EdfiDoc: new JsonObject
                        {
                            ["id"] = "12345678-1234-1234-1234-123456789012",
                            ["studentUniqueId"] = "STU001",
                            ["firstName"] = "Existing",
                            ["lastName"] = "Preserved",
                        },
                        LastModifiedDate: DateTime.UtcNow,
                        LastModifiedTraceId: "existing-trace-id"
                    )
                );
            }

            public Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest) =>
                throw new NotImplementedException();

            public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest) =>
                throw new NotImplementedException();
        }

        private readonly CapturingRepository _repository = new();
        private readonly MappingSet _mappingSet = RelationalWriteSeamFixture
            .Create()
            .CreateSupportedMappingSet(SqlDialect.Pgsql);
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(
                method: RequestMethod.PUT,
                scopedServiceProvider: BuildScopedServiceProvider(_repository)
            );
            _requestInfo.PathComponents = new PathComponents(
                ProjectEndpointName: new CoreApiSchemaModel.ProjectEndpointName("ed-fi"),
                EndpointName: new CoreApiSchemaModel.EndpointName("students"),
                DocumentUuid: new DocumentUuid(Guid.Parse("12345678-1234-1234-1234-123456789012"))
            );
            _requestInfo.MappingSet = _mappingSet;
            _requestInfo.ProfileContext = CreateWriteProfileContext(
                MemberSelection.IncludeOnly,
                [new PropertyRule("firstName")]
            );
            _requestInfo.ParsedBody = new JsonObject
            {
                ["id"] = "12345678-1234-1234-1234-123456789012",
                ["studentUniqueId"] = "STU001",
                ["firstName"] = "Updated",
                ["lastName"] = "ShouldBeMergedBack",
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
        public void It_loads_the_existing_document_using_the_stored_document_read_mode()
        {
            _nextCalled.Should().BeTrue();
            _repository.CapturedRequest.Should().NotBeNull();
            _repository.CapturedRequest!.MappingSet.Should().BeSameAs(_mappingSet);
            _repository
                .CapturedRequest.ResourceInfo.Should()
                .BeEquivalentTo(
                    new BaseResourceInfo(new ProjectName("Ed-Fi"), new ResourceName("Student"), false)
                );
            _repository.CapturedRequest.ReadMode.Should().Be(RelationalGetRequestReadMode.StoredDocument);
            _repository.CapturedRequest.ReadableProfileProjectionContext.Should().BeNull();
        }
    }
}
