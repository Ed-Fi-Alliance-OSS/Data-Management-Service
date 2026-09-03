// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

[TestFixture]
public class Given_A_CachedApplicationContextProvider
{
    private IConfigurationServiceApplicationProvider _configurationServiceApplicationProvider = null!;
    private HybridCache _hybridCache = null!;
    private CachedApplicationContextProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _configurationServiceApplicationProvider = A.Fake<IConfigurationServiceApplicationProvider>();
        _hybridCache = CreateHybridCache();
        _provider = CreateProvider();
    }

    [Test]
    public async Task It_Uses_The_Exact_Single_Tenant_Cache_Key()
    {
        var expectedContext = CreateApplicationContext("client-id", 1);
        await _hybridCache.SetAsync("ApplicationContext:single:client-id", expectedContext);

        ApplicationContextResult result = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeEquivalentTo(new ApplicationContextResult.Success(expectedContext));
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    A<string>.Ignored,
                    A<string?>.Ignored
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_Uses_The_Exact_Normalized_Tenant_Cache_Key()
    {
        var expectedContext = CreateApplicationContext("client-id", 2);
        await _hybridCache.SetAsync("ApplicationContext:tenant:districta:client-id", expectedContext);

        ApplicationContextResult result = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            "DistrictA"
        );

        result.Should().BeEquivalentTo(new ApplicationContextResult.Success(expectedContext));
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    A<string>.Ignored,
                    A<string?>.Ignored
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_Keeps_The_Same_Client_Isolated_Between_Tenants()
    {
        var northContext = CreateApplicationContext("client-id", 1);
        var southContext = CreateApplicationContext("client-id", 2);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync("client-id", "north")
            )
            .Returns(new ApplicationContextResult.Success(northContext));
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync("client-id", "south")
            )
            .Returns(new ApplicationContextResult.Success(southContext));

        ApplicationContextResult north = await _provider.GetApplicationByClientIdAsync("client-id", "north");
        ApplicationContextResult south = await CreateProvider()
            .GetApplicationByClientIdAsync("client-id", "south");

        north.Should().BeEquivalentTo(new ApplicationContextResult.Success(northContext));
        south.Should().BeEquivalentTo(new ApplicationContextResult.Success(southContext));
    }

    [Test]
    public async Task It_Normalizes_Tenant_Case_While_Preserving_The_Original_Tenant_For_Cms()
    {
        var expectedContext = CreateApplicationContext("client-id", 1);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    "DistrictA"
                )
            )
            .Returns(new ApplicationContextResult.Success(expectedContext));

        await _provider.GetApplicationByClientIdAsync("client-id", "DistrictA");
        var secondScopeProvider = CreateProvider();
        ApplicationContextResult result = await secondScopeProvider.GetApplicationByClientIdAsync(
            "client-id",
            "districta"
        );

        result.Should().BeEquivalentTo(new ApplicationContextResult.Success(expectedContext));
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    A<string?>.Ignored
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_Performs_One_Normal_Lookup_On_A_Cold_NotFound_Without_Reloading()
    {
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null
                )
            )
            .Returns(new ApplicationContextResult.NotFound());

        ApplicationContextResult result = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeOfType<ApplicationContextResult.NotFound>();
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                    A<string>.Ignored,
                    A<string?>.Ignored
                )
            )
            .MustNotHaveHappened();
    }

    [TestCase(ApplicationContextOutcome.Success)]
    [TestCase(ApplicationContextOutcome.NotFound)]
    [TestCase(ApplicationContextOutcome.Unavailable)]
    public async Task It_Memoizes_The_First_Outcome_For_The_Request(ApplicationContextOutcome outcome)
    {
        ApplicationContextResult expectedResult = CreateResult(outcome);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null
                )
            )
            .Returns(expectedResult);

        ApplicationContextResult first = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );
        ApplicationContextResult second = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        second.Should().BeSameAs(first);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [TestCase(ApplicationContextOutcome.NotFound)]
    [TestCase(ApplicationContextOutcome.Unavailable)]
    public async Task It_Does_Not_Admit_Failed_Results_To_The_Shared_Cache(ApplicationContextOutcome outcome)
    {
        var expectedContext = CreateApplicationContext("client-id", 1);
        var lookupCount = 0;
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null
                )
            )
            .ReturnsLazily(_ =>
            {
                lookupCount++;
                return Task.FromResult<ApplicationContextResult>(
                    lookupCount == 1
                        ? CreateResult(outcome)
                        : new ApplicationContextResult.Success(expectedContext)
                );
            });

        ApplicationContextResult failed = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );
        ApplicationContextResult recovered = await CreateProvider()
            .GetApplicationByClientIdAsync("client-id", tenant: null);

        failed
            .GetType()
            .Should()
            .Be(
                outcome switch
                {
                    ApplicationContextOutcome.NotFound => typeof(ApplicationContextResult.NotFound),
                    ApplicationContextOutcome.Unavailable => typeof(ApplicationContextResult.Unavailable),
                    _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
                }
            );
        recovered.Should().BeEquivalentTo(new ApplicationContextResult.Success(expectedContext));
        lookupCount.Should().Be(2);
    }

    [Test]
    public async Task It_Serves_A_Warm_Success_Cache_When_Cms_Is_Unavailable()
    {
        var expectedContext = CreateApplicationContext("client-id", 1);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null
                )
            )
            .Returns(new ApplicationContextResult.Success(expectedContext));

        await _provider.GetApplicationByClientIdAsync("client-id", tenant: null);
        var outageScopeProvider = CreateProvider();
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null
                )
            )
            .Returns(new ApplicationContextResult.Unavailable());

        ApplicationContextResult result = await outageScopeProvider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeEquivalentTo(new ApplicationContextResult.Success(expectedContext));
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_Reloads_Only_The_Matching_Normalized_Tenant_Key()
    {
        var staleNorthContext = CreateApplicationContext("client-id", 1);
        var southContext = CreateApplicationContext("client-id", 2);
        var refreshedNorthContext = CreateApplicationContext("client-id", 3);
        await _hybridCache.SetAsync("ApplicationContext:tenant:north:client-id", staleNorthContext);
        await _hybridCache.SetAsync("ApplicationContext:tenant:south:client-id", southContext);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                    "client-id",
                    "North"
                )
            )
            .Returns(new ApplicationContextResult.Success(refreshedNorthContext));

        ApplicationContextResult reloadResult = await _provider.ReloadApplicationByClientIdAsync(
            "client-id",
            "North"
        );
        ApplicationContextResult north = await CreateProvider()
            .GetApplicationByClientIdAsync("client-id", "north");
        ApplicationContextResult south = await CreateProvider()
            .GetApplicationByClientIdAsync("client-id", "south");

        reloadResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(refreshedNorthContext));
        north.Should().BeEquivalentTo(new ApplicationContextResult.Success(refreshedNorthContext));
        south.Should().BeEquivalentTo(new ApplicationContextResult.Success(southContext));
        A.CallTo(() =>
                _configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                    "client-id",
                    "North"
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    A<string>.Ignored,
                    A<string?>.Ignored
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_Returns_A_Typed_NotFound_Result_For_A_Blank_Client_Without_Caching()
    {
        ApplicationContextResult result = await _provider.GetApplicationByClientIdAsync(" ", tenant: null);

        result.Should().BeOfType<ApplicationContextResult.NotFound>();
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    A<string>.Ignored,
                    A<string?>.Ignored
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_Invalidates_The_Request_Scoped_Memo_On_Reload()
    {
        var reloadedContext = CreateApplicationContext("client-id", 9);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null
                )
            )
            .Returns(new ApplicationContextResult.NotFound());
        A.CallTo(() =>
                _configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                    "client-id",
                    tenant: null
                )
            )
            .Returns(new ApplicationContextResult.Success(reloadedContext));

        ApplicationContextResult beforeReload = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );
        ApplicationContextResult reloadResult = await _provider.ReloadApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );
        ApplicationContextResult afterReload = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        beforeReload.Should().BeOfType<ApplicationContextResult.NotFound>();
        reloadResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(reloadedContext));
        afterReload.Should().BeEquivalentTo(new ApplicationContextResult.Success(reloadedContext));
    }

    [Test]
    public async Task It_Preserves_Ownership_Tokens_Through_The_Cache()
    {
        ApplicationContext expectedContext = CreateApplicationContext("client-id", 1) with
        {
            CreatorOwnershipTokenId = 303,
            OwnershipTokenIds = [202, 404],
        };
        await _hybridCache.SetAsync("ApplicationContext:single:client-id", expectedContext);

        ApplicationContextResult result = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        var success = result.Should().BeOfType<ApplicationContextResult.Success>().Subject;
        success.ApplicationContext.CreatorOwnershipTokenId.Should().Be(303);
        success.ApplicationContext.OwnershipTokenIds.Should().Equal((short)202, (short)404);
    }

    private CachedApplicationContextProvider CreateProvider() =>
        new(
            _configurationServiceApplicationProvider,
            _hybridCache,
            new CacheSettings { ApplicationContextCacheExpirationSeconds = 123 },
            NullLogger<CachedApplicationContextProvider>.Instance
        );

    private static HybridCache CreateHybridCache()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    private static ApplicationContextResult CreateResult(ApplicationContextOutcome outcome) =>
        outcome switch
        {
            ApplicationContextOutcome.Success => new ApplicationContextResult.Success(
                CreateApplicationContext("client-id", 1)
            ),
            ApplicationContextOutcome.NotFound => new ApplicationContextResult.NotFound(),
            ApplicationContextOutcome.Unavailable => new ApplicationContextResult.Unavailable(),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };

    private static ApplicationContext CreateApplicationContext(string clientId, long applicationId) =>
        new(applicationId, 100, clientId, Guid.NewGuid(), [1, 2, 3], null, []);

    public enum ApplicationContextOutcome
    {
        Success,
        NotFound,
        Unavailable,
    }
}
