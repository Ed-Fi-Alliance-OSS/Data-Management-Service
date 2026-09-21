// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.TestHelpers;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using FakeItEasy;
using FluentAssertions;
using Flurl.Http;
using Keycloak.Net.Models.Clients;
using Keycloak.Net.Models.ClientScopes;
using Keycloak.Net.Models.Roles;
using Keycloak.Net.Models.Users;
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

    /// <summary>
    /// Builds the exception Flurl raises when the request never produced a response at all, so
    /// the call carries no status. The provider's answer is unknown rather than a refusal, which
    /// is what makes the operation's outcome unconfirmed.
    /// </summary>
    protected static FlurlHttpException CreateFlurlTransportException(
        HttpMethod? method = null,
        string url = "http://localhost:8045/admin/realms/edfi/clients/x"
    ) =>
        new(
            new FlurlCall
            {
                Request = new FlurlRequest(url),
                HttpRequestMessage = new HttpRequestMessage(method ?? HttpMethod.Delete, url),
            },
            new HttpRequestException("connection reset")
        );

    protected static ClientProtocolMapper ClaimMapper(string name, string claimName, string value) =>
        new()
        {
            Name = name,
            Protocol = "openid-connect",
            ProtocolMapper = "oidc-hardcoded-claim-mapper",
            Config = new Dictionary<string, string> { { "claim.name", claimName }, { "claim.value", value } },
        };

    protected static ClientProtocolMapper MapperWithoutClaimName() =>
        new()
        {
            Name = "Configuration service role mapper",
            Protocol = "openid-connect",
            ProtocolMapper = "oidc-usermodel-realm-role-mapper",
            Config = new Dictionary<string, string> { { "multivalued", "true" } },
        };

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

    /// <summary>
    /// The scope is absent, the provider reports its creation successful, and the follow-up
    /// lookup still cannot find it: the update fails on the post-creation lookup, not on the
    /// creation result.
    /// </summary>
    [TestFixture]
    public class Given_an_update_whose_requested_scope_is_missing : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetClientScopesAsync("edfi"))
                .Returns(Task.FromResult<IEnumerable<ClientScope>>([]));
            A.CallTo(() => _keycloakClientFacade.CreateClientScopeAsync("edfi", A<ClientScope>.Ignored))
                .Returns(true);

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

    /// <summary>
    /// Audit-contract fixture (DMS-1365 D-4): a scope creation the provider reports unsuccessful
    /// fails the update immediately as a provider failure, before any mutation. The no-mutation
    /// assertions are the behavioral guarantee; the single scope lookup documents the fail-fast
    /// contract, which no longer consults the provider a second time.
    /// </summary>
    [TestFixture]
    public class Given_an_update_whose_scope_creation_is_rejected : InPlaceUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetClientScopesAsync("edfi"))
                .Returns(Task.FromResult<IEnumerable<ClientScope>>([]));
            A.CallTo(() => _keycloakClientFacade.CreateClientScopeAsync("edfi", A<ClientScope>.Ignored))
                .Returns(false);

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureIdentityProvider>();

        [Test]
        public void It_does_not_update_the_client() => _clientUpdates.Should().BeEmpty();

        [Test]
        public void It_does_not_converge_the_scopes() => _scopeCallOrder.Should().BeEmpty();

        [Test]
        public void It_never_deletes_or_recreates_the_client() => AssertClientIdentityPreserved();

        [Test]
        public void It_looks_the_scopes_up_only_once() =>
            A.CallTo(() => _keycloakClientFacade.GetClientScopesAsync("edfi")).MustHaveHappenedOnceExactly();

        [Test]
        public void It_logs_the_rejected_scope_creation() =>
            _logger.VerifyLogError("did not create the client scope");
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

    /// <summary>
    /// Shared arrangement for the Vendor namespace-claim update. The stored client carries a
    /// mapper with no <c>claim.name</c> at all, so any claim lookup that indexes the
    /// configuration blindly fails here rather than in production, alongside the
    /// education-organization and data-store claims whose survival the update must not disturb.
    /// </summary>
    public abstract class NamespaceClaimUpdateTestBase : KeycloakClientRepositoryTests
    {
        protected const string NewPrefixes = "uri://ed-fi.org,uri://new.org";

        protected string _clientUuid = null!;
        protected Client _storedClient = null!;
        protected ClientUpdateResult _result = null!;
        protected List<Client> _clientUpdates = null!;

        [SetUp]
        public void SetUpNamespaceClaimDefaults()
        {
            _clientUuid = Guid.NewGuid().ToString();
            _clientUpdates = [];

            _storedClient = new Client
            {
                ClientId = "test-client",
                Secret = "ExistingSecret123!",
                Name = "Original Client",
                Enabled = true,
                ServiceAccountsEnabled = true,
                DefaultClientScopes = ["claim-set-scope"],
                ProtocolMappers =
                [
                    MapperWithoutClaimName(),
                    ClaimMapper("Namespace Prefixes", "namespacePrefixes", "uri://ed-fi.org"),
                    ClaimMapper("Education Organization Ids", "educationOrganizationIds", "100"),
                    ClaimMapper("Data Store IDs", "dataStoreIds", "7,8"),
                ],
            };

            A.CallTo(() => _keycloakClientFacade.GetClientAsync("edfi", _clientUuid)).Returns(_storedClient);

            A.CallTo(() => _keycloakClientFacade.UpdateClientAsync("edfi", _clientUuid, A<Client>.Ignored))
                .Invokes(call => _clientUpdates.Add(call.GetArgument<Client>(2)!))
                .Returns(true);
        }

        protected async Task ActUpdateAsync(string? namespacePrefixes = null) =>
            _result = await _repository.UpdateClientNamespaceClaimAsync(
                _clientUuid,
                namespacePrefixes ?? NewPrefixes
            );

        protected Client AppliedClient() => _clientUpdates.Single();

        protected static List<ClientProtocolMapper> NamespaceClaims(Client applied) =>
            [
                .. applied.ProtocolMappers.Where(mapper =>
                    mapper.Config is not null
                    && mapper.Config.TryGetValue("claim.name", out string? claimName)
                    && claimName == "namespacePrefixes"
                ),
            ];

        protected static string? ClaimValue(Client applied, string claimName) =>
            applied
                .ProtocolMappers.FirstOrDefault(mapper =>
                    mapper.Config is not null
                    && mapper.Config.TryGetValue("claim.name", out string? configured)
                    && configured == claimName
                )
                ?.Config["claim.value"];

        /// <summary>
        /// The whole point of the change: the stored client is never destroyed, so the UUID the
        /// database holds stays valid and the client keeps its secret, service account, and realm
        /// role mappings with no reassignment.
        /// </summary>
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

        protected void AssertNoProviderMutation()
        {
            A.CallTo(() =>
                    _keycloakClientFacade.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<Client>.Ignored
                    )
                )
                .MustNotHaveHappened();
            AssertClientIdentityPreserved();
        }
    }

    [TestFixture]
    public class Given_a_namespace_claim_update_replacing_an_existing_claim : NamespaceClaimUpdateTestBase
    {
        [SetUp]
        public async Task Act() => await ActUpdateAsync();

        [Test]
        public void It_returns_success_carrying_the_stored_uuid()
        {
            _result.Should().BeOfType<ClientUpdateResult.Success>();
            ((ClientUpdateResult.Success)_result).ClientUuid.Should().Be(Guid.Parse(_clientUuid));
        }

        [Test]
        public void It_never_deletes_or_recreates_the_client() => AssertClientIdentityPreserved();

        [Test]
        public void It_updates_the_client_once_under_its_existing_uuid()
        {
            _clientUpdates.Should().HaveCount(1);
            A.CallTo(() => _keycloakClientFacade.UpdateClientAsync("edfi", _clientUuid, A<Client>.Ignored))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_applies_the_requested_namespace_prefixes() =>
            ClaimValue(AppliedClient(), "namespacePrefixes").Should().Be(NewPrefixes);

        [Test]
        public void It_leaves_exactly_one_namespace_claim() =>
            NamespaceClaims(AppliedClient()).Should().ContainSingle();

        [Test]
        public void It_preserves_the_education_organization_and_data_store_claims()
        {
            ClaimValue(AppliedClient(), "educationOrganizationIds").Should().Be("100");
            ClaimValue(AppliedClient(), "dataStoreIds").Should().Be("7,8");
        }

        [Test]
        public void It_preserves_unrelated_protocol_mappers() =>
            AppliedClient()
                .ProtocolMappers.Should()
                .Contain(mapper => mapper.Name == "Configuration service role mapper");

        [Test]
        public void It_omits_the_secret_from_the_update() => AppliedClient().Secret.Should().BeNull();

        [Test]
        public void It_preserves_the_name_and_enabled_state()
        {
            AppliedClient().Name.Should().Be("Original Client");
            AppliedClient().Enabled.Should().BeTrue();
        }

        [Test]
        public void It_performs_no_role_service_account_or_scope_work()
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
    public class Given_a_namespace_claim_update_adding_a_missing_claim : NamespaceClaimUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _storedClient.ProtocolMappers =
            [
                MapperWithoutClaimName(),
                ClaimMapper("Education Organization Ids", "educationOrganizationIds", "100"),
            ];

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_success() => _result.Should().BeOfType<ClientUpdateResult.Success>();

        [Test]
        public void It_appends_a_single_fully_configured_namespace_claim()
        {
            ClientProtocolMapper added = NamespaceClaims(AppliedClient()).Should().ContainSingle().Subject;
            added.ProtocolMapper.Should().Be("oidc-hardcoded-claim-mapper");
            added.Config["claim.value"].Should().Be(NewPrefixes);
            added.Config["access.token.claim"].Should().Be("true");
        }

        [Test]
        public void It_preserves_the_existing_mappers()
        {
            ClaimValue(AppliedClient(), "educationOrganizationIds").Should().Be("100");
            AppliedClient()
                .ProtocolMappers.Should()
                .Contain(mapper => mapper.Name == "Configuration service role mapper");
        }

        [Test]
        public void It_never_deletes_or_recreates_the_client() => AssertClientIdentityPreserved();
    }

    [TestFixture]
    public class Given_a_namespace_claim_update_whose_client_carries_duplicate_claims
        : NamespaceClaimUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _storedClient.ProtocolMappers =
            [
                ClaimMapper("Namespace Prefixes", "namespacePrefixes", "uri://first.org"),
                ClaimMapper("Duplicate Namespace Prefixes", "namespacePrefixes", "uri://second.org"),
                ClaimMapper("Education Organization Ids", "educationOrganizationIds", "100"),
            ];

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_success() => _result.Should().BeOfType<ClientUpdateResult.Success>();

        [Test]
        public void It_collapses_the_duplicates_onto_the_first_mapper()
        {
            ClientProtocolMapper survivor = NamespaceClaims(AppliedClient()).Should().ContainSingle().Subject;
            survivor.Name.Should().Be("Namespace Prefixes");
            survivor.Config["claim.value"].Should().Be(NewPrefixes);
        }

        [Test]
        public void It_preserves_the_unrelated_claim() =>
            ClaimValue(AppliedClient(), "educationOrganizationIds").Should().Be("100");
    }

    [TestFixture]
    public class Given_a_namespace_claim_update_whose_client_has_no_protocol_mappers
        : NamespaceClaimUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _storedClient.ProtocolMappers = null!;

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_success() => _result.Should().BeOfType<ClientUpdateResult.Success>();

        [Test]
        public void It_adds_the_namespace_claim() =>
            NamespaceClaims(AppliedClient()).Should().ContainSingle();
    }

    [TestFixture]
    public class Given_a_namespace_claim_update_whose_stored_client_lookup_reports_not_found
        : NamespaceClaimUpdateTestBase
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
    }

    [TestFixture]
    public class Given_a_namespace_claim_update_whose_stored_client_lookup_fails_at_keycloak
        : NamespaceClaimUpdateTestBase
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
    public class Given_a_namespace_claim_update_whose_stored_client_lookup_throws_an_unexpected_error
        : NamespaceClaimUpdateTestBase
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
    public class Given_a_namespace_claim_update_whose_stored_client_is_absent : NamespaceClaimUpdateTestBase
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
    public class Given_a_namespace_claim_update_whose_client_update_reports_not_found
        : NamespaceClaimUpdateTestBase
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
        public void It_never_deletes_or_recreates_the_client() => AssertClientIdentityPreserved();
    }

    [TestFixture]
    public class Given_a_namespace_claim_update_whose_client_update_reports_no_change
        : NamespaceClaimUpdateTestBase
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
        public void It_never_deletes_or_recreates_the_client() => AssertClientIdentityPreserved();
    }

    [TestFixture]
    public class Given_a_namespace_claim_update_whose_client_update_fails_at_keycloak
        : NamespaceClaimUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.UpdateClientAsync("edfi", _clientUuid, A<Client>.Ignored))
                .Throws(CreateFlurlHttpException(HttpStatusCode.BadGateway, HttpMethod.Put));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureIdentityProvider>();

        [Test]
        public void It_never_deletes_or_recreates_the_client() => AssertClientIdentityPreserved();
    }

    [TestFixture]
    public class Given_a_namespace_claim_update_whose_client_update_throws_an_unexpected_error
        : NamespaceClaimUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.UpdateClientAsync("edfi", _clientUuid, A<Client>.Ignored))
                .Throws(new InvalidOperationException("transport misconfigured"));

            await ActUpdateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientUpdateResult.FailureUnknown>();

        [Test]
        public void It_never_deletes_or_recreates_the_client() => AssertClientIdentityPreserved();
    }

    [TestFixture]
    public class Given_a_namespace_claim_update_whose_stored_identifier_is_not_a_uuid
        : NamespaceClaimUpdateTestBase
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
        public void It_never_reads_the_client() =>
            A.CallTo(() => _keycloakClientFacade.GetClientAsync(A<string>.Ignored, A<string>.Ignored))
                .MustNotHaveHappened();

        [Test]
        public void It_performs_no_provider_mutation() => AssertNoProviderMutation();
    }

    /// <summary>
    /// Shared arrangement for client creation, with a stateful provider fake: the set of clients
    /// the provider holds, the role mappings it has recorded, and the order of every mutating
    /// call. Defaults describe a realm where the role and the claim-set scope already exist and
    /// every provider call succeeds; fixtures override the one call under test.
    /// </summary>
    public abstract class CreateClientTestBase : KeycloakClientRepositoryTests
    {
        protected const string RoleName = "dms-client";
        protected const string RoleId = "dms-client-role-id";
        protected const string ScopeName = "claim-set-scope";
        protected const string ScopeId = "claim-set-scope-id";
        protected const string ServiceAccountUserId = "service-account-user-id";
        protected const string ClientKey = "generated-client-key";
        protected const string ClientSecret = "GeneratedSecret1234567890!Abcdefghij";

        protected ClientCreateResult _result = null!;
        protected List<string> _providerClients = null!;
        protected List<Client> _createdClients = null!;
        protected List<string> _createdUuids = null!;
        protected List<string> _deletedUuids = null!;
        protected List<(string UserId, string RoleName)> _roleMappings = null!;
        protected List<string> _callOrder = null!;

        /// <summary>
        /// Overrides the identifier the create call reports, for the case where the provider
        /// returns something the caller cannot parse as a UUID.
        /// </summary>
        protected string? _identifierToReturn;

        [SetUp]
        public void SetUpCreateDefaults()
        {
            _providerClients = [];
            _createdClients = [];
            _createdUuids = [];
            _deletedUuids = [];
            _roleMappings = [];
            _callOrder = [];
            _identifierToReturn = null;

            A.CallTo(() => _keycloakClientFacade.GetRolesAsync("edfi"))
                .Returns(Task.FromResult<IEnumerable<Role>>([new Role { Id = RoleId, Name = RoleName }]));

            A.CallTo(() => _keycloakClientFacade.CreateRoleAsync("edfi", A<Role>.Ignored))
                .Invokes(_ => _callOrder.Add("create-role"))
                .Returns(true);

            A.CallTo(() => _keycloakClientFacade.GetRoleByNameAsync("edfi", RoleName))
                .Returns(new Role { Id = RoleId, Name = RoleName });

            A.CallTo(() => _keycloakClientFacade.GetClientScopesAsync("edfi"))
                .Returns(
                    Task.FromResult<IEnumerable<ClientScope>>([
                        new ClientScope { Id = ScopeId, Name = ScopeName },
                    ])
                );

            A.CallTo(() => _keycloakClientFacade.CreateClientScopeAsync("edfi", A<ClientScope>.Ignored))
                .Invokes(_ => _callOrder.Add("create-scope"))
                .Returns(true);

            A.CallTo(() =>
                    _keycloakClientFacade.CreateClientAndRetrieveClientIdAsync("edfi", A<Client>.Ignored)
                )
                .ReturnsLazily(call =>
                {
                    string createdUuid = _identifierToReturn ?? Guid.NewGuid().ToString();
                    _providerClients.Add(createdUuid);
                    _createdUuids.Add(createdUuid);
                    _createdClients.Add(call.GetArgument<Client>(1)!);
                    _callOrder.Add("create-client");
                    return Task.FromResult<string?>(createdUuid);
                });

            A.CallTo(() => _keycloakClientFacade.GetUserForServiceAccountAsync("edfi", A<string>.Ignored))
                .Invokes(_ => _callOrder.Add("service-account"))
                .Returns(new User { Id = ServiceAccountUserId });

            A.CallTo(() =>
                    _keycloakClientFacade.AddRealmRoleMappingsToUserAsync(
                        "edfi",
                        A<string>.Ignored,
                        A<IEnumerable<Role>>.Ignored
                    )
                )
                .ReturnsLazily(call =>
                {
                    string userId = call.GetArgument<string>(1)!;
                    foreach (Role mapped in call.GetArgument<IEnumerable<Role>>(2)!)
                    {
                        _roleMappings.Add((userId, mapped.Name));
                    }
                    _callOrder.Add("role-mapping");
                    return Task.FromResult(true);
                });

            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync("edfi", A<string>.Ignored))
                .ReturnsLazily(call =>
                {
                    string requested = call.GetArgument<string>(1)!;
                    _providerClients.Remove(requested);
                    _deletedUuids.Add(requested);
                    _callOrder.Add("delete-client");
                    return Task.FromResult(true);
                });
        }

        protected async Task ActCreateAsync(string clientId = ClientKey, bool isApproved = true) =>
            _result = await _repository.CreateClientAsync(
                clientId,
                ClientSecret,
                RoleName,
                "Display Name",
                ScopeName,
                "uri://ed-fi.org",
                "255901",
                [2, 1],
                isApproved
            );

        protected void AssertNoClientCreated()
        {
            A.CallTo(() =>
                    _keycloakClientFacade.CreateClientAndRetrieveClientIdAsync(
                        A<string>.Ignored,
                        A<Client>.Ignored
                    )
                )
                .MustNotHaveHappened();
            _providerClients.Should().BeEmpty();
        }

        /// <summary>
        /// The identifier the provider reported for the one client this request created.
        /// </summary>
        protected string CreatedUuid() => _createdUuids.Should().ContainSingle().Subject;

        /// <summary>
        /// Recovery never rebuilds what it removed: the client is created once and never again,
        /// no update or secret regeneration reissues a credential, the secret the caller supplied
        /// is the only one ever sent, and nothing deletes a realm-shared role or scope.
        /// </summary>
        protected void AssertNoRecreationOrCredentialReissue()
        {
            A.CallTo(() =>
                    _keycloakClientFacade.CreateClientAndRetrieveClientIdAsync(
                        A<string>.Ignored,
                        A<Client>.Ignored
                    )
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(() =>
                    _keycloakClientFacade.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<Client>.Ignored
                    )
                )
                .MustNotHaveHappened();
            A.CallTo(() =>
                    _keycloakClientFacade.GenerateClientSecretAsync(A<string>.Ignored, A<string>.Ignored)
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

            _createdClients.Should().OnlyContain(created => created.Secret == ClientSecret);

            int lastDeletion = _callOrder.LastIndexOf("delete-client");
            if (lastDeletion >= 0)
            {
                _callOrder
                    .Skip(lastDeletion)
                    .Should()
                    .NotContain("create-client", "a deleted client is never recreated");
            }
        }

        /// <summary>
        /// No log entry may carry the client secret, in its message or in the text of an
        /// exception logged beside it.
        /// </summary>
        protected void AssertNoSecretLogged() =>
            _logger.LoggedEntryTexts().Should().OnlyContain(entry => !entry.Contains(ClientSecret));
    }

    [TestFixture]
    public class Given_a_client_creation_whose_scope_already_exists : CreateClientTestBase
    {
        [SetUp]
        public async Task Act() => await ActCreateAsync();

        [Test]
        public void It_returns_success() => _result.Should().BeOfType<ClientCreateResult.Success>();

        [Test]
        public void It_does_not_create_the_scope() =>
            A.CallTo(() =>
                    _keycloakClientFacade.CreateClientScopeAsync(A<string>.Ignored, A<ClientScope>.Ignored)
                )
                .MustNotHaveHappened();

        [Test]
        public void It_does_not_create_the_role() =>
            A.CallTo(() => _keycloakClientFacade.CreateRoleAsync(A<string>.Ignored, A<Role>.Ignored))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_client_creation_whose_role_is_created_on_demand : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetRolesAsync("edfi"))
                .Returns(Task.FromResult<IEnumerable<Role>>([]));

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_success() => _result.Should().BeOfType<ClientCreateResult.Success>();

        [Test]
        public void It_creates_the_role_before_the_client()
        {
            _callOrder.Should().Contain("create-role").And.Contain("create-client");
            _callOrder.IndexOf("create-role").Should().BeLessThan(_callOrder.IndexOf("create-client"));
        }

        [Test]
        public void It_maps_the_looked_up_role_to_the_service_account() =>
            _roleMappings.Should().Equal((ServiceAccountUserId, RoleName));
    }

    /// <summary>
    /// Fails against the implementation that discarded the role-creation result: that version
    /// went on to look the role up by name.
    /// </summary>
    [TestFixture]
    public class Given_a_client_creation_whose_role_creation_is_rejected : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetRolesAsync("edfi"))
                .Returns(Task.FromResult<IEnumerable<Role>>([]));
            A.CallTo(() => _keycloakClientFacade.CreateRoleAsync("edfi", A<Role>.Ignored)).Returns(false);

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientCreateResult.FailureIdentityProvider>();

        [Test]
        public void It_does_not_look_the_role_up() =>
            A.CallTo(() => _keycloakClientFacade.GetRoleByNameAsync(A<string>.Ignored, A<string>.Ignored))
                .MustNotHaveHappened();

        [Test]
        public void It_creates_no_client() => AssertNoClientCreated();

        [Test]
        public void It_logs_the_role_creation_phase() => _logger.VerifyLogError("role-creation");
    }

    /// <summary>
    /// Fails against the implementation that created the client before checking the role: that
    /// version returned the same failure with a client left behind in the provider.
    /// </summary>
    [TestFixture]
    public class Given_a_client_creation_whose_role_cannot_be_found_after_creation : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetRolesAsync("edfi"))
                .Returns(Task.FromResult<IEnumerable<Role>>([]));
            A.CallTo(() => _keycloakClientFacade.GetRoleByNameAsync("edfi", RoleName))
                .Returns(Task.FromResult<Role>(null!));

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientCreateResult.FailureUnknown>();

        [Test]
        public void It_creates_no_client() => AssertNoClientCreated();

        [Test]
        public void It_logs_the_role_lookup_phase() => _logger.VerifyLogError("role-lookup");
    }

    /// <summary>
    /// Fails against the implementation that discarded the scope-creation result: that version
    /// returned <see cref="ClientCreateResult.Success"/> for a client whose claim-set scope the
    /// provider had refused to create.
    /// </summary>
    [TestFixture]
    public class Given_a_client_creation_whose_scope_creation_is_rejected : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetClientScopesAsync("edfi"))
                .Returns(Task.FromResult<IEnumerable<ClientScope>>([]));
            A.CallTo(() => _keycloakClientFacade.CreateClientScopeAsync("edfi", A<ClientScope>.Ignored))
                .Returns(false);

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientCreateResult.FailureIdentityProvider>();

        [Test]
        public void It_creates_no_client() => AssertNoClientCreated();

        [Test]
        public void It_logs_the_scope_creation_phase() => _logger.VerifyLogError("scope-creation");
    }

    [TestFixture]
    public class Given_a_client_creation_that_succeeds : CreateClientTestBase
    {
        [SetUp]
        public async Task Act() => await ActCreateAsync(isApproved: false);

        [Test]
        public void It_returns_success_carrying_the_created_identifier()
        {
            _result.Should().BeOfType<ClientCreateResult.Success>();
            ((ClientCreateResult.Success)_result).ClientUuid.Should().Be(Guid.Parse(CreatedUuid()));
        }

        [Test]
        public void It_assigns_the_configured_role_to_the_service_account() =>
            _roleMappings.Should().Equal((ServiceAccountUserId, RoleName));

        [Test]
        public void It_leaves_the_client_in_place() => _providerClients.Should().Equal(CreatedUuid());

        [Test]
        public void It_performs_no_cleanup() =>
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync(A<string>.Ignored, A<string>.Ignored))
                .MustNotHaveHappened();

        [Test]
        public void It_sends_the_requested_enabled_state_and_scope()
        {
            Client created = _createdClients.Should().ContainSingle().Subject;
            created.Enabled.Should().BeFalse();
            created.DefaultClientScopes.Should().Equal(ScopeName);
            created.ServiceAccountsEnabled.Should().BeTrue();
        }

        [Test]
        public void It_sends_the_supplied_secret_once() => AssertNoRecreationOrCredentialReissue();

        [Test]
        public void It_assigns_the_role_after_creating_the_client() =>
            _callOrder.Should().Equal("create-client", "service-account", "role-mapping");
    }

    /// <summary>
    /// The shape <c>IdentityModule.RegisterClient</c> uses: a caller-chosen key and secret, the
    /// configuration-service role, the admin scope, and no namespace or education-organization
    /// claims. It provisions by the same contract, so its failures compensate the same way.
    /// </summary>
    [TestFixture]
    public class Given_a_registration_shaped_client_creation_that_succeeds : CreateClientTestBase
    {
        private const string CallerChosenKey = "CSClientApp";
        private const string ConfigServiceRole = "cms-client";
        private const string AdminScope = "edfi_admin_api/full_access";

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetRolesAsync("edfi"))
                .Returns(
                    Task.FromResult<IEnumerable<Role>>([
                        new Role { Id = "cms-client-role-id", Name = ConfigServiceRole },
                    ])
                );
            A.CallTo(() => _keycloakClientFacade.GetClientScopesAsync("edfi"))
                .Returns(
                    Task.FromResult<IEnumerable<ClientScope>>([
                        new ClientScope { Id = "admin-scope-id", Name = AdminScope },
                    ])
                );

            _result = await _repository.CreateClientAsync(
                CallerChosenKey,
                ClientSecret,
                ConfigServiceRole,
                "CSClientApp",
                AdminScope,
                string.Empty,
                string.Empty
            );
        }

        [Test]
        public void It_returns_success() => _result.Should().BeOfType<ClientCreateResult.Success>();

        [Test]
        public void It_assigns_the_configuration_service_role() =>
            _roleMappings.Should().Equal((ServiceAccountUserId, ConfigServiceRole));

        [Test]
        public void It_registers_the_caller_chosen_key() =>
            _createdClients.Should().ContainSingle().Which.ClientId.Should().Be(CallerChosenKey);
    }

    /// <summary>
    /// Shared arrangement for a provisioning failure the repository has to compensate: the client
    /// was created, and the role assignment the client needs was reported unsuccessful. Fixtures
    /// override the cleanup outcome.
    /// </summary>
    public abstract class RejectedRoleAssignmentTestBase : CreateClientTestBase
    {
        [SetUp]
        public void SetUpRejectedRoleAssignment() =>
            A.CallTo(() =>
                    _keycloakClientFacade.AddRealmRoleMappingsToUserAsync(
                        "edfi",
                        A<string>.Ignored,
                        A<IEnumerable<Role>>.Ignored
                    )
                )
                .Invokes(_ => _callOrder.Add("role-mapping"))
                .Returns(false);
    }

    /// <summary>
    /// The defect this ticket fixes: the provider refused the role assignment and the previous
    /// implementation still reported success, leaving a client that authenticates without the
    /// role its tokens need.
    /// </summary>
    [TestFixture]
    public class Given_a_client_creation_whose_role_assignment_is_rejected : RejectedRoleAssignmentTestBase
    {
        [SetUp]
        public async Task Act() => await ActCreateAsync();

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientCreateResult.FailureIdentityProvider>();

        [Test]
        public void It_deletes_the_client_it_created() => _deletedUuids.Should().Equal(CreatedUuid());

        [Test]
        public void It_leaves_no_client_at_the_provider() => _providerClients.Should().BeEmpty();

        [Test]
        public void It_logs_the_role_assignment_phase() => _logger.VerifyLogError("role-assignment");

        [Test]
        public void It_logs_the_confirmed_deletion() =>
            _logger.VerifyLog(LogLevel.Information, "Deleted provider client");

        [Test]
        public void It_reports_no_unconfirmed_cleanup() =>
            _logger.VerifyNoLog(LogLevel.Error, "Could not confirm deletion");

        [Test]
        public void It_deletes_after_attempting_the_assignment() =>
            _callOrder.Should().Equal("create-client", "service-account", "role-mapping", "delete-client");

        [Test]
        public void It_never_recreates_the_client() => AssertNoRecreationOrCredentialReissue();

        [Test]
        public void It_logs_no_secret() => AssertNoSecretLogged();
    }

    [TestFixture]
    public class Given_a_client_creation_whose_role_assignment_throws_at_keycloak : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _keycloakClientFacade.AddRealmRoleMappingsToUserAsync(
                        "edfi",
                        A<string>.Ignored,
                        A<IEnumerable<Role>>.Ignored
                    )
                )
                .Throws(CreateFlurlHttpException(HttpStatusCode.InternalServerError));

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientCreateResult.FailureIdentityProvider>();

        [Test]
        public void It_deletes_the_client_it_created() => _deletedUuids.Should().Equal(CreatedUuid());

        [Test]
        public void It_logs_the_role_assignment_phase() => _logger.VerifyLogError("role-assignment");

        [Test]
        public void It_never_recreates_the_client() => AssertNoRecreationOrCredentialReissue();

        [Test]
        public void It_logs_no_secret() => AssertNoSecretLogged();
    }

    /// <summary>
    /// The assignment reached Keycloak and then the response was lost. The role state is
    /// unconfirmed rather than known absent, and deleting the client resolves it either way.
    /// </summary>
    [TestFixture]
    public class Given_a_client_creation_whose_role_assignment_takes_effect_then_throws : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _keycloakClientFacade.AddRealmRoleMappingsToUserAsync(
                        "edfi",
                        A<string>.Ignored,
                        A<IEnumerable<Role>>.Ignored
                    )
                )
                .Invokes(call =>
                {
                    string userId = call.GetArgument<string>(1)!;
                    foreach (Role mapped in call.GetArgument<IEnumerable<Role>>(2)!)
                    {
                        _roleMappings.Add((userId, mapped.Name));
                    }
                    _callOrder.Add("role-mapping");
                })
                .Throws(CreateFlurlTransportException(HttpMethod.Post));

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientCreateResult.FailureIdentityProvider>();

        [Test]
        public void It_had_already_recorded_the_assignment() =>
            _roleMappings.Should().Equal((ServiceAccountUserId, RoleName));

        [Test]
        public void It_deletes_the_client_it_created() => _deletedUuids.Should().Equal(CreatedUuid());

        [Test]
        public void It_leaves_no_client_at_the_provider() => _providerClients.Should().BeEmpty();

        [Test]
        public void It_logs_the_role_assignment_phase() => _logger.VerifyLogError("role-assignment");

        [Test]
        public void It_never_recreates_the_client() => AssertNoRecreationOrCredentialReissue();

        [Test]
        public void It_logs_no_secret() => AssertNoSecretLogged();
    }

    [TestFixture]
    public class Given_a_client_creation_whose_role_assignment_throws_unexpectedly : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _keycloakClientFacade.AddRealmRoleMappingsToUserAsync(
                        "edfi",
                        A<string>.Ignored,
                        A<IEnumerable<Role>>.Ignored
                    )
                )
                .Throws(new InvalidOperationException("unexpected"));

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientCreateResult.FailureUnknown>();

        [Test]
        public void It_deletes_the_client_it_created() => _deletedUuids.Should().Equal(CreatedUuid());

        [Test]
        public void It_logs_the_role_assignment_phase() => _logger.VerifyLogError("role-assignment");

        [Test]
        public void It_logs_no_secret() => AssertNoSecretLogged();
    }

    [TestFixture]
    public class Given_a_client_creation_whose_service_account_lookup_fails_at_keycloak : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetUserForServiceAccountAsync("edfi", A<string>.Ignored))
                .Throws(CreateFlurlHttpException(HttpStatusCode.Forbidden));

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientCreateResult.FailureIdentityProvider>();

        [Test]
        public void It_deletes_the_client_it_created() => _deletedUuids.Should().Equal(CreatedUuid());

        [Test]
        public void It_assigns_no_role() => _roleMappings.Should().BeEmpty();

        [Test]
        public void It_logs_the_service_account_lookup_phase() =>
            _logger.VerifyLogError("service-account-lookup");

        [Test]
        public void It_logs_no_secret() => AssertNoSecretLogged();
    }

    [TestFixture]
    public class Given_a_client_creation_whose_service_account_has_no_identifier : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetUserForServiceAccountAsync("edfi", A<string>.Ignored))
                .Returns(new User { Id = string.Empty });

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientCreateResult.FailureUnknown>();

        [Test]
        public void It_deletes_the_client_it_created() => _deletedUuids.Should().Equal(CreatedUuid());

        [Test]
        public void It_assigns_no_role() => _roleMappings.Should().BeEmpty();

        [Test]
        public void It_logs_the_service_account_lookup_phase() =>
            _logger.VerifyLogError("service-account-lookup");
    }

    /// <summary>
    /// The identifier the provider returned cannot be parsed, so it can never be stored — but it
    /// can still be deleted, and the raw value is what the deletion has to use.
    /// </summary>
    [TestFixture]
    public class Given_a_client_creation_whose_created_identifier_is_not_a_uuid : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _identifierToReturn = "not-a-uuid";

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientCreateResult.FailureUnknown>();

        [Test]
        public void It_deletes_the_identifier_the_provider_returned() =>
            _deletedUuids.Should().Equal("not-a-uuid");

        [Test]
        public void It_never_looks_up_the_service_account() =>
            A.CallTo(() =>
                    _keycloakClientFacade.GetUserForServiceAccountAsync(A<string>.Ignored, A<string>.Ignored)
                )
                .MustNotHaveHappened();

        [Test]
        public void It_logs_the_identifier_parse_phase() => _logger.VerifyLogError("client-identifier-parse");
    }

    [TestFixture]
    public class Given_a_rejected_role_assignment_whose_cleanup_finds_the_client_already_absent
        : RejectedRoleAssignmentTestBase
    {
        [SetUp]
        public async Task Act()
        {
            // The client is already gone when the deletion runs, which is what the provider's 404
            // reports. Cleanup is idempotent, so the request still ends in the state it wanted.
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync("edfi", A<string>.Ignored))
                .Invokes(call =>
                {
                    string requested = call.GetArgument<string>(1)!;
                    _providerClients.Remove(requested);
                    _deletedUuids.Add(requested);
                    _callOrder.Add("delete-client");
                })
                .Throws(CreateFlurlHttpException(HttpStatusCode.NotFound, HttpMethod.Delete));

            await ActCreateAsync();
        }

        [Test]
        public void It_keeps_the_provisioning_classification() =>
            _result.Should().BeOfType<ClientCreateResult.FailureIdentityProvider>();

        [Test]
        public void It_targets_the_client_it_created() => _deletedUuids.Should().Equal(CreatedUuid());

        [Test]
        public void It_leaves_no_client_at_the_provider() => _providerClients.Should().BeEmpty();

        [Test]
        public void It_logs_the_absent_client_as_a_warning() =>
            _logger.VerifyLog(LogLevel.Warning, "was already absent during cleanup");

        [Test]
        public void It_reports_no_unconfirmed_cleanup() =>
            _logger.VerifyNoLog(LogLevel.Error, "Could not confirm deletion");

        [Test]
        public void It_never_recreates_the_client() => AssertNoRecreationOrCredentialReissue();

        [Test]
        public void It_logs_no_secret() => AssertNoSecretLogged();
    }

    [TestFixture]
    public class Given_a_rejected_role_assignment_whose_cleanup_fails_at_keycloak
        : RejectedRoleAssignmentTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync("edfi", A<string>.Ignored))
                .Throws(CreateFlurlHttpException(HttpStatusCode.Forbidden, HttpMethod.Delete));

            await ActCreateAsync();
        }

        [Test]
        public void It_keeps_the_provider_classification() =>
            _result.Should().BeOfType<ClientCreateResult.FailureIdentityProvider>();

        [Test]
        public void It_leaves_the_client_at_the_provider() => _providerClients.Should().Equal(CreatedUuid());

        [Test]
        public void It_logs_the_unconfirmed_cleanup_with_both_identifiers()
        {
            _logger.VerifyLogError("Could not confirm deletion of provider client");
            _logger
                .LoggedMessages()
                .Should()
                .Contain(message =>
                    message.Contains("Could not confirm deletion")
                    && message.Contains(CreatedUuid())
                    && message.Contains(ClientKey)
                    && message.Contains(nameof(ClientDeleteResult.FailureIdentityProvider))
                );
        }

        [Test]
        public void It_never_recreates_the_client() => AssertNoRecreationOrCredentialReissue();

        [Test]
        public void It_logs_no_secret() => AssertNoSecretLogged();
    }

    /// <summary>
    /// The deletion request never produced a response, so the client's removal is unconfirmed
    /// rather than known to have failed. The request stays provider attributable.
    /// </summary>
    [TestFixture]
    public class Given_a_rejected_role_assignment_whose_cleanup_is_unreachable
        : RejectedRoleAssignmentTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync("edfi", A<string>.Ignored))
                .Throws(CreateFlurlTransportException());

            await ActCreateAsync();
        }

        [Test]
        public void It_keeps_the_provider_classification() =>
            _result.Should().BeOfType<ClientCreateResult.FailureIdentityProvider>();

        [Test]
        public void It_logs_the_unconfirmed_cleanup() =>
            _logger.VerifyLogError("Could not confirm deletion of provider client");

        [Test]
        public void It_logs_no_secret() => AssertNoSecretLogged();
    }

    /// <summary>
    /// The provider reported the deletion unsuccessful without saying why, so the request is no
    /// longer purely provider attributable and its outcome becomes an unknown failure.
    /// </summary>
    [TestFixture]
    public class Given_a_rejected_role_assignment_whose_cleanup_reports_no_change
        : RejectedRoleAssignmentTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync("edfi", A<string>.Ignored))
                .Invokes(_ => _callOrder.Add("delete-client"))
                .Returns(false);

            await ActCreateAsync();
        }

        [Test]
        public void It_escalates_to_failure_unknown() =>
            _result.Should().BeOfType<ClientCreateResult.FailureUnknown>();

        [Test]
        public void It_leaves_the_client_at_the_provider() => _providerClients.Should().Equal(CreatedUuid());

        [Test]
        public void It_logs_the_unconfirmed_cleanup_outcome()
        {
            _logger.VerifyLogError("Could not confirm deletion of provider client");
            _logger
                .LoggedMessages()
                .Should()
                .Contain(message =>
                    message.Contains("Could not confirm deletion")
                    && message.Contains(nameof(ClientDeleteResult.FailureUnknown))
                );
        }

        [Test]
        public void It_never_recreates_the_client() => AssertNoRecreationOrCredentialReissue();
    }

    [TestFixture]
    public class Given_a_rejected_role_assignment_whose_cleanup_throws_unexpectedly
        : RejectedRoleAssignmentTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync("edfi", A<string>.Ignored))
                .Throws(new InvalidOperationException("unexpected"));

            await ActCreateAsync();
        }

        [Test]
        public void It_escalates_to_failure_unknown() =>
            _result.Should().BeOfType<ClientCreateResult.FailureUnknown>();

        [Test]
        public void It_leaves_the_client_at_the_provider() => _providerClients.Should().Equal(CreatedUuid());

        [Test]
        public void It_logs_the_exception_with_the_unconfirmed_cleanup()
        {
            _logger.VerifyLogError("Could not confirm deletion of provider client");
            A.CallTo(_logger)
                .Where(call =>
                    call.Method.Name == "Log" && call.Arguments.Get<Exception>(3) is InvalidOperationException
                )
                .MustHaveHappened();
        }

        [Test]
        public void It_logs_no_secret() => AssertNoSecretLogged();
    }

    /// <summary>
    /// The deletion took effect and the response was then lost. The client is gone, but this
    /// request cannot prove it, so the outcome is reported as unknown and the log says
    /// unconfirmed rather than claiming the client remains.
    /// </summary>
    [TestFixture]
    public class Given_a_rejected_role_assignment_whose_cleanup_takes_effect_then_throws
        : RejectedRoleAssignmentTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync("edfi", A<string>.Ignored))
                .Invokes(call =>
                {
                    string requested = call.GetArgument<string>(1)!;
                    _providerClients.Remove(requested);
                    _deletedUuids.Add(requested);
                    _callOrder.Add("delete-client");
                })
                .Throws(new InvalidOperationException("unexpected"));

            await ActCreateAsync();
        }

        [Test]
        public void It_escalates_to_failure_unknown() =>
            _result.Should().BeOfType<ClientCreateResult.FailureUnknown>();

        [Test]
        public void It_had_already_removed_the_client() => _providerClients.Should().BeEmpty();

        [Test]
        public void It_reports_the_cleanup_as_unconfirmed_rather_than_remaining() =>
            _logger
                .LoggedMessages()
                .Should()
                .Contain(message => message.Contains("Could not confirm deletion of provider client"));

        [Test]
        public void It_never_recreates_the_client() => AssertNoRecreationOrCredentialReissue();

        [Test]
        public void It_logs_no_secret() => AssertNoSecretLogged();
    }

    /// <summary>
    /// An unknown provisioning failure combined with a provider-attributable cleanup failure
    /// stays an unknown failure: the request was never purely the provider's fault.
    /// </summary>
    [TestFixture]
    public class Given_a_failed_provisioning_whose_base_failure_is_unknown_and_cleanup_fails_at_keycloak
        : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            _identifierToReturn = "not-a-uuid";
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync("edfi", A<string>.Ignored))
                .Throws(CreateFlurlHttpException(HttpStatusCode.Forbidden, HttpMethod.Delete));

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientCreateResult.FailureUnknown>();

        [Test]
        public void It_logs_both_the_parse_phase_and_the_unconfirmed_cleanup()
        {
            _logger.VerifyLogError("client-identifier-parse");
            _logger.VerifyLogError("Could not confirm deletion of provider client");
        }
    }

    /// <summary>
    /// A preflight failure states that creation was never attempted, which is what keeps an
    /// operator from hunting for a client this request could not have created.
    /// </summary>
    [TestFixture]
    public class Given_a_client_creation_whose_preflight_call_throws_at_keycloak : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _keycloakClientFacade.GetRolesAsync("edfi"))
                .Throws(CreateFlurlHttpException(HttpStatusCode.InternalServerError));

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientCreateResult.FailureIdentityProvider>();

        [Test]
        public void It_creates_no_client() => AssertNoClientCreated();

        [Test]
        public void It_attempts_no_cleanup() =>
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync(A<string>.Ignored, A<string>.Ignored))
                .MustNotHaveHappened();

        [Test]
        public void It_reports_that_creation_was_not_attempted()
        {
            _logger.VerifyLogError("creation not attempted");
            _logger.VerifyNoLog(LogLevel.Error, "creation outcome unconfirmed");
        }

        [Test]
        public void It_logs_the_client_identifier() =>
            _logger.LoggedMessages().Should().Contain(message => message.Contains(ClientKey));
    }

    [TestFixture]
    public class Given_a_client_creation_whose_create_call_throws_without_a_status : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _keycloakClientFacade.CreateClientAndRetrieveClientIdAsync("edfi", A<Client>.Ignored)
                )
                .Throws(CreateFlurlTransportException(HttpMethod.Post));

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_identity_provider() =>
            _result.Should().BeOfType<ClientCreateResult.FailureIdentityProvider>();

        [Test]
        public void It_attempts_no_cleanup() =>
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync(A<string>.Ignored, A<string>.Ignored))
                .MustNotHaveHappened();

        [Test]
        public void It_reports_the_creation_outcome_as_unconfirmed()
        {
            _logger.VerifyLogError("creation outcome unconfirmed");
            _logger.VerifyNoLog(LogLevel.Error, "creation not attempted");
        }

        [Test]
        public void It_logs_no_secret() => AssertNoSecretLogged();
    }

    [TestFixture]
    public class Given_a_client_creation_whose_create_call_throws_unexpectedly : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _keycloakClientFacade.CreateClientAndRetrieveClientIdAsync("edfi", A<Client>.Ignored)
                )
                .Throws(new NullReferenceException("no location header"));

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientCreateResult.FailureUnknown>();

        [Test]
        public void It_attempts_no_cleanup() =>
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync(A<string>.Ignored, A<string>.Ignored))
                .MustNotHaveHappened();

        [Test]
        public void It_reports_the_creation_outcome_as_unconfirmed() =>
            _logger.VerifyLogError("creation outcome unconfirmed");
    }

    [TestFixture]
    public class Given_a_client_creation_whose_create_call_returns_no_identifier : CreateClientTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() =>
                    _keycloakClientFacade.CreateClientAndRetrieveClientIdAsync("edfi", A<Client>.Ignored)
                )
                .Returns(Task.FromResult<string?>(string.Empty));

            await ActCreateAsync();
        }

        [Test]
        public void It_returns_failure_unknown() =>
            _result.Should().BeOfType<ClientCreateResult.FailureUnknown>();

        [Test]
        public void It_attempts_no_cleanup() =>
            A.CallTo(() => _keycloakClientFacade.DeleteClientAsync(A<string>.Ignored, A<string>.Ignored))
                .MustNotHaveHappened();

        [Test]
        public void It_reports_the_creation_outcome_as_unconfirmed() =>
            _logger.VerifyLogError("creation outcome unconfirmed");
    }

    /// <summary>
    /// Client-derived text reaches the failure logs, so it passes through the sanitizer first:
    /// line breaks and markup characters cannot be used to forge or break log entries.
    /// </summary>
    [TestFixture]
    public class Given_a_client_creation_whose_client_id_needs_sanitizing : RejectedRoleAssignmentTestBase
    {
        private const string RawClientId = "key\r\n<b>{x}</b>";
        private const string SanitizedClientId = "keybx/b";

        [SetUp]
        public async Task Act() => await ActCreateAsync(RawClientId);

        [Test]
        public void It_logs_the_sanitized_client_identifier() =>
            _logger
                .LoggedMessages()
                .Should()
                .Contain(message =>
                    message.Contains("role-assignment") && message.Contains(SanitizedClientId)
                );

        [Test]
        public void It_logs_none_of_the_raw_characters() =>
            _logger
                .LoggedMessages()
                .Should()
                .OnlyContain(message =>
                    !message.Contains('\r')
                    && !message.Contains('\n')
                    && !message.Contains('<')
                    && !message.Contains('>')
                    && !message.Contains('{')
                    && !message.Contains('}')
                );

        [Test]
        public void It_logs_no_secret() => AssertNoSecretLogged();
    }
}
