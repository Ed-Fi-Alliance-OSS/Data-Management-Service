// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Response;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.IdentityCmsStubHost;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Proves the three tenant-existence outcomes stay distinguishable end to end: an identity request
/// travels the real host, the production tenant snapshot and the production Configuration Service
/// data-store provider to a stubbed tenant list, so a listed tenant continues, an unlisted one is a
/// tenant 404, and a transport failure or a null list is a 503, with no path collapsing absence into
/// unavailability or the reverse.
/// </summary>
public class IdentityTenantExistenceEndToEndTests
{
    private const string TenantNotFoundDetail = "The specified tenant could not be found.";
    private const string ServiceUnavailableType = "urn:ed-fi:api:service-unavailable";

    private StubHost _host = null!;
    private HttpStatusCode _status;
    private JsonNode? _body;

    private protected async Task SendIdentityGetAsync(CmsStub cms)
    {
        _host = Create(cms);
        using HttpClient client = _host.Factory.CreateClient();
        using HttpRequestMessage request = IdentityGet("605943412");
        using HttpResponseMessage response = await client.SendAsync(request);
        _status = response.StatusCode;
        _body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _host.Factory.DisposeAsync();

    protected HttpStatusCode Status => _status;

    protected string? ProblemType => _body?["type"]?.GetValue<string>();

    protected string? ProblemDetail => _body?["detail"]?.GetValue<string>();

    protected bool ReachedTheApplicationLookup =>
        _host.Cms.RequestPaths.Any(path => path.Contains("v3/apiClients/", StringComparison.Ordinal));

    protected bool TouchedTheIdentityService => Fake.GetCalls(_host.IdentityService).Any();

    [TestFixture]
    public class Given_A_Tenant_The_Configuration_Service_Lists : IdentityTenantExistenceEndToEndTests
    {
        [OneTimeSetUp]
        public async Task OneTimeSetUp() => await SendIdentityGetAsync(new CmsStub());

        [Test]
        public void It_continues_past_tenant_existence_to_the_identity_service()
        {
            Status.Should().Be(HttpStatusCode.NotFound);
            ProblemType.Should().Be(IdentityFailureResponse.OperationNotSupportedType);
            TouchedTheIdentityService.Should().BeTrue();
        }

        [Test]
        public void It_goes_on_to_bind_the_client_to_the_tenant()
        {
            ReachedTheApplicationLookup.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_A_Tenant_The_Configuration_Service_Does_Not_List : IdentityTenantExistenceEndToEndTests
    {
        [OneTimeSetUp]
        public async Task OneTimeSetUp() =>
            await SendIdentityGetAsync(new CmsStub(tenantsResponse: _ => TenantList("some-other-tenant")));

        [Test]
        public void It_returns_a_tenant_404()
        {
            Status.Should().Be(HttpStatusCode.NotFound);
            ProblemType.Should().Be("urn:ed-fi:api:not-found");
            ProblemDetail.Should().Be(TenantNotFoundDetail);
        }

        [Test]
        public void It_stops_before_the_client_binding_and_the_identity_service()
        {
            ReachedTheApplicationLookup.Should().BeFalse();
            TouchedTheIdentityService.Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_A_Transport_Failure_Fetching_The_Tenant_List : IdentityTenantExistenceEndToEndTests
    {
        [OneTimeSetUp]
        public async Task OneTimeSetUp() =>
            await SendIdentityGetAsync(
                new CmsStub(tenantsResponse: _ =>
                    throw new HttpRequestException("Configuration Service is unreachable")
                )
            );

        [Test]
        public void It_returns_a_503_rather_than_a_tenant_404()
        {
            Status.Should().Be(HttpStatusCode.ServiceUnavailable);
            ProblemType.Should().Be(ServiceUnavailableType);
        }

        [Test]
        public void It_stops_before_the_client_binding_and_the_identity_service()
        {
            ReachedTheApplicationLookup.Should().BeFalse();
            TouchedTheIdentityService.Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_A_Tenant_List_That_Deserializes_To_Null : IdentityTenantExistenceEndToEndTests
    {
        [OneTimeSetUp]
        public async Task OneTimeSetUp() =>
            await SendIdentityGetAsync(new CmsStub(tenantsResponse: _ => NullTenantList()));

        [Test]
        public void It_returns_a_503_rather_than_a_tenant_404()
        {
            Status.Should().Be(HttpStatusCode.ServiceUnavailable);
            ProblemType.Should().Be(ServiceUnavailableType);
        }

        [Test]
        public void It_stops_before_the_client_binding_and_the_identity_service()
        {
            ReachedTheApplicationLookup.Should().BeFalse();
            TouchedTheIdentityService.Should().BeFalse();
        }
    }
}
