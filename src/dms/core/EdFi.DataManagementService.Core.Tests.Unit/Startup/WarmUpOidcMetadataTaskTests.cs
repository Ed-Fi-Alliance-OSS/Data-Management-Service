// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
public class WarmUpOidcMetadataTaskTests
{
    private static AppSettings AppSettingsWith(bool bypassAuthorization) =>
        new() { AllowIdentityUpdateOverrides = string.Empty, BypassAuthorization = bypassAuthorization };

    [TestFixture]
    public class Given_Default_Construction : WarmUpOidcMetadataTaskTests
    {
        private WarmUpOidcMetadataTask _task = null!;

        [SetUp]
        public void Setup()
        {
            _task = new WarmUpOidcMetadataTask(
                A.Fake<IServiceProvider>(),
                Options.Create(AppSettingsWith(bypassAuthorization: false)),
                NullLogger<WarmUpOidcMetadataTask>.Instance
            );
        }

        [Test]
        public void It_has_order_400()
        {
            _task.Order.Should().Be(400);
        }

        [Test]
        public void It_has_expected_name()
        {
            _task.Name.Should().Be("Warm Up OIDC Metadata Cache");
        }
    }

    [TestFixture]
    public class Given_BypassAuthorization_Is_Enabled : WarmUpOidcMetadataTaskTests
    {
        private IServiceProvider _serviceProvider = null!;
        private WarmUpOidcMetadataTask _task = null!;

        [SetUp]
        public async Task Setup()
        {
            _serviceProvider = A.Fake<IServiceProvider>();
            _task = new WarmUpOidcMetadataTask(
                _serviceProvider,
                Options.Create(AppSettingsWith(bypassAuthorization: true)),
                NullLogger<WarmUpOidcMetadataTask>.Instance
            );

            await _task.ExecuteAsync(CancellationToken.None);
        }

        [Test]
        public void It_does_not_resolve_the_oidc_configuration_manager()
        {
            A.CallTo(() =>
                    _serviceProvider.GetService(typeof(IConfigurationManager<OpenIdConnectConfiguration>))
                )
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_Bypass_Disabled_And_Metadata_Succeeds : WarmUpOidcMetadataTaskTests
    {
        private IServiceProvider _serviceProvider = null!;
        private IConfigurationManager<OpenIdConnectConfiguration> _configurationManager = null!;
        private WarmUpOidcMetadataTask _task = null!;

        [SetUp]
        public async Task Setup()
        {
            _configurationManager = A.Fake<IConfigurationManager<OpenIdConnectConfiguration>>();
            A.CallTo(() => _configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .Returns(new OpenIdConnectConfiguration { Issuer = "https://issuer.example" });

            _serviceProvider = A.Fake<IServiceProvider>();
            A.CallTo(() =>
                    _serviceProvider.GetService(typeof(IConfigurationManager<OpenIdConnectConfiguration>))
                )
                .Returns(_configurationManager);

            _task = new WarmUpOidcMetadataTask(
                _serviceProvider,
                Options.Create(AppSettingsWith(bypassAuthorization: false)),
                NullLogger<WarmUpOidcMetadataTask>.Instance
            );

            await _task.ExecuteAsync(CancellationToken.None);
        }

        [Test]
        public void It_resolves_the_oidc_configuration_manager()
        {
            A.CallTo(() =>
                    _serviceProvider.GetService(typeof(IConfigurationManager<OpenIdConnectConfiguration>))
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_calls_get_configuration_async_exactly_once()
        {
            A.CallTo(() => _configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_Metadata_Fetch_Fails : WarmUpOidcMetadataTaskTests
    {
        private WarmUpOidcMetadataTask _task = null!;

        [SetUp]
        public void Setup()
        {
            var configurationManager = A.Fake<IConfigurationManager<OpenIdConnectConfiguration>>();
            A.CallTo(() => configurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException("OIDC metadata unavailable"));

            var serviceProvider = A.Fake<IServiceProvider>();
            A.CallTo(() =>
                    serviceProvider.GetService(typeof(IConfigurationManager<OpenIdConnectConfiguration>))
                )
                .Returns(configurationManager);

            _task = new WarmUpOidcMetadataTask(
                serviceProvider,
                Options.Create(AppSettingsWith(bypassAuthorization: false)),
                NullLogger<WarmUpOidcMetadataTask>.Instance
            );
        }

        [Test]
        public async Task It_propagates_the_exception()
        {
            Func<Task> act = async () => await _task.ExecuteAsync(CancellationToken.None);

            await act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("OIDC metadata unavailable");
        }
    }
}
