// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Identity;
using FakeItEasy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// A real multi-tenant DMS host whose identity pipeline reaches a stubbed Configuration Service: the
/// production tenant snapshot, application-context cache and claim-set cache all run over one
/// <see cref="CmsStub" />. Only JWT validation, the identity service, and the host-wide datastore
/// provider are replaced. That datastore provider reports no tenants, so startup's tenant load and
/// claim-set warm-up make no Configuration Service call, and every lookup, token acquisition
/// included, first happens inside a test's own request.
/// </summary>
internal static class IdentityCmsStubHost
{
    public const string Tenant = "cms-stub-tenant";
    public const string ClientId = "identity-cms-stub-client";
    public const string ClaimSetName = "IdentityCmsStubClaimSet";
    public const string CallerHeader = "X-Identity-Test-Caller";
    private static readonly Guid _stableClientUuid = Guid.Parse("33333333-3333-4333-8333-333333333333");

    public sealed record StubHost(
        WebApplicationFactory<Program> Factory,
        CmsStub Cms,
        IIdentityService IdentityService,
        ServerOutcomeRecorder Outcomes
    );

    /// <summary>What the server side did with one tagged request, observed ahead of every DMS middleware.</summary>
    public sealed record ServerOutcome(bool RequestAborted, Exception? Escaped, bool ResponseStarted);

    /// <summary>
    /// A Configuration Service double. Answers every call from a canned responder, lets a test replace
    /// the tenant-list answer, records each request path, and optionally blocks every request whose path
    /// contains a gate substring until the test releases it.
    /// </summary>
    public sealed class CmsStub(
        string? gateUrlSubstring = null,
        Func<HttpRequestMessage, HttpResponseMessage>? tenantsResponse = null
    ) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gateReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _gateReleased = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public ConcurrentQueue<string> RequestPaths { get; } = new();

        /// <summary>Completes the first time a request matching the gate substring arrives.</summary>
        public Task GateReached => _gateReached.Task;

        /// <summary>Releases every request currently blocked at the gate, and every later one.</summary>
        public void ReleaseGate() => _gateReleased.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            RequestPaths.Enqueue(path);

            if (gateUrlSubstring is not null && path.Contains(gateUrlSubstring, StringComparison.Ordinal))
            {
                _gateReached.TrySetResult();
                await _gateReleased.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (tenantsResponse is not null && path.Contains("v3/tenants/", StringComparison.Ordinal))
            {
                return tenantsResponse(request);
            }

            return CannedResponse(path);
        }

        private static HttpResponseMessage CannedResponse(string path)
        {
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

            if (path.Contains("v3/tenants/", StringComparison.Ordinal))
            {
                return TenantList(Tenant);
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

            if (path.Contains("v3/authorizationMetadata", StringComparison.Ordinal))
            {
                return JsonResponse(
                    new[]
                    {
                        new
                        {
                            claimSetName = ClaimSetName,
                            claims = new[]
                            {
                                new
                                {
                                    name = $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity",
                                    authorizationId = 1,
                                },
                            },
                            authorizations = new[]
                            {
                                new
                                {
                                    id = 1,
                                    actions = new[] { IdentityAction("Create"), IdentityAction("Read") },
                                },
                            },
                        },
                    }
                );
            }

            throw new InvalidOperationException(
                $"IdentityCmsStubHost received an unexpected CMS request to {path}."
            );
        }

        private static object IdentityAction(string name) =>
            new
            {
                name,
                authorizationStrategies = new[]
                {
                    new { name = AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired },
                },
            };
    }

    /// <summary>
    /// Records, ahead of every DMS middleware, how the server finished each request carrying
    /// <see cref="CallerHeader" />: whether <c>RequestAborted</c> fired, what escaped the pipeline, and
    /// whether any response was started.
    /// </summary>
    public sealed class ServerOutcomeRecorder : IStartupFilter
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ServerOutcome>> _outcomes = new();

        public Task<ServerOutcome> OutcomeOf(string caller) => SourceFor(caller).Task;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    async (context, nextMiddleware) =>
                    {
                        string? caller = context.Request.Headers[CallerHeader];
                        if (caller is null)
                        {
                            await nextMiddleware();
                            return;
                        }

                        Exception? escaped = null;
                        try
                        {
                            await nextMiddleware();
                        }
                        catch (Exception exception)
                        {
                            escaped = exception;
                            throw;
                        }
                        finally
                        {
                            SourceFor(caller)
                                .TrySetResult(
                                    new ServerOutcome(
                                        context.RequestAborted.IsCancellationRequested,
                                        escaped,
                                        context.Response.HasStarted
                                    )
                                );
                        }
                    }
                );
                next(app);
            };

        private TaskCompletionSource<ServerOutcome> SourceFor(string caller) =>
            _outcomes.GetOrAdd(
                caller,
                _ => new TaskCompletionSource<ServerOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )
            );
    }

    public static HttpRequestMessage IdentityGet(string uniqueId, string? caller = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/{Tenant}/identity/v2/identities/{uniqueId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "identity-cms-stub-bearer");
        if (caller is not null)
        {
            request.Headers.Add(CallerHeader, caller);
        }
        return request;
    }

    public static HttpResponseMessage TenantList(params string[] names) =>
        JsonResponse(names.Select((name, index) => new { Id = index + 1L, Name = name }).ToArray());

    public static HttpResponseMessage NullTenantList() =>
        new(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") };

    public static StubHost Create(CmsStub cms)
    {
        var responseHandler = new ConfigurationServiceResponseHandler(
            NullLogger<ConfigurationServiceResponseHandler>.Instance
        )
        {
            InnerHandler = cms,
        };
        var apiClient = new ConfigurationServiceApiClient(
            new HttpClient(responseHandler) { BaseAddress = new Uri("https://cms.example.com/") }
        );

        IJwtValidationService jwtValidationService = CreateJwtValidationService();

        var identityService = A.Fake<IIdentityService>();
        A.CallTo(() => identityService.Capabilities).Returns(IdentityCapabilities.None);

        var startupDataStoreProvider = A.Fake<IDataStoreProvider>();
        A.CallTo(() => startupDataStoreProvider.LoadTenants(A<CancellationToken>._))
            .Returns(new List<string>());

        var outcomes = new ServerOutcomeRecorder();

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:EnableIdentityManagement"] = "true",
                            ["AppSettings:MultiTenancy"] = "true",
                        }
                    );
                }
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);

                services.AddTransient(_ => startupDataStoreProvider);

                services.RemoveAll<IJwtValidationService>();
                services.AddSingleton(jwtValidationService);

                services.Replace(ServiceDescriptor.Scoped<IIdentityService>(_ => identityService));

                // Every CMS-facing production provider resolves this client, so the token handler, the
                // data-store provider, the application provider and the claim-set provider all travel
                // over the stub.
                services.RemoveAll<ConfigurationServiceApiClient>();
                services.AddSingleton(apiClient);

                // The identity snapshot reads tenants through the production CMS data-store provider
                // rather than the host-wide stand-in above.
                services.RemoveAll<IdentityTenantSnapshot>();
                services.AddSingleton(serviceProvider => new IdentityTenantSnapshot(
                    serviceProvider.GetRequiredService<ConfigurationServiceDataStoreProvider>(),
                    serviceProvider.GetRequiredService<TimeProvider>(),
                    serviceProvider.GetRequiredService<IHostApplicationLifetime>(),
                    serviceProvider.GetRequiredService<ILogger<IdentityTenantSnapshot>>()
                ));

                // Restores the production claim-set cache that TestMockHelper replaced with a fake.
                services.RemoveAll<IClaimSetProvider>();
                services.AddSingleton<IClaimSetProvider>(serviceProvider =>
                    serviceProvider.GetRequiredService<CachedClaimSetProvider>()
                );

                services.AddSingleton<IStartupFilter>(outcomes);
            });
        });

        return new StubHost(factory, cms, identityService, outcomes);
    }

    private static IJwtValidationService CreateJwtValidationService()
    {
        var jwtValidationService = A.Fake<IJwtValidationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("client_id", ClientId)], "test"));
        var clientAuthorizations = new ClientAuthorizations(
            TokenId: "identity-cms-stub-token-id",
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
        return jwtValidationService;
    }

    private static HttpResponseMessage JsonResponse(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
}
