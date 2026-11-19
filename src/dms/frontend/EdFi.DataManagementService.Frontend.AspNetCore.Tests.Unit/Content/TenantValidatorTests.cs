// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Content;

/// <summary>
/// <see cref="TenantValidator.DetermineTenantExistenceAsync" /> over the real
/// <see cref="ConfigurationServiceDataStoreProvider" />, its <see cref="ConfigurationServiceResponseHandler" />
/// and a fake Configuration Service. The fake answers an unknown tenant's data-store request with 400, as the
/// Configuration Service's tenant resolution does, so the provider's failure is the real wrapped one.
/// </summary>
public class TenantValidatorTests
{
    private const string Tenant = "Tenant_255901";

    private static readonly TimeSpan _hangGuard = TimeSpan.FromSeconds(30);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string body = "{}") =>
        Task.FromResult(Json(status, body));

    private static Task<HttpResponseMessage> UnknownTenant() =>
        Respond(HttpStatusCode.BadRequest, """{"detail":"Invalid tenant: Tenant_255901","status":400}""");

    private static Task<HttpResponseMessage> TenantList(params string[] names) =>
        Respond(
            HttpStatusCode.OK,
            "["
                + string.Join(
                    ",",
                    names.Select((name, index) => $$"""{"id":{{index + 1}},"name":"{{name}}"}""")
                )
                + "]"
        );

    /// <summary>A fake Configuration Service: data-store and tenant requests each answered by their own function.</summary>
    private sealed class FakeConfigurationService : HttpMessageHandler
    {
        private readonly List<string> _paths = [];

        public Func<CancellationToken, Task<HttpResponseMessage>> DataStores { get; set; } =
            _ => Respond(HttpStatusCode.OK, "[]");

        public Func<CancellationToken, Task<HttpResponseMessage>> Tenants { get; set; } =
            _ => TenantList(Tenant);

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_paths)
                {
                    return [.. _paths];
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string path = request.RequestUri!.AbsolutePath;
            lock (_paths)
            {
                _paths.Add(path);
            }
            return path.EndsWith("/v3/tenants/", StringComparison.Ordinal)
                ? Tenants(cancellationToken)
                : DataStores(cancellationToken);
        }
    }

    private FakeConfigurationService _configurationService = null!;
    private HttpClient _httpClient = null!;
    private ConfigurationServiceDataStoreProvider _provider = null!;
    private TenantValidator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _configurationService = new FakeConfigurationService();
        IConfigurationServiceTokenHandler tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
        A.CallTo(() =>
                tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._)
            )
            .Returns(new ConfigurationServiceTokenLease("token", 1));

        _httpClient = new(
            new ConfigurationServiceResponseHandler(NullLogger<ConfigurationServiceResponseHandler>.Instance)
            {
                InnerHandler = _configurationService,
            }
        )
        {
            BaseAddress = new Uri("https://cms.example.org/"),
        };

        _provider = new ConfigurationServiceDataStoreProvider(
            new ConfigurationServiceApiClient(_httpClient),
            tokenHandler,
            new ConfigurationServiceContext("client", "secret", "scope"),
            NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
            new ConnectionStringDecryptionService("TestEncryptionKey123456789012345678901234567890")
        );
        _validator = new TenantValidator(_provider, NullLogger<TenantValidator>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _configurationService.Dispose();
    }

    private Task<TenantExistence> Determine(
        string tenant = Tenant,
        CancellationToken cancellationToken = default
    ) => _validator.DetermineTenantExistenceAsync(tenant, cancellationToken);

    [Test]
    public async Task It_answers_a_cached_tenant_without_asking_the_configuration_service()
    {
        await _provider.LoadDataStores(Tenant);
        _configurationService.DataStores = _ => Respond(HttpStatusCode.ServiceUnavailable);
        _configurationService.Tenants = _ => Respond(HttpStatusCode.ServiceUnavailable);
        int requestsBefore = _configurationService.Paths.Count;

        (await Determine()).Should().Be(TenantExistence.Exists);
        _configurationService.Paths.Should().HaveCount(requestsBefore);
    }

    [Test]
    public async Task It_finds_a_tenant_whose_data_stores_load_even_when_it_has_none()
    {
        _configurationService.Tenants = _ => Respond(HttpStatusCode.ServiceUnavailable);

        (await Determine()).Should().Be(TenantExistence.Exists);
        _provider
            .TenantExists(Tenant)
            .Should()
            .BeTrue("the load caches the tenant, as ValidateTenantAsync's does");
    }

    [Test]
    public async Task It_confirms_absence_only_from_a_tenant_list_without_the_tenant()
    {
        _configurationService.DataStores = _ => UnknownTenant();
        _configurationService.Tenants = _ => TenantList("Tenant_255902");

        (await Determine()).Should().Be(TenantExistence.Absent);
    }

    [Test]
    public async Task It_finds_a_listed_tenant_whose_data_stores_failed_to_load_in_any_letter_case()
    {
        _configurationService.DataStores = _ => Respond(HttpStatusCode.InternalServerError);
        _configurationService.Tenants = _ => TenantList("TENANT_255901");

        (await Determine()).Should().Be(TenantExistence.Exists);
    }

    [TestCase(HttpStatusCode.BadRequest, HttpStatusCode.ServiceUnavailable)]
    [TestCase(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.ServiceUnavailable, HttpStatusCode.InternalServerError)]
    public async Task It_is_undetermined_when_the_tenant_list_cannot_be_loaded(
        HttpStatusCode dataStores,
        HttpStatusCode tenants
    )
    {
        _configurationService.DataStores = _ => Respond(dataStores);
        _configurationService.Tenants = _ => Respond(tenants);

        (await Determine()).Should().Be(TenantExistence.Undetermined);
    }

    [Test]
    public async Task It_is_undetermined_when_the_configuration_service_cannot_be_reached()
    {
        _configurationService.DataStores = _ => throw new HttpRequestException("connection refused");
        _configurationService.Tenants = _ => throw new HttpRequestException("connection refused");

        (await Determine()).Should().Be(TenantExistence.Undetermined);
    }

    [Test]
    public async Task It_is_undetermined_when_the_tenant_list_is_malformed()
    {
        _configurationService.DataStores = _ => UnknownTenant();
        _configurationService.Tenants = _ => Respond(HttpStatusCode.OK, "[{\"id\":1,\"name\":\"\"}]");

        (await Determine()).Should().Be(TenantExistence.Undetermined);
    }

    [Test]
    public async Task It_propagates_cancellation_during_the_data_store_load_without_asking_for_the_tenant_list()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _configurationService.DataStores = async cancellationToken =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Json(HttpStatusCode.OK, "[]");
        };
        using CancellationTokenSource cancellation = new();

        Task<TenantExistence> determination = Determine(cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(_hangGuard);
        await cancellation.CancelAsync();

        Func<Task> act = () => determination.WaitAsync(_hangGuard);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _configurationService.Paths.Should().NotContain(path => path.EndsWith("/v3/tenants/"));
    }

    [Test]
    public async Task It_propagates_cancellation_during_the_tenant_list_load_rather_than_answering_undetermined()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _configurationService.DataStores = _ => UnknownTenant();
        _configurationService.Tenants = async cancellationToken =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Json(HttpStatusCode.OK, "[]");
        };
        using CancellationTokenSource cancellation = new();

        Task<TenantExistence> determination = Determine(cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(_hangGuard);
        await cancellation.CancelAsync();

        Func<Task> act = () => determination.WaitAsync(_hangGuard);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_keeps_the_existing_validation_answer_for_other_callers()
    {
        _configurationService.DataStores = _ => Respond(HttpStatusCode.ServiceUnavailable);
        _configurationService.Tenants = _ => Respond(HttpStatusCode.ServiceUnavailable);

        (await _validator.ValidateTenantAsync(Tenant)).Should().BeFalse();
        _configurationService.Paths.Should().NotContain(path => path.EndsWith("/v3/tenants/"));
    }
}
