// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Extensions.Logging;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// The production host with the real plugin loader, authorization on, the identity toggle on,
/// multitenancy with route qualifiers, an identity-granting claim set and per-token clients.
/// </summary>
/// <remarks>
/// <para>
/// Built directly rather than by widening <see cref="PluginHostProbe.CreateHost"/>, whose
/// bypass-authorization shape other classes depend on. It does not derive from
/// <c>ApiIntegrationTestBase</c>, so it leases no database and runs wherever the plugin lane does:
/// the tenant provider returns tenants and no data stores, and nothing dials.
/// </para>
/// <para>
/// Settings are applied in a fixed order so a later layer wins: the probe's settings, the identity
/// settings, the fixture's settings, then the caller's. The authority has to be
/// <see cref="FakeOidcConfigurationManager.Issuer"/>, because startup aborts on any other value
/// while it warms the OIDC metadata.
/// </para>
/// </remarks>
internal sealed class IdentityPluginHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _pluginRoot;

    private IdentityPluginHost(
        WebApplicationFactory<Program> factory,
        PluginLogCapture capture,
        RecordingConfigurationServiceApplicationProvider bindings,
        string pluginRoot,
        string startupStatusFilePath
    )
    {
        _factory = factory;
        _pluginRoot = pluginRoot;
        Capture = capture;
        Bindings = bindings;
        StartupStatusFilePath = startupStatusFilePath;
    }

    public WebApplicationFactory<Program> Factory => _factory;

    /// <summary>Every event the host logged, after the identity redaction stage production applies.</summary>
    public PluginLogCapture Capture { get; }

    /// <summary>The client-to-tenant binding table, which records every lookup the host made.</summary>
    public RecordingConfigurationServiceApplicationProvider Bindings { get; }

    public string StartupStatusFilePath { get; }

    /// <summary>
    /// Builds the host without starting it. The host starts on the first <see cref="TryBoot"/> or
    /// <see cref="CreateClient"/>.
    /// </summary>
    /// <param name="stagedFixtures">
    /// The packed contract fixtures to copy into a temporary plugin root, by directory name.
    /// </param>
    /// <param name="allowed">The <c>Plugins:Allowed</c> value, which may name fewer fixtures than are staged.</param>
    /// <param name="projectReferenceFixtures">
    /// Fixtures staged under <see cref="PluginHostProbe.StagedFixtureRoot"/> (the project-reference
    /// fixtures, such as <c>Acme.DmsContributor</c>) to place in the same plugin root, by directory name.
    /// </param>
    /// <param name="fixtureSettings">Settings the fixture reads, applied before the caller's.</param>
    /// <param name="settings">Settings applied last, so they win over everything else.</param>
    /// <param name="configureServices">Applied after the doubles, so it can replace any of them.</param>
    public static IdentityPluginHost Create(
        IReadOnlyList<string> stagedFixtures,
        string allowed,
        IReadOnlyDictionary<string, string>? fixtureSettings = null,
        IReadOnlyDictionary<string, string>? settings = null,
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyList<string>? projectReferenceFixtures = null
    )
    {
        FixtureContext fixture = FixtureContextLoader.Load(FixtureKey.ProfileRootOnlyMerge);
        string pluginRoot = PluginHostProbe.CreatePluginRootFromSource(
            PluginHostProbe.PackedContractFixtureRoot,
            [.. stagedFixtures]
        );

        if (projectReferenceFixtures is { Count: > 0 })
        {
            string projectReferenceRoot = PluginHostProbe.CreatePluginRootFromSource(
                PluginHostProbe.StagedFixtureRoot,
                [.. projectReferenceFixtures]
            );

            foreach (string name in projectReferenceFixtures)
            {
                Directory.Move(Path.Combine(projectReferenceRoot, name), Path.Combine(pluginRoot, name));
            }

            PluginHostProbe.DeleteIfPresent(projectReferenceRoot);
        }
        string startupStatusFilePath = Path.Combine(
            Path.GetTempPath(),
            $"identity-plugin-startup-{Guid.NewGuid():N}.json"
        );
        PluginLogCapture capture = new();
        Logger captureLogger = capture.CreateLogger(applyIdentityRedaction: true);
        RecordingConfigurationServiceApplicationProvider bindings = new(IdentityTestClients.ResolveBinding);

        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder =>
            {
                builder.UseEnvironment("Test");

                Dictionary<string, string> layered = new()
                {
                    ["AppSettings:UseApiSchemaPath"] = "true",
                    ["AppSettings:ApiSchemaPath"] = fixture.ApiSchemaDirectory,
                    ["AppSettings:StartupStatusFilePath"] = startupStatusFilePath,
                    ["AppSettings:Datastore"] = "postgresql",
                    ["ConfigurationServiceSettings:BaseUrl"] = "http://localhost/test-cms",
                    ["ConfigurationServiceSettings:ClientId"] = "test-cms-client",
                    ["ConfigurationServiceSettings:ClientSecret"] = "test-cms-secret",
                    ["ConfigurationServiceSettings:Scope"] = "edfi_admin_api/full_access",
                    ["Plugins:Directory"] = pluginRoot,
                    ["Plugins:Allowed"] = allowed,
                    ["AppSettings:BypassAuthorization"] = "false",
                    ["JwtAuthentication:Authority"] = FakeOidcConfigurationManager.Issuer,
                    ["AppSettings:EnableIdentityManagement"] = "true",
                    ["AppSettings:MultiTenancy"] = "true",
                    ["AppSettings:RouteQualifierSegments"] = IdentityTestClients.RouteQualifierSegments,
                };

                foreach ((string key, string value) in fixtureSettings ?? new Dictionary<string, string>())
                {
                    layered[key] = value;
                }

                foreach ((string key, string value) in settings ?? new Dictionary<string, string>())
                {
                    layered[key] = value;
                }

                foreach ((string key, string value) in layered)
                {
                    builder.UseSetting(key, value);
                }

                builder.ConfigureServices(services =>
                {
                    // After AddServices clears the providers, as PluginHostProbe.CreateHost does.
                    services.AddSingleton<ILoggerProvider>(
                        new SerilogLoggerProvider(captureLogger, dispose: true)
                    );

                    ExternalDoublesRegistration.RegisterAll(
                        services,
                        fixture,
                        leasedConnectionString: "Host=localhost;Database=unused;Username=unused;Password=unused",
                        new IdentityGrantingClaimSetProvider(fixture),
                        clientEducationOrganizationIds: [],
                        applicationContextConfigurationProvider: bindings,
                        dataStoreProviderOverride: new IdentityTenantsDataStoreProvider(
                            IdentityTestClients.Tenants
                        ),
                        jwtValidationServiceOverride: IdentityTestClients.CreateJwtValidationService()
                    );

                    configureServices?.Invoke(services);
                });
            }
        );

        return new IdentityPluginHost(factory, capture, bindings, pluginRoot, startupStatusFilePath);
    }

    /// <summary>
    /// Starts the host and returns null, or returns the exception the start failed with. A boot the
    /// loader or a startup guard refuses surfaces through the first client, not through the factory.
    /// </summary>
    public Exception? TryBoot()
    {
        try
        {
            using HttpClient client = _factory.CreateClient();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// A client that does not follow redirects, presenting <paramref name="token"/> as a bearer
    /// token when one is given.
    /// </summary>
    public HttpClient CreateClient(string? token = null)
    {
        HttpClient client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        PluginHostProbe.DeleteIfPresent(_pluginRoot);
        PluginHostProbe.DeleteIfPresent(StartupStatusFilePath);
    }
}
