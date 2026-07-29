// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using FakeItEasy;
using FluentAssertions;
using Flurl.Http;
using Keycloak.Net.Models.Clients;
using Keycloak.Net.Models.ClientScopes;
using Keycloak.Net.Models.Roles;
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
            new ClientSecretValidationOptions { MinimumLength = 40, MaximumLength = 128 }
        );

        _repository = new KeycloakClientRepository(
            new KeycloakContext("http://localhost:8045", "edfi", "admin-client", "secret", "role"),
            _keycloakClientFacade,
            _logger,
            _clientSecretValidationOptionsAccessor
        );
    }

    /// <summary>
    /// Builds an exception shaped the way Keycloak.Net raises one: its calls go through Flurl,
    /// which converts every non-success status into a <see cref="FlurlHttpException"/> carrying
    /// the response, so a missing client never surfaces as a null return.
    /// </summary>
    protected static FlurlHttpException CreateFlurlHttpException(
        HttpStatusCode statusCode,
        HttpMethod? method = null,
        string url = "http://localhost:8045/admin/realms/edfi/clients/x"
    )
    {
        var call = new FlurlCall
        {
            Request = new FlurlRequest(url),
            HttpRequestMessage = new HttpRequestMessage(method ?? HttpMethod.Get, url),
            HttpResponseMessage = new HttpResponseMessage(statusCode),
        };
        call.Response = new FlurlResponse(call);
        return new FlurlHttpException(call);
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

            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", clientUuid)).Returns(existingClient);
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
        public async Task It_should_generate_a_secret_free_of_transport_unsafe_characters()
        {
            var clientUuid = Guid.NewGuid().ToString();
            var existingClient = new Client
            {
                ClientId = "test-client",
                Secret = "ExistingSecret123!",
                Name = "Test Client",
            };

            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", clientUuid)).Returns(existingClient);
            A.CallTo(() => _keycloakClientFacade.UpdateClientAsync("edfi", clientUuid, existingClient))
                .Returns(true);

            var result = await _repository.ResetCredentialsAsync(clientUuid);

            result.Should().BeOfType<ClientResetResult.Success>();
            var success = (ClientResetResult.Success)result;
            success.ClientSecret.Should().NotContainAny("+", "%", "=", "&", " ");
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

            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", clientUuid)).Returns(existingClient);
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

    public abstract class DeleteClientTestBase : KeycloakClientRepositoryTests
    {
        protected string _clientUuid = null!;
        protected ClientDeleteResult _result = null!;
    }

    [TestFixture]
    public class Given_a_client_delete_that_succeeds : DeleteClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _clientUuid = Guid.NewGuid().ToString();
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync("edfi", _clientUuid)).Returns(true);

            _result = await _repository.DeleteClientAsync(_clientUuid);
        }

        [Test]
        public void It_returns_success() => _result.Should().BeOfType<ClientDeleteResult.Success>();
    }

    [TestFixture]
    public class Given_a_client_delete_whose_client_is_already_missing : DeleteClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _clientUuid = Guid.NewGuid().ToString();
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync("edfi", _clientUuid))
                .Throws(CreateFlurlHttpException(HttpStatusCode.NotFound));

            _result = await _repository.DeleteClientAsync(_clientUuid);
        }

        [Test]
        public void It_returns_failure_client_not_found() =>
            _result.Should().BeOfType<ClientDeleteResult.FailureClientNotFound>();
    }

    [TestFixture]
    public class Given_a_client_delete_that_fails_at_keycloak : DeleteClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _clientUuid = Guid.NewGuid().ToString();
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync("edfi", _clientUuid))
                .Throws(CreateFlurlHttpException(HttpStatusCode.Forbidden));

            _result = await _repository.DeleteClientAsync(_clientUuid);
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientDeleteResult.FailureIdentityProvider>();
    }

    /// <summary>
    /// Shared arrangement for the stored-client lookup phase of an update. No mutation is
    /// arranged, so any provider mutation a fixture observes is one the lookup phase should
    /// never have reached.
    /// </summary>
    public abstract class UpdateStoredClientLookupTestBase : KeycloakClientRepositoryTests
    {
        protected string _clientUuid = null!;
        protected ClientUpdateResult _result = null!;

        [SetUp]
        public void SetUpLookupDefaults() => _clientUuid = Guid.NewGuid().ToString();

        protected async Task ActUpdateAsync() =>
            _result = await _repository.UpdateClientAsync(
                _clientUuid,
                "Updated Client",
                "test-scope",
                "200,300",
                [1, 2],
                false,
                "role"
            );

        protected void AssertNoProviderMutation()
        {
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync(A<string>.Ignored, A<string>.Ignored))
                .MustNotHaveHappened();
            A.CallTo(() =>
                    _keycloakClientFacade.CreateClientAndRetrieveClientIdAsync(
                        A<string>.Ignored,
                        A<Client>.Ignored
                    )
                )
                .MustNotHaveHappened();
            A.CallTo(() =>
                    _keycloakClientFacade.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<Client>.Ignored
                    )
                )
                .MustNotHaveHappened();
            A.CallTo(() =>
                    _keycloakClientFacade.GetUserForServiceAccountAsync(A<string>.Ignored, A<string>.Ignored)
                )
                .MustNotHaveHappened();
            A.CallTo(() =>
                    _keycloakClientFacade.AddRealmRoleMappingsToUserAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<IEnumerable<Role>>.Ignored
                    )
                )
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_an_update_whose_stored_client_lookup_reports_not_found
        : UpdateStoredClientLookupTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", _clientUuid))
                .Throws(CreateFlurlHttpException(HttpStatusCode.NotFound));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_not_found() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureNotFound>();

        [Test]
        public void It_performs_no_provider_mutation() => AssertNoProviderMutation();

        [Test]
        public void It_does_not_read_realm_roles() =>
            A.CallTo(() => _keycloakClientFacade.GetRolesAsync(A<string>.Ignored)).MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_an_update_whose_stored_client_lookup_fails_at_keycloak
        : UpdateStoredClientLookupTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", _clientUuid))
                .Throws(CreateFlurlHttpException(HttpStatusCode.Forbidden));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureIdentityProvider>();

        [Test]
        public void It_performs_no_provider_mutation() => AssertNoProviderMutation();
    }

    [TestFixture]
    public class Given_an_update_whose_stored_client_lookup_throws_an_unexpected_error
        : UpdateStoredClientLookupTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", _clientUuid))
                .Throws(new InvalidOperationException("transport misconfigured"));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureUnknown>();

        [Test]
        public void It_performs_no_provider_mutation() => AssertNoProviderMutation();
    }

    [TestFixture]
    public class Given_an_update_whose_stored_client_is_absent : UpdateStoredClientLookupTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", _clientUuid))
                .Returns(Task.FromResult<Client>(null!));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_not_found() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureNotFound>();

        [Test]
        public void It_performs_no_provider_mutation() => AssertNoProviderMutation();
    }

    [TestFixture]
    public class Given_UpdateClientAsync : KeycloakClientRepositoryTests
    {
        [Test]
        public async Task It_should_recreate_the_client_with_the_requested_enabled_state()
        {
            var clientUuid = Guid.NewGuid().ToString();
            var recreatedClientUuid = Guid.NewGuid();
            var existingClient = new Client
            {
                ClientId = "test-client",
                Secret = "ExistingSecret123!",
                Name = "Test Client",
                Enabled = true,
                ProtocolMappers = [],
                DefaultClientScopes = ["test-scope"],
            };

            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", clientUuid)).Returns(existingClient);
            A.CallTo(() => _keycloakClientFacade.GetClientScopesAsync("edfi"))
                .Returns([new ClientScope { Name = "test-scope" }]);
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync("edfi", clientUuid)).Returns(true);
            A.CallTo(() =>
                    _keycloakClientFacade.CreateClientAndRetrieveClientIdAsync("edfi", A<Client>.Ignored)
                )
                .Invokes(call =>
                {
                    var recreatedClient = call.GetArgument<Client>(1);
                    recreatedClient.Should().NotBeNull();
                    recreatedClient!.Enabled.Should().BeFalse();
                    recreatedClient.Name.Should().Be("Updated Client");
                    recreatedClient.DefaultClientScopes.Should().Equal("test-scope");
                })
                .Returns(recreatedClientUuid.ToString());

            var result = await _repository.UpdateClientAsync(
                clientUuid,
                "Updated Client",
                "test-scope",
                "200,300",
                [1, 2],
                false
            );

            result.Should().BeOfType<ClientUpdateResult.Success>();
            ((ClientUpdateResult.Success)result).ClientUuid.Should().Be(recreatedClientUuid);
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync("edfi", clientUuid))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() =>
                    _keycloakClientFacade.CreateClientAndRetrieveClientIdAsync("edfi", A<Client>.Ignored)
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _keycloakClientFacade.UpdateClientAsync("edfi", clientUuid, A<Client>.Ignored))
                .MustNotHaveHappened();
        }
    }
}
