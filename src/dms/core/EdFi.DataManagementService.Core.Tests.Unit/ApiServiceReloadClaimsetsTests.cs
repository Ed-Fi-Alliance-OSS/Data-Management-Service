// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
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

namespace EdFi.DataManagementService.Core.Tests.Unit;

/// <summary>
/// Tests for ApiService.ReloadClaimsetsAsync to verify correct tenant parameter handling.
/// These tests ensure that cache invalidation is properly tenant-scoped in multi-tenant scenarios.
/// </summary>
[TestFixture]
public class Given_ClaimsetReloadIsEnabled_When_ReloadClaimsetsAsyncIsCalled
{
    private ApiService _apiService = null!;
    private IMemoryCache _fakeMemoryCache = null!;
    private IClaimSetProvider _fakeClaimSetProvider = null!;

    private ApiService CreateApiService(
        IMemoryCache memoryCache,
        IClaimSetProvider claimSetProvider,
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

        // Create ClaimSetsCache with the fake IMemoryCache
        var claimSetsCache = new ClaimSetsCache(memoryCache, TimeSpan.FromMinutes(10));

        return new ApiService(
            fakeApiSchemaProvider,
            claimSetProvider,
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
            claimSetsCache,
            fakeResourceDependencyGraphMLFactory,
            fakeCompiledSchemaCache
        );
    }

    [TestFixture]
    public class Given_TenantIsSpecified : Given_ClaimsetReloadIsEnabled_When_ReloadClaimsetsAsyncIsCalled
    {
        private const string TestTenant = "test-tenant";

        [SetUp]
        public async Task Setup()
        {
            _fakeMemoryCache = A.Fake<IMemoryCache>();
            _fakeClaimSetProvider = A.Fake<IClaimSetProvider>();
            _apiService = CreateApiService(_fakeMemoryCache, _fakeClaimSetProvider, true);

            await _apiService.ReloadClaimsetsAsync(TestTenant);
        }

        [Test]
        public void It_should_clear_cache_for_the_specified_tenant()
        {
            // Verify that Remove was called with the tenant-specific cache key.
            A.CallTo(() => _fakeMemoryCache.Remove("ClaimSetsCache:test-tenant"))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_should_reload_claimsets_for_the_specified_tenant()
        {
            // Verify that GetAllClaimSets was called with the correct tenant parameter
            A.CallTo(() => _fakeClaimSetProvider.GetAllClaimSets(TestTenant)).MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_TenantIsNotSpecified : Given_ClaimsetReloadIsEnabled_When_ReloadClaimsetsAsyncIsCalled
    {
        [SetUp]
        public async Task Setup()
        {
            _fakeMemoryCache = A.Fake<IMemoryCache>();
            _fakeClaimSetProvider = A.Fake<IClaimSetProvider>();
            _apiService = CreateApiService(_fakeMemoryCache, _fakeClaimSetProvider, true);

            await _apiService.ReloadClaimsetsAsync();
        }

        [Test]
        public void It_should_clear_cache_with_default_key()
        {
            // Verify that Remove was called with the default cache key (no tenant suffix)
            A.CallTo(() => _fakeMemoryCache.Remove("ClaimSetsCache")).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_should_reload_claimsets_with_null_tenant()
        {
            // Verify that GetAllClaimSets was called with null (single-tenant mode)
            A.CallTo(() => _fakeClaimSetProvider.GetAllClaimSets(null)).MustHaveHappenedOnceExactly();
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
            _fakeMemoryCache = A.Fake<IMemoryCache>();
            _fakeClaimSetProvider = A.Fake<IClaimSetProvider>();
            _apiService = CreateApiService(_fakeMemoryCache, _fakeClaimSetProvider, true);

            await _apiService.ReloadClaimsetsAsync(TenantA);
            await _apiService.ReloadClaimsetsAsync(TenantB);
        }

        [Test]
        public void It_should_clear_cache_for_each_tenant_separately()
        {
            // Verify that Remove was called once for each tenant with tenant-specific keys
            A.CallTo(() => _fakeMemoryCache.Remove("ClaimSetsCache:tenant-a")).MustHaveHappenedOnceExactly();
            A.CallTo(() => _fakeMemoryCache.Remove("ClaimSetsCache:tenant-b")).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_should_not_clear_cache_for_default_tenant()
        {
            // Verify that Remove was not called with the default key (no tenant)
            A.CallTo(() => _fakeMemoryCache.Remove("ClaimSetsCache")).MustNotHaveHappened();
        }
    }
}

[TestFixture]
public class Given_ClaimsetReloadIsDisabled_When_ReloadClaimsetsAsyncIsCalled
{
    private ApiService _apiService = null!;
    private IMemoryCache _fakeMemoryCache = null!;
    private IClaimSetProvider _fakeClaimSetProvider = null!;
    private int _statusCode;

    private ApiService CreateApiService(IMemoryCache memoryCache, IClaimSetProvider claimSetProvider)
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

        // Create ClaimSetsCache with the fake IMemoryCache
        var claimSetsCache = new ClaimSetsCache(memoryCache, TimeSpan.FromMinutes(10));

        return new ApiService(
            fakeApiSchemaProvider,
            claimSetProvider,
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
            claimSetsCache,
            fakeResourceDependencyGraphMLFactory,
            fakeCompiledSchemaCache
        );
    }

    [SetUp]
    public async Task Setup()
    {
        _fakeMemoryCache = A.Fake<IMemoryCache>();
        _fakeClaimSetProvider = A.Fake<IClaimSetProvider>();
        _apiService = CreateApiService(_fakeMemoryCache, _fakeClaimSetProvider);

        var response = await _apiService.ReloadClaimsetsAsync("any-tenant");
        _statusCode = response.StatusCode;
    }

    [Test]
    public void It_should_return_404_status()
    {
        _statusCode.Should().Be(404);
    }

    [Test]
    public void It_should_not_clear_cache()
    {
        A.CallTo(() => _fakeMemoryCache.Remove(A<object>._)).MustNotHaveHappened();
    }

    [Test]
    public void It_should_not_reload_claimsets()
    {
        A.CallTo(() => _fakeClaimSetProvider.GetAllClaimSets(A<string?>._)).MustNotHaveHappened();
    }
}
