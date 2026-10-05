// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

public class ServiceCollectionExtensionsTests
{
    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider Provider(Dictionary<string, string?> values, int registrations = 1)
    {
        ServiceCollection services = new();
        IConfiguration configuration = Configuration(values);
        for (int i = 0; i < registrations; i++)
        {
            services.AddDmsEducationOrganizationProjectionReader(configuration);
        }
        return services.BuildServiceProvider();
    }

    [TestFixture]
    public class Given_invalid_settings
    {
        private ServiceProvider _provider = null!;
        private Exception? _exception;

        [SetUp]
        public void Setup()
        {
            _provider = Provider(
                new()
                {
                    ["DmsEducationOrganizationProjectionSettings:DmsBaseUrl"] = "https://dms.example.org",
                    ["DmsEducationOrganizationProjectionSettings:PageSize"] = "0",
                }
            );
            try
            {
                // What the host runs at start for every options type registered with ValidateOnStart.
                _provider.GetRequiredService<IStartupValidator>().Validate();
                _exception = null;
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_fails_startup_validation() =>
            _exception
                .Should()
                .BeOfType<OptionsValidationException>()
                .Which.Message.Should()
                .Contain("DmsEducationOrganizationProjectionSettings:PageSize must be between 1 and 10000");
    }

    [TestFixture]
    public class Given_the_registration
    {
        private ServiceProvider _provider = null!;
        private IJobErrorCodeRegistry _registry = null!;

        [SetUp]
        public void Setup()
        {
            _provider = Provider([]);
            _registry = _provider.GetRequiredService<IJobErrorCodeRegistry>();
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_passes_startup_validation_when_not_configured() =>
            _provider.GetRequiredService<IStartupValidator>().Validate();

        [Test]
        public void It_registers_every_projection_error_code_with_its_message()
        {
            foreach ((string code, string message) in EducationOrganizationProjectionJobErrorCodes.All)
            {
                _registry.TryGet(code, out JobErrorCode? errorCode).Should().BeTrue(code);
                errorCode.Should().Be(new JobErrorCode(code, message));
            }
        }

        [Test]
        public void It_registers_the_logger_the_named_client_uses() =>
            _provider.GetService<ProjectionHttpClientLogger>().Should().NotBeNull();

        [Test]
        public void It_registers_a_time_provider() =>
            _provider.GetService<TimeProvider>().Should().BeSameAs(TimeProvider.System);

        [Test]
        public void It_registers_one_discovery_client_for_the_process() =>
            _provider
                .GetRequiredService<IDmsDiscoveryClient>()
                .Should()
                .BeOfType<DmsDiscoveryClient>()
                .And.BeSameAs(_provider.GetRequiredService<IDmsDiscoveryClient>());

        [Test]
        public void It_registers_one_token_provider_for_the_process() =>
            _provider
                .GetRequiredService<IProjectionServiceTokenProvider>()
                .Should()
                .BeOfType<ProjectionServiceTokenProvider>()
                .And.BeSameAs(_provider.GetRequiredService<IProjectionServiceTokenProvider>());
    }

    [TestFixture]
    public class Given_the_registration_called_twice
    {
        private Exception? _exception;
        private int _validators;

        [SetUp]
        public void Setup()
        {
            try
            {
                using ServiceProvider provider = Provider([], registrations: 2);
                provider.GetRequiredService<IJobErrorCodeRegistry>();
                _validators = provider
                    .GetServices<IValidateOptions<DmsEducationOrganizationProjectionSettings>>()
                    .Count();
                _exception = null;
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [Test]
        public void It_does_not_register_the_error_codes_twice() => _exception.Should().BeNull();

        [Test]
        public void It_registers_one_validator() => _validators.Should().Be(1);
    }

    [TestFixture]
    public class Given_the_named_http_client
    {
        private ServiceProvider _provider = null!;
        private HttpClient _client = null!;
        private List<HttpMessageHandler> _chain = null!;

        [SetUp]
        public void Setup()
        {
            _provider = Provider([]);
            _client = _provider
                .GetRequiredService<IHttpClientFactory>()
                .CreateClient(DmsEducationOrganizationProjectionHttpClient.Name);

            _chain = [];
            HttpMessageHandler? handler = _provider
                .GetRequiredService<IHttpMessageHandlerFactory>()
                .CreateHandler(DmsEducationOrganizationProjectionHttpClient.Name);
            while (handler is not null)
            {
                _chain.Add(handler);
                handler = (handler as DelegatingHandler)?.InnerHandler;
            }
        }

        [TearDown]
        public void TearDown()
        {
            _client.Dispose();
            _provider.Dispose();
        }

        [Test]
        public void It_has_no_client_wide_timeout() => _client.Timeout.Should().Be(Timeout.InfiniteTimeSpan);

        [Test]
        public void It_does_not_follow_redirects() =>
            _chain.OfType<SocketsHttpHandler>().Single().AllowAutoRedirect.Should().BeFalse();

        [Test]
        public void It_does_not_use_cookies() =>
            _chain.OfType<SocketsHttpHandler>().Single().UseCookies.Should().BeFalse();

        [Test]
        public void It_ends_in_the_configured_primary_handler() =>
            _chain[^1].Should().BeOfType<SocketsHttpHandler>();

        [Test]
        public void It_has_no_default_logging_handler() =>
            _chain
                .Select(handler => handler.GetType().FullName)
                .Should()
                .NotContain(name =>
                    name!.StartsWith("Microsoft.Extensions.Http.Logging.LoggingHttpMessageHandler")
                    || name.StartsWith("Microsoft.Extensions.Http.Logging.LoggingScopeHttpMessageHandler")
                );
    }
}
