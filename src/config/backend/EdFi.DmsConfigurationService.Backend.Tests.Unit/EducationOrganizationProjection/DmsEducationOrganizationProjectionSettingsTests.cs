// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

public class DmsEducationOrganizationProjectionSettingsTests
{
    private const string Secret = "s3cret-Value-1440";

    /// <summary>The settings as the registration binds them from <paramref name="values"/>.</summary>
    private static DmsEducationOrganizationProjectionSettings Bind(Dictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        ServiceCollection services = new();
        services.AddDmsEducationOrganizationProjectionReader(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<DmsEducationOrganizationProjectionSettings>>().Value;
    }

    [TestFixture]
    public class Given_no_configured_section
    {
        private DmsEducationOrganizationProjectionSettings _settings = null!;

        [SetUp]
        public void Setup() => _settings = Bind([]);

        [Test]
        public void It_is_not_configured() => _settings.DmsBaseUrl.Should().BeNull();

        [Test]
        public void It_offers_the_supported_contract_version() =>
            _settings.EffectiveContractVersions.Should().Equal("educationOrganizationProjection.v1");

        [Test]
        public void It_has_the_spec_defaults()
        {
            _settings.PageSize.Should().Be(2000);
            _settings.DiscoveryTimeoutSeconds.Should().Be(10);
            _settings.TokenRequestTimeoutSeconds.Should().Be(30);
            _settings.PageRequestTimeoutSeconds.Should().Be(60);
            _settings.TotalReadTimeoutSeconds.Should().Be(600);
            _settings.MaxPages.Should().Be(2000);
            _settings.MaxItems.Should().Be(500_000);
            _settings.MaxResponseBodyBytes.Should().Be(8_388_608);
            _settings.MaxWalkRestarts.Should().Be(3);
            _settings.TokenExpirySafetyMarginSeconds.Should().Be(60);
            _settings.DiscoveryCacheSeconds.Should().Be(300);
        }
    }

    [TestFixture]
    public class Given_a_configured_section
    {
        private DmsEducationOrganizationProjectionSettings _settings = null!;

        [SetUp]
        public void Setup() =>
            _settings = Bind(
                new()
                {
                    ["DmsEducationOrganizationProjectionSettings:DmsBaseUrl"] = "https://dms.example.org/api",
                    ["DmsEducationOrganizationProjectionSettings:Credentials:ClientId"] = "shared-client",
                    ["DmsEducationOrganizationProjectionSettings:Credentials:ClientSecret"] = Secret,
                    ["DmsEducationOrganizationProjectionSettings:TenantCredentials:Tenant1:ClientId"] =
                        "t1-client",
                    ["DmsEducationOrganizationProjectionSettings:TenantCredentials:Tenant1:ClientSecret"] =
                        Secret,
                    ["DmsEducationOrganizationProjectionSettings:ContractVersions:0"] =
                        "educationOrganizationProjection.v1",
                    ["DmsEducationOrganizationProjectionSettings:PageSize"] = "500",
                }
            );

        [Test]
        public void It_binds_the_base_url() =>
            _settings.DmsBaseUrl.Should().Be("https://dms.example.org/api");

        [Test]
        public void It_binds_the_shared_credential()
        {
            _settings.Credentials!.ClientId.Should().Be("shared-client");
            _settings.Credentials.ClientSecret.Should().Be(Secret);
        }

        [Test]
        public void It_finds_a_tenant_credential_in_any_letter_case() =>
            _settings.TenantCredentials["TENANT1"].ClientId.Should().Be("t1-client");

        [Test]
        public void It_replaces_rather_than_appends_to_the_default_contract_versions() =>
            _settings.EffectiveContractVersions.Should().Equal("educationOrganizationProjection.v1");

        [Test]
        public void It_binds_a_numeric_setting() => _settings.PageSize.Should().Be(500);
    }

    [TestFixture]
    public class Given_settings_with_secrets
    {
        private DmsEducationOrganizationProjectionSettings _settings = null!;

        [SetUp]
        public void Setup()
        {
            _settings = new()
            {
                DmsBaseUrl = "https://dms.example.org/api",
                Credentials = new() { ClientId = "shared-client", ClientSecret = Secret },
            };
            _settings.TenantCredentials["Tenant1"] = new() { ClientId = "t1-client", ClientSecret = Secret };
        }

        [Test]
        public void It_keeps_both_credential_values_out_of_the_credential_text() =>
            _settings.Credentials!.ToString().Should().NotContain(Secret).And.NotContain("shared-client");

        [Test]
        public void It_keeps_secrets_out_of_the_settings_text() =>
            _settings.ToString().Should().NotContain(Secret);

        [Test]
        public void It_keeps_secrets_out_of_the_tenant_credential_text() =>
            string.Join(",", _settings.TenantCredentials.Values).Should().NotContain(Secret);
    }
}
