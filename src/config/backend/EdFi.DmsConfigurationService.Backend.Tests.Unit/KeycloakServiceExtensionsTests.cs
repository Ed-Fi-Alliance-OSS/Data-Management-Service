// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// DMS-1327 D-12, registration half: Keycloak mode registers the Keycloak revocation and introspection
/// managers with a lifetime compatible with the scoped <see cref="KeycloakContext"/>. The provider
/// validates scopes, so a singleton registration capturing the scoped context would fail to resolve.
/// </summary>
public class KeycloakServiceExtensionsTests
{
    [TestFixture]
    public class Given_the_keycloak_services_are_registered
    {
        private ServiceCollection _services = null!;
        private ServiceProvider _provider = null!;
        private ITokenRevocationManager? _resolved;
        private ITokenIntrospectionManager _resolvedIntrospection = null!;

        [SetUp]
        public void Setup()
        {
            _services = new ServiceCollection();
            _services.AddSingleton(A.Fake<IHttpClientFactory>());
            _services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            _services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            _services.AddKeycloakServices(
                "http://keycloak.test/realms/edfi",
                "cms-service",
                "service-secret",
                "role"
            );
            _provider = _services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

            using IServiceScope scope = _provider.CreateScope();
            _resolved = scope.ServiceProvider.GetService<ITokenRevocationManager>();
            _resolvedIntrospection = scope.ServiceProvider.GetRequiredService<ITokenIntrospectionManager>();
        }

        [TearDown]
        public void TearDown() => _provider?.Dispose();

        [Test]
        public void It_resolves_the_keycloak_revocation_manager_in_a_request_scope() =>
            _resolved.Should().BeOfType<KeycloakTokenRevocationManager>();

        [Test]
        public void It_registers_the_manager_as_transient() =>
            _services
                .Should()
                .ContainSingle(descriptor => descriptor.ServiceType == typeof(ITokenRevocationManager))
                .Which.Lifetime.Should()
                .Be(ServiceLifetime.Transient);

        // DMS-1493 D-3: the CMS API client repository requires an un-approval participant in every
        // mode, so Keycloak mode registers the explicit no-op rather than leaving it unresolvable.
        [Test]
        public void It_resolves_the_no_op_unapproval_token_revocation()
        {
            using IServiceScope scope = _provider.CreateScope();
            scope
                .ServiceProvider.GetService<IUnapprovalTokenRevocation>()
                .Should()
                .BeOfType<NoOpUnapprovalTokenRevocation>();
        }

        [Test]
        public void It_registers_one_unapproval_token_revocation() =>
            _services
                .Should()
                .ContainSingle(descriptor => descriptor.ServiceType == typeof(IUnapprovalTokenRevocation));

        [Test]
        public void It_resolves_the_keycloak_introspection_manager_in_a_request_scope() =>
            _resolvedIntrospection.Should().BeOfType<KeycloakTokenIntrospectionManager>();

        [Test]
        public void It_registers_the_introspection_manager_as_transient() =>
            _services
                .Should()
                .ContainSingle(descriptor => descriptor.ServiceType == typeof(ITokenIntrospectionManager))
                .Which.Lifetime.Should()
                .Be(ServiceLifetime.Transient);
    }
}
