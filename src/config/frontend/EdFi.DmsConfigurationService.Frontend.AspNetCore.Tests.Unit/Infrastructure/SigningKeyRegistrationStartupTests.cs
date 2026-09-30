// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Net;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Spec §5 step 2.1: the application registers the signing-key services only for the self-contained identity provider,
/// with one provider, one refresh service, and the source its key mode selects, and it still starts with the existing
/// token manager. The refresh service starts with the host, so each host gets a key store it can never reach; its
/// failed loads are non-fatal.
/// </summary>
public class SigningKeyRegistrationStartupTests
{
    private const string UnreachableDatabase =
        "host=127.0.0.1;port=1;database=unreachable;username=none;timeout=1";

    /// <summary>
    /// <paramref name="hostSettings"/> go through <c>UseSetting</c>, because the identity provider and the datastore
    /// are read while the services are registered; <paramref name="settings"/> reach the options binders.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(
        Dictionary<string, string>? hostSettings = null,
        Dictionary<string, string?>? settings = null
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("DatabaseSettings:DatabaseConnection", UnreachableDatabase);
            foreach ((string key, string value) in hostSettings ?? [])
            {
                builder.UseSetting(key, value);
            }
            builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(settings ?? [])
            );
        });

    private static Type[] RefreshServices(WebApplicationFactory<Program> factory) =>
        [
            .. factory
                .Services.GetServices<IHostedService>()
                .OfType<SigningKeyRefreshService>()
                .Select(service => service.GetType()),
        ];

    [TestFixture]
    public class Given_the_self_contained_identity_provider_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpStatusCode _health;
        private Type[] _refreshServices = [];
        private ISigningKeySnapshotProvider _first = null!;
        private ISigningKeySnapshotProvider _second = null!;
        private int _providerRegistrations;
        private Type _source = null!;
        private Type _tokenManager = null!;
        private SigningKeyConfigurationManager _configurationManager = null!;
        private int _configurationManagerRegistrations;
        private SigningKeyBearerEvents _bearerEvents = null!;
        private int _bearerEventsRegistrations;

        [SetUp]
        public async Task Act()
        {
            _factory = CreateFactory();
            using (var client = _factory.CreateClient())
            {
                _health = (await client.GetAsync("/health")).StatusCode;
            }

            _refreshServices = RefreshServices(_factory);
            _first = _factory.Services.GetRequiredService<ISigningKeySnapshotProvider>();
            _second = _factory.Services.GetRequiredService<ISigningKeySnapshotProvider>();
            _providerRegistrations = _factory.Services.GetServices<ISigningKeySnapshotProvider>().Count();
            _source = _factory.Services.GetRequiredService<ISigningKeySource>().GetType();
            _tokenManager = _factory.Services.GetRequiredService<ITokenManager>().GetType();
            _configurationManager = _factory.Services.GetRequiredService<SigningKeyConfigurationManager>();
            _configurationManagerRegistrations = _factory
                .Services.GetServices<SigningKeyConfigurationManager>()
                .Count();
            _bearerEvents = _factory.Services.GetRequiredService<SigningKeyBearerEvents>();
            _bearerEventsRegistrations = _factory.Services.GetServices<SigningKeyBearerEvents>().Count();
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_starts() => _health.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_registers_one_refresh_service() =>
            _refreshServices.Should().Equal(typeof(SigningKeyRefreshService));

        [Test]
        public void It_registers_one_provider() => _providerRegistrations.Should().Be(1);

        [Test]
        public void It_shares_one_provider_instance() => _second.Should().BeSameAs(_first);

        [Test]
        public void It_reads_database_keys() => _source.Should().Be(typeof(DatabaseSigningKeySource));

        [Test]
        public void It_still_resolves_the_existing_token_manager() =>
            _tokenManager.Should().Be(typeof(OpenIddictTokenManager));

        // Step 2.3: resolvable singletons, not yet wired into either scheme (steps 3.1-3.2).
        [Test]
        public void It_registers_one_configuration_manager() =>
            _configurationManagerRegistrations.Should().Be(1);

        [Test]
        public void It_shares_one_configuration_manager_instance() =>
            _factory
                .Services.GetRequiredService<SigningKeyConfigurationManager>()
                .Should()
                .BeSameAs(_configurationManager);

        [Test]
        public void It_registers_one_bearer_events() => _bearerEventsRegistrations.Should().Be(1);

        [Test]
        public void It_shares_one_bearer_events_instance() =>
            _factory.Services.GetRequiredService<SigningKeyBearerEvents>().Should().BeSameAs(_bearerEvents);
    }

    [TestFixture]
    public class Given_the_mssql_datastore_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Type[] _refreshServices = [];
        private Type _source = null!;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory(new Dictionary<string, string> { ["AppSettings:Datastore"] = "mssql" });
            _refreshServices = RefreshServices(_factory);
            _source = _factory.Services.GetRequiredService<ISigningKeySource>().GetType();
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_registers_one_refresh_service() =>
            _refreshServices.Should().Equal(typeof(SigningKeyRefreshService));

        [Test]
        public void It_reads_database_keys() => _source.Should().Be(typeof(DatabaseSigningKeySource));
    }

    [TestFixture]
    public class Given_certificate_signing_keys_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Type _source = null!;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory(
                settings: new Dictionary<string, string?>
                {
                    ["IdentitySettings:UseCertificates"] = "true",
                    ["IdentitySettings:CertificatePath"] = "does-not-exist.pfx",
                }
            );
            _source = _factory.Services.GetRequiredService<ISigningKeySource>().GetType();
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_reads_certificate_keys() => _source.Should().Be(typeof(CertificateSigningKeySource));
    }

    [TestFixture]
    public class Given_the_keycloak_identity_provider_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Type[] _refreshServices = [];
        private ISigningKeySnapshotProvider? _provider;
        private ISigningKeySource? _source;
        private SigningKeyConfigurationManager? _configurationManager;
        private SigningKeyBearerEvents? _bearerEvents;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory(
                new Dictionary<string, string> { ["AppSettings:IdentityProvider"] = "keycloak" }
            );
            _refreshServices = RefreshServices(_factory);
            _provider = _factory.Services.GetService<ISigningKeySnapshotProvider>();
            _source = _factory.Services.GetService<ISigningKeySource>();
            _configurationManager = _factory.Services.GetService<SigningKeyConfigurationManager>();
            _bearerEvents = _factory.Services.GetService<SigningKeyBearerEvents>();
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_registers_no_refresh_service() => _refreshServices.Should().BeEmpty();

        [Test]
        public void It_registers_no_provider() => _provider.Should().BeNull();

        [Test]
        public void It_registers_no_source() => _source.Should().BeNull();

        [Test]
        public void It_registers_no_configuration_manager() => _configurationManager.Should().BeNull();

        [Test]
        public void It_registers_no_bearer_events() => _bearerEvents.Should().BeNull();
    }
}
