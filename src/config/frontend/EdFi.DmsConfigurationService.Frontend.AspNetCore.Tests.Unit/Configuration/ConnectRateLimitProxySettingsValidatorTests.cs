// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

public class ConnectRateLimitProxySettingsValidatorTests
{
    [TestFixture(false, "172.30.148.2")]
    [TestFixture(true, "")]
    [TestFixture(true, "172.30.148.20")]
    [TestFixture(true, "::ffff:172.30.148.2")]
    public class Given_an_unready_proxy_configuration(bool forwardingEnabled, string knownProxies)
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = new ConnectRateLimitProxySettingsValidator(
                Options.Create(
                    new ReverseProxySettings
                    {
                        UseForwardedHeaders = forwardingEnabled,
                        KnownProxies = knownProxies,
                        KnownNetworks = "172.30.148.0/29",
                    }
                )
            ).Validate(null, new ConnectRateLimitSettings { RequiredProxyAddress = "172.30.148.2" });

        [Test]
        public void It_refuses_to_serve_with_a_shared_proxy_source_budget() =>
            _result.Failed.Should().BeTrue();

        [Test]
        public void It_explains_the_required_trust_configuration() =>
            _result
                .Failures.Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    "ConnectRateLimit:RequiredProxyAddress requires AppSettings:ReverseProxy:UseForwardedHeaders=true "
                        + "and that exact address in AppSettings:ReverseProxy:KnownProxies."
                );
    }

    [TestFixture("172.30.148.2")]
    [TestFixture(" 172.30.148.20, 172.30.148.2 ")]
    public class Given_an_explicitly_trusted_proxy(string knownProxies)
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = new ConnectRateLimitProxySettingsValidator(
                Options.Create(
                    new ReverseProxySettings { UseForwardedHeaders = true, KnownProxies = knownProxies }
                )
            ).Validate(null, new ConnectRateLimitSettings { RequiredProxyAddress = "172.30.148.2" });

        [Test]
        public void It_accepts_the_configuration() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_invalid_required_proxy_address
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = new ConnectRateLimitProxySettingsValidator(
                Options.Create(new ReverseProxySettings())
            ).Validate(null, new ConnectRateLimitSettings { RequiredProxyAddress = "not-an-address" });

        [Test]
        public void It_names_the_invalid_setting() =>
            _result
                .Failures.Should()
                .ContainSingle()
                .Which.Should()
                .Be("ConnectRateLimit:RequiredProxyAddress must be an IP address.");
    }

    [TestFixture(true, "")]
    [TestFixture(false, "172.30.148.2")]
    public class Given_direct_traffic_or_disabled_limiting(bool enabled, string requiredProxyAddress)
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = new ConnectRateLimitProxySettingsValidator(
                Options.Create(new ReverseProxySettings())
            ).Validate(
                null,
                new ConnectRateLimitSettings
                {
                    Enabled = enabled,
                    RequiredProxyAddress = requiredProxyAddress,
                }
            );

        [Test]
        public void It_keeps_the_existing_configuration_valid() => _result.Succeeded.Should().BeTrue();
    }
}
