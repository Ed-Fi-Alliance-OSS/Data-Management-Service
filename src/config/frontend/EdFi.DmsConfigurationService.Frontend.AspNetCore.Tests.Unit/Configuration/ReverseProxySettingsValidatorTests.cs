// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

[TestFixture]
public class Given_ReverseProxySettingsValidator
{
    private ReverseProxySettingsValidator _validator = null!;

    [SetUp]
    public void SetUp() => _validator = new ReverseProxySettingsValidator();

    [Test]
    public void It_should_succeed_when_no_trusted_sources_are_configured()
    {
        var result = _validator.Validate(null, new ReverseProxySettings());

        result.Succeeded.Should().BeTrue();
    }

    [TestCase("10.0.0.5")]
    [TestCase("10.0.0.5,10.0.0.6")]
    [TestCase("::1")]
    [TestCase("2001:db8::1")]
    [TestCase(" 10.0.0.5 , 10.0.0.6 ")]
    public void It_should_succeed_for_valid_known_proxies(string proxies)
    {
        var result = _validator.Validate(null, new ReverseProxySettings { KnownProxies = proxies });

        result.Succeeded.Should().BeTrue();
    }

    [TestCase("10.0.0.0/8")]
    [TestCase("172.16.0.0/12,10.0.0.0/8")]
    [TestCase("::1/128")]
    [TestCase("2001:db8::/32")]
    [TestCase("10.0.0.5/8")] // non-canonical base address is normalized to 10.0.0.0/8
    public void It_should_succeed_for_valid_known_networks(string networks)
    {
        var result = _validator.Validate(null, new ReverseProxySettings { KnownNetworks = networks });

        result.Succeeded.Should().BeTrue();
    }

    [TestCase("not-an-ip")]
    [TestCase("10.0.0.5,999.0.0.1")]
    [TestCase("10.0.0.0/8")] // CIDR is not a valid bare IP
    public void It_should_fail_for_invalid_known_proxies(string proxies)
    {
        var result = _validator.Validate(null, new ReverseProxySettings { KnownProxies = proxies });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("KnownProxies");
    }

    [TestCase("10.0.0.0")] // missing prefix length
    [TestCase("10.0.0.0/99")] // prefix out of range
    [TestCase("10.0.0.0/")] // missing prefix value
    [TestCase("garbage/8")]
    public void It_should_fail_for_invalid_known_networks(string networks)
    {
        var result = _validator.Validate(null, new ReverseProxySettings { KnownNetworks = networks });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("KnownNetworks");
    }
}
