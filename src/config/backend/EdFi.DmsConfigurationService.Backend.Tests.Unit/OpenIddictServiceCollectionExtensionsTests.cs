// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// Covers what <see cref="OpenIddictServiceCollectionExtensions.AddOpenIddictIdentityOptions"/>
/// actually reads out of configuration. Nothing else does: the token-manager tests construct
/// <see cref="IdentityOptions"/> directly and the HTTP tests replace <c>ITokenManager</c>
/// wholesale, so a key spelled wrong here would bind nothing and fail no test.
///
/// That is not hypothetical. The same method reads
/// <c>IdentitySettings:TokenExpirationMinutes</c> while both appsettings files spell the key
/// <c>OpenIddictTokenExpirationTimeMinutes</c>, so that setting never binds and its hardcoded
/// default always wins - masked only because the two values happen to agree.
/// </summary>
[TestFixture]
public class OpenIddictServiceCollectionExtensionsTests
{
    private static IdentityOptions BindIdentityOptions(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ServiceCollection services = new();
        services.AddOpenIddictIdentityOptions(configuration);

        return services.BuildServiceProvider().GetRequiredService<IOptions<IdentityOptions>>().Value;
    }

    [TestFixture]
    public class Given_no_bearer_token_per_client_limit_is_configured
    {
        private IdentityOptions _options = null!;

        [SetUp]
        public void Setup() => _options = BindIdentityOptions([]);

        [Test]
        public void It_defaults_to_five() => _options.BearerTokenPerClientLimit.Should().Be(5);
    }

    [TestFixture]
    public class Given_a_bearer_token_per_client_limit_is_configured
    {
        private IdentityOptions _options = null!;

        [SetUp]
        public void Setup() =>
            _options = BindIdentityOptions(
                new Dictionary<string, string?> { ["IdentitySettings:BearerTokenPerClientLimit"] = "25" }
            );

        // Binding the configured value is also what proves the key name: read under any other
        // name, this would silently come back as the default 5.
        [Test]
        public void It_binds_the_configured_value() => _options.BearerTokenPerClientLimit.Should().Be(25);
    }

    [TestFixture]
    public class Given_a_bearer_token_per_client_limit_that_disables_enforcement
    {
        private IdentityOptions _options = null!;

        [SetUp]
        public void Setup() =>
            _options = BindIdentityOptions(
                new Dictionary<string, string?> { ["IdentitySettings:BearerTokenPerClientLimit"] = "-1" }
            );

        // The documented disable value reaches the repository unchanged; nothing clamps it to a
        // positive number on the way through.
        [Test]
        public void It_binds_the_negative_value_unchanged() =>
            _options.BearerTokenPerClientLimit.Should().Be(-1);
    }
}
