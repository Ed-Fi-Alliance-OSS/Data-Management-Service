// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Validation;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Polly;

namespace EdFi.DataManagementService.Core.Tests.Unit;

/// <summary>
/// Tests for ApiService.ReloadClaimsetsAsync to verify correct tenant parameter handling.
/// These tests ensure that cache invalidation is properly tenant-scoped in multi-tenant scenarios.
/// </summary>
[TestFixture]
public class Given_ClaimsetReloadIsEnabled_When_ReloadClaimsetsAsyncIsCalled
{
    private ApiService _apiService = null!;
    private IConfigurationServiceClaimSetProvider _fakeConfigServiceClaimSetProvider = null!;

    protected static HybridCache CreateHybridCache()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    private ApiService CreateApiService(
        IConfigurationServiceClaimSetProvider configServiceClaimSetProvider,
        bool enableClaimsetReload
    )
    {
        var appSettings = Options.Create(
            new AppSettings
            {
                AllowIdentityUpdateOverrides = "",
                EnableClaimsetReload = enableClaimsetReload,
                EnableManagementEndpoints = true,
            }
        );

        var fakeApiSchemaProvider = A.Fake<IApiSchemaProvider>();
        var fakeDocumentValidator = A.Fake<IDocumentValidator>();
        var fakeMatchingDocumentUuidsValidator = A.Fake<IMatchingDocumentUuidsValidator>();
        var fakeEqualityConstraintValidator = A.Fake<IEqualityConstraintValidator>();
        var fakeDecimalValidator = A.Fake<IDecimalValidator>();
        var fakeAuthorizationServiceFactory = A.Fake<IAuthorizationServiceFactory>();
        var fakeApiSchemaUploadService = A.Fake<IUploadApiSchemaService>();
        var fakeResourceDependencyGraphMLFactory = A.Fake<IResourceDependencyGraphMLFactory>();
        var fakeCompiledSchemaCache = new CompiledSchemaCache();
        var fakeResourceDependencyGraphFactory = A.Fake<IResourceDependencyGraphFactory>();
        var resourceLoadOrderCalculator = new ResourceLoadOrderCalculator(
            [],
            fakeResourceDependencyGraphFactory
        );

        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();

        var hybridCache = CreateHybridCache();
        var cacheSettings = new CacheSettings();

        // Create CachedClaimSetProvider with HybridCache
        var cachedClaimSetProvider = new CachedClaimSetProvider(
            configServiceClaimSetProvider,
            hybridCache,
            cacheSettings,
            NullLogger<CachedClaimSetProvider>.Instance
        );

        return new ApiService(
            fakeApiSchemaProvider,
            cachedClaimSetProvider, // Use as IClaimSetProvider
            fakeDocumentValidator,
            fakeMatchingDocumentUuidsValidator,
            fakeEqualityConstraintValidator,
            fakeDecimalValidator,
            NullLogger<ApiService>.Instance,
            appSettings,
            fakeAuthorizationServiceFactory,
            ResiliencePipeline.Empty,
            resourceLoadOrderCalculator,
            fakeApiSchemaUploadService,
            serviceProvider,
            cachedClaimSetProvider, // Use as CachedClaimSetProvider
            fakeResourceDependencyGraphMLFactory,
            fakeCompiledSchemaCache,
            A.Fake<IProfileService>()
        );
    }

    [TestFixture]
    public class Given_TenantIsSpecified : Given_ClaimsetReloadIsEnabled_When_ReloadClaimsetsAsyncIsCalled
    {
        private const string TestTenant = "test-tenant";

        [SetUp]
        public async Task Setup()
        {
            _fakeConfigServiceClaimSetProvider = A.Fake<IConfigurationServiceClaimSetProvider>();
            A.CallTo(() => _fakeConfigServiceClaimSetProvider.GetAllClaimSets(A<string?>.Ignored))
                .Returns(new List<ClaimSet> { new("TestClaimSet", []) });

            _apiService = CreateApiService(_fakeConfigServiceClaimSetProvider, true);

            await _apiService.ReloadClaimsetsAsync(TestTenant);
        }

        [Test]
        public void It_should_reload_claimsets_for_the_specified_tenant()
        {
            // Verify that GetAllClaimSets was called with the correct tenant parameter
            A.CallTo(() => _fakeConfigServiceClaimSetProvider.GetAllClaimSets(TestTenant))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_TenantIsNotSpecified : Given_ClaimsetReloadIsEnabled_When_ReloadClaimsetsAsyncIsCalled
    {
        [SetUp]
        public async Task Setup()
        {
            _fakeConfigServiceClaimSetProvider = A.Fake<IConfigurationServiceClaimSetProvider>();
            A.CallTo(() => _fakeConfigServiceClaimSetProvider.GetAllClaimSets(A<string?>.Ignored))
                .Returns(new List<ClaimSet> { new("TestClaimSet", []) });

            _apiService = CreateApiService(_fakeConfigServiceClaimSetProvider, true);

            await _apiService.ReloadClaimsetsAsync();
        }

        [Test]
        public void It_should_reload_claimsets_with_null_tenant()
        {
            // Verify that GetAllClaimSets was called with null (single-tenant mode)
            A.CallTo(() => _fakeConfigServiceClaimSetProvider.GetAllClaimSets(null))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_MultipleTenants : Given_ClaimsetReloadIsEnabled_When_ReloadClaimsetsAsyncIsCalled
    {
        private const string TenantA = "tenant-a";
        private const string TenantB = "tenant-b";

        [SetUp]
        public async Task Setup()
        {
            _fakeConfigServiceClaimSetProvider = A.Fake<IConfigurationServiceClaimSetProvider>();
            A.CallTo(() => _fakeConfigServiceClaimSetProvider.GetAllClaimSets(A<string?>.Ignored))
                .Returns(new List<ClaimSet> { new("TestClaimSet", []) });

            _apiService = CreateApiService(_fakeConfigServiceClaimSetProvider, true);

            await _apiService.ReloadClaimsetsAsync(TenantA);
            await _apiService.ReloadClaimsetsAsync(TenantB);
        }

        [Test]
        public void It_should_reload_claimsets_for_each_tenant()
        {
            A.CallTo(() => _fakeConfigServiceClaimSetProvider.GetAllClaimSets(TenantA))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _fakeConfigServiceClaimSetProvider.GetAllClaimSets(TenantB))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_should_not_reload_claimsets_for_default_tenant()
        {
            // Verify that GetAllClaimSets was not called with null (default tenant)
            A.CallTo(() => _fakeConfigServiceClaimSetProvider.GetAllClaimSets(null)).MustNotHaveHappened();
        }
    }
}

[TestFixture]
public class Given_ClaimsetReloadIsDisabled_When_ReloadClaimsetsAsyncIsCalled
{
    private ApiService _apiService = null!;
    private IConfigurationServiceClaimSetProvider _fakeConfigServiceClaimSetProvider = null!;
    private int _statusCode;

    protected static HybridCache CreateHybridCache()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    private ApiService CreateApiService(IConfigurationServiceClaimSetProvider configServiceClaimSetProvider)
    {
        var appSettings = Options.Create(
            new AppSettings
            {
                AllowIdentityUpdateOverrides = "",
                EnableClaimsetReload = false,
                EnableManagementEndpoints = true,
            }
        );

        var fakeApiSchemaProvider = A.Fake<IApiSchemaProvider>();
        var fakeDocumentValidator = A.Fake<IDocumentValidator>();
        var fakeMatchingDocumentUuidsValidator = A.Fake<IMatchingDocumentUuidsValidator>();
        var fakeEqualityConstraintValidator = A.Fake<IEqualityConstraintValidator>();
        var fakeDecimalValidator = A.Fake<IDecimalValidator>();
        var fakeAuthorizationServiceFactory = A.Fake<IAuthorizationServiceFactory>();
        var fakeApiSchemaUploadService = A.Fake<IUploadApiSchemaService>();
        var fakeResourceDependencyGraphMLFactory = A.Fake<IResourceDependencyGraphMLFactory>();
        var fakeCompiledSchemaCache = new CompiledSchemaCache();
        var fakeResourceDependencyGraphFactory = A.Fake<IResourceDependencyGraphFactory>();
        var resourceLoadOrderCalculator = new ResourceLoadOrderCalculator(
            [],
            fakeResourceDependencyGraphFactory
        );

        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();

        var hybridCache = CreateHybridCache();
        var cacheSettings = new CacheSettings();

        var cachedClaimSetProvider = new CachedClaimSetProvider(
            configServiceClaimSetProvider,
            hybridCache,
            cacheSettings,
            NullLogger<CachedClaimSetProvider>.Instance
        );

        return new ApiService(
            fakeApiSchemaProvider,
            cachedClaimSetProvider,
            fakeDocumentValidator,
            fakeMatchingDocumentUuidsValidator,
            fakeEqualityConstraintValidator,
            fakeDecimalValidator,
            NullLogger<ApiService>.Instance,
            appSettings,
            fakeAuthorizationServiceFactory,
            ResiliencePipeline.Empty,
            resourceLoadOrderCalculator,
            fakeApiSchemaUploadService,
            serviceProvider,
            cachedClaimSetProvider,
            fakeResourceDependencyGraphMLFactory,
            fakeCompiledSchemaCache,
            A.Fake<IProfileService>()
        );
    }

    [SetUp]
    public async Task Setup()
    {
        _fakeConfigServiceClaimSetProvider = A.Fake<IConfigurationServiceClaimSetProvider>();
        _apiService = CreateApiService(_fakeConfigServiceClaimSetProvider);

        var response = await _apiService.ReloadClaimsetsAsync("any-tenant");
        _statusCode = response.StatusCode;
    }

    [Test]
    public void It_should_return_404_status()
    {
        _statusCode.Should().Be(404);
    }

    [Test]
    public void It_should_not_reload_claimsets()
    {
        A.CallTo(() => _fakeConfigServiceClaimSetProvider.GetAllClaimSets(A<string?>._))
            .MustNotHaveHappened();
    }
}
