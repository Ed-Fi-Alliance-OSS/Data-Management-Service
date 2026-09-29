// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>
    /// Resolves the host's real hasher from a container whose options came through the binder, so
    /// an assertion on its output proves the configured count reaches the hash rather than stopping
    /// at the options object.
    /// </summary>
    private static ClientSecretHasher ResolveBoundHasher(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ServiceCollection services = new();
        services.AddOpenIddictIdentityOptions(configuration);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ClientSecretHasher>();

        return services.BuildServiceProvider().GetRequiredService<ClientSecretHasher>();
    }

    /// <summary>
    /// Runs the checks <c>ValidateOnStart</c> registered, which is what the host runs before it
    /// starts serving; resolving <c>IOptions.Value</c> would validate even without ValidateOnStart.
    /// </summary>
    private static Action ValidateAtStartup(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ServiceCollection services = new();
        services.AddOpenIddictIdentityOptions(configuration);

        IStartupValidator startupValidator = services
            .BuildServiceProvider()
            .GetRequiredService<IStartupValidator>();
        return startupValidator.Validate;
    }

    private static ClientSecretHasher CreateHasherAt(int iterations) =>
        new(
            NullLogger<ClientSecretHasher>.Instance,
            Options.Create(new IdentityOptions { ClientSecretHashingIterations = iterations })
        );

    private const string Secret = "Sup3r-Secret-Client-Value-0123456789";
    private const int DefaultHashingIterations = 210000;

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

    [TestFixture]
    public class Given_no_client_secret_hashing_iterations_is_configured
    {
        private IdentityOptions _options = null!;

        [SetUp]
        public void Setup() => _options = BindIdentityOptions([]);

        [Test]
        public void It_defaults_to_210000() =>
            _options.ClientSecretHashingIterations.Should().Be(DefaultHashingIterations);
    }

    /// <summary>
    /// The compose files used to set <c>IdentitySettings:HashingIterations</c>, which nothing binds.
    /// It must stay unbound so that only one iteration key is ever live.
    /// </summary>
    [TestFixture]
    public class Given_only_the_legacy_hashing_iterations_key_is_configured
    {
        private static readonly Dictionary<string, string?> _settings = new()
        {
            ["IdentitySettings:HashingIterations"] = "1000",
        };

        private IdentityOptions _options = null!;
        private bool _verifiedAtDefault;
        private bool _verifiedAtLegacyValue;

        [SetUp]
        public async Task Setup()
        {
            _options = BindIdentityOptions(_settings);

            string hash = await ResolveBoundHasher(_settings).HashSecretAsync(Secret);
            _verifiedAtDefault = await CreateHasherAt(DefaultHashingIterations)
                .VerifySecretAsync(Secret, hash);
            _verifiedAtLegacyValue = await CreateHasherAt(1000).VerifySecretAsync(Secret, hash);
        }

        [Test]
        public void It_leaves_the_bound_count_at_the_default() =>
            _options.ClientSecretHashingIterations.Should().Be(DefaultHashingIterations);

        [Test]
        public void It_hashes_at_the_default_count() => _verifiedAtDefault.Should().BeTrue();

        [Test]
        public void It_does_not_hash_at_the_legacy_value() => _verifiedAtLegacyValue.Should().BeFalse();
    }

    [TestFixture]
    public class Given_both_hashing_iterations_keys_are_configured
    {
        private IdentityOptions _options = null!;

        [SetUp]
        public void Setup() =>
            _options = BindIdentityOptions(
                new Dictionary<string, string?>
                {
                    ["IdentitySettings:HashingIterations"] = "1000",
                    ["IdentitySettings:ClientSecretHashingIterations"] = "3000",
                }
            );

        [Test]
        public void It_binds_only_the_client_secret_hashing_iterations_key() =>
            _options.ClientSecretHashingIterations.Should().Be(3000);
    }

    /// <summary>
    /// Before the binder read this key, the host hashed at the model default whatever the key said,
    /// so a hash from the bound hasher verified at the default and not at the configured count.
    /// </summary>
    [TestFixture]
    public class Given_a_non_default_client_secret_hashing_iterations_is_configured
    {
        private const int ConfiguredIterations = 3000;

        private bool _verifiedAtConfiguredCount;
        private bool _verifiedAtDefault;

        [SetUp]
        public async Task Setup()
        {
            ClientSecretHasher boundHasher = ResolveBoundHasher(
                new Dictionary<string, string?>
                {
                    ["IdentitySettings:ClientSecretHashingIterations"] = ConfiguredIterations.ToString(),
                }
            );

            string hash = await boundHasher.HashSecretAsync(Secret);
            _verifiedAtConfiguredCount = await CreateHasherAt(ConfiguredIterations)
                .VerifySecretAsync(Secret, hash);
            _verifiedAtDefault = await CreateHasherAt(DefaultHashingIterations)
                .VerifySecretAsync(Secret, hash);
        }

        [Test]
        public void It_hashes_at_the_configured_count() => _verifiedAtConfiguredCount.Should().BeTrue();

        [Test]
        public void It_does_not_hash_at_the_default_count() => _verifiedAtDefault.Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_positive_client_secret_hashing_iterations_is_configured
    {
        private Action _validate = null!;

        [SetUp]
        public void Setup() =>
            _validate = ValidateAtStartup(
                new Dictionary<string, string?> { ["IdentitySettings:ClientSecretHashingIterations"] = "1" }
            );

        [Test]
        public void It_passes_startup_validation() => _validate.Should().NotThrow();
    }

    /// <summary>
    /// PBKDF2 throws on a non-positive count, so an unchecked value would only fail on the first
    /// client create or token request.
    /// </summary>
    [TestFixture]
    public class Given_a_zero_client_secret_hashing_iterations_is_configured
    {
        private Action _validate = null!;

        [SetUp]
        public void Setup() =>
            _validate = ValidateAtStartup(
                new Dictionary<string, string?> { ["IdentitySettings:ClientSecretHashingIterations"] = "0" }
            );

        [Test]
        public void It_fails_startup_validation() =>
            _validate
                .Should()
                .Throw<OptionsValidationException>()
                .WithMessage("*IdentitySettings:ClientSecretHashingIterations must be greater than zero*");
    }

    [TestFixture]
    public class Given_a_negative_client_secret_hashing_iterations_is_configured
    {
        private Action _validate = null!;

        [SetUp]
        public void Setup() =>
            _validate = ValidateAtStartup(
                new Dictionary<string, string?> { ["IdentitySettings:ClientSecretHashingIterations"] = "-5" }
            );

        [Test]
        public void It_fails_startup_validation() =>
            _validate
                .Should()
                .Throw<OptionsValidationException>()
                .WithMessage("*IdentitySettings:ClientSecretHashingIterations must be greater than zero*");
    }
}
