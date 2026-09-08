// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Net;
using EdFi.DmsConfigurationService.Backend;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

/// <summary>
/// Verifies that a rejected DatabaseSettings:EncryptionKey stops the host from starting, rather than
/// starting a host that answers every request with a generic 500. The application resolves
/// IOptions&lt;DatabaseOptions&gt; outside the invalid-configuration gate for exactly this reason, so
/// these fixtures are the only coverage of that behavior: a request-level assertion would pass just as
/// well against a started-but-disabled application.
/// </summary>
public class DatabaseOptionsStartupTests
{
    /// <summary>
    /// 32 characters, and deliberately not the key in appsettings.Test.json, so the override is what
    /// the successful boot runs on.
    /// </summary>
    private const string ValidEncryptionKey = "Fk3pQ8sT2vW9xZ4bC6dE1gH5jL7mN0rY";

    private const string ShippedDefaultEncryptionKey = "YourSecureEncryptionKey32Characters";

    /// <summary>
    /// The 32-character prefix of <see cref="ShippedDefaultEncryptionKey" />, which derives the same AES
    /// key. It passes the length and ASCII rules, so it is the shape most likely to reach a running host
    /// if the known-default rule ever stops covering significant prefixes.
    /// </summary>
    private const string ShippedDefaultEncryptionKeySignificantPrefix = "YourSecureEncryptionKey32Charact";

    private static WebApplicationFactory<Program> CreateFactory(string encryptionKey) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["DatabaseSettings:EncryptionKey"] = encryptionKey }
                    )
            );
        });

    /// <summary>
    /// The test host wraps exceptions thrown from the entry point before RunAsync, so the validation
    /// failure is located by walking the chain instead of asserting on the outermost type.
    /// </summary>
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

    /// <summary>
    /// Boot is triggered here rather than by constructing the factory: WebApplicationFactory defers
    /// the entry point until the server is first needed.
    /// </summary>
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

    [TestFixture]
    public class Given_a_short_database_encryption_key_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private OptionsValidationException? _validationFailure;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory("abc");
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
        public void It_reports_the_minimum_length_rule() =>
            _validationFailure!
                .Message.Should()
                .Contain("DatabaseSettings:EncryptionKey")
                .And.Contain("at least 32");
    }

    [TestFixture]
    public class Given_the_known_default_database_encryption_key_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private OptionsValidationException? _validationFailure;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory(ShippedDefaultEncryptionKey);
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
        public void It_reports_the_known_default_rule() =>
            _validationFailure!
                .Message.Should()
                .Contain("DatabaseSettings:EncryptionKey")
                .And.Contain("default");
    }

    [TestFixture]
    public class Given_the_significant_prefix_of_the_default_database_encryption_key_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private Exception? _exception;
        private OptionsValidationException? _validationFailure;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory(ShippedDefaultEncryptionKeySignificantPrefix);
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
        public void It_reports_the_known_default_rule() =>
            _validationFailure!
                .Message.Should()
                .Contain("DatabaseSettings:EncryptionKey")
                .And.Contain("default");
    }

    [TestFixture]
    public class Given_a_valid_database_encryption_key_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Act()
        {
            _factory = CreateFactory(ValidEncryptionKey);
            using var client = _factory.CreateClient();
            _response = await client.GetAsync("/health");
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _factory.Dispose();
        }

        [Test]
        public void It_starts_and_serves_the_endpoint() =>
            _response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The value shipped in appsettings.json must be one the validator rejects, so a deployment that
    /// never overrides it fails loudly instead of running on a key published in this repository.
    /// Every other fixture here supplies its own key, and the running host reads the override in
    /// appsettings.Test.json, so nothing else observes what is actually shipped. Reading the file from
    /// the build output rather than through the host is what makes the shipped value visible; the copy
    /// is produced from the application project on every build.
    /// </summary>
    [TestFixture]
    public class Given_the_shipped_application_settings
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act()
        {
            IConfigurationRoot shippedConfiguration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
                .Build();

            DatabaseOptions shippedOptions = new()
            {
                DatabaseConnection =
                    shippedConfiguration["DatabaseSettings:DatabaseConnection"] ?? string.Empty,
                EncryptionKey = shippedConfiguration["DatabaseSettings:EncryptionKey"] ?? string.Empty,
            };

            _result = new DatabaseOptionsValidator().Validate(Options.DefaultName, shippedOptions);
        }

        [Test]
        public void It_is_rejected_by_the_validator() => _result.Failed.Should().BeTrue();

        /// <summary>
        /// DatabaseConnection is validated first, so naming EncryptionKey here also proves the shipped
        /// connection string is intact and that the rejection is not coming from the wrong setting.
        /// </summary>
        [Test]
        public void It_is_rejected_for_the_encryption_key() =>
            _result.FailureMessage.Should().Contain("EncryptionKey");
    }
}
