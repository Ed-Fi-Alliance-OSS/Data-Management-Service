// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Startup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NUnit.Framework;
using Serilog;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
public class AuthStartupTaskRegistrationTests
{
    [TestFixture]
    public class Given_DmsCoreServices_Are_Registered : AuthStartupTaskRegistrationTests
    {
        private IServiceCollection _services = null!;

        [SetUp]
        public void Setup()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

            _services = new ServiceCollection();
            _services.AddDmsDefaultConfiguration(
                new LoggerConfiguration().CreateLogger(),
                configuration.GetSection("CircuitBreaker"),
                configuration.GetSection("DeadlockRetry"),
                false
            );
        }

        [Test]
        public void It_registers_the_oidc_warm_up_task_as_an_IDmsStartupTask()
        {
            _services
                .Should()
                .Contain(descriptor =>
                    descriptor.ServiceType == typeof(IDmsStartupTask)
                    && descriptor.ImplementationType != null
                    && descriptor.ImplementationType.Name == "WarmUpOidcMetadataTask"
                );
        }

        [Test]
        public void It_registers_the_cache_claim_sets_task_as_an_IDmsStartupTask()
        {
            _services
                .Should()
                .Contain(descriptor =>
                    descriptor.ServiceType == typeof(IDmsStartupTask)
                    && descriptor.ImplementationType != null
                    && descriptor.ImplementationType.Name == "CacheClaimSetsTask"
                );
        }

        [Test]
        public void It_registers_deadlock_retry_settings_for_backend_startup_services()
        {
            ServiceDescriptor descriptor = _services.Single(descriptor =>
                descriptor.ServiceType == typeof(DeadlockRetrySettings)
            );

            descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
            descriptor.ImplementationInstance.Should().BeOfType<DeadlockRetrySettings>();
        }

        [Test]
        public void It_binds_deadlock_retry_settings_for_backend_startup_services()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["DeadlockRetry:MaxRetryAttempts"] = "7",
                        ["DeadlockRetry:BaseDelayMilliseconds"] = "25",
                        ["DeadlockRetry:UseJitter"] = "false",
                    }
                )
                .Build();
            var services = new ServiceCollection();

            services.AddDmsDefaultConfiguration(
                new LoggerConfiguration().CreateLogger(),
                configuration.GetSection("CircuitBreaker"),
                configuration.GetSection("DeadlockRetry"),
                false
            );

            var settings = services
                .Single(descriptor => descriptor.ServiceType == typeof(DeadlockRetrySettings))
                .ImplementationInstance.Should()
                .BeOfType<DeadlockRetrySettings>()
                .Subject;

            settings.MaxRetryAttempts.Should().Be(7);
            settings.BaseDelayMilliseconds.Should().Be(25);
            settings.UseJitter.Should().BeFalse();
        }
    }

    private static ServiceProvider BuildJwtAuthenticationProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddJwtAuthentication(configuration);

        return services.BuildServiceProvider();
    }

    [TestFixture]
    public class Given_Jwt_Authentication_Without_An_Authority : AuthStartupTaskRegistrationTests
    {
        private Action _resolve = null!;

        [SetUp]
        public void Setup()
        {
            ServiceProvider provider = BuildJwtAuthenticationProvider(
                new Dictionary<string, string?>
                {
                    ["JwtAuthentication:MetadataAddress"] =
                        "https://keycloak.example.com/realms/edfi/.well-known/openid-configuration",
                }
            );

            _resolve = () => provider.GetRequiredService<IConfigurationManager<OpenIdConnectConfiguration>>();
        }

        [Test]
        public void It_throws_naming_the_missing_authority_setting()
        {
            _resolve
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("JwtAuthentication:Authority must be configured for JWT authentication");
        }
    }

    [TestFixture]
    public class Given_Jwt_Authentication_With_Authority_And_Metadata_Address
        : AuthStartupTaskRegistrationTests
    {
        private IConfigurationManager<OpenIdConnectConfiguration> _configurationManager = null!;

        [SetUp]
        public void Setup()
        {
            ServiceProvider provider = BuildJwtAuthenticationProvider(
                new Dictionary<string, string?>
                {
                    ["JwtAuthentication:Authority"] = "https://keycloak.example.com/realms/edfi",
                    ["JwtAuthentication:MetadataAddress"] =
                        "https://keycloak.example.com/realms/edfi/.well-known/openid-configuration",
                }
            );

            _configurationManager = provider.GetRequiredService<
                IConfigurationManager<OpenIdConnectConfiguration>
            >();
        }

        [Test]
        public void It_resolves_a_configuration_manager()
        {
            _configurationManager.Should().BeOfType<ConfigurationManager<OpenIdConnectConfiguration>>();
        }
    }

    [TestFixture]
    public class Given_Jwt_Authentication_With_A_Scheme_Less_Metadata_Address
        : AuthStartupTaskRegistrationTests
    {
        private Action _resolve = null!;

        [SetUp]
        public void Setup()
        {
            // Uri.TryCreate accepts this as absolute, with "localhost" as its scheme.
            ServiceProvider provider = BuildJwtAuthenticationProvider(
                new Dictionary<string, string?>
                {
                    ["JwtAuthentication:Authority"] = "http://localhost:8081",
                    ["JwtAuthentication:MetadataAddress"] = "localhost:8081/.well-known/openid-configuration",
                }
            );

            _resolve = () => provider.GetRequiredService<IConfigurationManager<OpenIdConnectConfiguration>>();
        }

        [Test]
        public void It_throws_naming_the_metadata_address_setting()
        {
            _resolve
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage(
                    "JwtAuthentication:MetadataAddress must be an absolute http(s) URL for JWT authentication"
                );
        }
    }

    /// <summary>
    /// HttpDocumentRetriever checks only the address it is handed, so the client it fetches with
    /// must not follow a redirect to another origin.
    /// </summary>
    [TestFixture]
    public class Given_The_Oidc_Metadata_Http_Client : AuthStartupTaskRegistrationTests
    {
        private HttpMessageHandler _primaryHandler = null!;

        [SetUp]
        public void Setup()
        {
            ServiceProvider provider = BuildJwtAuthenticationProvider([]);

            HttpMessageHandler handler = provider
                .GetRequiredService<IHttpMessageHandlerFactory>()
                .CreateHandler(Core.Security.HttpDocumentRetriever.HttpClientName);

            while (handler is DelegatingHandler delegating)
            {
                handler = delegating.InnerHandler!;
            }

            _primaryHandler = handler;
        }

        [Test]
        public void It_does_not_follow_redirects()
        {
            _primaryHandler
                .Should()
                .BeOfType<SocketsHttpHandler>()
                .Which.AllowAutoRedirect.Should()
                .BeFalse();
        }
    }

    private const string RecordedMetadataAddress =
        "https://idp.test/realms/edfi/.well-known/openid-configuration";

    /// <summary>
    /// Replaces the named OIDC client's primary handler, serving a metadata document that names
    /// <paramref name="jwksUri"/>, and records every request that reaches it.
    /// </summary>
    private sealed class RecordingHandler(List<Uri> requests, string jwksUri) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            requests.Add(request.RequestUri!);
            string body =
                request.RequestUri!.AbsoluteUri == RecordedMetadataAddress
                    ? $$"""{"issuer":"https://idp.test/realms/edfi","jwks_uri":"{{jwksUri}}"}"""
                    : """{"keys":[]}""";
            return Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) }
            );
        }
    }

    /// <summary>
    /// Runs the registered configuration manager against the recorder and returns the recorded
    /// requests and any failure.
    /// </summary>
    private static async Task<(List<Uri> Requests, Exception? Exception)> FetchThroughRegisteredManager(
        string jwksUri
    )
    {
        List<Uri> requests = [];
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["JwtAuthentication:Authority"] = "https://idp.test/realms/edfi",
                    ["JwtAuthentication:MetadataAddress"] = RecordedMetadataAddress,
                }
            )
            .Build();

        var services = new ServiceCollection();
        services.AddJwtAuthentication(configuration);
        services
            .AddHttpClient(Core.Security.HttpDocumentRetriever.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(requests, jwksUri));

        using ServiceProvider provider = services.BuildServiceProvider();

        try
        {
            await provider
                .GetRequiredService<IConfigurationManager<OpenIdConnectConfiguration>>()
                .GetConfigurationAsync(CancellationToken.None);
            return (requests, null);
        }
        catch (InvalidOperationException exception)
        {
            return (requests, exception);
        }
    }

    /// <summary>
    /// The no-redirect registration above protects nothing unless the configuration manager fetches
    /// through it. The recorder replaces only that named client's primary handler, so a manager built
    /// on any other client never reaches it (and cannot reach the .test host either).
    /// </summary>
    [TestFixture]
    public class Given_The_Configuration_Manager_Fetches_Metadata : AuthStartupTaskRegistrationTests
    {
        private List<Uri> _requests = null!;

        [SetUp]
        public async Task Setup()
        {
            // Only the recorded requests matter; a manager on another client fails to connect.
            (_requests, _) = await FetchThroughRegisteredManager("https://idp.test/realms/edfi/certs");
        }

        [Test]
        public void It_fetches_the_metadata_address_through_the_named_client()
        {
            _requests.Should().Contain(new Uri(RecordedMetadataAddress));
        }
    }

    /// <summary>
    /// The origin pin protects nothing unless the registered manager fetches through the pinning
    /// retriever with the MetadataAddress origin. A manager built on any other retriever would fetch
    /// the foreign jwks_uri through the recorder.
    /// </summary>
    [TestFixture]
    public class Given_The_Configuration_Manager_Meets_A_Foreign_Jwks_Uri : AuthStartupTaskRegistrationTests
    {
        private List<Uri> _requests = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            (_requests, _exception) = await FetchThroughRegisteredManager("https://evil.test/certs");
        }

        [Test]
        public void It_fails_the_retrieval()
        {
            _exception.Should().BeOfType<InvalidOperationException>();
        }

        [Test]
        public void It_contacts_only_the_metadata_address()
        {
            _requests.Should().Equal(new Uri(RecordedMetadataAddress));
        }
    }

    [TestFixture]
    public class Given_Https_Metadata_Required_With_An_Http_Metadata_Address
        : AuthStartupTaskRegistrationTests
    {
        private Action _resolve = null!;

        [SetUp]
        public void Setup()
        {
            ServiceProvider provider = BuildJwtAuthenticationProvider(
                new Dictionary<string, string?>
                {
                    ["JwtAuthentication:Authority"] = "http://keycloak.example.com/realms/edfi",
                    ["JwtAuthentication:MetadataAddress"] =
                        "http://keycloak.example.com/realms/edfi/.well-known/openid-configuration",
                    ["JwtAuthentication:RequireHttpsMetadata"] = "true",
                }
            );

            _resolve = () => provider.GetRequiredService<IConfigurationManager<OpenIdConnectConfiguration>>();
        }

        [Test]
        public void It_throws_naming_both_settings()
        {
            _resolve
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage(
                    "JwtAuthentication:MetadataAddress must use https when JwtAuthentication:RequireHttpsMetadata is true"
                );
        }
    }

    [TestFixture]
    public class Given_Https_Metadata_Not_Required_With_An_Http_Metadata_Address
        : AuthStartupTaskRegistrationTests
    {
        private IConfigurationManager<OpenIdConnectConfiguration> _configurationManager = null!;

        [SetUp]
        public void Setup()
        {
            ServiceProvider provider = BuildJwtAuthenticationProvider(
                new Dictionary<string, string?>
                {
                    ["JwtAuthentication:Authority"] = "http://keycloak.example.com/realms/edfi",
                    ["JwtAuthentication:MetadataAddress"] =
                        "http://keycloak.example.com/realms/edfi/.well-known/openid-configuration",
                    ["JwtAuthentication:RequireHttpsMetadata"] = "false",
                }
            );

            _configurationManager = provider.GetRequiredService<
                IConfigurationManager<OpenIdConnectConfiguration>
            >();
        }

        [Test]
        public void It_resolves_a_configuration_manager()
        {
            _configurationManager.Should().BeOfType<ConfigurationManager<OpenIdConnectConfiguration>>();
        }
    }
}
