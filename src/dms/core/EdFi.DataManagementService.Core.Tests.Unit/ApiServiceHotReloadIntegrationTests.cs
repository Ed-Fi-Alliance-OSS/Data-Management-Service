// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Validation;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Polly;

namespace EdFi.DataManagementService.Core.Tests.Unit;

/// <summary>
/// Integration tests for ApiService hot reload functionality using ApiSchemaProvider.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ApiServiceHotReloadIntegrationTests
{
    private string _testDirectory = null!;
    private ApiSchemaProvider _apiSchemaFileLoader = null!;
    private ApiService _apiService = null!;
    private IOptions<AppSettings> _appSettings = null!;

    [SetUp]
    public void Setup()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);

        _appSettings = Options.Create(
            new AppSettings
            {
                AllowIdentityUpdateOverrides = "",
                UseApiSchemaPath = true,
                ApiSchemaPath = _testDirectory,
                EnableManagementEndpoints = true,
            }
        );

        var apiSchemaValidator = A.Fake<IApiSchemaValidator>();
        // By default, return no validation errors
        A.CallTo(() => apiSchemaValidator.Validate(A<JsonNode>._))
            .Returns(new List<SchemaValidationFailure>());

        _apiSchemaFileLoader = new ApiSchemaProvider(
            NullLogger<ApiSchemaProvider>.Instance,
            _appSettings,
            apiSchemaValidator
        );

        // Create ApiService with minimal fakes for other dependencies
        var services = new ServiceCollection();

        // Register JWT authentication services
        services.Configure<JwtAuthenticationOptions>(options => { });
        services.AddTransient<JwtAuthenticationMiddleware>();
        services.AddTransient<IJwtValidationService>(sp => A.Fake<IJwtValidationService>());
        services.AddTransient<ILogger<JwtAuthenticationMiddleware>>(_ =>
            NullLogger<JwtAuthenticationMiddleware>.Instance
        );

        // Register DMS Instance Selection services
        services.AddTransient<DmsInstanceSelectionMiddleware>();

        var fakeApplicationContextProvider = A.Fake<IApplicationContextProvider>();
        A.CallTo(() => fakeApplicationContextProvider.GetApplicationByClientIdAsync(A<string>._))
            .Returns(
                Task.FromResult<ApplicationContext?>(
                    new ApplicationContext(
                        Id: 1,
                        ApplicationId: 1,
                        ClientId: "test-client",
                        ClientUuid: Guid.NewGuid(),
                        DmsInstanceIds: [1]
                    )
                )
            );
        services.AddSingleton<IApplicationContextProvider>(fakeApplicationContextProvider);

        var fakeDmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
        A.CallTo(() => fakeDmsInstanceProvider.GetById(A<long>._))
            .Returns(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "Test",
                    InstanceName: "Test Instance",
                    ConnectionString: "test-connection-string",
                    RouteContext: []
                )
            );
        services.AddSingleton<IDmsInstanceProvider>(fakeDmsInstanceProvider);

        var fakeRequestConnectionStringProvider = A.Fake<IRequestConnectionStringProvider>();
        A.CallTo(() => fakeRequestConnectionStringProvider.GetConnectionString())
            .Returns("test-connection-string");
        services.AddSingleton<IRequestConnectionStringProvider>(fakeRequestConnectionStringProvider);

        services.AddTransient<ILogger<DmsInstanceSelectionMiddleware>>(_ =>
            NullLogger<DmsInstanceSelectionMiddleware>.Instance
        );

        var serviceProvider = services.BuildServiceProvider();
        var claimSetProvider = new NoClaimsClaimSetProvider(NullLogger.Instance);
        var documentValidator = new DocumentValidator();
        var matchingDocumentUuidsValidator = new MatchingDocumentUuidsValidator();
        var equalityConstraintValidator = new EqualityConstraintValidator();
        var decimalValidator = new DecimalValidator();
        var authorizationServiceFactory = new NamedAuthorizationServiceFactory(serviceProvider);
        var resourceLoadOrderCalculator = new ResourceLoadOrderCalculator(
            [],
            A.Fake<IResourceDependencyGraphFactory>()
        );

        var apiSchemaUploadService = A.Fake<IUploadApiSchemaService>();

        _apiService = new ApiService(
            _apiSchemaFileLoader,
            claimSetProvider,
            documentValidator,
            matchingDocumentUuidsValidator,
            equalityConstraintValidator,
            decimalValidator,
            NullLogger<ApiService>.Instance,
            _appSettings,
            authorizationServiceFactory,
            ResiliencePipeline.Empty,
            resourceLoadOrderCalculator,
            apiSchemaUploadService,
            serviceProvider,
            A.Fake<ClaimSetsCache>(),
            A.Fake<IResourceDependencyGraphMLFactory>()
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class ReloadBehaviorTests : ApiServiceHotReloadIntegrationTests
    {
        [Test]
        public async Task ReloadApiSchemaAsync_WithSchemaChanges_UpdatesSchema()
        {
            // Arrange - Create initial schema
            await WriteTestSchemaFile("ApiSchema.json", CreateSchemaWithResource("Student", "5.0.0"));

            // Load initial schema
            await _apiService.ReloadApiSchemaAsync();
            var initialReloadId = _apiSchemaFileLoader.ReloadId;

            // Act - Update schema file with new version
            await WriteTestSchemaFile("ApiSchema.json", CreateSchemaWithResource("Student", "5.1.0"));
            var reloadResult = await _apiService.ReloadApiSchemaAsync();
            var newReloadId = _apiSchemaFileLoader.ReloadId;

            // Assert
            reloadResult.StatusCode.Should().Be(200);
            newReloadId.Should().NotBe(initialReloadId);

            // Verify schema content changed
            var schemaNodes = _apiSchemaFileLoader.GetApiSchemaNodes();
            var projectVersion = schemaNodes
                .CoreApiSchemaRootNode["projectSchema"]
                ?["projectVersion"]?.GetValue<string>();
            projectVersion.Should().Be("5.1.0");
        }

        [Test]
        public async Task ConcurrentRequestsAndReload_MaintainsConsistency()
        {
            // Arrange - Create initial schema
            await WriteTestSchemaFile("ApiSchema.json", CreateSchemaWithResource("Course", "1.0.0"));
            await _apiService.ReloadApiSchemaAsync();

            var request = CreateTestRequest("/ed-fi/courses");
            var tasks = new List<Task<IFrontendResponse>>();
            var reloadIds = new List<Guid>();
            var barrier = new Barrier(15); // 10 requests + 5 reloads

            // Act - Start concurrent requests
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(
                    Task.Run(async () =>
                    {
                        barrier.SignalAndWait();
                        var response = await _apiService.Get(request);
                        lock (reloadIds)
                        {
                            reloadIds.Add(_apiSchemaFileLoader.ReloadId);
                        }
                        return response;
                    })
                );
            }

            // Start concurrent reloads
            var reloadTasks = new List<Task<IFrontendResponse>>();
            for (int i = 0; i < 5; i++)
            {
                int version = i;
                reloadTasks.Add(
                    Task.Run(async () =>
                    {
                        barrier.SignalAndWait();
                        await Task.Delay(10); // Small delay to increase contention
                        await WriteTestSchemaFile(
                            "ApiSchema.json",
                            CreateSchemaWithResource("Course", $"1.{version}.0")
                        );
                        var result = await _apiService.ReloadApiSchemaAsync();
                        return result;
                    })
                );
            }

            // Wait for all operations
            var responses = await Task.WhenAll(tasks);
            var reloadResults = await Task.WhenAll(reloadTasks);

            // Assert
            responses.Should().HaveCount(10);
            responses.Should().OnlyContain(r => r != null);
            reloadResults.Should().Contain(r => r.StatusCode == 200); // At least one reload should succeed
            reloadIds.Count.Should().Be(10); // All requests should have captured a reload ID
        }

        [Test]
        public async Task ReloadWithInvalidSchema_FailsGracefully()
        {
            // Arrange - Create valid initial schema
            await WriteTestSchemaFile("ApiSchema.json", CreateSchemaWithResource("Teacher", "1.0.0"));
            var initialReloadResult = await _apiService.ReloadApiSchemaAsync();
            var initialReloadId = _apiSchemaFileLoader.ReloadId;

            // Act - Write invalid schema and attempt reload
            await WriteTestSchemaFile("ApiSchema.json", "{ invalid json");
            var failedReloadResult = await _apiService.ReloadApiSchemaAsync();
            var afterFailedReloadId = _apiSchemaFileLoader.ReloadId;

            // Assert
            initialReloadResult.StatusCode.Should().Be(200);
            failedReloadResult.StatusCode.Should().Be(500);
            afterFailedReloadId.Should().Be(initialReloadId, "reload ID should not change on failed reload");

            // Verify original schema is still active
            var request = CreateTestRequest("/ed-fi/teachers");
            var response = await _apiService.Get(request);
            response.Should().NotBeNull();
        }

        [Test]
        public async Task MultiplePipelineTypes_AllInvalidatedOnReload()
        {
            // Arrange - Create schema with multiple resources
            await WriteTestSchemaFile("ApiSchema.json", CreateSchemaWithMultipleResources());
            await _apiService.ReloadApiSchemaAsync();

            var getRequest = CreateTestRequest("/ed-fi/students/123");
            var queryRequest = CreateTestRequest("/ed-fi/students");
            var postRequest = CreateTestRequest("/ed-fi/students", "{}");
            var putRequest = CreateTestRequest("/ed-fi/students/123", "{}");
            var deleteRequest = CreateTestRequest("/ed-fi/students/123");

            // Capture initial reload ID
            var initialReloadId = _apiSchemaFileLoader.ReloadId;

            // Act - Make requests to populate all pipeline caches
            await _apiService.Get(getRequest);
            await _apiService.Get(queryRequest);
            await _apiService.Upsert(postRequest);
            await _apiService.UpdateById(putRequest);
            await _apiService.DeleteById(deleteRequest);

            // Update schema and reload
            await WriteTestSchemaFile("ApiSchema.json", CreateSchemaWithMultipleResources("2.0.0"));
            await _apiService.ReloadApiSchemaAsync();

            var newReloadId = _apiSchemaFileLoader.ReloadId;

            // Make requests again
            await _apiService.Get(getRequest);
            await _apiService.Get(queryRequest);
            await _apiService.Upsert(postRequest);
            await _apiService.UpdateById(putRequest);
            await _apiService.DeleteById(deleteRequest);

            // Assert
            newReloadId.Should().NotBe(initialReloadId);

            // Verify new schema is being used
            var schemaNodes = _apiSchemaFileLoader.GetApiSchemaNodes();
            var version = schemaNodes
                .CoreApiSchemaRootNode["projectSchema"]
                ?["projectVersion"]?.GetValue<string>();
            version.Should().Be("2.0.0");
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class ThreadSafetyTests : ApiServiceHotReloadIntegrationTests
    {
        [Test]
        public async Task ParallelReloads_OneSucceeds()
        {
            // Arrange
            await WriteTestSchemaFile("ApiSchema.json", CreateSchemaWithResource("School", "1.0.0"));
            await _apiService.ReloadApiSchemaAsync();

            var barrier = new Barrier(10);
            var reloadTasks = new List<Task<IFrontendResponse>>();
            var reloadIds = new List<Guid>();

            // Act - Start 10 concurrent reloads
            for (int i = 0; i < 10; i++)
            {
                reloadTasks.Add(
                    Task.Run(async () =>
                    {
                        barrier.SignalAndWait();
                        var result = await _apiService.ReloadApiSchemaAsync();
                        lock (reloadIds)
                        {
                            reloadIds.Add(_apiSchemaFileLoader.ReloadId);
                        }
                        return result;
                    })
                );
            }

            var results = await Task.WhenAll(reloadTasks);

            // Assert
            results.Should().HaveCount(10);
            results
                .Count(r => r.StatusCode == 200)
                .Should()
                .BeGreaterOrEqualTo(1, "at least one reload should succeed");
        }

        [Test]
        public async Task ReloadApiSchemaAsync_WhenManagementEndpointsDisabled_Returns404()
        {
            // Arrange - Create a new ApiService with management endpoints disabled
            var disabledSettings = Options.Create(
                new AppSettings
                {
                    AllowIdentityUpdateOverrides = "",
                    UseApiSchemaPath = true,
                    ApiSchemaPath = _testDirectory,
                    EnableManagementEndpoints = false,
                }
            );

            var apiSchemaUploadService = A.Fake<IUploadApiSchemaService>();

            var apiServiceWithDisabledEndpoints = new ApiService(
                _apiSchemaFileLoader,
                new NoClaimsClaimSetProvider(NullLogger.Instance),
                new DocumentValidator(),
                new MatchingDocumentUuidsValidator(),
                new EqualityConstraintValidator(),
                new DecimalValidator(),
                NullLogger<ApiService>.Instance,
                disabledSettings,
                new NamedAuthorizationServiceFactory(new ServiceCollection().BuildServiceProvider()),
                ResiliencePipeline.Empty,
                new ResourceLoadOrderCalculator([], A.Fake<IResourceDependencyGraphFactory>()),
                apiSchemaUploadService,
                new ServiceCollection().BuildServiceProvider(),
                A.Fake<ClaimSetsCache>(),
                A.Fake<IResourceDependencyGraphMLFactory>()
            );

            await WriteTestSchemaFile("ApiSchema.json", CreateSchemaWithResource("Student", "5.0.0"));

            // Act
            var reloadResult = await apiServiceWithDisabledEndpoints.ReloadApiSchemaAsync();

            // Assert
            reloadResult.StatusCode.Should().Be(404);
            reloadResult.Body.Should().BeNull();
        }
    }

    protected async Task WriteTestSchemaFile(string fileName, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(_testDirectory, fileName), content);
    }

    protected static FrontendRequest CreateTestRequest(string path, string? body = null)
    {
        return new FrontendRequest(
            Path: path,
            Body: body,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("test-trace-id")
        );
    }

    protected static string CreateSchemaWithResource(string resourceName, string version = "1.0.0")
    {
        var schema = new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = new JsonObject
            {
                ["projectName"] = "Ed-Fi",
                ["projectVersion"] = version,
                ["description"] = "Test Ed-Fi project",
                ["projectEndpointName"] = "ed-fi",
                ["isExtensionProject"] = false,
                ["abstractResources"] = new JsonObject(),
                ["caseInsensitiveEndpointNameMapping"] = new JsonObject
                {
                    [$"{resourceName.ToLower()}s"] = $"{resourceName}s",
                },
                ["resourceNameMapping"] = new JsonObject { [resourceName] = $"{resourceName.ToLower()}s" },
                ["resourceSchemas"] = new JsonObject
                {
                    [$"{resourceName.ToLower()}s"] = new JsonObject
                    {
                        ["resourceName"] = resourceName,
                        ["isDescriptor"] = false,
                        ["allowIdentityUpdates"] = false,
                        ["isSchoolYearEnumeration"] = false,
                        ["isSubclass"] = false,
                        ["identityJsonPaths"] = new JsonArray { "$.id" },
                        ["jsonSchemaForInsert"] = new JsonObject(),
                        ["equalityConstraints"] = new JsonArray(),
                    },
                },
                ["educationOrganizationHierarchy"] = new JsonObject(),
                ["educationOrganizationTypes"] = new JsonArray(),
            },
        };

        return schema.ToJsonString();
    }

    protected static string CreateSchemaWithMultipleResources(string version = "1.0.0")
    {
        var schema = new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = new JsonObject
            {
                ["projectName"] = "Ed-Fi",
                ["projectVersion"] = version,
                ["description"] = "Test Ed-Fi project",
                ["projectEndpointName"] = "ed-fi",
                ["isExtensionProject"] = false,
                ["abstractResources"] = new JsonObject(),
                ["caseInsensitiveEndpointNameMapping"] = new JsonObject
                {
                    ["students"] = "Students",
                    ["teachers"] = "Teachers",
                    ["courses"] = "Courses",
                },
                ["resourceNameMapping"] = new JsonObject
                {
                    ["Student"] = "students",
                    ["Teacher"] = "teachers",
                    ["Course"] = "courses",
                },
                ["resourceSchemas"] = new JsonObject
                {
                    ["students"] = CreateResourceSchema("Student"),
                    ["teachers"] = CreateResourceSchema("Teacher"),
                    ["courses"] = CreateResourceSchema("Course"),
                },
                ["educationOrganizationHierarchy"] = new JsonObject(),
                ["educationOrganizationTypes"] = new JsonArray(),
            },
        };

        return schema.ToJsonString();
    }

    protected static JsonObject CreateResourceSchema(string resourceName)
    {
        return new JsonObject
        {
            ["resourceName"] = resourceName,
            ["isDescriptor"] = false,
            ["allowIdentityUpdates"] = false,
            ["isSchoolYearEnumeration"] = false,
            ["isSubclass"] = false,
            ["identityJsonPaths"] = new JsonArray { "$.id" },
            ["jsonSchemaForInsert"] = new JsonObject(),
            ["equalityConstraints"] = new JsonArray(),
        };
    }
}
