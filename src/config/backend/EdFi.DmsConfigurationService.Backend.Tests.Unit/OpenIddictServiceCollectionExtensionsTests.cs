// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

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
        return BuildProvider(settings).GetRequiredService<IOptions<IdentityOptions>>().Value;
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ServiceCollection services = new();
        services.AddOpenIddictIdentityOptions(configuration);

        return services.BuildServiceProvider();
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
    public class Given_no_signing_key_settings_are_configured
    {
        private IdentityOptions _options = null!;

        [SetUp]
        public void Setup() => _options = BindIdentityOptions([]);

        [Test]
        public void It_defaults_the_refresh_interval_to_300_seconds() =>
            _options.SigningKeyRefreshIntervalSeconds.Should().Be(300);

        [Test]
        public void It_defaults_the_max_staleness_to_3600_seconds() =>
            _options.SigningKeyMaxStalenessSeconds.Should().Be(3600);

        [Test]
        public void It_defaults_the_unknown_key_cooldown_to_30_seconds() =>
            _options.SigningKeyUnknownKeyRefreshCooldownSeconds.Should().Be(30);

        [Test]
        public void It_defaults_the_load_timeout_to_10_seconds() =>
            _options.SigningKeyLoadTimeoutSeconds.Should().Be(10);
    }

    [TestFixture]
    public class Given_signing_key_settings_are_configured
    {
        private IdentityOptions _options = null!;

        [SetUp]
        public void Setup() =>
            _options = BindIdentityOptions(
                new Dictionary<string, string?>
                {
                    ["IdentitySettings:SigningKeyRefreshIntervalSeconds"] = "120",
                    ["IdentitySettings:SigningKeyMaxStalenessSeconds"] = "900",
                    ["IdentitySettings:SigningKeyUnknownKeyRefreshCooldownSeconds"] = "15",
                    ["IdentitySettings:SigningKeyLoadTimeoutSeconds"] = "5",
                }
            );

        // Each value differs from its default, so a misspelled key would come back as the default and fail here.
        [Test]
        public void It_binds_the_refresh_interval() =>
            _options.SigningKeyRefreshIntervalSeconds.Should().Be(120);

        [Test]
        public void It_binds_the_max_staleness() => _options.SigningKeyMaxStalenessSeconds.Should().Be(900);

        [Test]
        public void It_binds_the_unknown_key_cooldown() =>
            _options.SigningKeyUnknownKeyRefreshCooldownSeconds.Should().Be(15);

        [Test]
        public void It_binds_the_load_timeout() => _options.SigningKeyLoadTimeoutSeconds.Should().Be(5);
    }

    [TestFixture]
    public class Given_an_invalid_signing_key_setting
    {
        private ServiceProvider _provider = null!;

        [SetUp]
        public void Setup() =>
            _provider = BuildProvider(
                new Dictionary<string, string?> { ["IdentitySettings:SigningKeyLoadTimeoutSeconds"] = "0" }
            );

        [TearDown]
        public void TearDown() => _provider.Dispose();

        // The host runs IStartupValidator before it starts serving, so the options are rejected at startup rather
        // than on the first authenticated request.
        [Test]
        public void It_fails_startup_validation()
        {
            Action validate = () => _provider.GetRequiredService<IStartupValidator>().Validate();

            validate
                .Should()
                .Throw<OptionsValidationException>()
                .Which.Failures.Should()
                .Equal("IdentitySettings:SigningKeyLoadTimeoutSeconds must be between 1 and 60; it is 0.");
        }
    }

    [TestFixture]
    public class Given_the_options_are_registered_twice
    {
        private ServiceProvider _provider = null!;

        [SetUp]
        public void Setup()
        {
            IConfiguration configuration = new ConfigurationBuilder().Build();
            ServiceCollection services = new();
            services.AddOpenIddictIdentityOptions(configuration);
            services.AddOpenIddictIdentityOptions(configuration);
            _provider = services.BuildServiceProvider();
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_registers_the_signing_key_validator_once() =>
            _provider
                .GetServices<IValidateOptions<IdentityOptions>>()
                .OfType<SigningKeyOptionsValidator>()
                .Should()
                .ContainSingle();
    }

    /// <summary>
    /// The options, logging, a faked key repository, and the signing-key services registered
    /// <paramref name="registrations"/> times.
    /// </summary>
    private static ServiceProvider BuildSigningKeyProvider(
        IOpenIddictTokenRepository repository,
        Dictionary<string, string?>? settings = null,
        int registrations = 1,
        Action<IServiceCollection>? before = null
    )
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? [])
            .Build();
        ServiceCollection services = new();
        before?.Invoke(services);
        services.AddOpenIddictIdentityOptions(configuration);
        services.AddLogging();
        services.AddSingleton(repository);
        for (int registration = 0; registration < registrations; registration++)
        {
            services.AddSigningKeyServices();
        }

        return services.BuildServiceProvider();
    }

    // Step 2.1: registering twice still yields one provider and one refresh service, and that service refreshes the
    // provider every consumer resolves.
    [TestFixture]
    public class Given_the_signing_key_services_are_registered_twice
    {
        private ServiceProvider _provider = null!;
        private IReadOnlyList<IHostedService> _refreshServices = [];
        private ISigningKeySnapshotProvider _first = null!;
        private ISigningKeySnapshotProvider _second = null!;
        private SigningKeySnapshot? _loadedByTheService;

        [SetUp]
        public async Task Setup()
        {
            KeyRepositoryHarness harness = new(TimeProvider.System);
            harness.ReturnsKeys("key-1");
            _provider = BuildSigningKeyProvider(harness.Repository, registrations: 2);

            _refreshServices =
            [
                .. _provider.GetServices<IHostedService>().OfType<SigningKeyRefreshService>(),
            ];
            _first = _provider.GetRequiredService<ISigningKeySnapshotProvider>();
            _second = _provider.GetRequiredService<ISigningKeySnapshotProvider>();

            foreach (IHostedService service in _refreshServices)
            {
                await service.StartAsync(CancellationToken.None);
            }

            await SigningKeyTestSupport.WaitUntilAsync(
                () => _first.Current is not null,
                () => "The refresh service did not load into the shared provider."
            );
            _loadedByTheService = _first.Current;

            foreach (IHostedService service in _refreshServices)
            {
                await service.StopAsync(CancellationToken.None);
            }
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_registers_one_refresh_service() => _refreshServices.Should().ContainSingle();

        [Test]
        public void It_registers_one_provider_descriptor() =>
            _provider.GetServices<ISigningKeySnapshotProvider>().Should().ContainSingle();

        [Test]
        public void It_shares_one_provider_instance() => _second.Should().BeSameAs(_first);

        [Test]
        public void It_refreshes_the_shared_provider() =>
            _loadedByTheService!.ContainsKeyId("key-1").Should().BeTrue();
    }

    [TestFixture]
    public class Given_database_signing_keys
    {
        private ServiceProvider _provider = null!;

        [SetUp]
        public void Setup() =>
            _provider = BuildSigningKeyProvider(new KeyRepositoryHarness(TimeProvider.System).Repository);

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_selects_the_database_source() =>
            _provider.GetRequiredService<ISigningKeySource>().Should().BeOfType<DatabaseSigningKeySource>();

        [Test]
        public void It_uses_the_system_clock() =>
            _provider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }

    [TestFixture]
    public class Given_certificate_signing_keys
    {
        private ServiceProvider _provider = null!;

        [SetUp]
        public void Setup() =>
            _provider = BuildSigningKeyProvider(
                new KeyRepositoryHarness(TimeProvider.System).Repository,
                new Dictionary<string, string?> { ["IdentitySettings:UseCertificates"] = "true" }
            );

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_selects_the_certificate_source() =>
            _provider
                .GetRequiredService<ISigningKeySource>()
                .Should()
                .BeOfType<CertificateSigningKeySource>();

        [Test]
        public void It_registers_one_development_certificate_store() =>
            _provider
                .GetRequiredService<DevelopmentCertificateStore>()
                .Should()
                .BeSameAs(_provider.GetRequiredService<DevelopmentCertificateStore>());
    }

    [TestFixture]
    public class Given_a_time_provider_registered_first
    {
        private ServiceProvider _provider = null!;
        private FakeTimeProvider _time = null!;

        [SetUp]
        public void Setup()
        {
            _time = new FakeTimeProvider();
            _provider = BuildSigningKeyProvider(
                new KeyRepositoryHarness(TimeProvider.System).Repository,
                before: services => services.AddSingleton<TimeProvider>(_time)
            );
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_keeps_the_registered_clock() =>
            _provider.GetRequiredService<TimeProvider>().Should().BeSameAs(_time);
    }

    // Step 2.3: the configuration manager and the shared bearer events are one singleton each, however often the
    // signing-key services are registered.
    [TestFixture]
    public class Given_the_request_boundary_services_are_registered_twice
    {
        private ServiceProvider _provider = null!;

        [SetUp]
        public void Setup() =>
            _provider = BuildSigningKeyProvider(
                new KeyRepositoryHarness(TimeProvider.System).Repository,
                registrations: 2,
                before: services => services.AddSingleton(A.Fake<ITokenManager>())
            );

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_registers_one_configuration_manager() =>
            _provider.GetServices<SigningKeyConfigurationManager>().Should().ContainSingle();

        [Test]
        public void It_shares_one_configuration_manager_instance() =>
            _provider
                .GetRequiredService<SigningKeyConfigurationManager>()
                .Should()
                .BeSameAs(_provider.GetRequiredService<SigningKeyConfigurationManager>());

        [Test]
        public void It_registers_one_bearer_events() =>
            _provider.GetServices<SigningKeyBearerEvents>().Should().ContainSingle();

        [Test]
        public void It_shares_one_bearer_events_instance() =>
            _provider
                .GetRequiredService<SigningKeyBearerEvents>()
                .Should()
                .BeSameAs(_provider.GetRequiredService<SigningKeyBearerEvents>());
    }

    // Each self-contained store registration adds the signing-key services, once however often it is called.
    [TestFixture("postgresql")]
    [TestFixture("postgresql-jwt-settings")]
    [TestFixture("mssql")]
    public class Given_a_self_contained_store_registration_called_twice(string registration)
    {
        private ServiceCollection _services = null!;

        [SetUp]
        public void Setup()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["IdentitySettings:Authority"] = "http://localhost",
                        ["IdentitySettings:Audience"] = "account",
                    }
                )
                .Build();
            _services = [];
            for (int call = 0; call < 2; call++)
            {
                _ = registration switch
                {
                    "postgresql" => _services.AddPostgresOpenIddictStores(configuration, "http://localhost"),
                    "postgresql-jwt-settings" => _services.AddPostgresOpenIddictStores(
                        configuration,
                        "http://localhost",
                        new JwtSettings { Issuer = "http://localhost", Audience = "account" }
                    ),
                    _ => _services.AddMssqlOpenIddictStores(configuration, "http://localhost"),
                };
            }
        }

        private int Count(Type serviceType, Type? implementationType = null) =>
            _services.Count(descriptor =>
                descriptor.ServiceType == serviceType
                && (implementationType is null || descriptor.ImplementationType == implementationType)
            );

        [Test]
        public void It_registers_one_provider() => Count(typeof(ISigningKeySnapshotProvider)).Should().Be(1);

        [Test]
        public void It_registers_one_refresh_service() =>
            Count(typeof(IHostedService), typeof(SigningKeyRefreshService)).Should().Be(1);

        [Test]
        public void It_registers_one_source() => Count(typeof(ISigningKeySource)).Should().Be(1);

        [Test]
        public void It_registers_one_development_certificate_store() =>
            Count(typeof(DevelopmentCertificateStore)).Should().Be(1);

        [Test]
        public void It_registers_one_clock() => Count(typeof(TimeProvider)).Should().Be(1);

        [Test]
        public void It_registers_one_configuration_manager() =>
            Count(typeof(SigningKeyConfigurationManager)).Should().Be(1);

        [Test]
        public void It_registers_one_bearer_events() => Count(typeof(SigningKeyBearerEvents)).Should().Be(1);
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
