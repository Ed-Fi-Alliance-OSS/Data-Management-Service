// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// PUT /v3/applications/{id} through the HTTP pipeline against the real database (DMS-1642), with a
/// fake identity provider that records each update and can fail chosen clients. The Application
/// holds 255901, 255902 and 255903 and three clients: an approved inheriting client with one data
/// store, an unapproved client narrowed to {255902}, and an approved client narrowed to
/// {255901, 255903} with both data stores.
/// </summary>
public abstract class ApplicationUpdateSyncApiTests : DatabaseTestBase
{
    protected const long A1 = 255901;
    protected const long A2 = 255902;
    protected const long A3 = 255903;
    protected const long A4 = 255904;
    protected const string ClaimSet = "SyncClaimSet";

    protected sealed record ProviderUpdate(
        string ClientUuid,
        string DisplayName,
        string ClaimSetName,
        string EducationOrganizationIds,
        int[] DataStoreIds,
        bool IsApproved
    );

    protected sealed record Client(int Id, string ClientUuid, string Name);

    private WebApplicationFactory<Program> _factory = null!;

    protected List<ProviderUpdate> ProviderUpdates { get; } = [];
    protected List<string> ProviderResets { get; } = [];

    /// <summary>The provider fails the update of these client UUIDs.</summary>
    protected Dictionary<string, Func<ClientUpdateResult>> FailingClients { get; } = [];

    protected int VendorId { get; private set; }
    protected int ApplicationId { get; private set; }
    protected string ApplicationName { get; private set; } = "";
    protected int FirstDataStore { get; private set; }
    protected int SecondDataStore { get; private set; }
    protected Client Inheriting { get; private set; } = null!;
    protected Client Unapproved { get; private set; } = null!;
    protected Client Narrowed { get; private set; } = null!;

    [SetUp]
    public async Task StartHost()
    {
        ProviderUpdates.Clear();
        ProviderResets.Clear();
        FailingClients.Clear();

        IIdentityProviderRepository identityProvider = A.Fake<IIdentityProviderRepository>();
        A.CallTo(() =>
                identityProvider.CreateClientAsync(
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<int[]?>.Ignored,
                    A<bool>.Ignored
                )
            )
            .ReturnsLazily(() =>
                Task.FromResult<ClientCreateResult>(new ClientCreateResult.Success(Guid.NewGuid()))
            );
        A.CallTo(() => identityProvider.ResetCredentialsAsync(A<string>.Ignored))
            .ReturnsLazily(call =>
            {
                ProviderResets.Add(call.GetArgument<string>(0)!);
                return Task.FromResult<ClientResetResult>(new ClientResetResult.Success("rotated-secret"));
            });
        A.CallTo(() =>
                identityProvider.UpdateClientAsync(
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<int[]?>.Ignored,
                    A<bool>.Ignored,
                    A<string>.Ignored
                )
            )
            .ReturnsLazily(call =>
            {
                string clientUuid = call.GetArgument<string>(0)!;
                ProviderUpdates.Add(
                    new(
                        clientUuid,
                        call.GetArgument<string>(1)!,
                        call.GetArgument<string>(2)!,
                        call.GetArgument<string>(3)!,
                        call.GetArgument<int[]?>(4) ?? [],
                        call.GetArgument<bool>(5)
                    )
                );
                return Task.FromResult(
                    FailingClients.TryGetValue(clientUuid, out var failure)
                        ? failure()
                        : new ClientUpdateResult.Success(Guid.Parse(clientUuid))
                );
            });

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("AppSettings:Datastore", "mssql");
            builder.UseSetting("AppSettings:MultiTenancy", "false");
            builder.UseSetting(
                "DatabaseSettings:DatabaseConnection",
                MssqlTestConfiguration.DatabaseConnectionString
            );
            builder.ConfigureServices(services =>
            {
                services.AddTestAuthentication();
                services.AddTransient(_ => identityProvider);
            });
        });

        VendorId = await CreateAsync(
            "/v3/vendors/",
            $$"""
            {
              "company": "Sync Vendor {{Guid.NewGuid():N}}",
              "contactName": "Test Contact",
              "contactEmailAddress": "test@test.test",
              "namespacePrefixes": "uri://sync"
            }
            """
        );
        ApplicationName = $"Sync Application {Guid.NewGuid():N}";
        ApplicationId = await CreateApplicationAsync(ApplicationName, [A1, A2, A3]);
        FirstDataStore = await CreateDataStoreAsync();
        SecondDataStore = await CreateDataStoreAsync();
        Inheriting = await CreateClientAsync(true, "", [FirstDataStore]);
        Unapproved = await CreateClientAsync(false, List(A2), []);
        Narrowed = await CreateClientAsync(true, List(A1, A3), [FirstDataStore, SecondDataStore]);
    }

    [TearDown]
    public async Task StopHost()
    {
        // Setup can fail before the host is assigned, so teardown disposes only a started host
        // and clears the field for the next test.
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null!;
        }
    }

    protected async Task<(HttpStatusCode Status, JsonNode Body)> SendAsync(
        HttpMethod method,
        string path,
        string? json = null
    )
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        string content = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, content.Length == 0 ? new JsonObject() : JsonNode.Parse(content)!);
    }

    /// <summary>
    /// Posts a resource and returns its id, read from the body when the endpoint returns one and
    /// otherwise from the Location header (vendor creation returns no body).
    /// </summary>
    private async Task<int> CreateAsync(string path, string json)
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        using HttpResponseMessage response = await client.PostAsync(
            path,
            new StringContent(json, Encoding.UTF8, "application/json")
        );
        string content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, content);
        return content.Length > 0
            ? JsonNode.Parse(content)!["id"]!.GetValue<int>()
            : int.Parse(response.Headers.Location!.Segments[^1]);
    }

    protected Task<int> CreateApplicationAsync(string name, long[] educationOrganizationIds) =>
        CreateAsync(
            "/v3/applications/",
            $$"""
            {
              "applicationName": "{{name}}",
              "vendorId": {{VendorId}},
              "claimSetName": "{{ClaimSet}}",
              "educationOrganizationIds": [{{string.Join(", ", educationOrganizationIds)}}],
              "createDefaultCredentials": false
            }
            """
        );

    private Task<int> CreateDataStoreAsync() =>
        CreateAsync(
            "/v3/dataStores/",
            $$"""
            {
              "dataStoreType": "Production",
              "name": "Sync-{{Guid.NewGuid():N}}",
              "connectionString": "Server=db;Database=edfi;User Id=edfi;Password=secret"
            }
            """
        );

    protected async Task<Client> CreateClientAsync(
        bool isApproved,
        string scopeJson,
        int[] dataStoreIds,
        int? applicationId = null
    )
    {
        string name = $"Client {Guid.NewGuid():N}";
        int id = await CreateAsync(
            "/v3/apiClients/",
            $$"""
            {
              "applicationId": {{applicationId ?? ApplicationId}},
              "name": "{{name}}",
              "isApproved": {{(isApproved ? "true" : "false")}},
              "dataStoreIds": [{{string.Join(", ", dataStoreIds)}}]{{scopeJson}}
            }
            """
        );
        return new(id, await ClientUuidAsync(id), name);
    }

    protected static string List(params long[] ids) =>
        $$""", "educationOrganizationIds": [{{string.Join(", ", ids)}}]""";

    protected Task<(HttpStatusCode Status, JsonNode Body)> PutApplicationAsync(
        long[] educationOrganizationIds,
        string? name = null,
        string claimSetName = ClaimSet,
        int? applicationId = null,
        int[]? profileIds = null
    ) =>
        SendAsync(
            HttpMethod.Put,
            $"/v3/applications/{applicationId ?? ApplicationId}",
            $$"""
            {
              "id": {{applicationId ?? ApplicationId}},
              "applicationName": "{{name ?? ApplicationName}}",
              "vendorId": {{VendorId}},
              "claimSetName": "{{claimSetName}}",
              "educationOrganizationIds": [{{string.Join(", ", educationOrganizationIds)}}],
              "profileIds": [{{string.Join(", ", profileIds ?? [])}}]
            }
            """
        );

    protected void FailAtProvider(Client client) =>
        FailingClients[client.ClientUuid] = () =>
            new ClientUpdateResult.FailureIdentityProvider(new IdentityProviderError("provider unavailable"));

    private static async Task<string> ClientUuidAsync(int apiClientId)
    {
        await using var connection = await OpenConnectionAsync();
        return (
            await connection.ExecuteScalarAsync<Guid>(
                "SELECT ClientUuid FROM dmscs.ApiClient WHERE Id = @Id;",
                new { Id = apiClientId }
            )
        ).ToString();
    }

    /// <summary>The ids of every client awaiting an identity provider update.</summary>
    protected static async Task<int[]> PendingClientIdsAsync()
    {
        await using var connection = await OpenConnectionAsync();
        return
        [
            .. await connection.QueryAsync<int>(
                """
                SELECT Id FROM dmscs.ApiClient
                WHERE IdentityProviderSyncPendingSince IS NOT NULL ORDER BY Id;
                """
            ),
        ];
    }

    /// <summary>Every client awaiting an identity provider update, with its marker time.</summary>
    protected static async Task<Dictionary<int, DateTime>> PendingMarkersAsync()
    {
        await using var connection = await OpenConnectionAsync();
        return (
            await connection.QueryAsync<(int Id, DateTime PendingSince)>(
                """
                SELECT Id, IdentityProviderSyncPendingSince FROM dmscs.ApiClient
                WHERE IdentityProviderSyncPendingSince IS NOT NULL;
                """
            )
        ).ToDictionary(row => row.Id, row => row.PendingSince);
    }

    /// <summary>PUTs a client, by default exactly as it was created.</summary>
    protected Task<(HttpStatusCode Status, JsonNode Body)> PutClientAsync(
        Client client,
        bool isApproved,
        int[] dataStoreIds,
        string scopeJson = "",
        int? applicationId = null
    ) =>
        SendAsync(
            HttpMethod.Put,
            $"/v3/apiClients/{client.Id}",
            $$"""
            {
              "id": {{client.Id}},
              "applicationId": {{applicationId ?? ApplicationId}},
              "name": "{{client.Name}}",
              "isApproved": {{(isApproved ? "true" : "false")}},
              "dataStoreIds": [{{string.Join(", ", dataStoreIds)}}]{{scopeJson}}
            }
            """
        );

    protected async Task<string> ClientEducationOrganizationIdsAsync(Client client)
    {
        var (status, apiClient) = await SendAsync(HttpMethod.Get, $"/v3/apiClients/{client.Id}");
        status.Should().Be(HttpStatusCode.OK);
        return string.Join(
            ",",
            apiClient["educationOrganizationIds"]!.AsArray().Select(id => id!.GetValue<long>()).Order()
        );
    }

    protected async Task<string> ApplicationEducationOrganizationIdsAsync()
    {
        var (status, application) = await SendAsync(HttpMethod.Get, $"/v3/applications/{ApplicationId}");
        status.Should().Be(HttpStatusCode.OK);
        return string.Join(
            ",",
            application["educationOrganizationIds"]!.AsArray().Select(id => id!.GetValue<long>()).Order()
        );
    }

    protected async Task<string> StoredApplicationNameAsync()
    {
        var (status, application) = await SendAsync(HttpMethod.Get, $"/v3/applications/{ApplicationId}");
        status.Should().Be(HttpStatusCode.OK);
        return application["applicationName"]!.GetValue<string>();
    }

    protected string[] UpdatedClients() => [.. ProviderUpdates.Select(update => update.ClientUuid)];

    protected static void ShouldBePartialSync(
        (HttpStatusCode Status, JsonNode Body) response,
        params int[] ids
    )
    {
        response.Status.Should().Be(HttpStatusCode.BadGateway, response.Body.ToJsonString());
        response.Body["type"]!
            .GetValue<string>()
            .Should()
            .Be("urn:ed-fi:management-api:bad-gateway:partial-sync");
        response.Body["apiClientIds"]!.AsArray().Select(id => id!.GetValue<int>()).Should().Equal(ids);
        response.Body["detail"]!.GetValue<string>().Should().StartWith("The Application change is saved");
    }

    [TestFixture]
    public class Given_an_application_rename_whose_unapproved_client_fails_at_the_provider
        : ApplicationUpdateSyncApiTests
    {
        private (HttpStatusCode Status, JsonNode Body) _response;
        private int[] _pendingAfterFailure = [];
        private ProviderUpdate[] _firstAttempt = [];
        private (HttpStatusCode Status, JsonNode Body) _retry;
        private string[] _retried = [];

        [SetUp]
        public async Task Setup()
        {
            FailAtProvider(Unapproved);
            _response = await PutApplicationAsync([A1, A2, A3], "Renamed Sync Application", "OtherClaimSet");
            _pendingAfterFailure = await PendingClientIdsAsync();
            _firstAttempt = [.. ProviderUpdates];

            FailingClients.Clear();
            ProviderUpdates.Clear();
            _retry = await PutApplicationAsync([A1, A2, A3], "Renamed Sync Application", "OtherClaimSet");
            _retried = UpdatedClients();
        }

        [Test]
        public void It_returns_the_partial_sync_contract_for_the_failed_client() =>
            ShouldBePartialSync(_response, Unapproved.Id);

        [Test]
        public void It_sends_every_client_the_new_name_and_claim_set_with_its_own_scope_data_stores_and_approval() =>
            _firstAttempt
                .Should()
                .BeEquivalentTo(
                    [
                        new ProviderUpdate(
                            Inheriting.ClientUuid,
                            "Renamed Sync Application",
                            "OtherClaimSet",
                            "255901,255902,255903",
                            [FirstDataStore],
                            true
                        ),
                        new ProviderUpdate(
                            Unapproved.ClientUuid,
                            "Renamed Sync Application",
                            "OtherClaimSet",
                            "255902",
                            [],
                            false
                        ),
                        new ProviderUpdate(
                            Narrowed.ClientUuid,
                            "Renamed Sync Application",
                            "OtherClaimSet",
                            "255901,255903",
                            [FirstDataStore, SecondDataStore],
                            true
                        ),
                    ],
                    options => options.WithStrictOrdering()
                );

        [Test]
        public void It_leaves_exactly_the_failed_client_pending() =>
            _pendingAfterFailure.Should().Equal(Unapproved.Id);

        [Test]
        public async Task It_keeps_the_committed_Application_change() =>
            (await StoredApplicationNameAsync()).Should().Be("Renamed Sync Application");

        [Test]
        public void It_completes_the_identical_retry() => _retry.Status.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_retries_only_the_pending_client() => _retried.Should().Equal(Unapproved.ClientUuid);

        [Test]
        public async Task It_leaves_no_client_pending_after_the_retry() =>
            (await PendingClientIdsAsync()).Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_later_different_application_update_while_a_client_is_still_pending
        : ApplicationUpdateSyncApiTests
    {
        private (HttpStatusCode Status, JsonNode Body) _addition;

        [SetUp]
        public async Task Setup()
        {
            FailAtProvider(Narrowed);
            ShouldBePartialSync(
                await PutApplicationAsync([A1, A2, A3], "Renamed Sync Application"),
                Narrowed.Id
            );

            ProviderUpdates.Clear();
            FailingClients.Clear();
            _addition = await PutApplicationAsync([A1, A2, A3, A4], "Renamed Sync Application");
        }

        [Test]
        public void It_completes_the_update() => _addition.Status.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_updates_the_inheriting_client_it_affects_and_finishes_the_pending_client() =>
            ProviderUpdates
                .Select(update => (update.ClientUuid, update.EducationOrganizationIds))
                .Should()
                .Equal(
                    (Inheriting.ClientUuid, "255901,255902,255903,255904"),
                    (Narrowed.ClientUuid, "255901,255903")
                );

        [Test]
        public async Task It_leaves_no_client_pending() => (await PendingClientIdsAsync()).Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_application_update_whose_first_provider_update_throws
        : ApplicationUpdateSyncApiTests
    {
        private (HttpStatusCode Status, JsonNode Body) _response;

        [SetUp]
        public async Task Setup()
        {
            FailingClients[Inheriting.ClientUuid] = () => throw new HttpRequestException("connection reset");
            _response = await PutApplicationAsync([A1, A3]);
        }

        [Test]
        public void It_reports_the_client_whose_update_threw() =>
            ShouldBePartialSync(_response, Inheriting.Id);

        [Test]
        public void It_still_updates_the_later_client_that_held_the_removed_id() =>
            UpdatedClients().Should().Equal(Inheriting.ClientUuid, Unapproved.ClientUuid);

        [Test]
        public async Task It_leaves_exactly_that_client_pending() =>
            (await PendingClientIdsAsync()).Should().Equal(Inheriting.Id);
    }

    [TestFixture]
    public class Given_an_application_update_rejected_for_a_duplicate_name : ApplicationUpdateSyncApiTests
    {
        private (HttpStatusCode Status, JsonNode Body) _response;

        [SetUp]
        public async Task Setup()
        {
            string otherName = $"Other Sync Application {Guid.NewGuid().ToString("N")[..12]}";
            await CreateApplicationAsync(otherName, [A1]);
            _response = await PutApplicationAsync([A1], otherName);
        }

        [Test]
        public void It_rejects_the_name() => _response.Status.Should().Be(HttpStatusCode.BadRequest);

        [Test]
        public void It_makes_no_provider_update() => ProviderUpdates.Should().BeEmpty();

        [Test]
        public async Task It_marks_no_client_and_keeps_the_Application() =>
            ((await PendingClientIdsAsync()), await StoredApplicationNameAsync())
                .Should()
                .Be((Array.Empty<int>(), ApplicationName));
    }

    /// <summary>
    /// A request that both takes another Application's name and references a missing profile. The
    /// save checks the name before the profiles, so the duplicate name is what is reported.
    /// </summary>
    [TestFixture]
    public class Given_an_application_update_with_a_taken_name_and_a_missing_profile
        : ApplicationUpdateSyncApiTests
    {
        private readonly string _otherName = $"Other Sync Application {Guid.NewGuid().ToString("N")[..12]}";
        private (HttpStatusCode Status, JsonNode Body) _response;

        [SetUp]
        public async Task Setup()
        {
            await CreateApplicationAsync(_otherName, [A1]);
            _response = await PutApplicationAsync([A1], _otherName, profileIds: [int.MaxValue]);
        }

        [Test]
        public void It_reports_only_the_duplicate_name()
        {
            _response.Status.Should().Be(HttpStatusCode.BadRequest);
            _response.Body["type"]!
                .GetValue<string>()
                .Should()
                .Be("urn:ed-fi:management-api:bad-request:data");
            JsonNode
                .DeepEquals(
                    _response.Body["validationErrors"],
                    new JsonObject
                    {
                        ["ApplicationName"] = new JsonArray(
                            $"Application '{_otherName}' already exists for vendor."
                        ),
                    }
                )
                .Should()
                .BeTrue(_response.Body.ToJsonString());
        }

        [Test]
        public void It_makes_no_provider_update() => ProviderUpdates.Should().BeEmpty();

        [Test]
        public async Task It_marks_no_client_and_keeps_the_Application() =>
            (
                (await PendingClientIdsAsync()),
                await StoredApplicationNameAsync(),
                await ApplicationEducationOrganizationIdsAsync()
            )
                .Should()
                .Be((Array.Empty<int>(), ApplicationName, "255901,255902,255903"));
    }

    /// <summary>
    /// After a rename leaves the unapproved and narrowed clients pending, an unchanged PUT of the
    /// unapproved client completes its synchronization and only its own.
    /// </summary>
    [TestFixture]
    public class Given_a_partial_application_sync_followed_by_an_unchanged_client_put
        : ApplicationUpdateSyncApiTests
    {
        private Dictionary<int, DateTime> _pendingBefore = [];
        private (HttpStatusCode Status, JsonNode Body) _response;

        [SetUp]
        public async Task Setup()
        {
            FailAtProvider(Unapproved);
            FailAtProvider(Narrowed);
            ShouldBePartialSync(
                await PutApplicationAsync([A1, A2, A3], "Renamed Sync Application"),
                Unapproved.Id,
                Narrowed.Id
            );
            _pendingBefore = await PendingMarkersAsync();
            FailingClients.Clear();
            ProviderUpdates.Clear();

            _response = await PutClientAsync(Unapproved, false, []);
        }

        [Test]
        public void It_completes_the_client_update() =>
            _response.Status.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_sends_the_client_its_current_scope_and_settings() =>
            ProviderUpdates
                .Select(update => (update.ClientUuid, update.EducationOrganizationIds, update.IsApproved))
                .Should()
                .Equal((Unapproved.ClientUuid, "255902", false));

        [Test]
        public async Task It_clears_only_that_client_and_keeps_the_other_marker_time() =>
            (await PendingMarkersAsync())
                .Should()
                .BeEquivalentTo(
                    new Dictionary<int, DateTime> { [Narrowed.Id] = _pendingBefore[Narrowed.Id] }
                );
    }

    /// <summary>
    /// A pending client's PUT that the provider rejects, and two PUTs rejected before the provider
    /// is called, all leave its marker with its original time.
    /// </summary>
    [TestFixture]
    public class Given_rejected_puts_of_a_pending_client : ApplicationUpdateSyncApiTests
    {
        private DateTime _pendingSince;
        private HttpStatusCode _providerRejected;
        private HttpStatusCode _missingDataStore;
        private HttpStatusCode _scopeOffTheApplication;

        [SetUp]
        public async Task Setup()
        {
            FailAtProvider(Unapproved);
            ShouldBePartialSync(
                await PutApplicationAsync([A1, A2, A3], "Renamed Sync Application"),
                Unapproved.Id
            );
            _pendingSince = (await PendingMarkersAsync())[Unapproved.Id];

            (_providerRejected, _) = await PutClientAsync(Unapproved, false, []);
            FailingClients.Clear();
            (_missingDataStore, _) = await PutClientAsync(Unapproved, false, [int.MaxValue]);
            (_scopeOffTheApplication, _) = await PutClientAsync(Unapproved, false, [], List(A4));
        }

        [Test]
        public void It_rejects_each_put()
        {
            _providerRejected.Should().Be(HttpStatusCode.BadGateway);
            _missingDataStore.Should().Be(HttpStatusCode.Conflict);
            _scopeOffTheApplication.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task It_keeps_the_marker_with_its_original_time() =>
            (await PendingMarkersAsync())
                .Should()
                .BeEquivalentTo(new Dictionary<int, DateTime> { [Unapproved.Id] = _pendingSince });
    }

    /// <summary>
    /// A pending client moved to an Application that holds its education organization completes its
    /// synchronization; a move to one that lacks it is rejected and keeps the marker.
    /// </summary>
    [TestFixture]
    public class Given_moves_of_a_pending_client : ApplicationUpdateSyncApiTests
    {
        private Dictionary<int, DateTime> _pendingBefore = [];
        private HttpStatusCode _rejectedMove;
        private Dictionary<int, DateTime> _pendingAfterRejectedMove = [];
        private HttpStatusCode _move;
        private int _destination;

        [SetUp]
        public async Task Setup()
        {
            FailAtProvider(Unapproved);
            FailAtProvider(Narrowed);
            ShouldBePartialSync(
                await PutApplicationAsync([A1, A2, A3], "Renamed Sync Application"),
                Unapproved.Id,
                Narrowed.Id
            );
            _pendingBefore = await PendingMarkersAsync();
            FailingClients.Clear();

            int lacking = await CreateApplicationAsync($"Lacking {Guid.NewGuid():N}", [A1]);
            (_rejectedMove, _) = await PutClientAsync(Unapproved, false, [], applicationId: lacking);
            _pendingAfterRejectedMove = await PendingMarkersAsync();

            _destination = await CreateApplicationAsync($"Destination {Guid.NewGuid():N}", [A1, A2]);
            (_move, _) = await PutClientAsync(Unapproved, false, [], applicationId: _destination);
        }

        [Test]
        public void It_rejects_the_move_to_an_Application_without_the_clients_list() =>
            _rejectedMove.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_keeps_both_markers_after_the_rejected_move() =>
            _pendingAfterRejectedMove.Should().BeEquivalentTo(_pendingBefore);

        [Test]
        public void It_completes_the_move() => _move.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public async Task It_clears_the_moved_client_and_keeps_the_other_marker_time() =>
            (await PendingMarkersAsync())
                .Should()
                .BeEquivalentTo(
                    new Dictionary<int, DateTime> { [Narrowed.Id] = _pendingBefore[Narrowed.Id] }
                );
    }

    /// <summary>
    /// Credential resets of pending clients, through the client and through a single-client
    /// Application, rotate only the secret.
    /// </summary>
    [TestFixture]
    public class Given_credential_resets_of_pending_clients : ApplicationUpdateSyncApiTests
    {
        private Client _soleClient = null!;
        private Dictionary<int, DateTime> _pendingBefore = [];
        private (HttpStatusCode Status, JsonNode Body) _clientReset;
        private (HttpStatusCode Status, JsonNode Body) _applicationReset;

        [SetUp]
        public async Task Setup()
        {
            int single = await CreateApplicationAsync($"Single {Guid.NewGuid():N}", [A1, A2]);
            _soleClient = await CreateClientAsync(true, List(A2), [FirstDataStore], single);

            FailAtProvider(Unapproved);
            ShouldBePartialSync(
                await PutApplicationAsync([A1, A2, A3], "Renamed Sync Application"),
                Unapproved.Id
            );
            FailAtProvider(_soleClient);
            ShouldBePartialSync(
                await PutApplicationAsync(
                    [A1, A2],
                    $"Renamed Single {Guid.NewGuid():N}",
                    applicationId: single
                ),
                _soleClient.Id
            );
            _pendingBefore = await PendingMarkersAsync();
            ProviderUpdates.Clear();

            _clientReset = await SendAsync(
                HttpMethod.Put,
                $"/v3/apiClients/{Unapproved.Id}/reset-credential"
            );
            _applicationReset = await SendAsync(
                HttpMethod.Put,
                $"/v3/applications/{single}/reset-credential"
            );
        }

        [Test]
        public void It_returns_each_reset_with_the_new_secret()
        {
            _clientReset.Status.Should().Be(HttpStatusCode.OK);
            _clientReset.Body["secret"]!.GetValue<string>().Should().Be("rotated-secret");
            _applicationReset.Status.Should().Be(HttpStatusCode.OK);
            _applicationReset.Body["secret"]!.GetValue<string>().Should().Be("rotated-secret");
        }

        [Test]
        public void It_rotates_only_the_secrets() =>
            (ProviderResets.ToArray(), ProviderUpdates.Count)
                .Should()
                .BeEquivalentTo((new[] { Unapproved.ClientUuid, _soleClient.ClientUuid }, 0));

        [Test]
        public async Task It_keeps_both_markers_with_their_times() =>
            (await PendingMarkersAsync()).Should().BeEquivalentTo(_pendingBefore);

        [Test]
        public async Task It_keeps_each_clients_scope()
        {
            (await ClientEducationOrganizationIdsAsync(Unapproved)).Should().Be("255902");
            (await ClientEducationOrganizationIdsAsync(_soleClient)).Should().Be("255902");
        }
    }

    [TestFixture]
    public class Given_an_update_of_an_application_with_no_clients : ApplicationUpdateSyncApiTests
    {
        private int _clientless;
        private (HttpStatusCode Status, JsonNode Body) _response;

        [SetUp]
        public async Task Setup()
        {
            _clientless = await CreateApplicationAsync($"Clientless {Guid.NewGuid():N}", [A1]);
            _response = await PutApplicationAsync(
                [A2],
                $"Renamed Clientless {Guid.NewGuid().ToString("N")[..12]}",
                "OtherClaimSet",
                _clientless
            );
        }

        [Test]
        public void It_completes_the_update() => _response.Status.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_makes_no_provider_update() => ProviderUpdates.Should().BeEmpty();
    }
}
