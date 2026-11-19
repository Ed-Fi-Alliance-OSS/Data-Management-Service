// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Net;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.EducationOrganizationProjection;

/// <summary>
/// Verifies that the host registers the DMS projection reader settings with startup validation (DMS-1440 spec §5.2):
/// invalid settings stop the host with a message that names the setting and carries no secret, and the shipped
/// settings, which leave the reader unconfigured, start it.
/// </summary>
public class DmsEducationOrganizationProjectionStartupTests
{
    private const string Section = "DmsEducationOrganizationProjectionSettings";
    private const string Secret = "s3cret-Value-1440";

    private static WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(settings)
            );
        });

    /// <summary>The test host wraps exceptions thrown from the entry point, so the chain is walked.</summary>
    private static OptionsValidationException? FindOptionsValidationException(Exception? exception) =>
        exception switch
        {
            null => null,
            OptionsValidationException match => match,
            AggregateException aggregate => aggregate
                .InnerExceptions.Select(FindOptionsValidationException)
                .FirstOrDefault(found => found is not null),
            _ => FindOptionsValidationException(exception.InnerException),
        };

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

    [TestFixture("PageSize", "0", "PageSize must be between 1 and 10000; it is 0.")]
    [TestFixture(
        "MaxResponseBodyBytes",
        "4097023",
        "MaxResponseBodyBytes must be at least DmsEducationOrganizationProjectionSettings:PageSize (2000) x 2048 + 1024"
    )]
    [TestFixture("DmsBaseUrl", "ftp://dms.example.org", "DmsBaseUrl must be an absolute http or https URL")]
    [TestFixture("Credentials:ClientId", "", "Credentials or at least one")]
    public class Given_an_invalid_projection_setting_at_startup(string key, string value, string expected)
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private OptionsValidationException? _validationFailure;

        [SetUp]
        public void Act()
        {
            Dictionary<string, string?> settings = new()
            {
                [$"{Section}:DmsBaseUrl"] = "https://dms.example.org/api",
                [$"{Section}:Credentials:ClientId"] = "projection-client",
                [$"{Section}:Credentials:ClientSecret"] = Secret,
            };
            settings[$"{Section}:{key}"] = value;
            if (key.StartsWith("Credentials"))
            {
                settings[$"{Section}:Credentials:ClientSecret"] = "";
            }

            _factory = CreateFactory(settings);
            _exception = StartupExceptionFor(_factory);
            _validationFailure = FindOptionsValidationException(_exception);
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_fails_to_start() => _exception.Should().NotBeNull();

        [Test]
        public void It_fails_with_an_options_validation_exception() =>
            _validationFailure.Should().NotBeNull();

        [Test]
        public void It_names_the_setting() =>
            _validationFailure!.Message.Should().Contain($"{Section}:{expected}");
    }

    [TestFixture]
    public class Given_secrets_in_invalid_projection_settings_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory(
                new()
                {
                    [$"{Section}:DmsBaseUrl"] = $"https://user:{Secret}@dms.example.org/api",
                    [$"{Section}:Credentials:ClientSecret"] = Secret,
                    [$"{Section}:TenantCredentials:Tenant1:ClientSecret"] = Secret,
                }
            );
            _exception = StartupExceptionFor(_factory);
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_fails_to_start() => FindOptionsValidationException(_exception).Should().NotBeNull();

        [Test]
        public void It_keeps_every_secret_out_of_the_startup_failure() =>
            _exception!.ToString().Should().NotContain(Secret);
    }

    [TestFixture]
    public class Given_the_shipped_projection_settings_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpStatusCode _health;
        private DmsEducationOrganizationProjectionSettings _settings = null!;
        private IJobErrorCodeRegistry _registry = null!;
        private HttpClient _projectionClient = null!;

        [SetUp]
        public async Task Act()
        {
            _factory = CreateFactory([]);
            using (var client = _factory.CreateClient())
            {
                _health = (await client.GetAsync("/health")).StatusCode;
            }

            _settings = _factory
                .Services.GetRequiredService<IOptions<DmsEducationOrganizationProjectionSettings>>()
                .Value;
            _registry = _factory.Services.GetRequiredService<IJobErrorCodeRegistry>();
            _projectionClient = _factory
                .Services.GetRequiredService<IHttpClientFactory>()
                .CreateClient(DmsEducationOrganizationProjectionHttpClient.Name);
        }

        [TearDown]
        public void TearDown()
        {
            _projectionClient.Dispose();
            _factory.Dispose();
        }

        [Test]
        public void It_starts() => _health.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_leaves_the_reader_unconfigured() => _settings.DmsBaseUrl.Should().BeNull();

        [Test]
        public void It_registers_the_projection_error_codes() =>
            EducationOrganizationProjectionJobErrorCodes
                .All.Where(pair => !_registry.TryGet(pair.Key, out _))
                .Should()
                .BeEmpty();

        [Test]
        public void It_registers_the_named_client() =>
            _projectionClient.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [TestFixture]
    public class Given_valid_projection_settings_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpStatusCode _health;

        [SetUp]
        public async Task Act()
        {
            _factory = CreateFactory(
                new()
                {
                    [$"{Section}:DmsBaseUrl"] = "https://dms.example.org/api",
                    [$"{Section}:TenantCredentials:Tenant1:ClientId"] = "t1-client",
                    [$"{Section}:TenantCredentials:Tenant1:ClientSecret"] = Secret,
                    [$"{Section}:PageSize"] = "4000",
                    [$"{Section}:MaxResponseBodyBytes"] = "8193024",
                }
            );
            using var client = _factory.CreateClient();
            _health = (await client.GetAsync("/health")).StatusCode;
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_starts() => _health.Should().Be(HttpStatusCode.OK);
    }
}
