// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Jobs;
using EdFi.DmsConfigurationService.Secrets;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

/// <summary>
/// Verifies that <c>SecretsSettings</c> is bound from the shipped settings, that an out-of-range value
/// stops the host at startup naming the setting, and that the host registers its secret value cache
/// without registering a resolver of its own.
/// </summary>
public class SecretsOptionsStartupTests
{
    private static WebApplicationFactory<Program> CreateFactory(
        Dictionary<string, string?> settings,
        ILoggerProvider? logs = null
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(settings)
            );
            if (logs is not null)
            {
                builder.ConfigureServices(services => services.AddSingleton(logs));
            }
        });

    /// <summary>Boot is triggered here: WebApplicationFactory defers the entry point until the server is needed.</summary>
    private static Exception? StartupExceptionFor(WebApplicationFactory<Program> factory)
    {
        try
        {
            using var client = factory.CreateClient();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [TestFixture("SecretsSettings:CacheExpirationSeconds", "-1")]
    [TestFixture("SecretsSettings:ResolveTimeoutSeconds", "0")]
    [TestFixture("SecretsSettings:ResolveTimeoutSeconds", "-1")]
    [TestFixture("SecretsSettings:ResolveTimeoutSeconds", "4294968")]
    public class Given_an_invalid_secrets_setting_at_startup(string key, string value)
    {
        private CapturingLoggerProvider _logs = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private Exception?[] _startupFaults = [];

        /// <summary>
        /// The validation failure is read from the host's startup-fault log rather than from the
        /// exception CreateClient throws. RunAsync disposes the failed host before the entry point
        /// reports the failure, so the factory's own start can lose that race and throw
        /// ObjectDisposedException instead. The host always logs the failure before disposing.
        /// </summary>
        [SetUp]
        public void Act()
        {
            _logs = new CapturingLoggerProvider();
            _factory = CreateFactory(new Dictionary<string, string?> { [key] = value }, _logs);
            _exception = StartupExceptionFor(_factory);
            _startupFaults =
            [
                .. _logs
                    .Entries.Where(entry =>
                        entry.Category == "Microsoft.Extensions.Hosting.Internal.Host"
                        && entry.EventId.Name == "HostedServiceStartupFaulted"
                    )
                    .Select(entry => entry.Exception),
            ];
        }

        [TearDown]
        public void TearDown()
        {
            _factory.Dispose();
            _logs.Dispose();
        }

        [Test]
        public void It_fails_to_start() => _exception.Should().NotBeNull();

        [Test]
        public void It_names_the_setting() =>
            _startupFaults
                .Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<OptionsValidationException>()
                .Which.Message.Should()
                .Contain($"{key} must be");
    }

    [TestFixture]
    public class Given_the_shipped_secrets_settings_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private SecretsOptions _options = null!;
        private SecretValueCache _cache = null!;
        private SecretValueCache _cacheFromScope = null!;
        private ISecretResolver? _resolver;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory([]);
            _exception = StartupExceptionFor(_factory);
            _options = _factory.Services.GetRequiredService<IOptions<SecretsOptions>>().Value;
            _cache = _factory.Services.GetRequiredService<SecretValueCache>();
            using IServiceScope scope = _factory.Services.CreateScope();
            _cacheFromScope = scope.ServiceProvider.GetRequiredService<SecretValueCache>();
            _resolver = _factory.Services.GetService<ISecretResolver>();
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_starts() => _exception.Should().BeNull();

        [Test]
        public void It_binds_the_shipped_cache_expiration() =>
            _options.CacheExpirationSeconds.Should().Be(300);

        [Test]
        public void It_binds_the_shipped_resolve_timeout() => _options.ResolveTimeoutSeconds.Should().Be(10);

        [Test]
        public void It_registers_one_cache_for_the_life_of_the_host() =>
            _cacheFromScope.Should().BeSameAs(_cache);

        [Test]
        public void It_registers_no_resolver_without_a_plugin() => _resolver.Should().BeNull();
    }

    [TestFixture]
    public class Given_caching_disabled_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private SecretsOptions _options = null!;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory(
                new Dictionary<string, string?>
                {
                    ["SecretsSettings:CacheExpirationSeconds"] = "0",
                    ["SecretsSettings:ResolveTimeoutSeconds"] = "1",
                }
            );
            _exception = StartupExceptionFor(_factory);
            _options = _factory.Services.GetRequiredService<IOptions<SecretsOptions>>().Value;
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_starts() => _exception.Should().BeNull();

        [Test]
        public void It_binds_the_overrides()
        {
            _options.CacheExpirationSeconds.Should().Be(0);
            _options.ResolveTimeoutSeconds.Should().Be(1);
        }
    }
}
