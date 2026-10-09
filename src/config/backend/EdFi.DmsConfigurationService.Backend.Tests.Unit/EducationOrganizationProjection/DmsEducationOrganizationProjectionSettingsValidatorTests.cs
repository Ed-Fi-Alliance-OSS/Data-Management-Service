// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

public class DmsEducationOrganizationProjectionSettingsValidatorTests
{
    private const string Section = "DmsEducationOrganizationProjectionSettings";
    private const string Secret = "s3cret-Value-1440";

    /// <summary>Configured settings that pass: the defaults plus a base URL and a shared credential.</summary>
    private static DmsEducationOrganizationProjectionSettings Valid() =>
        new()
        {
            DmsBaseUrl = "https://dms.example.org/api",
            Credentials = new() { ClientId = "projection-client", ClientSecret = Secret },
        };

    private static ValidateOptionsResult Validate(DmsEducationOrganizationProjectionSettings settings) =>
        new DmsEducationOrganizationProjectionSettingsValidator().Validate(null, settings);

    [TestFixture]
    public class Given_the_defaults_with_a_base_url_and_a_credential
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() => _result = Validate(Valid());

        [Test]
        public void It_succeeds() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture(new object?[] { null })]
    [TestFixture("")]
    [TestFixture("   ")]
    public class Given_no_base_url(string? baseUrl)
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // Every other value is out of range; none is checked while the reader is not configured.
            _result = Validate(
                new()
                {
                    DmsBaseUrl = baseUrl,
                    Credentials = new() { ClientId = "only-an-id" },
                    PageSize = 0,
                    MaxResponseBodyBytes = 0,
                    ContractVersions = ["educationOrganizationProjection.v9"],
                }
            );
        }

        [Test]
        public void It_succeeds_without_checking_the_other_settings() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture(nameof(DmsEducationOrganizationProjectionSettings.PageSize), 0, 1, 10_000, 10_001)]
    [TestFixture(nameof(DmsEducationOrganizationProjectionSettings.DiscoveryTimeoutSeconds), 0, 1, 300, 301)]
    [TestFixture(
        nameof(DmsEducationOrganizationProjectionSettings.TokenRequestTimeoutSeconds),
        0,
        1,
        300,
        301
    )]
    [TestFixture(
        nameof(DmsEducationOrganizationProjectionSettings.PageRequestTimeoutSeconds),
        0,
        1,
        600,
        601
    )]
    [TestFixture(
        nameof(DmsEducationOrganizationProjectionSettings.TotalReadTimeoutSeconds),
        0,
        1,
        86_400,
        86_401
    )]
    [TestFixture(nameof(DmsEducationOrganizationProjectionSettings.MaxPages), 0, 1, 1_000_000, 1_000_001)]
    [TestFixture(nameof(DmsEducationOrganizationProjectionSettings.MaxItems), 0, 1, 10_000_000, 10_000_001)]
    [TestFixture(nameof(DmsEducationOrganizationProjectionSettings.MaxWalkRestarts), -1, 0, 10, 11)]
    [TestFixture(
        nameof(DmsEducationOrganizationProjectionSettings.TokenExpirySafetyMarginSeconds),
        -1,
        0,
        3_600,
        3_601
    )]
    [TestFixture(
        nameof(DmsEducationOrganizationProjectionSettings.DiscoveryCacheSeconds),
        -1,
        0,
        86_400,
        86_401
    )]
    public class Given_a_bounded_setting(
        string setting,
        int belowMinimum,
        int minimum,
        int maximum,
        int aboveMaximum
    )
    {
        private ValidateOptionsResult With(int value)
        {
            DmsEducationOrganizationProjectionSettings settings = Valid();
            typeof(DmsEducationOrganizationProjectionSettings)
                .GetProperty(setting)!
                .SetValue(settings, value);

            // Keep the body-size rule satisfied, so only the bound under test can fail.
            settings.MaxResponseBodyBytes = int.MaxValue;
            return Validate(settings);
        }

        [Test]
        public void It_accepts_the_minimum() => With(minimum).Succeeded.Should().BeTrue();

        [Test]
        public void It_accepts_the_maximum() => With(maximum).Succeeded.Should().BeTrue();

        [Test]
        public void It_rejects_a_value_below_the_minimum() =>
            With(belowMinimum)
                .Failures.Should()
                .Equal($"{Section}:{setting} must be between {minimum} and {maximum}; it is {belowMinimum}.");

        [Test]
        public void It_rejects_a_value_above_the_maximum() =>
            With(aboveMaximum)
                .Failures.Should()
                .Equal($"{Section}:{setting} must be between {minimum} and {maximum}; it is {aboveMaximum}.");
    }

    [TestFixture]
    public class Given_the_response_body_size_rule
    {
        private static ValidateOptionsResult With(int pageSize, int maxResponseBodyBytes)
        {
            DmsEducationOrganizationProjectionSettings settings = Valid();
            settings.PageSize = pageSize;
            settings.MaxResponseBodyBytes = maxResponseBodyBytes;
            return Validate(settings);
        }

        [Test]
        public void It_accepts_a_cap_equal_to_a_full_page_of_the_largest_items() =>
            With(2000, 2000 * 2048 + 1024).Succeeded.Should().BeTrue();

        [Test]
        public void It_rejects_a_cap_one_byte_below_a_full_page() =>
            With(2000, 2000 * 2048 + 1023)
                .Failures.Should()
                .Equal(
                    $"{Section}:MaxResponseBodyBytes must be at least {Section}:PageSize (2000) x 2048 + 1024 = 4097024; "
                        + "it is 4097023."
                );

        [Test]
        public void It_accepts_the_default_cap_for_the_default_page_size() =>
            Validate(Valid()).Succeeded.Should().BeTrue();

        [Test]
        public void It_rejects_the_default_cap_for_the_largest_page_size() =>
            With(10_000, 8_388_608)
                .Failures.Should()
                .Equal(
                    $"{Section}:MaxResponseBodyBytes must be at least {Section}:PageSize (10000) x 2048 + 1024 = 20481024; "
                        + "it is 8388608."
                );

        [Test]
        public void It_reports_the_rule_without_overflow_for_an_out_of_range_page_size() =>
            With(int.MaxValue, int.MaxValue)
                .Failures.Should()
                .Contain(
                    $"{Section}:MaxResponseBodyBytes must be at least {Section}:PageSize (2147483647) x 2048 + 1024 = "
                        + $"{(long)int.MaxValue * 2048 + 1024}; it is 2147483647."
                );
    }

    [TestFixture("dms.example.org/api")]
    [TestFixture("/api")]
    [TestFixture("ftp://dms.example.org/api")]
    [TestFixture("https://dms.example.org/api?tenant=a")]
    [TestFixture("https://dms.example.org/api?")]
    [TestFixture("https://dms.example.org/api#top")]
    [TestFixture("https://user:" + Secret + "@dms.example.org/api")]
    public class Given_an_invalid_base_url(string baseUrl)
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup()
        {
            DmsEducationOrganizationProjectionSettings settings = Valid();
            settings.DmsBaseUrl = baseUrl;
            _result = Validate(settings);
        }

        [Test]
        public void It_names_the_setting_and_the_accepted_form() =>
            _result
                .Failures.Should()
                .Equal(
                    $"{Section}:DmsBaseUrl must be an absolute http or https URL, optionally with a base path, and with "
                        + "no user information, query or fragment."
                );

        [Test]
        public void It_does_not_echo_the_url() => _result.FailureMessage.Should().NotContain(baseUrl);
    }

    [TestFixture("http://dms.example.org")]
    [TestFixture("http://localhost:8080/")]
    [TestFixture("https://dms.example.org/api/")]
    [TestFixture("https://dms.example.org:8443/deep/base/path")]
    public class Given_a_valid_base_url(string baseUrl)
    {
        [Test]
        public void It_succeeds()
        {
            DmsEducationOrganizationProjectionSettings settings = Valid();
            settings.DmsBaseUrl = baseUrl;
            Validate(settings).Succeeded.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_credentials
    {
        private static DmsEducationOrganizationProjectionSettings WithCredentials(
            DmsEducationOrganizationProjectionCredentials? shared,
            Dictionary<string, DmsEducationOrganizationProjectionCredentials>? tenants = null
        )
        {
            DmsEducationOrganizationProjectionSettings settings = Valid();
            settings.Credentials = shared;
            foreach (
                (string tenant, DmsEducationOrganizationProjectionCredentials credentials) in tenants ?? []
            )
            {
                settings.TenantCredentials[tenant] = credentials;
            }
            return settings;
        }

        [Test]
        public void It_accepts_tenant_credentials_without_a_shared_pair() =>
            Validate(
                WithCredentials(
                    null,
                    new()
                    {
                        ["Tenant1"] = new() { ClientId = "t1", ClientSecret = Secret },
                    }
                )
            )
                .Succeeded.Should()
                .BeTrue();

        [Test]
        public void It_treats_an_empty_shared_pair_as_absent() =>
            Validate(
                WithCredentials(
                    new() { ClientId = "", ClientSecret = " " },
                    new()
                    {
                        ["Tenant1"] = new() { ClientId = "t1", ClientSecret = Secret },
                    }
                )
            )
                .Succeeded.Should()
                .BeTrue();

        [Test]
        public void It_rejects_a_shared_secret_without_a_client_id() =>
            Validate(WithCredentials(new() { ClientSecret = Secret }))
                .Failures.Should()
                .Contain($"{Section}:Credentials must set both ClientId and ClientSecret, or neither.");

        [Test]
        public void It_rejects_a_shared_client_id_without_a_secret() =>
            Validate(WithCredentials(new() { ClientId = "projection-client" }))
                .Failures.Should()
                .Contain($"{Section}:Credentials must set both ClientId and ClientSecret, or neither.");

        [Test]
        public void It_rejects_a_tenant_entry_without_a_secret() =>
            Validate(
                WithCredentials(
                    new() { ClientId = "projection-client", ClientSecret = Secret },
                    new() { ["Tenant1"] = new() { ClientId = "t1" } }
                )
            )
                .Failures.Should()
                .Equal($"{Section}:TenantCredentials:Tenant1 must set both ClientId and ClientSecret.");

        [Test]
        public void It_rejects_a_tenant_entry_without_a_client_id() =>
            Validate(
                WithCredentials(
                    new() { ClientId = "projection-client", ClientSecret = Secret },
                    new() { ["Tenant1"] = new() { ClientSecret = Secret } }
                )
            )
                .Failures.Should()
                .Equal($"{Section}:TenantCredentials:Tenant1 must set both ClientId and ClientSecret.");

        [Test]
        public void It_sanitizes_the_tenant_name_in_the_message() =>
            Validate(
                WithCredentials(
                    new() { ClientId = "projection-client", ClientSecret = Secret },
                    new() { ["Tenant\r\nFORGED"] = new() { ClientId = "t1" } }
                )
            )
                .Failures.Should()
                .Equal($"{Section}:TenantCredentials:TenantFORGED must set both ClientId and ClientSecret.");

        [Test]
        public void It_rejects_a_base_url_with_no_credential_at_all() =>
            Validate(WithCredentials(null))
                .Failures.Should()
                .Equal(
                    $"{Section}:Credentials or at least one {Section}:TenantCredentials entry must be set when "
                        + $"{Section}:DmsBaseUrl is set."
                );

        [Test]
        public void It_never_puts_a_secret_or_client_id_in_a_message() =>
            Validate(
                WithCredentials(
                    new() { ClientSecret = Secret },
                    new() { ["Tenant1"] = new() { ClientId = "tenant-client-id" } }
                )
            )
                .FailureMessage.Should()
                .NotContain(Secret)
                .And.NotContain("tenant-client-id");
    }

    [TestFixture]
    public class Given_contract_versions
    {
        private static ValidateOptionsResult With(string[]? contractVersions)
        {
            DmsEducationOrganizationProjectionSettings settings = Valid();
            settings.ContractVersions = contractVersions;
            return Validate(settings);
        }

        private const string Failure =
            $"{Section}:ContractVersions must list distinct versions from: educationOrganizationProjection.v1.";

        [Test]
        public void It_accepts_none_configured() => With(null).Succeeded.Should().BeTrue();

        [Test]
        public void It_accepts_the_supported_version() =>
            With(["educationOrganizationProjection.v1"]).Succeeded.Should().BeTrue();

        [Test]
        public void It_rejects_a_version_it_cannot_parse() =>
            With(["educationOrganizationProjection.v2"]).Failures.Should().Equal(Failure);

        [Test]
        public void It_rejects_a_version_in_another_letter_case() =>
            With(["EducationOrganizationProjection.V1"]).Failures.Should().Equal(Failure);

        [Test]
        public void It_rejects_a_repeated_version() =>
            With(["educationOrganizationProjection.v1", "educationOrganizationProjection.v1"])
                .Failures.Should()
                .Equal(Failure);

        [Test]
        public void It_rejects_an_empty_list() => With([]).Failures.Should().Equal(Failure);
    }

    [TestFixture]
    public class Given_several_invalid_settings
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup()
        {
            DmsEducationOrganizationProjectionSettings settings = Valid();
            settings.DmsBaseUrl = "relative/path";
            settings.Credentials = null;
            settings.PageSize = 0;
            settings.MaxWalkRestarts = 11;
            _result = Validate(settings);
        }

        [Test]
        public void It_reports_every_failure() =>
            _result
                .Failures.Should()
                .HaveCount(4)
                .And.Contain(failure => failure.StartsWith($"{Section}:DmsBaseUrl "))
                .And.Contain(failure => failure.StartsWith($"{Section}:Credentials or at least one "))
                .And.Contain(failure => failure.StartsWith($"{Section}:PageSize "))
                .And.Contain(failure => failure.StartsWith($"{Section}:MaxWalkRestarts "));
    }
}
