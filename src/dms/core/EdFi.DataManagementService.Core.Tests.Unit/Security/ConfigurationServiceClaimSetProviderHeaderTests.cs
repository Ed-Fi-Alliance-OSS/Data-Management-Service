// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

/// <summary>
/// Covers the outbound header contract of ConfigurationServiceClaimSetProvider. The provider is a
/// singleton serving every tenant over one long-lived HttpClient, so authorization and tenant selection
/// must travel on the individual request rather than on the client's default headers.
/// </summary>
public class ConfigurationServiceClaimSetProviderHeaderTests
{
    private const string TenantHeaderName = "Tenant";
    private const string BaseAddress = "https://api.example.com";
    private const string CmsToken = "cms-access-token";

    /// <summary>
    /// What one outbound call looked like from inside the transport: the headers the request itself
    /// carried, plus the tenant state visible on the shared client at the moment the request arrived.
    /// </summary>
    private sealed record ObservedRequest(
        string? RequestTenantHeader,
        string? RequestAuthorizationParameter,
        string? SharedClientTenantHeader
    );

    /// <summary>
    /// Records each outbound request and holds every request open until the expected number of them has
    /// arrived, so concurrent fetches are genuinely in flight together rather than serialized by chance.
    /// </summary>
    private sealed class GatedRecordingHandler(int releaseAfterRequestCount) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<ObservedRequest> _observed = new();
        private readonly TaskCompletionSource _allRequestsArrived = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _arrivedCount;

        /// <summary>
        /// The one long-lived client the provider uses for every tenant. The handler snapshots its
        /// default headers on arrival so a test can tell whether the fetch published its tenant onto
        /// that shared state.
        /// </summary>
        public HttpClient? SharedClient { get; set; }

        /// <summary>
        /// Produces the authorization metadata body for the tenant the request asked for.
        /// </summary>
        public Func<string?, string> ResponseBodyForTenant { get; set; } = _ => "[]";

        public IReadOnlyList<ObservedRequest> Observed => [.. _observed];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string? requestTenant = SingleHeaderValue(request.Headers, TenantHeaderName);
            string? sharedClientTenant = SharedClient is null
                ? null
                : SingleHeaderValue(SharedClient.DefaultRequestHeaders, TenantHeaderName);

            _observed.Enqueue(
                new ObservedRequest(
                    requestTenant,
                    request.Headers.Authorization?.Parameter,
                    sharedClientTenant
                )
            );

            if (Interlocked.Increment(ref _arrivedCount) >= releaseAfterRequestCount)
            {
                _allRequestsArrived.TrySetResult();
            }

            await _allRequestsArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    ResponseBodyForTenant(requestTenant),
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        }

        private static string? SingleHeaderValue(System.Net.Http.Headers.HttpHeaders headers, string name) =>
            headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
    }

    private static HttpClient CreateSharedClient(GatedRecordingHandler handler)
    {
        var responseHandler = new ConfigurationServiceResponseHandler(
            NullLogger<ConfigurationServiceResponseHandler>.Instance
        )
        {
            InnerHandler = handler,
        };

        var client = new HttpClient(responseHandler) { BaseAddress = new Uri(BaseAddress) };
        handler.SharedClient = client;
        return client;
    }

    private static ConfigurationServiceClaimSetProvider CreateProvider(HttpClient client)
    {
        var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
        A.CallTo(() =>
                tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._)
            )
            .Returns(CmsToken);

        return new ConfigurationServiceClaimSetProvider(
            new ConfigurationServiceApiClient(client),
            tokenHandler,
            new ConfigurationServiceContext("dms-client", "dms-secret", "fullaccess")
        );
    }

    /// <summary>
    /// One claim set named for the tenant that asked for it, so a crossed request is visible in the result.
    /// </summary>
    private static string ClaimSetMetadataBodyFor(string? tenant) =>
        JsonSerializer.Serialize(
            new List<ClaimSetMetadata> { new($"{tenant ?? "NoTenant"}ClaimSet", [], []) }
        );

    [TestFixture]
    [Parallelizable]
    public class Given_A_ClaimSet_Fetch_For_A_Tenant : ConfigurationServiceClaimSetProviderHeaderTests
    {
        private GatedRecordingHandler _handler = null!;
        private HttpClient _client = null!;

        [SetUp]
        public async Task Setup()
        {
            _handler = new GatedRecordingHandler(releaseAfterRequestCount: 1)
            {
                ResponseBodyForTenant = ClaimSetMetadataBodyFor,
            };
            _client = CreateSharedClient(_handler);

            await CreateProvider(_client).GetAllClaimSets("TenantA");
        }

        [TearDown]
        public void TearDown() => _client.Dispose();

        [Test]
        public void It_Should_Send_The_Tenant_On_The_Individual_Request()
        {
            _handler.Observed.Should().ContainSingle();
            _handler.Observed[0].RequestTenantHeader.Should().Be("TenantA");
        }

        [Test]
        public void It_Should_Send_The_Cms_Bearer_Token_On_The_Individual_Request()
        {
            _handler.Observed[0].RequestAuthorizationParameter.Should().Be(CmsToken);
        }

        [Test]
        public void It_Should_Not_Write_The_Tenant_To_The_Shared_Client()
        {
            _client.DefaultRequestHeaders.Contains(TenantHeaderName).Should().BeFalse();
        }

        [Test]
        public void It_Should_Not_Write_The_Authorization_To_The_Shared_Client()
        {
            _client.DefaultRequestHeaders.Authorization.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_ClaimSet_Fetch_Without_A_Tenant : ConfigurationServiceClaimSetProviderHeaderTests
    {
        private GatedRecordingHandler _handler = null!;
        private HttpClient _client = null!;

        [SetUp]
        public async Task Setup()
        {
            _handler = new GatedRecordingHandler(releaseAfterRequestCount: 1)
            {
                ResponseBodyForTenant = ClaimSetMetadataBodyFor,
            };
            _client = CreateSharedClient(_handler);

            await CreateProvider(_client).GetAllClaimSets();
        }

        [TearDown]
        public void TearDown() => _client.Dispose();

        [Test]
        public void It_Should_Omit_The_Tenant_Header()
        {
            _handler.Observed.Should().ContainSingle();
            _handler.Observed[0].RequestTenantHeader.Should().BeNull();
        }

        [Test]
        public void It_Should_Not_Write_The_Authorization_To_The_Shared_Client()
        {
            _client.DefaultRequestHeaders.Authorization.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_ClaimSet_Fetch_With_An_Empty_Tenant : ConfigurationServiceClaimSetProviderHeaderTests
    {
        private GatedRecordingHandler _handler = null!;
        private HttpClient _client = null!;

        [SetUp]
        public async Task Setup()
        {
            _handler = new GatedRecordingHandler(releaseAfterRequestCount: 1)
            {
                ResponseBodyForTenant = ClaimSetMetadataBodyFor,
            };
            _client = CreateSharedClient(_handler);

            await CreateProvider(_client).GetAllClaimSets("");
        }

        [TearDown]
        public void TearDown() => _client.Dispose();

        [Test]
        public void It_Should_Omit_The_Tenant_Header()
        {
            _handler.Observed.Should().ContainSingle();
            _handler.Observed[0].RequestTenantHeader.Should().BeNull();
        }
    }

    /// <summary>
    /// The cached provider's stampede lock is keyed per tenant, so two cold misses for different tenants
    /// are unsynchronized and reach the CMS provider concurrently. This is the window the fix closes.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Concurrent_Cold_Cache_Misses_For_Two_Tenants
        : ConfigurationServiceClaimSetProviderHeaderTests
    {
        private GatedRecordingHandler _handler = null!;
        private HttpClient _client = null!;
        private MemoryCache _memoryCache = null!;
        private IList<ClaimSet> _tenantAClaimSets = null!;
        private IList<ClaimSet> _tenantBClaimSets = null!;

        [SetUp]
        public async Task Setup()
        {
            _handler = new GatedRecordingHandler(releaseAfterRequestCount: 2)
            {
                ResponseBodyForTenant = ClaimSetMetadataBodyFor,
            };
            _client = CreateSharedClient(_handler);
            _memoryCache = new MemoryCache(new MemoryCacheOptions());

            var cachedProvider = new CachedClaimSetProvider(
                CreateProvider(_client),
                _memoryCache,
                new CacheSettings(),
                NullLogger<CachedClaimSetProvider>.Instance
            );

            Task<IList<ClaimSet>> tenantA = Task.Run(() => cachedProvider.GetAllClaimSets("TenantA"));
            Task<IList<ClaimSet>> tenantB = Task.Run(() => cachedProvider.GetAllClaimSets("TenantB"));

            IList<ClaimSet>[] results = await Task.WhenAll(tenantA, tenantB);
            _tenantAClaimSets = results[0];
            _tenantBClaimSets = results[1];
        }

        [TearDown]
        public void TearDown()
        {
            _memoryCache.Dispose();
            _client.Dispose();
        }

        [Test]
        public void It_Should_Send_Each_Tenant_On_Its_Own_Request()
        {
            _handler
                .Observed.Select(observed => observed.RequestTenantHeader)
                .Should()
                .BeEquivalentTo("TenantA", "TenantB");
        }

        [Test]
        public void It_Should_Not_Expose_Tenant_State_On_The_Shared_Client_During_The_Fetches()
        {
            _handler
                .Observed.Select(observed => observed.SharedClientTenantHeader)
                .Should()
                .AllSatisfy(sharedTenant => sharedTenant.Should().BeNull());
        }

        [Test]
        public void It_Should_Return_Each_Tenants_Own_Claim_Sets()
        {
            _tenantAClaimSets.Should().ContainSingle().Which.Name.Should().Be("TenantAClaimSet");
            _tenantBClaimSets.Should().ContainSingle().Which.Name.Should().Be("TenantBClaimSet");
        }

        [Test]
        public void It_Should_Cache_Each_Tenants_Claim_Sets_Under_Its_Own_Key()
        {
            _memoryCache
                .Get<IList<ClaimSet>>("ClaimSets:TenantA")
                .Should()
                .ContainSingle()
                .Which.Name.Should()
                .Be("TenantAClaimSet");

            _memoryCache
                .Get<IList<ClaimSet>>("ClaimSets:TenantB")
                .Should()
                .ContainSingle()
                .Which.Name.Should()
                .Be("TenantBClaimSet");
        }
    }
}
