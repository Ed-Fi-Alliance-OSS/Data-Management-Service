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
            A.CallTo(() =>
                    _keycloakClientFacade.CreateClientScopeAsync(A<string>.Ignored, A<ClientScope>.Ignored)
                )
                .MustNotHaveHappened();
            A.CallTo(() =>
                    _keycloakClientFacade.UpdateDefaultClientScopeAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored
                    )
                )
                .MustNotHaveHappened();
            A.CallTo(() =>
                    _keycloakClientFacade.DeleteDefaultClientScopeAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored
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

    /// <summary>
    /// Shared arrangement for the in-place update. The stored client carries a mapper with no
    /// <c>claim.name</c> at all, so any claim lookup that indexes the configuration blindly fails
    /// here rather than in production.
    /// </summary>
    public abstract class InPlaceUpdateTestBase : KeycloakClientRepositoryTests
    {
        protected const string TargetScopeName = "target-scope";
        protected const string TargetScopeId = "target-scope-id";
        protected const string StaleScopeName = "stale-scope";
        protected const string StaleScopeId = "stale-scope-id";
        protected const string RealmDefaultScopeId = "realm-default-scope-id";
        protected const string ServiceAccountScopeId = "service-account-scope-id";

        protected string _clientUuid = null!;
        protected Client _storedClient = null!;
        protected ClientUpdateResult _result = null!;
        protected List<Client> _clientUpdates = null!;
        protected List<string> _scopeCallOrder = null!;

        [SetUp]
        public void SetUpInPlaceDefaults()
        {
            _clientUuid = Guid.NewGuid().ToString();
            _clientUpdates = [];
            _scopeCallOrder = [];

            _storedClient = new Client
            {
                ClientId = "test-client",
                Secret = "ExistingSecret123!",
                Name = "Original Client",
                Enabled = true,
                ServiceAccountsEnabled = true,
                DefaultClientScopes = [StaleScopeName],
                ProtocolMappers =
                [
                    MapperWithoutClaimName(),
                    ClaimMapper("Education Organization Ids", "educationOrganizationIds", "100"),
                    ClaimMapper("Data Store IDs", "dataStoreIds", "7,8"),
                ],
            };

            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", _clientUuid)).Returns(_storedClient);

            A.CallTo(() => _keycloakClientFacade.GetClientScopesAsync("edfi"))
                .Returns([
                    new ClientScope { Id = TargetScopeId, Name = TargetScopeName },
                    new ClientScope { Id = StaleScopeId, Name = StaleScopeName },
                ]);

            A.CallTo(() => _keycloakClientFacade.GetDefaultClientScopesAsync("edfi", _clientUuid))
                .Returns([
                    new ClientScope { Id = ServiceAccountScopeId, Name = "service_account" },
                    new ClientScope { Id = RealmDefaultScopeId, Name = "profile" },
                    new ClientScope { Id = StaleScopeId, Name = StaleScopeName },
                ]);

            A.CallTo(() => _keycloakClientFacade.GetRealmDefaultClientScopesAsync("edfi"))
                .Returns([new ClientScope { Id = RealmDefaultScopeId, Name = "profile" }]);

            A.CallTo(() => _keycloakClientFacade.UpdateClientAsync("edfi", _clientUuid, A<Client>.Ignored))
                .Invokes(call => _clientUpdates.Add(call.GetArgument<Client>(2)!))
                .Returns(true);

            A.CallTo(() =>
                    _keycloakClientFacade.DeleteDefaultClientScopeAsync(
                        "edfi",
                        _clientUuid,
                        A<string>.Ignored
                    )
                )
                .Invokes(call => _scopeCallOrder.Add($"remove:{call.GetArgument<string>(2)}"))
                .Returns(true);

            A.CallTo(() =>
                    _keycloakClientFacade.UpdateDefaultClientScopeAsync(
                        "edfi",
                        _clientUuid,
                        A<string>.Ignored
                    )
                )
                .Invokes(call => _scopeCallOrder.Add($"assign:{call.GetArgument<string>(2)}"))
                .Returns(true);
        }

        protected static ClientProtocolMapper ClaimMapper(string name, string claimName, string value) =>
            new()
            {
                Name = name,
                Protocol = "openid-connect",
                ProtocolMapper = "oidc-hardcoded-claim-mapper",
                Config = new Dictionary<string, string>
                {
                    { "claim.name", claimName },
                    { "claim.value", value },
                },
            };

        protected static ClientProtocolMapper MapperWithoutClaimName() =>
            new()
            {
                Name = "Configuration service role mapper",
                Protocol = "openid-connect",
                ProtocolMapper = "oidc-usermodel-realm-role-mapper",
                Config = new Dictionary<string, string> { { "multivalued", "true" } },
            };

        protected async Task ActUpdateAsync(int[]? dataStoreIds = null) =>
            _result = await _repository.UpdateClientAsync(
                _clientUuid,
                "Updated Client",
                TargetScopeName,
                "200,300",
                dataStoreIds ?? [2, 1],
                false,
                "role"
            );

        protected Client AppliedClient() => _clientUpdates.Single();

        protected static string? ClaimValue(Client applied, string claimName) =>
            applied
                .ProtocolMappers.FirstOrDefault(mapper =>
                    mapper.Config.TryGetValue("claim.name", out string? configured) && configured == claimName
                )
                ?.Config["claim.value"];

        protected void AssertClientIdentityPreserved()
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
        }
    }

    [TestFixture]
    public class Given_a_successful_in_place_client_update : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act() => await ActUpdateAsync();

        [Test]
        public void It_returns_success_carrying_the_original_uuid()
        {
            _result.Should().BeOfType<ClientUpdateResult.Success>();
            ((ClientUpdateResult.Success)_result).ClientUuid.Should().Be(Guid.Parse(_clientUuid));
        }

        [Test]
        public void It_never_deletes_or_recreates_the_client() => AssertClientIdentityPreserved();

        [Test]
        public void It_applies_the_requested_name_and_enabled_state()
        {
            AppliedClient().Name.Should().Be("Updated Client");
            AppliedClient().Enabled.Should().BeFalse();
        }

        [Test]
        public void It_omits_the_secret_from_the_update() => AppliedClient().Secret.Should().BeNull();

        [Test]
        public void It_upserts_the_education_organization_claim() =>
            ClaimValue(AppliedClient(), "educationOrganizationIds").Should().Be("200,300");

        [Test]
        public void It_replaces_the_data_store_claim_with_sorted_ids() =>
            ClaimValue(AppliedClient(), "dataStoreIds").Should().Be("1,2");

        [Test]
        public void It_preserves_unrelated_protocol_mappers() =>
            AppliedClient()
                .ProtocolMappers.Should()
                .Contain(mapper => mapper.Name == "Configuration service role mapper");

        [Test]
        public void It_performs_no_role_or_service_account_work()
        {
            A.CallTo(() => _keycloakClientFacade.GetRolesAsync(A<string>.Ignored)).MustNotHaveHappened();
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

        [Test]
        public void It_removes_the_stale_scope_before_assigning_the_target() =>
            _scopeCallOrder.Should().Equal($"remove:{StaleScopeId}", $"assign:{TargetScopeId}");

        [Test]
        public void It_preserves_the_realm_default_and_service_account_scopes() =>
            _scopeCallOrder
                .Should()
                .NotContain($"remove:{RealmDefaultScopeId}")
                .And.NotContain($"remove:{ServiceAccountScopeId}");
    }

    [TestFixture]
    public class Given_an_update_removing_every_data_store : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act() => await ActUpdateAsync([]);

        [Test]
        public void It_returns_success() => _result.Should().BeOfType<ClientUpdateResult.Success>();

        [Test]
        public void It_removes_the_data_store_claim() =>
            AppliedClient()
                .ProtocolMappers.Should()
                .NotContain(mapper =>
                    mapper.Config.ContainsKey("claim.name") && mapper.Config["claim.name"] == "dataStoreIds"
                );

        [Test]
        public void It_never_deletes_or_recreates_the_client() => AssertClientIdentityPreserved();
    }

    [TestFixture]
    public class Given_an_update_whose_education_organization_mapper_is_absent : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _storedClient.ProtocolMappers = [MapperWithoutClaimName()];
            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_success() => _result.Should().BeOfType<ClientUpdateResult.Success>();

        [Test]
        public void It_adds_the_education_organization_claim() =>
            ClaimValue(AppliedClient(), "educationOrganizationIds").Should().Be("200,300");
    }

    [TestFixture]
    public class Given_an_update_whose_target_scope_is_already_assigned : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetDefaultClientScopesAsync("edfi", _clientUuid))
                .Returns([
                    new ClientScope { Id = ServiceAccountScopeId, Name = "service_account" },
                    new ClientScope { Id = RealmDefaultScopeId, Name = "profile" },
                    new ClientScope { Id = TargetScopeId, Name = TargetScopeName },
                ]);

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_success() => _result.Should().BeOfType<ClientUpdateResult.Success>();

        [Test]
        public void It_changes_no_scope_assignment() => _scopeCallOrder.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_update_whose_client_update_reports_no_change : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.UpdateClientAsync("edfi", _clientUuid, A<Client>.Ignored))
                .Returns(false);

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureUnknown>();

        [Test]
        public void It_leaves_the_stored_client_intact() => AssertClientIdentityPreserved();

        [Test]
        public void It_does_not_converge_the_scopes() => _scopeCallOrder.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_update_whose_client_update_reports_not_found : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.UpdateClientAsync("edfi", _clientUuid, A<Client>.Ignored))
                .Throws(CreateFlurlHttpException(HttpStatusCode.NotFound, HttpMethod.Put));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_not_found() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureNotFound>();

        [Test]
        public void It_does_not_converge_the_scopes() => _scopeCallOrder.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_update_whose_client_update_fails_at_keycloak : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.UpdateClientAsync("edfi", _clientUuid, A<Client>.Ignored))
                .Throws(CreateFlurlHttpException(HttpStatusCode.Forbidden, HttpMethod.Put));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureIdentityProvider>();

        [Test]
        public void It_does_not_converge_the_scopes() => _scopeCallOrder.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_scope_removal_reporting_not_found_for_a_surviving_client : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _keycloakClientFacade.DeleteDefaultClientScopeAsync(
                        "edfi",
                        _clientUuid,
                        A<string>.Ignored
                    )
                )
                .Throws(CreateFlurlHttpException(HttpStatusCode.NotFound, HttpMethod.Delete));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureIdentityProvider>();

        [Test]
        public void It_confirms_the_client_once() =>
            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", _clientUuid))
                .MustHaveHappenedTwiceExactly();
    }

    [TestFixture]
    public class Given_a_scope_removal_reporting_not_found_for_a_vanished_client : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            int lookups = 0;
            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", _clientUuid))
                .ReturnsLazily(_ =>
                {
                    lookups++;
                    return lookups == 1
                        ? Task.FromResult(_storedClient)
                        : Task.FromException<Client>(CreateFlurlHttpException(HttpStatusCode.NotFound));
                });

            A.CallTo(() =>
                    _keycloakClientFacade.DeleteDefaultClientScopeAsync(
                        "edfi",
                        _clientUuid,
                        A<string>.Ignored
                    )
                )
                .Throws(CreateFlurlHttpException(HttpStatusCode.NotFound, HttpMethod.Delete));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_not_found() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureNotFound>();
    }

    [TestFixture]
    public class Given_a_scope_assignment_reporting_no_change : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _keycloakClientFacade.UpdateDefaultClientScopeAsync(
                        "edfi",
                        _clientUuid,
                        A<string>.Ignored
                    )
                )
                .Invokes(call => _scopeCallOrder.Add($"assign:{call.GetArgument<string>(2)}"))
                .Returns(false);

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureUnknown>();

        [Test]
        public void It_had_already_removed_the_stale_scope() =>
            _scopeCallOrder.Should().Equal($"remove:{StaleScopeId}", $"assign:{TargetScopeId}");

        [Test]
        public void It_leaves_the_stored_client_intact() => AssertClientIdentityPreserved();
    }

    [TestFixture]
    public class Given_an_update_whose_realm_default_scope_lookup_fails : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetRealmDefaultClientScopesAsync("edfi"))
                .Throws(CreateFlurlHttpException(HttpStatusCode.Forbidden));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureIdentityProvider>();

        [Test]
        public void It_does_not_update_the_client() => _clientUpdates.Should().BeEmpty();

        [Test]
        public void It_does_not_converge_the_scopes() => _scopeCallOrder.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_update_whose_requested_scope_is_missing : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetClientScopesAsync("edfi"))
                .Returns(Task.FromResult<IEnumerable<ClientScope>>([]));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureIdentityProvider>();

        [Test]
        public void It_does_not_update_the_client() => _clientUpdates.Should().BeEmpty();

        [Test]
        public void It_never_deletes_or_recreates_the_client() => AssertClientIdentityPreserved();
    }

    [TestFixture]
    public class Given_an_update_whose_stored_identifier_is_not_a_uuid : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _clientUuid = "not-a-uuid";
            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureUnknown>();

        [Test]
        public void It_reads_nothing_from_the_provider() =>
            A.CallTo(() => _keycloakClientFacade.GetClientAsync(A<string>.Ignored, A<string>.Ignored))
                .MustNotHaveHappened();
    }

    /// <summary>
    /// Proves the identity-preserving, retry-convergent contract: a scope-phase failure leaves the
    /// client without its claim-set scope — access is lost rather than over-granted — and an
    /// identical retry converges on exactly the intended assignments.
    /// </summary>
    [TestFixture]
    public class Given_a_scope_convergence_failure_followed_by_an_identical_retry : InPlaceUpdateTestBase
    {
        private List<ClientScope> _providerScopeState = null!;
        private ClientUpdateResult _retryResult = null!;
        private List<string> _scopesAfterFailure = null!;

        [SetUp]
        public async Task Act()
        {
            _providerScopeState =
            [
                new ClientScope { Id = ServiceAccountScopeId, Name = "service_account" },
                new ClientScope { Id = RealmDefaultScopeId, Name = "profile" },
                new ClientScope { Id = StaleScopeId, Name = StaleScopeName },
            ];

            A.CallTo(() => _keycloakClientFacade.GetDefaultClientScopesAsync("edfi", _clientUuid))
                .ReturnsLazily(_ => Task.FromResult<IEnumerable<ClientScope>>([.. _providerScopeState]));

            A.CallTo(() =>
                    _keycloakClientFacade.DeleteDefaultClientScopeAsync(
                        "edfi",
                        _clientUuid,
                        A<string>.Ignored
                    )
                )
                .ReturnsLazily(call =>
                {
                    string scopeId = call.GetArgument<string>(2)!;
                    _scopeCallOrder.Add($"remove:{scopeId}");
                    _providerScopeState.RemoveAll(assigned => assigned.Id == scopeId);
                    return Task.FromResult(true);
                });

            int assignments = 0;
            A.CallTo(() =>
                    _keycloakClientFacade.UpdateDefaultClientScopeAsync(
                        "edfi",
                        _clientUuid,
                        A<string>.Ignored
                    )
                )
                .ReturnsLazily(call =>
                {
                    string scopeId = call.GetArgument<string>(2)!;
                    _scopeCallOrder.Add($"assign:{scopeId}");
                    assignments++;
                    if (assignments == 1)
                    {
                        // The first assignment is rejected by the provider.
                        return Task.FromResult(false);
                    }

                    _providerScopeState.Add(new ClientScope { Id = scopeId, Name = TargetScopeName });
                    return Task.FromResult(true);
                });

            await ActUpdateAsync();
            _scopesAfterFailure = [.. _providerScopeState.Select(assigned => assigned.Id)];

            _retryResult = await _repository.UpdateClientAsync(
                _clientUuid,
                "Updated Client",
                TargetScopeName,
                "200,300",
                [2, 1],
                false,
                "role"
            );
        }

        [Test]
        public void It_fails_the_first_attempt() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureUnknown>();

        [Test]
        public void It_leaves_the_client_without_a_claim_set_scope_after_the_failure() =>
            _scopesAfterFailure.Should().Equal(ServiceAccountScopeId, RealmDefaultScopeId);

        [Test]
        public void It_converges_on_an_identical_retry()
        {
            _retryResult.Should().BeOfType<ClientUpdateResult.Success>();
            ((ClientUpdateResult.Success)_retryResult).ClientUuid.Should().Be(Guid.Parse(_clientUuid));
        }

        [Test]
        public void It_ends_with_exactly_the_expected_scopes() =>
            _providerScopeState
                .Select(assigned => assigned.Id)
                .Should()
                .Equal(ServiceAccountScopeId, RealmDefaultScopeId, TargetScopeId);

        [Test]
        public void It_never_deletes_or_recreates_the_client() => AssertClientIdentityPreserved();
    }
}
