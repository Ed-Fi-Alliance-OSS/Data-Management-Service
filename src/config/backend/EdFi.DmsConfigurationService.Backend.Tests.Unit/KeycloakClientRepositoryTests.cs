// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using FakeItEasy;
using FluentAssertions;
using Keycloak.Net.Models.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class KeycloakClientRepositoryTests
{
    private IKeycloakClientFacade _keycloakClientFacade = null!;
    private ILogger<KeycloakClientRepository> _logger = null!;
    private IOptions<ClientSecretValidationOptions> _clientSecretValidationOptionsAccessor = null!;
    private KeycloakClientRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _keycloakClientFacade = A.Fake<IKeycloakClientFacade>();
        _logger = A.Fake<ILogger<KeycloakClientRepository>>();
        _clientSecretValidationOptionsAccessor = Options.Create(
            new ClientSecretValidationOptions
            {
                MinimumLength = 40,
                MaximumLength = 128,
            }
        );

        _repository = new KeycloakClientRepository(
            new KeycloakContext("http://localhost:8045", "edfi", "admin-client", "secret", "role"),
            _keycloakClientFacade,
            _logger,
            _clientSecretValidationOptionsAccessor
        );
    }

    [TestFixture]
    public class Given_ResetCredentialsAsync : KeycloakClientRepositoryTests
    {
        [Test]
        public async Task It_should_generate_a_secret_using_the_configured_policy_and_update_the_client()
        {
            var clientUuid = Guid.NewGuid().ToString();
            var existingClient = new Client
            {
                ClientId = "test-client",
                Secret = "ExistingSecret123!",
                Name = "Test Client",
            };

            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", clientUuid))
                .Returns(existingClient);
            A.CallTo(() => _keycloakClientFacade.UpdateClientAsync("edfi", clientUuid, existingClient))
                .Returns(true);

            var result = await _repository.ResetCredentialsAsync(clientUuid);

            result.Should().BeOfType<ClientResetResult.Success>();
            var success = (ClientResetResult.Success)result;
            success.ClientSecret.Should().HaveLength(40);
            success
                .ClientSecret.Should()
                .MatchRegex(
                    ClientSecretValidation.BuildComplexityPattern(
                        _clientSecretValidationOptionsAccessor.Value
                    )
                );
            existingClient.Secret.Should().Be(success.ClientSecret);
            A.CallTo(() => _keycloakClientFacade.UpdateClientAsync("edfi", clientUuid, existingClient))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_should_return_failure_unknown_when_the_update_does_not_succeed()
        {
            var clientUuid = Guid.NewGuid().ToString();
            var existingClient = new Client
            {
                ClientId = "test-client",
                Secret = "ExistingSecret123!",
                Name = "Test Client",
            };

            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", clientUuid))
                .Returns(existingClient);
            A.CallTo(() => _keycloakClientFacade.UpdateClientAsync("edfi", clientUuid, existingClient))
                .Returns(false);

            var result = await _repository.ResetCredentialsAsync(clientUuid);

            result.Should().BeOfType<ClientResetResult.FailureUnknown>();
        }

        [Test]
        public async Task It_should_return_failure_client_not_found_when_keycloak_returns_not_found()
        {
            var clientUuid = Guid.NewGuid().ToString();
            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", clientUuid))
                .Returns(Task.FromResult<Client>(null!));

            var result = await _repository.ResetCredentialsAsync(clientUuid);

            result.Should().BeOfType<ClientResetResult.FailureClientNotFound>();
        }
    }
}
