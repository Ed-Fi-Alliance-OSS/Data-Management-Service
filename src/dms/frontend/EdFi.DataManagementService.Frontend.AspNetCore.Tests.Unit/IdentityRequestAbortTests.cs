// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Identity;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Proves A16 at the HTTP boundary: the request must run inside the real identity pipeline, blocked at
/// a real CMS call, so abort propagation through DMS's own code is what gets exercised - not merely
/// that <c>TestServer</c> links client cancellation to <see cref="Microsoft.AspNetCore.Http.HttpContext.RequestAborted" />
/// (already established by the Contract round's probe (b), and reused here only as this fixture's
/// precondition: <c>cts.Token</c> is passed directly into <c>client.GetAsync</c>, with no fallback
/// middleware driving <see cref="Microsoft.AspNetCore.Http.HttpContext.Abort" />).
/// <para>
/// The host is real (<see cref="WebApplicationFactory{TEntryPoint}" /> over <see cref="Program" />), with
/// only <see cref="IJwtValidationService" />, <see cref="IClaimSetProvider" />, <see cref="IIdentityService" />,
/// and the CMS-facing <see cref="ConfigurationServiceApiClient" /> replaced: everything else, including
/// the production <c>ValidateClientTenantBindingMiddleware</c> and the real <c>CachedApplicationContextProvider</c>
/// / <c>ConfigurationServiceApplicationProvider</c> chain, is untouched. The CMS transport is a
/// <see cref="GatedCmsHandler" /> (the same shape <see cref="Identity.IdentityCmsCancellationTests" />
/// uses for A15) gated on the application-lookup URL (<c>v3/apiClients/</c>), so an identity GET genuinely
/// reaches that real CMS call and blocks there before the identity service is ever resolved.
/// </para>
/// </summary>
[TestFixture]
public class IdentityRequestAbortTests
{
    private const string ClientId = "identity-request-abort-client";
    private const string ClaimSetName = "IdentityRequestAbortClaimSet";
    private static readonly Guid _stableClientUuid = Guid.Parse("33333333-3333-4333-8333-333333333333");

    /// <summary>
    /// Blocks every CMS request whose path contains a chosen gate substring until the test releases it,
    /// signaling when the request first arrived. Every other CMS request answers immediately from a
    /// canned responder. Same shape as <see cref="Identity.IdentityCmsCancellationTests" />'s
    /// <c>GatedCmsHandler</c>, duplicated here because the two fixtures live in different assemblies.
    /// </summary>
    private sealed class GatedCmsHandler(
        string gateUrlSubstring,
        Func<HttpRequestMessage, HttpResponseMessage> respond
    ) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gateReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _gateReleased = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task GateReached => _gateReached.Task;

        public void ReleaseGate() => _gateReleased.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Contains(gateUrlSubstring, StringComparison.Ordinal))
            {
                _gateReached.TrySetResult();
                await _gateReleased.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return respond(request);
        }
    }

    /// <summary>
    /// A thin pass-through middleware, ahead of routing, that only observes whether
    /// <c>RequestAborted</c> fired for the request it wraps. It always calls <c>next()</c>, so the
    /// identity route below it is always dispatched - the CMS gate, not this middleware, is what blocks
    /// the request.
    /// </summary>
    private sealed class RequestAbortedObservingStartupFilter(TaskCompletionSource requestAbortedObserved)
        : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    async (context, nextMiddleware) =>
                    {
                        using CancellationTokenRegistration registration = context.RequestAborted.Register(
                            () =>
                                requestAbortedObserved.TrySetResult()
                        );

                        try
                        {
                            await nextMiddleware();
                        }
                        finally
                        {
                            // Belt-and-suspenders alongside the Register callback above: even if the
                            // callback's own timing were ever in question, the flag itself is the
                            // authoritative signal that RequestAborted fired for this request.
                            if (context.RequestAborted.IsCancellationRequested)
                            {
                                requestAbortedObserved.TrySetResult();
                            }
                        }
                    }
                );
                next(app);
            };
    }

    private sealed record TestHost(
        WebApplicationFactory<Program> Factory,
        GatedCmsHandler Handler,
        TaskCompletionSource RequestAbortedObserved,
        IIdentityService IdentityService
    );

    private static HttpResponseMessage BuildCannedResponse(HttpRequestMessage request)
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (path.Contains("connect/token", StringComparison.Ordinal))
        {
            return JsonResponse(
                new
                {
                    access_token = "cms-test-token",
                    token_type = "bearer",
                    expires_in = 300,
                }
            );
        }

        if (path.Contains("v3/apiClients/", StringComparison.Ordinal))
        {
            return JsonResponse(
                new
                {
                    id = 1L,
                    applicationId = 1L,
                    clientId = ClientId,
                    clientUuid = _stableClientUuid,
                    dataStoreIds = Array.Empty<long>(),
                    ownershipTokenIds = Array.Empty<short>(),
                }
            );
        }

        throw new InvalidOperationException(
            $"IdentityRequestAbortTests received an unexpected CMS request to {path}."
        );
    }

    private static HttpResponseMessage JsonResponse(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    private static TestHost CreateHost()
    {
        var requestAbortedObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var handler = new GatedCmsHandler("v3/apiClients/", BuildCannedResponse);
        var responseHandler = new ConfigurationServiceResponseHandler(
            NullLogger<ConfigurationServiceResponseHandler>.Instance
        )
        {
            InnerHandler = handler,
        };
        var apiClient = new ConfigurationServiceApiClient(
            new HttpClient(responseHandler) { BaseAddress = new Uri("https://cms.example.com/") }
        );

        var jwtValidationService = A.Fake<IJwtValidationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("client_id", ClientId)], "test"));
        var clientAuthorizations = new ClientAuthorizations(
            TokenId: "identity-request-abort-token-id",
            ClientId: ClientId,
            ClaimSetName: ClaimSetName,
            EducationOrganizationIds: [],
            NamespacePrefixes: [],
            DataStoreIds: []
        );
        A.CallTo(() =>
                jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult(((ClaimsPrincipal?)principal, (ClientAuthorizations?)clientAuthorizations))
            );

        // Grants the identity service claim so a request that is not cancelled - the negative control -
        // genuinely clears ServiceClaimAuthorizationMiddleware and reaches the identity service, rather
        // than being forbidden for an unrelated reason that would make either assertion meaningless.
        var claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
            .Returns(
                Task.FromResult<IList<ClaimSet>>([
                    new ClaimSet(
                        ClaimSetName,
                        [
                            new ResourceClaim(
                                $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity",
                                "Create",
                                [
                                    new AuthorizationStrategy(
                                        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                                    ),
                                ]
                            ),
                            new ResourceClaim(
                                $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity",
                                "Read",
                                [
                                    new AuthorizationStrategy(
                                        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                                    ),
                                ]
                            ),
                        ]
                    ),
                ])
            );

        var identityService = A.Fake<IIdentityService>();
        A.CallTo(() => identityService.Capabilities).Returns(IdentityCapabilities.None);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AppSettings:EnableIdentityManagement"] = "true" }
                    );
                }
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);

                services.RemoveAll<IJwtValidationService>();
                services.AddSingleton(jwtValidationService);

                services.RemoveAll<IClaimSetProvider>();
                services.AddSingleton(claimSetProvider);

                services.RemoveAll<IIdentityService>();
                services.AddSingleton(identityService);

                // Leaves the production IApplicationContextProvider registration
                // (CachedApplicationContextProvider over ConfigurationServiceApplicationProvider) in
                // place and swaps only the transport its CMS calls travel over, so
                // ValidateClientTenantBindingMiddleware's application lookup is the real production call,
                // genuinely blocked at the gate rather than short-circuited ahead of it.
                services.RemoveAll<ConfigurationServiceApiClient>();
                services.AddSingleton(apiClient);

                services.AddSingleton<IStartupFilter>(
                    new RequestAbortedObservingStartupFilter(requestAbortedObserved)
                );
            });
        });

        return new TestHost(factory, handler, requestAbortedObserved, identityService);
    }

    [Test]
    public async Task Cancelling_the_client_token_aborts_the_request_before_the_identity_provider_runs()
    {
        TestHost host = CreateHost();
        await using WebApplicationFactory<Program> factory = host.Factory;
        using HttpClient client = factory.CreateClient();
        using var cancellationSource = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/identity/v2/identities/605943412");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "identity-request-abort-bearer"
        );

        Task<HttpResponseMessage> requestTask = client.SendAsync(request, cancellationSource.Token);

        await host.Handler.GateReached.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellationSource.CancelAsync();

        Func<Task> act = async () => await requestTask;
        await act.Should().ThrowAsync<TaskCanceledException>();

        await host.RequestAbortedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        A.CallTo(host.IdentityService).MustNotHaveHappened();
    }

    /// <summary>
    /// The negative control: with the identical gate, releasing it instead of cancelling the client must
    /// let the request reach the identity service. Without this, "MustNotHaveHappened" above would be
    /// vacuously true regardless of whether cancellation ever propagated correctly.
    /// </summary>
    [Test]
    public async Task Releasing_the_gate_without_cancelling_reaches_the_identity_provider()
    {
        TestHost host = CreateHost();
        await using WebApplicationFactory<Program> factory = host.Factory;
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/identity/v2/identities/605943412");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "identity-request-abort-bearer"
        );

        Task<HttpResponseMessage> requestTask = client.SendAsync(request);

        await host.Handler.GateReached.WaitAsync(TimeSpan.FromSeconds(5));

        host.Handler.ReleaseGate();

        using HttpResponseMessage response = await requestTask.WaitAsync(TimeSpan.FromSeconds(5));

        // NoIdentityService-equivalent (Capabilities = None) still answers operation-unsupported 404, but
        // only after the gate cleared and the pipeline actually reached the capability gate.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        A.CallTo(() => host.IdentityService.Capabilities).MustHaveHappened();
    }
}
