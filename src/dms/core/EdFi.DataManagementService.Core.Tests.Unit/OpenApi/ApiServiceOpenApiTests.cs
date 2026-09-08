// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Validation;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Polly;
using static EdFi.DataManagementService.Core.Tests.Unit.OpenApi.ChangeQueriesOpenApiDocumentTestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

public class ApiServiceOpenApiTests
{
    private const string TokenUrl = "https://example.org/oauth/token";

    private static JsonArray Servers(string url)
    {
        return [new JsonObject { ["url"] = url }];
    }

    private static IMemoryCache CreateMemoryCache() => new MemoryCache(new MemoryCacheOptions());

    private static ApiService CreateApiService(
        ApiSchemaDocumentNodes apiSchemaDocumentNodes,
        IProfileService? profileService = null
    )
    {
        var apiSchemaProvider = A.Fake<IApiSchemaProvider>();
        A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes()).Returns(apiSchemaDocumentNodes);
        A.CallTo(() => apiSchemaProvider.SchemaLoadId).Returns(Guid.NewGuid());

        var cachedClaimSetProvider = new CachedClaimSetProvider(
            A.Fake<IConfigurationServiceClaimSetProvider>(),
            CreateMemoryCache(),
            new CacheSettings(),
            NullLogger<CachedClaimSetProvider>.Instance
        );

        var resourceLoadOrderCalculator = new ResourceLoadOrderCalculator(
            [],
            A.Fake<IResourceDependencyGraphFactory>()
        );

        return new ApiService(
            apiSchemaProvider,
            A.Fake<IEffectiveApiSchemaProvider>(),
            cachedClaimSetProvider,
            A.Fake<IDocumentValidator>(),
            A.Fake<IMatchingDocumentUuidsValidator>(),
            A.Fake<IEqualityConstraintValidator>(),
            A.Fake<IDecimalValidator>(),
            NullLogger<ApiService>.Instance,
            NullLoggerFactory.Instance,
            Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", AuthenticationService = TokenUrl }
            ),
            ResiliencePipeline.Empty,
            resourceLoadOrderCalculator,
            new ServiceCollection().BuildServiceProvider(),
            A.Fake<IServiceScopeFactory>(),
            cachedClaimSetProvider,
            A.Fake<IResourceDependencyGraphMLFactory>(),
            profileService ?? A.Fake<IProfileService>()
        );
    }

    private sealed class CachingProfileOpenApiService : IProfileService
    {
        private JsonNode? _cachedSpecification;

        public int BaseSpecificationRequestCount { get; private set; }

        public Task<ProfileResolutionResult> ResolveProfileAsync(
            ParsedProfileHeader? parsedHeader,
            RequestMethod method,
            string resourceName,
            long applicationId,
            string? tenantId
        )
        {
            throw new NotSupportedException();
        }

        public Task<CachedApplicationProfiles> GetOrFetchApplicationProfilesAsync(
            long applicationId,
            string? tenantId
        )
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<string>> GetProfileNamesAsync(string? tenantId)
        {
            throw new NotSupportedException();
        }

        public Task<ProfileDefinition?> GetProfileDefinitionAsync(string profileName, string? tenantId)
        {
            throw new NotSupportedException();
        }

        public Task<JsonNode?> GetProfileOpenApiSpecAsync(
            string profileName,
            string? tenantId,
            Func<JsonNode> baseSpecificationProvider,
            Guid apiSchemaLoadId
        )
        {
            if (_cachedSpecification is null)
            {
                BaseSpecificationRequestCount++;
                _cachedSpecification = baseSpecificationProvider().DeepClone();
            }

            return Task.FromResult<JsonNode?>(_cachedSpecification.DeepClone());
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ApiService_With_A_Core_Change_Queries_OpenApi_Document : ApiServiceOpenApiTests
    {
        private ApiService apiService = null!;
        private bool hasChangeQueriesOpenApiSpecification;
        private JsonNode? result;

        [SetUp]
        public void Setup()
        {
            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: MinimalOpenApiDocument("Ed-Fi Resources API"),
                    descriptorsDoc: MinimalOpenApiDocument("Ed-Fi Descriptors API"),
                    changeQueriesDoc: ChangeQueriesOpenApiDocument("Ed-Fi Change Queries API")
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            apiService = CreateApiService(apiSchemaDocumentNodes);
            hasChangeQueriesOpenApiSpecification = apiService.HasChangeQueriesOpenApiSpecification();
            result = apiService.GetChangeQueriesOpenApiSpecification(
                Servers("https://example.org/changeQueries/v1")
            );
        }

        [Test]
        public void It_should_report_the_change_queries_document_is_present()
        {
            hasChangeQueriesOpenApiSpecification.Should().BeTrue();
        }

        [Test]
        public void It_should_return_the_change_queries_document_with_endpoint_metadata()
        {
            result.Should().NotBeNull();
            result!["info"]!["title"]!.GetValue<string>().Should().Be("Ed-Fi Change Queries API");
            result["paths"]!.AsObject().Should().ContainKey("/availableChangeVersions");
            result["servers"]![0]!["url"]!
                .GetValue<string>()
                .Should()
                .Be("https://example.org/changeQueries/v1");
            result["components"]!["securitySchemes"]!["oauth2_client_credentials"]!["flows"]![
                "clientCredentials"
            ]!["tokenUrl"]!
                .GetValue<string>()
                .Should()
                .Be(TokenUrl);
            result["security"]!.AsArray().Should().HaveCount(1);
        }

        [Test]
        public void It_should_not_mutate_the_cached_change_queries_document_between_calls()
        {
            result.Should().NotBeNull();
            result!["info"]!["title"] = "Mutated Change Queries API";
            result["servers"]![0]!["url"] = "https://example.org/mutated";

            JsonNode? secondResult = apiService.GetChangeQueriesOpenApiSpecification(
                Servers("https://example.org/changeQueries/v1/second")
            );

            secondResult.Should().NotBeNull();
            secondResult!["info"]!["title"]!.GetValue<string>().Should().Be("Ed-Fi Change Queries API");
            secondResult["servers"]![0]!["url"]!
                .GetValue<string>()
                .Should()
                .Be("https://example.org/changeQueries/v1/second");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ApiService_Without_A_Core_Change_Queries_OpenApi_Document : ApiServiceOpenApiTests
    {
        private bool hasChangeQueriesOpenApiSpecification;
        private JsonNode? result;

        [SetUp]
        public void Setup()
        {
            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: MinimalOpenApiDocument("Ed-Fi Resources API"),
                    descriptorsDoc: MinimalOpenApiDocument("Ed-Fi Descriptors API")
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            ApiService apiService = CreateApiService(apiSchemaDocumentNodes);
            hasChangeQueriesOpenApiSpecification = apiService.HasChangeQueriesOpenApiSpecification();
            result = apiService.GetChangeQueriesOpenApiSpecification(
                Servers("https://example.org/changeQueries/v1")
            );
        }

        [Test]
        public void It_should_report_the_change_queries_document_is_absent()
        {
            hasChangeQueriesOpenApiSpecification.Should().BeFalse();
        }

        [Test]
        public void It_should_return_null()
        {
            result.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ApiService_With_Only_An_Extension_Change_Queries_OpenApi_Document
        : ApiServiceOpenApiTests
    {
        private bool hasChangeQueriesOpenApiSpecification;
        private JsonNode? result;

        [SetUp]
        public void Setup()
        {
            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: MinimalOpenApiDocument("Ed-Fi Resources API"),
                    descriptorsDoc: MinimalOpenApiDocument("Ed-Fi Descriptors API")
                )
                .WithEndProject()
                .WithStartProject("Sample", "1.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: MinimalOpenApiDocument("Sample Resources API"),
                    descriptorsDoc: MinimalOpenApiDocument("Sample Descriptors API"),
                    changeQueriesDoc: MinimalOpenApiDocument("Sample Change Queries API")
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            ApiService apiService = CreateApiService(apiSchemaDocumentNodes);
            hasChangeQueriesOpenApiSpecification = apiService.HasChangeQueriesOpenApiSpecification();
            result = apiService.GetChangeQueriesOpenApiSpecification(
                Servers("https://example.org/changeQueries/v1")
            );
        }

        [Test]
        public void It_should_report_the_change_queries_document_is_absent()
        {
            hasChangeQueriesOpenApiSpecification.Should().BeFalse();
        }

        [Test]
        public void It_should_return_null()
        {
            result.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ApiService_With_Resource_And_Descriptor_OpenApi_Documents : ApiServiceOpenApiTests
    {
        private ApiService apiService = null!;

        [SetUp]
        public void Setup()
        {
            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: MinimalOpenApiDocument("Ed-Fi Resources API"),
                    descriptorsDoc: MinimalOpenApiDocument("Ed-Fi Descriptors API")
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            apiService = CreateApiService(apiSchemaDocumentNodes);
        }

        [Test]
        public void It_should_not_mutate_the_cached_resource_document_between_calls()
        {
            JsonNode firstResult = apiService.GetResourceOpenApiSpecification(
                Servers("https://example.org/data/first")
            );
            firstResult["info"]!["title"] = "Mutated Resources API";
            firstResult["servers"]![0]!["url"] = "https://example.org/mutated";

            JsonNode secondResult = apiService.GetResourceOpenApiSpecification(
                Servers("https://example.org/data/second")
            );

            secondResult["info"]!["title"]!.GetValue<string>().Should().Be("Ed-Fi API");
            secondResult["servers"]![0]!["url"]!
                .GetValue<string>()
                .Should()
                .Be("https://example.org/data/second");
        }

        [Test]
        public void It_should_not_mutate_the_cached_descriptor_document_between_calls()
        {
            JsonNode firstResult = apiService.GetDescriptorOpenApiSpecification(
                Servers("https://example.org/data/first")
            );
            firstResult["info"]!["title"] = "Mutated Descriptors API";
            firstResult["servers"]![0]!["url"] = "https://example.org/mutated";

            JsonNode secondResult = apiService.GetDescriptorOpenApiSpecification(
                Servers("https://example.org/data/second")
            );

            secondResult["info"]!["title"]!.GetValue<string>().Should().Be("Ed-Fi API");
            secondResult["servers"]![0]!["url"]!
                .GetValue<string>()
                .Should()
                .Be("https://example.org/data/second");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ApiService_With_A_Cached_Profile_OpenApi_Document : ApiServiceOpenApiTests
    {
        private ApiService apiService = null!;
        private CachingProfileOpenApiService profileService = null!;

        [SetUp]
        public void Setup()
        {
            var apiSchemaDocumentNodes = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: MinimalOpenApiDocument("Ed-Fi Resources API"),
                    descriptorsDoc: MinimalOpenApiDocument("Ed-Fi Descriptors API")
                )
                .WithEndProject()
                .AsApiSchemaNodes();

            profileService = new CachingProfileOpenApiService();
            apiService = CreateApiService(apiSchemaDocumentNodes, profileService);
        }

        [Test]
        public async Task It_should_apply_endpoint_metadata_after_profile_cache_retrieval()
        {
            JsonNode? firstResult = await apiService.GetProfileOpenApiSpecificationAsync(
                "StudentProfile",
                tenantId: null,
                Servers("https://example.org/data/first")
            );

            JsonNode? secondResult = await apiService.GetProfileOpenApiSpecificationAsync(
                "StudentProfile",
                tenantId: null,
                Servers("https://example.org/data/second")
            );

            firstResult.Should().NotBeNull();
            firstResult!["servers"]![0]!["url"]!
                .GetValue<string>()
                .Should()
                .Be("https://example.org/data/first");

            secondResult.Should().NotBeNull();
            secondResult!["servers"]![0]!["url"]!
                .GetValue<string>()
                .Should()
                .Be("https://example.org/data/second");
            secondResult["components"]!["securitySchemes"]!["oauth2_client_credentials"]!["flows"]![
                "clientCredentials"
            ]!["tokenUrl"]!
                .GetValue<string>()
                .Should()
                .Be(TokenUrl);
            profileService.BaseSpecificationRequestCount.Should().Be(1);
        }
    }
}
