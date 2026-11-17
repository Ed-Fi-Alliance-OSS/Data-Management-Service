// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.StepDefinitions;
using FluentAssertions;

namespace EdFi.InstanceManagement.Tests.E2E.UnitTests;

[TestFixture]
[Category("InstanceFixtureUnit")]
public class Given_Discovery_Url_Assertions
{
    private Uri _discoveryUri = null!;

    [SetUp]
    public void Setup()
    {
        _discoveryUri = new("https://dms.example/Tenant_255901/255901/2024");
    }

    [TestCase("https://dms.example/Tenant_255901/255901/2024/metadata/{section")]
    [TestCase("https://dms.example/Tenant_255901/255901/2024/metadata/section}")]
    [TestCase("https://dms.example/Tenant_255901/255901/2024/metadata/%7Bsection")]
    [TestCase("https://dms.example/Tenant_255901/255901/2024/metadata/section%7D")]
    [TestCase("https://dms.example/Tenant_255901/255901/2024/metadata/%7bsection%7d")]
    [TestCase("/Tenant_255901/255901/2024/metadata/dependencies")]
    [TestCase("Tenant_255901/255901/2024/metadata/dependencies")]
    [TestCase("http://dms.example/Tenant_255901/255901/2024/metadata/dependencies")]
    [TestCase("https://other.example/Tenant_255901/255901/2024/metadata/dependencies")]
    [TestCase("https://dms.example:8443/Tenant_255901/255901/2024/metadata/dependencies")]
    [TestCase("https://dms.example/255901/2024/metadata/dependencies")]
    [TestCase("https://dms.example/Tenant_255901/2024/metadata/dependencies")]
    [TestCase("https://dms.example/Tenant_255901/255901/metadata/dependencies")]
    [TestCase("https://dms.example/Tenant_255901/255901")]
    [TestCase("https://dms.example/Tenant_2559010/255901/2024/metadata/dependencies")]
    [TestCase("https://dms.example/Tenant_255901/2559010/2024/metadata/dependencies")]
    [TestCase("https://dms.example/Tenant_255901/255901/20240/metadata/dependencies")]
    public void It_rejects_urls_that_violate_the_discovery_context(string url)
    {
        Action act = () => DiscoveryUrlAssertions.AssertConcreteContextUrl(url, _discoveryUri);

        act.Should().Throw<AssertionException>();
    }

    [TestCase("https://dms.example/Tenant_255901/255901/2024")]
    [TestCase("https://dms.example/Tenant_255901/255901/2024/")]
    public void It_returns_the_concrete_url_with_the_complete_discovery_context(string discoveryUrl)
    {
        const string url = "https://dms.example/Tenant_255901/255901/2024/metadata/dependencies";

        var result = DiscoveryUrlAssertions.AssertConcreteContextUrl(url, new Uri(discoveryUrl));

        result.OriginalString.Should().Be(url);
    }
}
