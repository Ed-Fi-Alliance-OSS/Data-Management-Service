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

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// The client education organization rules of POST and PUT /v3/apiClients, through the HTTP
/// pipeline against the real database, with a fake identity provider that records the education
/// organization claim each provider call sends. Application A holds 255901, 255902 and 255903;
/// Application B, a move destination, holds 255901 and 255904. Neither has a default client.
/// </summary>
public abstract class ApiClientEducationOrganizationApiTests : DatabaseTestBase
{
    protected const long A1 = 255901;
    protected const long A2 = 255902;
    protected const long A3 = 255903;
    protected const long B4 = 255904;
    protected const string DataValidationType = "urn:ed-fi:management-api:bad-request:data";
    protected const string ScopeConflictType =
        "urn:ed-fi:management-api:conflict:education-organization-scope";

    protected sealed record ProviderCreate(string ClientId, Guid ClientUuid, string EducationOrganizationIds);

    protected sealed record ProviderUpdate(string ClientUuid, string EducationOrganizationIds);

    protected sealed record ClientScope(int ApplicationId, bool Inherits, (int, long)[] Rows);

    private WebApplicationFactory<Program> _factory = null!;

    protected IIdentityProviderRepository IdentityProvider { get; private set; } = null!;
    protected List<ProviderCreate> ProviderCreates { get; } = [];
    protected List<ProviderUpdate> ProviderUpdates { get; } = [];
    protected List<string> ProviderDeletes { get; } = [];

    /// <summary>Runs once, inside the next provider create or update, before it returns.</summary>
    protected Func<Task>? DuringNextProviderCall { get; set; }

    protected int VendorId { get; private set; }
    protected int ApplicationA { get; private set; }
    protected int ApplicationB { get; private set; }

    [SetUp]
    public async Task StartHost()
    {
        ProviderCreates.Clear();
        ProviderUpdates.Clear();
        ProviderDeletes.Clear();
        DuringNextProviderCall = null;

        IdentityProvider = A.Fake<IIdentityProviderRepository>();
        A.CallTo(() =>
                IdentityProvider.CreateClientAsync(
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
            .ReturnsLazily(async call =>
            {
                Guid clientUuid = Guid.NewGuid();
                ProviderCreates.Add(
                    new(call.GetArgument<string>(0)!, clientUuid, call.GetArgument<string>(6)!)
                );
                await RunDuringProviderCallAsync();
                return (ClientCreateResult)new ClientCreateResult.Success(clientUuid);
            });
        A.CallTo(() =>
                IdentityProvider.UpdateClientAsync(
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<int[]?>.Ignored,
                    A<bool>.Ignored,
                    A<string>.Ignored
                )
            )
            .ReturnsLazily(async call =>
            {
                string clientUuid = call.GetArgument<string>(0)!;
                ProviderUpdates.Add(new(clientUuid, call.GetArgument<string>(3)!));
                await RunDuringProviderCallAsync();
                return (ClientUpdateResult)new ClientUpdateResult.Success(Guid.Parse(clientUuid));
            });
        A.CallTo(() => IdentityProvider.DeleteClientAsync(A<string>.Ignored))
            .ReturnsLazily(call =>
            {
                ProviderDeletes.Add(call.GetArgument<string>(0)!);
                return Task.FromResult<ClientDeleteResult>(new ClientDeleteResult.Success());
            });

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("AppSettings:Datastore", "postgresql");
            builder.UseSetting("AppSettings:MultiTenancy", "false");
            builder.UseSetting(
                "DatabaseSettings:DatabaseConnection",
                Configuration.DatabaseOptions.Value.DatabaseConnection
            );
            builder.ConfigureServices(services =>
            {
                services.AddTestAuthentication();
                services.AddTransient(_ => IdentityProvider);
            });
        });

        VendorId = await CreateAsync(
            "/v3/vendors/",
            $$"""
            {
              "company": "Client Scope Vendor {{Guid.NewGuid():N}}",
              "contactName": "Test Contact",
              "contactEmailAddress": "test@test.test",
              "namespacePrefixes": "uri://client-scope"
            }
            """
        );
        ApplicationA = await CreateApplicationAsync("Application A", [A1, A2, A3]);
        ApplicationB = await CreateApplicationAsync("Application B", [A1, B4]);
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

    private async Task RunDuringProviderCallAsync()
    {
        if (DuringNextProviderCall is { } action)
        {
            DuringNextProviderCall = null;
            await action();
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
              "applicationName": "{{name}} {{Guid.NewGuid():N}}",
              "vendorId": {{VendorId}},
              "claimSetName": "ClientScopeClaimSet",
              "educationOrganizationIds": [{{string.Join(", ", educationOrganizationIds)}}],
              "createDefaultCredentials": false
            }
            """
        );

    /// <summary>POSTs a client; <paramref name="scopeJson"/> is appended inside the body.</summary>
    protected Task<(HttpStatusCode Status, JsonNode Body)> PostClientAsync(
        int applicationId,
        string scopeJson = "",
        string? name = null,
        int[]? dataStoreIds = null
    ) =>
        SendAsync(
            HttpMethod.Post,
            "/v3/apiClients/",
            $$"""
            {
              "applicationId": {{applicationId}},
              "name": "{{name ?? "Client " + Guid.NewGuid().ToString("N")}}",
              "isApproved": true,
              "dataStoreIds": [{{string.Join(", ", dataStoreIds ?? [])}}]{{scopeJson}}
            }
            """
        );

    protected sealed record ReadScope(bool Inherits, long[] EducationOrganizationIds);

    protected static ReadScope ReadScopeOf(JsonNode apiClient) =>
        new(
            apiClient["inheritsApplicationEducationOrganizations"]!.GetValue<bool>(),
            [.. apiClient["educationOrganizationIds"]!.AsArray().Select(id => id!.GetValue<long>())]
        );

    /// <summary>Creates a client and returns its id and OAuth client key.</summary>
    protected async Task<(int Id, string Key)> CreateKeyedClientAsync(
        int applicationId,
        string scopeJson = "",
        int[]? dataStoreIds = null
    )
    {
        var (status, body) = await PostClientAsync(applicationId, scopeJson, dataStoreIds: dataStoreIds);
        status.Should().Be(HttpStatusCode.Created, body.ToJsonString());
        return (body["id"]!.GetValue<int>(), body["key"]!.GetValue<string>());
    }

    protected async Task<int> CreateDataStoreAsync() =>
        await CreateAsync(
            "/v3/dataStores/",
            $$"""
            {
              "dataStoreType": "Production",
              "name": "ClientScope-{{Guid.NewGuid():N}}",
              "connectionString": "Host=db;Username=edfi;Database=edfi;Password=secret"
            }
            """
        );

    /// <summary>
    /// Reads a client from the collection, by its numeric id and by its OAuth client key, keyed
    /// by the read used.
    /// </summary>
    protected async Task<Dictionary<string, JsonNode>> ReadEveryWayAsync((int Id, string Key) client)
    {
        var (collectionStatus, collection) = await SendAsync(
            HttpMethod.Get,
            "/v3/apiClients/?offset=0&limit=500"
        );
        var (idStatus, byId) = await SendAsync(HttpMethod.Get, $"/v3/apiClients/{client.Id}");
        var (keyStatus, byKey) = await SendAsync(HttpMethod.Get, $"/v3/apiClients/{client.Key}");
        collectionStatus.Should().Be(HttpStatusCode.OK);
        idStatus.Should().Be(HttpStatusCode.OK);
        keyStatus.Should().Be(HttpStatusCode.OK);
        return new()
        {
            ["collection"] = collection.AsArray().Single(item => item!["id"]!.GetValue<int>() == client.Id)!,
            ["id"] = byId,
            ["key"] = byKey,
        };
    }

    protected async Task<string> ApplicationNameAsync(int applicationId)
    {
        var (status, application) = await SendAsync(HttpMethod.Get, $"/v3/applications/{applicationId}");
        status.Should().Be(HttpStatusCode.OK);
        return application["applicationName"]!.GetValue<string>();
    }

    /// <summary>PUTs an Application under its current name unless another is given.</summary>
    protected async Task<(HttpStatusCode Status, JsonNode Body)> PutApplicationAsync(
        int applicationId,
        long[] educationOrganizationIds,
        string? name = null
    ) =>
        await SendAsync(
            HttpMethod.Put,
            $"/v3/applications/{applicationId}",
            $$"""
            {
              "id": {{applicationId}},
              "applicationName": "{{name ?? await ApplicationNameAsync(applicationId)}}",
              "vendorId": {{VendorId}},
              "claimSetName": "ClientScopeClaimSet",
              "educationOrganizationIds": [{{string.Join(", ", educationOrganizationIds)}}],
              "profileIds": []
            }
            """
        );

    protected async Task<long[]> ApplicationEducationOrganizationIdsAsync(int applicationId)
    {
        var (_, application) = await SendAsync(HttpMethod.Get, $"/v3/applications/{applicationId}");
        return
        [
            .. application["educationOrganizationIds"]!.AsArray().Select(id => id!.GetValue<long>()).Order(),
        ];
    }

    protected async Task<ReadScope> ReadByIdAsync(int apiClientId)
    {
        var (status, apiClient) = await SendAsync(HttpMethod.Get, $"/v3/apiClients/{apiClientId}");
        status.Should().Be(HttpStatusCode.OK);
        return ReadScopeOf(apiClient);
    }

    /// <summary>Creates a client and returns its id and identity provider client UUID.</summary>
    protected async Task<(int Id, string ClientUuid)> CreateClientAsync(
        int applicationId,
        string scopeJson = "",
        string? name = null
    )
    {
        var (status, body) = await PostClientAsync(applicationId, scopeJson, name);
        status.Should().Be(HttpStatusCode.Created, body.ToJsonString());
        string key = body["key"]!.GetValue<string>();
        return (
            body["id"]!.GetValue<int>(),
            ProviderCreates.Single(c => c.ClientId == key).ClientUuid.ToString()
        );
    }

    /// <summary>PUTs a client; <paramref name="scopeJson"/> is appended inside the body.</summary>
    protected Task<(HttpStatusCode Status, JsonNode Body)> PutClientAsync(
        int id,
        int applicationId,
        string scopeJson = "",
        string? name = null
    ) =>
        SendAsync(
            HttpMethod.Put,
            $"/v3/apiClients/{id}",
            $$"""
            {
              "id": {{id}},
              "applicationId": {{applicationId}},
              "name": "{{name ?? "Updated " + Guid.NewGuid().ToString("N")}}",
              "isApproved": true,
              "dataStoreIds": []{{scopeJson}}
            }
            """
        );

    protected static string List(params long[] ids) =>
        $$""", "educationOrganizationIds": [{{string.Join(", ", ids)}}]""";

    protected static string Flag(bool inherits) =>
        $$""", "inheritsApplicationEducationOrganizations": {{(inherits ? "true" : "false")}}""";

    protected async Task<ClientScope> ClientScopeAsync(int apiClientId)
    {
        await using var connection = await DataSource!.OpenConnectionAsync();
        var client = await connection.QuerySingleAsync<(int ApplicationId, bool Inherits)>(
            """SELECT "ApplicationId", "InheritsApplicationEducationOrganizations" FROM "dmscs"."ApiClient" WHERE "Id" = @Id;""",
            new { Id = apiClientId }
        );
        var rows = await connection.QueryAsync<(int, long)>(
            """SELECT "ApplicationId", "EducationOrganizationId" FROM "dmscs"."ApiClientEducationOrganization" WHERE "ApiClientId" = @Id ORDER BY "EducationOrganizationId";""",
            new { Id = apiClientId }
        );
        return new(client.ApplicationId, client.Inherits, [.. rows]);
    }

    protected async Task<string> ClientNameAsync(int apiClientId)
    {
        await using var connection = await DataSource!.OpenConnectionAsync();
        return await connection.QuerySingleAsync<string>(
            """SELECT "Name" FROM "dmscs"."ApiClient" WHERE "Id" = @Id;""",
            new { Id = apiClientId }
        );
    }

    protected async Task<int> ApiClientCountAsync()
    {
        await using var connection = await DataSource!.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM "dmscs"."ApiClient";""");
    }

    /// <summary>Removes an education organization from an Application, cascading to its clients.</summary>
    protected async Task RemoveApplicationEducationOrganizationAsync(
        int applicationId,
        long educationOrganizationId
    )
    {
        await using var connection = await DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """DELETE FROM "dmscs"."ApplicationEducationOrganization" WHERE "ApplicationId" = @ApplicationId AND "EducationOrganizationId" = @EducationOrganizationId;""",
            new { ApplicationId = applicationId, EducationOrganizationId = educationOrganizationId }
        );
    }

    /// <summary>Adds an education organization to an Application without touching any client rows.</summary>
    protected async Task AddApplicationEducationOrganizationAsync(
        int applicationId,
        long educationOrganizationId
    )
    {
        await using var connection = await DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """INSERT INTO "dmscs"."ApplicationEducationOrganization" ("ApplicationId", "EducationOrganizationId") VALUES (@ApplicationId, @EducationOrganizationId);""",
            new { ApplicationId = applicationId, EducationOrganizationId = educationOrganizationId }
        );
    }

    protected async Task RenameClientAsync(int apiClientId, string name)
    {
        await using var connection = await DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """UPDATE "dmscs"."ApiClient" SET "Name" = @Name WHERE "Id" = @Id;""",
            new { Id = apiClientId, Name = name }
        );
    }

    protected static ClientScope Scope(int applicationId, bool inherits, params long[] ids) =>
        new(applicationId, inherits, [.. ids.Order().Select(id => (applicationId, id))]);

    protected static void ShouldMatch(ClientScope actual, ClientScope expected) =>
        actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());

    protected static void ShouldBeDataValidation(
        (HttpStatusCode Status, JsonNode Body) response,
        string field
    )
    {
        response.Status.Should().Be(HttpStatusCode.BadRequest, response.Body.ToJsonString());
        response.Body["type"]!.GetValue<string>().Should().Be(DataValidationType);
        response.Body["validationErrors"]!.AsObject().Select(error => error.Key).Should().Equal(field);
    }

    protected static void ShouldBeScopeConflict((HttpStatusCode Status, JsonNode Body) response)
    {
        response.Status.Should().Be(HttpStatusCode.Conflict, response.Body.ToJsonString());
        response.Body["type"]!.GetValue<string>().Should().Be(ScopeConflictType);
    }

    protected string[] ClaimsSentFor(string clientUuid) =>
        [
            .. ProviderUpdates
                .Where(update => update.ClientUuid == clientUuid)
                .Select(update => update.EducationOrganizationIds),
        ];

    [TestFixture]
    public class Given_api_clients_created_with_each_education_organization_request
        : ApiClientEducationOrganizationApiTests
    {
        private readonly Dictionary<string, (HttpStatusCode Status, int Id, string Claim)> _created = [];
        private int _largeIdApplication;
        private const long LargeId = 9007199254740993;

        [SetUp]
        public async Task Setup()
        {
            _largeIdApplication = await CreateApplicationAsync("Large Id Application", [A1, LargeId]);
            var requests = new (string Name, int ApplicationId, string Json)[]
            {
                ("neither", ApplicationA, ""),
                ("list", ApplicationA, List(A2)),
                ("list equal to the Application", ApplicationA, List(A3, A1, A2)),
                ("false with a repeated list", ApplicationA, List(A3, A1, A3) + Flag(false)),
                ("true", ApplicationA, Flag(true)),
                ("true with the Application list repeated", ApplicationA, List(A2, A3, A1, A3) + Flag(true)),
                ("large id", _largeIdApplication, List(LargeId)),
            };
            foreach (var (name, applicationId, json) in requests)
            {
                var (status, body) = await PostClientAsync(applicationId, json);
                string key = body["key"]!.GetValue<string>();
                _created[name] = (
                    status,
                    body["id"]!.GetValue<int>(),
                    ProviderCreates.Single(c => c.ClientId == key).EducationOrganizationIds
                );
            }
        }

        [Test]
        public void It_creates_every_client() =>
            _created.Values.Should().OnlyContain(created => created.Status == HttpStatusCode.Created);

        [TestCase("neither", true, new[] { A1, A2, A3 })]
        [TestCase("list", false, new[] { A2 })]
        [TestCase("list equal to the Application", false, new[] { A1, A2, A3 })]
        [TestCase("false with a repeated list", false, new[] { A1, A3 })]
        [TestCase("true", true, new[] { A1, A2, A3 })]
        [TestCase("true with the Application list repeated", true, new[] { A1, A2, A3 })]
        public async Task It_stores_the_resolved_scope(string request, bool inherits, long[] ids) =>
            ShouldMatch(await ClientScopeAsync(_created[request].Id), Scope(ApplicationA, inherits, ids));

        [TestCase("neither", "255901,255902,255903")]
        [TestCase("list", "255902")]
        [TestCase("list equal to the Application", "255901,255902,255903")]
        [TestCase("false with a repeated list", "255901,255903")]
        [TestCase("true", "255901,255902,255903")]
        [TestCase("true with the Application list repeated", "255901,255902,255903")]
        [TestCase("large id", "9007199254740993")]
        public void It_sends_the_effective_list_as_the_claim(string request, string claim) =>
            _created[request].Claim.Should().Be(claim);

        [Test]
        public async Task It_stores_a_large_education_organization_id_exactly() =>
            ShouldMatch(
                await ClientScopeAsync(_created["large id"].Id),
                Scope(_largeIdApplication, false, LargeId)
            );
    }

    [TestFixture]
    public class Given_api_client_creates_rejected_by_the_education_organization_rules
        : ApiClientEducationOrganizationApiTests
    {
        private readonly Dictionary<string, (HttpStatusCode Status, JsonNode Body)> _responses = [];

        [SetUp]
        public async Task Setup()
        {
            var requests = new (string Name, string Json)[]
            {
                ("empty list", """, "educationOrganizationIds": []"""),
                ("empty list with true", """, "educationOrganizationIds": []""" + Flag(true)),
                ("null list", """, "educationOrganizationIds": null"""),
                ("false without a list", Flag(false)),
                ("null flag", """, "inheritsApplicationEducationOrganizations": null"""),
                ("true with another list", List(A1, A2) + Flag(true)),
                ("list not on the Application", List(A1, B4)),
            };
            foreach (var (name, json) in requests)
            {
                _responses[name] = await PostClientAsync(ApplicationA, json);
            }
        }

        [TestCase("empty list", "EducationOrganizationIds")]
        [TestCase("empty list with true", "EducationOrganizationIds")]
        [TestCase("null list", "EducationOrganizationIds")]
        [TestCase("false without a list", "InheritsApplicationEducationOrganizations")]
        [TestCase("null flag", "InheritsApplicationEducationOrganizations")]
        [TestCase("true with another list", "EducationOrganizationIds")]
        [TestCase("list not on the Application", "EducationOrganizationIds")]
        public void It_rejects_the_request_on_the_field(string request, string field) =>
            ShouldBeDataValidation(_responses[request], field);

        [Test]
        public void It_names_the_education_organizations_not_on_the_Application() =>
            _responses["list not on the Application"].Body["validationErrors"]!["EducationOrganizationIds"]![
                0
            ]!
                .GetValue<string>()
                .Should()
                .EndWith("Not on the Application: 255904.");

        [Test]
        public void It_creates_no_identity_provider_client() => ProviderCreates.Should().BeEmpty();

        [Test]
        public async Task It_creates_no_client() => (await ApiClientCountAsync()).Should().Be(0);
    }

    [TestFixture]
    public class Given_api_clients_created_on_an_Application_without_education_organizations
        : ApiClientEducationOrganizationApiTests
    {
        private int _emptyApplication;
        private (int Id, string ClientUuid) _neither;
        private (int Id, string ClientUuid) _true;
        private (HttpStatusCode Status, JsonNode Body) _emptyListWithTrue;

        [SetUp]
        public async Task Setup()
        {
            _emptyApplication = await CreateApplicationAsync("Empty Application", []);
            _neither = await CreateClientAsync(_emptyApplication);
            _true = await CreateClientAsync(_emptyApplication, Flag(true));
            _emptyListWithTrue = await PostClientAsync(
                _emptyApplication,
                """, "educationOrganizationIds": []""" + Flag(true)
            );
        }

        [Test]
        public async Task It_makes_both_clients_inherit_the_empty_list()
        {
            ShouldMatch(await ClientScopeAsync(_neither.Id), Scope(_emptyApplication, true));
            ShouldMatch(await ClientScopeAsync(_true.Id), Scope(_emptyApplication, true));
        }

        [Test]
        public void It_sends_empty_claims() =>
            ProviderCreates.Select(create => create.EducationOrganizationIds).Should().Equal("", "");

        [Test]
        public void It_still_rejects_an_empty_list_sent_with_true() =>
            ShouldBeDataValidation(_emptyListWithTrue, "EducationOrganizationIds");
    }

    [TestFixture]
    public class Given_api_client_updates_on_the_same_Application : ApiClientEducationOrganizationApiTests
    {
        private readonly Dictionary<
            string,
            ((int Id, string ClientUuid) Client, HttpStatusCode Status)
        > _updates = [];

        [SetUp]
        public async Task Setup()
        {
            var requests = new (string Name, string CreateJson, string UpdateJson)[]
            {
                ("narrowed, neither", List(A2), ""),
                ("inheriting, neither", "", ""),
                ("narrowed, true", List(A2), Flag(true)),
                ("narrowed, true with the Application list", List(A2), List(A3, A2, A1, A1) + Flag(true)),
                ("inheriting, list", "", List(A3, A1)),
                ("inheriting, list equal to the Application", "", List(A1, A2, A3)),
                ("inheriting, false with a list", "", List(A2) + Flag(false)),
            };
            foreach (var (name, createJson, updateJson) in requests)
            {
                var client = await CreateClientAsync(ApplicationA, createJson);
                var (status, _) = await PutClientAsync(client.Id, ApplicationA, updateJson);
                _updates[name] = (client, status);
            }
        }

        [Test]
        public void It_updates_every_client() =>
            _updates.Values.Should().OnlyContain(update => update.Status == HttpStatusCode.NoContent);

        [TestCase("narrowed, neither", false, new[] { A2 })]
        [TestCase("inheriting, neither", true, new[] { A1, A2, A3 })]
        [TestCase("narrowed, true", true, new[] { A1, A2, A3 })]
        [TestCase("narrowed, true with the Application list", true, new[] { A1, A2, A3 })]
        [TestCase("inheriting, list", false, new[] { A1, A3 })]
        [TestCase("inheriting, list equal to the Application", false, new[] { A1, A2, A3 })]
        [TestCase("inheriting, false with a list", false, new[] { A2 })]
        public async Task It_stores_the_resolved_scope(string request, bool inherits, long[] ids) =>
            ShouldMatch(
                await ClientScopeAsync(_updates[request].Client.Id),
                Scope(ApplicationA, inherits, ids)
            );

        [TestCase("narrowed, neither", "255902")]
        [TestCase("inheriting, neither", "255901,255902,255903")]
        [TestCase("narrowed, true", "255901,255902,255903")]
        [TestCase("narrowed, true with the Application list", "255901,255902,255903")]
        [TestCase("inheriting, list", "255901,255903")]
        [TestCase("inheriting, list equal to the Application", "255901,255902,255903")]
        [TestCase("inheriting, false with a list", "255902")]
        public void It_sends_the_effective_list_as_the_claim(string request, string claim) =>
            ClaimsSentFor(_updates[request].Client.ClientUuid).Should().Equal(claim);
    }

    [TestFixture]
    public class Given_api_client_updates_rejected_by_the_education_organization_rules
        : ApiClientEducationOrganizationApiTests
    {
        private (int Id, string ClientUuid) _client;
        private readonly Dictionary<string, (HttpStatusCode Status, JsonNode Body)> _responses = [];

        [SetUp]
        public async Task Setup()
        {
            _client = await CreateClientAsync(ApplicationA, List(A2), "Narrowed client");
            var requests = new (string Name, string Json)[]
            {
                ("empty list", """, "educationOrganizationIds": []"""),
                ("null list", """, "educationOrganizationIds": null"""),
                ("false without a list", Flag(false)),
                ("null flag", """, "inheritsApplicationEducationOrganizations": null"""),
                ("true with another list", List(A2) + Flag(true)),
                ("list not on the Application", List(B4)),
            };
            foreach (var (name, json) in requests)
            {
                _responses[name] = await PutClientAsync(_client.Id, ApplicationA, json);
            }
        }

        [TestCase("empty list", "EducationOrganizationIds")]
        [TestCase("null list", "EducationOrganizationIds")]
        [TestCase("false without a list", "InheritsApplicationEducationOrganizations")]
        [TestCase("null flag", "InheritsApplicationEducationOrganizations")]
        [TestCase("true with another list", "EducationOrganizationIds")]
        [TestCase("list not on the Application", "EducationOrganizationIds")]
        public void It_rejects_the_request_on_the_field(string request, string field) =>
            ShouldBeDataValidation(_responses[request], field);

        [Test]
        public void It_updates_no_identity_provider_client() => ProviderUpdates.Should().BeEmpty();

        [Test]
        public async Task It_leaves_the_client_narrowed_and_named() =>
            (await ClientScopeAsync(_client.Id), await ClientNameAsync(_client.Id))
                .Should()
                .BeEquivalentTo((Scope(ApplicationA, false, A2), "Narrowed client"));
    }

    [TestFixture]
    public class Given_narrowed_clients_left_without_education_organizations
        : ApiClientEducationOrganizationApiTests
    {
        private (int Id, string ClientUuid) _kept;
        private (int Id, string ClientUuid) _moved;
        private HttpStatusCode _keptStatus;
        private HttpStatusCode _movedStatus;

        [SetUp]
        public async Task Setup()
        {
            _kept = await CreateClientAsync(ApplicationA, List(A2));
            _moved = await CreateClientAsync(ApplicationA, List(A2));
            await RemoveApplicationEducationOrganizationAsync(ApplicationA, A2);

            (_keptStatus, _) = await PutClientAsync(_kept.Id, ApplicationA);
            (_movedStatus, _) = await PutClientAsync(_moved.Id, ApplicationB);
        }

        [Test]
        public void It_accepts_both_updates()
        {
            _keptStatus.Should().Be(HttpStatusCode.NoContent);
            _movedStatus.Should().Be(HttpStatusCode.NoContent);
        }

        [Test]
        public async Task It_keeps_the_client_narrowed_to_nothing() =>
            ShouldMatch(await ClientScopeAsync(_kept.Id), Scope(ApplicationA, false));

        [Test]
        public async Task It_moves_the_empty_narrowed_client_without_a_conflict() =>
            ShouldMatch(await ClientScopeAsync(_moved.Id), Scope(ApplicationB, false));

        [Test]
        public void It_sends_empty_claims()
        {
            ClaimsSentFor(_kept.ClientUuid).Should().Equal("");
            ClaimsSentFor(_moved.ClientUuid).Should().Equal("");
        }
    }

    [TestFixture]
    public class Given_api_clients_moved_to_another_Application : ApiClientEducationOrganizationApiTests
    {
        private readonly Dictionary<
            string,
            ((int Id, string ClientUuid) Client, (HttpStatusCode Status, JsonNode Body) Response)
        > _moves = [];

        [SetUp]
        public async Task Setup()
        {
            var requests = new (string Name, string CreateJson, string MoveJson)[]
            {
                ("inheriting, neither", "", ""),
                ("narrowed fitting, neither", List(A1), ""),
                ("narrowed not fitting, neither", List(A2), ""),
                ("narrowed not fitting, replacement list", List(A2), List(B4)),
                ("narrowed not fitting, true", List(A2), Flag(true)),
                ("narrowed fitting, list only on the source", List(A1), List(A2)),
            };
            foreach (var (name, createJson, moveJson) in requests)
            {
                var client = await CreateClientAsync(ApplicationA, createJson);
                _moves[name] = (client, await PutClientAsync(client.Id, ApplicationB, moveJson));
            }
        }

        [TestCase("inheriting, neither")]
        [TestCase("narrowed fitting, neither")]
        [TestCase("narrowed not fitting, replacement list")]
        [TestCase("narrowed not fitting, true")]
        public void It_moves_the_client(string request) =>
            _moves[request].Response.Status.Should().Be(HttpStatusCode.NoContent);

        [TestCase("inheriting, neither", true, new[] { A1, B4 })]
        [TestCase("narrowed fitting, neither", false, new[] { A1 })]
        [TestCase("narrowed not fitting, replacement list", false, new[] { B4 })]
        [TestCase("narrowed not fitting, true", true, new[] { A1, B4 })]
        public async Task It_stores_the_scope_on_the_destination(string request, bool inherits, long[] ids) =>
            ShouldMatch(
                await ClientScopeAsync(_moves[request].Client.Id),
                Scope(ApplicationB, inherits, ids)
            );

        [TestCase("inheriting, neither", "255901,255904")]
        [TestCase("narrowed fitting, neither", "255901")]
        [TestCase("narrowed not fitting, replacement list", "255904")]
        [TestCase("narrowed not fitting, true", "255901,255904")]
        public void It_sends_the_destination_effective_list_as_the_claim(string request, string claim) =>
            ClaimsSentFor(_moves[request].Client.ClientUuid).Should().Equal(claim);

        [Test]
        public void It_rejects_the_move_of_a_narrowed_client_that_does_not_fit() =>
            ShouldBeScopeConflict(_moves["narrowed not fitting, neither"].Response);

        [Test]
        public void It_rejects_a_replacement_list_not_on_the_destination() =>
            ShouldBeDataValidation(
                _moves["narrowed fitting, list only on the source"].Response,
                "EducationOrganizationIds"
            );

        [TestCase("narrowed not fitting, neither", new[] { A2 })]
        [TestCase("narrowed fitting, list only on the source", new[] { A1 })]
        public async Task It_leaves_a_rejected_client_on_its_Application(string request, long[] ids) =>
            ShouldMatch(await ClientScopeAsync(_moves[request].Client.Id), Scope(ApplicationA, false, ids));

        [TestCase("narrowed not fitting, neither")]
        [TestCase("narrowed fitting, list only on the source")]
        public void It_updates_no_identity_provider_client_for_a_rejected_move(string request) =>
            ClaimsSentFor(_moves[request].Client.ClientUuid).Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_narrowed_client_create_whose_list_leaves_the_Application_during_the_provider_call
        : ApiClientEducationOrganizationApiTests
    {
        private (HttpStatusCode Status, JsonNode Body) _response;

        [SetUp]
        public async Task Setup()
        {
            DuringNextProviderCall = () => RemoveApplicationEducationOrganizationAsync(ApplicationA, A2);
            _response = await PostClientAsync(ApplicationA, List(A2));
        }

        [Test]
        public void It_rejects_the_list() => ShouldBeDataValidation(_response, "EducationOrganizationIds");

        [Test]
        public void It_deletes_the_provider_client_it_created() =>
            ProviderDeletes.Should().Equal(ProviderCreates.Single().ClientUuid.ToString());

        [Test]
        public async Task It_creates_no_client() => (await ApiClientCountAsync()).Should().Be(0);
    }

    [TestFixture]
    public class Given_a_narrowed_client_create_whose_list_leaves_the_Application_inside_the_repository_transaction
        : ApiClientEducationOrganizationApiTests
    {
        private (HttpStatusCode Status, JsonNode Body) _response;

        [SetUp]
        public async Task Setup()
        {
            // The removal is held uncommitted until the create waits on it, then commits, so it lands
            // between the repository's check of the list and its insert of the client rows
            await using var removal = await DataSource!.OpenConnectionAsync();
            await using var transaction = await removal.BeginTransactionAsync();
            await removal.ExecuteAsync(
                """DELETE FROM "dmscs"."ApplicationEducationOrganization" WHERE "ApplicationId" = @ApplicationId AND "EducationOrganizationId" = @EducationOrganizationId;""",
                new { ApplicationId = ApplicationA, EducationOrganizationId = A2 },
                transaction
            );

            Task<(HttpStatusCode Status, JsonNode Body)> create = PostClientAsync(ApplicationA, List(A2));
            await WaitUntilBlockedByAsync(removal.ProcessID, create);
            await transaction.CommitAsync();
            _response = await create;
        }

        [Test]
        public void It_rejects_the_list() => ShouldBeDataValidation(_response, "EducationOrganizationIds");

        [Test]
        public void It_deletes_the_provider_client_it_created() =>
            ProviderDeletes.Should().Equal(ProviderCreates.Single().ClientUuid.ToString());

        [Test]
        public async Task It_creates_no_client() => (await ApiClientCountAsync()).Should().Be(0);

        /// <summary>
        /// Returns once a database session waits on <paramref name="blockingProcessId"/>; the bound only
        /// turns a request that never waits into a failure instead of a hang.
        /// </summary>
        private async Task WaitUntilBlockedByAsync(int blockingProcessId, Task request)
        {
            await using var monitor = await DataSource!.OpenConnectionAsync();
            using CancellationTokenSource bound = new(TimeSpan.FromSeconds(60));
            while (
                !await monitor.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @BlockingProcessId = ANY(pg_blocking_pids(pid)));",
                    new { BlockingProcessId = blockingProcessId }
                )
            )
            {
                request.IsCompleted.Should().BeFalse("the create must wait on the uncommitted removal");
                await Task.Delay(TimeSpan.FromMilliseconds(20), bound.Token);
            }
        }
    }

    [TestFixture]
    public class Given_a_narrowed_client_move_whose_replacement_list_leaves_the_destination_during_the_provider_call
        : ApiClientEducationOrganizationApiTests
    {
        private (int Id, string ClientUuid) _client;
        private (HttpStatusCode Status, JsonNode Body) _response;

        [SetUp]
        public async Task Setup()
        {
            _client = await CreateClientAsync(ApplicationA, List(A3, A2));
            DuringNextProviderCall = () => RemoveApplicationEducationOrganizationAsync(ApplicationB, B4);
            _response = await PutClientAsync(_client.Id, ApplicationB, List(B4));
        }

        [Test]
        public void It_rejects_the_list() => ShouldBeDataValidation(_response, "EducationOrganizationIds");

        [Test]
        public void It_restores_the_original_narrowed_claim() =>
            ClaimsSentFor(_client.ClientUuid).Should().Equal("255904", "255902,255903");

        [Test]
        public async Task It_leaves_the_client_on_its_Application() =>
            ShouldMatch(await ClientScopeAsync(_client.Id), Scope(ApplicationA, false, A2, A3));
    }

    [TestFixture]
    public class Given_an_omitted_scope_move_whose_destination_loses_the_client_list_during_the_provider_call
        : ApiClientEducationOrganizationApiTests
    {
        private (int Id, string ClientUuid) _client;
        private (HttpStatusCode Status, JsonNode Body) _response;

        [SetUp]
        public async Task Setup()
        {
            _client = await CreateClientAsync(ApplicationA, List(A1));
            DuringNextProviderCall = () => RemoveApplicationEducationOrganizationAsync(ApplicationB, A1);
            _response = await PutClientAsync(_client.Id, ApplicationB);
        }

        [Test]
        public void It_reports_a_scope_conflict() => ShouldBeScopeConflict(_response);

        [Test]
        public void It_restores_the_original_narrowed_claim() =>
            ClaimsSentFor(_client.ClientUuid).Should().Equal("255901", "255901");

        [Test]
        public async Task It_leaves_the_client_on_its_Application() =>
            ShouldMatch(await ClientScopeAsync(_client.Id), Scope(ApplicationA, false, A1));
    }

    [TestFixture]
    public class Given_a_narrowed_client_move_that_fails_after_the_provider_update
        : ApiClientEducationOrganizationApiTests
    {
        private (int Id, string ClientUuid) _client;
        private (HttpStatusCode Status, JsonNode Body) _response;

        [SetUp]
        public async Task Setup()
        {
            _client = await CreateClientAsync(ApplicationA, List(A2), "Moving client");
            var other = await CreateClientAsync(ApplicationB);

            // The name is free when checked, and taken by the time the database update runs
            DuringNextProviderCall = () => RenameClientAsync(other.Id, "Taken name");
            _response = await PutClientAsync(_client.Id, ApplicationB, List(B4), "Taken name");
        }

        [Test]
        public void It_reports_the_duplicate_name() => ShouldBeDataValidation(_response, "Name");

        [Test]
        public void It_restores_the_original_narrowed_claim() =>
            ClaimsSentFor(_client.ClientUuid).Should().Equal("255904", "255902");

        [Test]
        public async Task It_leaves_the_client_on_its_Application_with_its_name()
        {
            ShouldMatch(await ClientScopeAsync(_client.Id), Scope(ApplicationA, false, A2));
            (await ClientNameAsync(_client.Id)).Should().Be("Moving client");
        }
    }

    [TestFixture]
    public class Given_an_inheriting_client_updated_after_its_Application_gains_an_education_organization
        : ApiClientEducationOrganizationApiTests
    {
        private const long A5 = 255905;
        private (int Id, string ClientUuid) _client;
        private HttpStatusCode _status;

        [SetUp]
        public async Task Setup()
        {
            _client = await CreateClientAsync(ApplicationA);

            // An Application save inserts only the added id; it does not copy it to the rows of
            // the Application's inheriting clients
            await AddApplicationEducationOrganizationAsync(ApplicationA, A5);
            (_status, _) = await PutClientAsync(_client.Id, ApplicationA);
        }

        [Test]
        public void It_updates_the_client() => _status.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public async Task It_keeps_the_client_inheriting_with_its_existing_rows() =>
            ShouldMatch(await ClientScopeAsync(_client.Id), Scope(ApplicationA, true, A1, A2, A3));

        [Test]
        public void It_sends_the_expanded_effective_list_as_the_claim() =>
            ClaimsSentFor(_client.ClientUuid).Should().Equal("255901,255902,255903,255905");
    }

    [TestFixture]
    public class Given_api_clients_read_with_their_effective_education_organizations
        : ApiClientEducationOrganizationApiTests
    {
        private const long A5 = 255905;
        private const long C6 = 255906;
        private const long LargeId = 9007199254740993;
        private readonly Dictionary<string, (int Id, string Key)> _clients = [];
        private readonly Dictionary<string, Dictionary<string, JsonNode>> _reads = [];

        [SetUp]
        public async Task Setup()
        {
            int emptyApplication = await CreateApplicationAsync("Empty Application", []);
            int cascadeApplication = await CreateApplicationAsync("Cascade Application", [C6]);
            int largeIdApplication = await CreateApplicationAsync("Large Id Application", [A1, LargeId]);

            _clients["inheriting"] = await CreateKeyedClientAsync(ApplicationA);
            _clients["narrowed subset"] = await CreateKeyedClientAsync(ApplicationA, List(A2));
            _clients["narrowed to the Application list"] = await CreateKeyedClientAsync(
                ApplicationA,
                List(A1, A2, A3)
            );
            _clients["inheriting an empty Application"] = await CreateKeyedClientAsync(emptyApplication);
            _clients["narrowed then emptied"] = await CreateKeyedClientAsync(cascadeApplication, List(C6));
            _clients["large id"] = await CreateKeyedClientAsync(largeIdApplication, List(LargeId));

            // An Application addition reaches the inheriting client's effective list, not its rows,
            // and never a narrowed client; a removal cascades to the narrowed client's rows
            await AddApplicationEducationOrganizationAsync(ApplicationA, A5);
            await RemoveApplicationEducationOrganizationAsync(cascadeApplication, C6);

            foreach (var (name, client) in _clients)
            {
                _reads[name] = await ReadEveryWayAsync(client);
            }
        }

        [TestCase("inheriting", true, new[] { A1, A2, A3, A5 })]
        [TestCase("narrowed subset", false, new[] { A2 })]
        [TestCase("narrowed to the Application list", false, new[] { A1, A2, A3 })]
        [TestCase("inheriting an empty Application", true, new long[0])]
        [TestCase("narrowed then emptied", false, new long[0])]
        [TestCase("large id", false, new[] { LargeId })]
        public void It_returns_the_flag_and_effective_list_from_every_read(
            string client,
            bool inherits,
            long[] ids
        ) =>
            _reads[client]
                .Values.Select(ReadScopeOf)
                .Should()
                .AllSatisfy(read =>
                    read.Should().BeEquivalentTo(new ReadScope(inherits, ids), o => o.WithStrictOrdering())
                )
                .And.HaveCount(3);

        [TestCase("inheriting an empty Application")]
        [TestCase("narrowed then emptied")]
        public void It_serializes_no_education_organizations_as_an_empty_array(string client) =>
            _reads[client]
                .Values.Should()
                .OnlyContain(read =>
                    read["educationOrganizationIds"] is JsonArray
                    && read["educationOrganizationIds"]!.AsArray().Count == 0
                );

        [Test]
        public async Task It_keeps_the_inheriting_client_rows_behind_its_effective_list() =>
            ShouldMatch(
                await ClientScopeAsync(_clients["inheriting"].Id),
                Scope(ApplicationA, true, A1, A2, A3)
            );
    }

    [TestFixture]
    public class Given_api_clients_read_after_moves : ApiClientEducationOrganizationApiTests
    {
        private Dictionary<string, JsonNode> _inheriting = null!;
        private Dictionary<string, JsonNode> _narrowed = null!;

        [SetUp]
        public async Task Setup()
        {
            var inheriting = await CreateKeyedClientAsync(ApplicationA);
            var narrowed = await CreateKeyedClientAsync(ApplicationA, List(A1));
            (await PutClientAsync(inheriting.Id, ApplicationB)).Status.Should().Be(HttpStatusCode.NoContent);
            (await PutClientAsync(narrowed.Id, ApplicationB)).Status.Should().Be(HttpStatusCode.NoContent);

            _inheriting = await ReadEveryWayAsync(inheriting);
            _narrowed = await ReadEveryWayAsync(narrowed);
        }

        [Test]
        public void It_returns_the_destination_list_for_the_inheriting_client() =>
            _inheriting
                .Values.Should()
                .AllSatisfy(read =>
                {
                    read["applicationId"]!.GetValue<int>().Should().Be(ApplicationB);
                    ReadScopeOf(read)
                        .Should()
                        .BeEquivalentTo(new ReadScope(true, [A1, B4]), o => o.WithStrictOrdering());
                });

        [Test]
        public void It_returns_the_kept_list_for_the_narrowed_client() =>
            _narrowed
                .Values.Should()
                .AllSatisfy(read =>
                {
                    read["applicationId"]!.GetValue<int>().Should().Be(ApplicationB);
                    ReadScopeOf(read)
                        .Should()
                        .BeEquivalentTo(new ReadScope(false, [A1]), o => o.WithStrictOrdering());
                });
    }

    [TestFixture]
    public class Given_a_page_of_one_api_client_with_several_education_organizations_and_data_stores
        : ApiClientEducationOrganizationApiTests
    {
        private int[] _dataStores = [];
        private (int Id, string Key) _second;
        private (HttpStatusCode Status, JsonNode Body) _page;

        [SetUp]
        public async Task Setup()
        {
            _dataStores =
            [
                await CreateDataStoreAsync(),
                await CreateDataStoreAsync(),
                await CreateDataStoreAsync(),
            ];
            await CreateKeyedClientAsync(ApplicationA, List(A1, A2), [_dataStores[0], _dataStores[1]]);
            _second = await CreateKeyedClientAsync(
                ApplicationA,
                List(A2, A3),
                [_dataStores[1], _dataStores[2]]
            );
            await CreateKeyedClientAsync(ApplicationA, "", [_dataStores[0], _dataStores[2]]);

            // Clients are paged before their education organizations and data stores join, so
            // the second page of one holds exactly the second client with all of its ids
            _page = await SendAsync(
                HttpMethod.Get,
                $"/v3/apiClients/?offset=1&limit=1&orderBy=id&applicationid={ApplicationA}"
            );
        }

        [Test]
        public void It_returns_exactly_the_second_client()
        {
            _page.Status.Should().Be(HttpStatusCode.OK);
            _page.Body.AsArray().Select(client => client!["id"]!.GetValue<int>()).Should().Equal(_second.Id);
        }

        [Test]
        public void It_returns_each_of_its_education_organizations_once() =>
            ReadScopeOf(_page.Body[0]!)
                .Should()
                .BeEquivalentTo(new ReadScope(false, [A2, A3]), o => o.WithStrictOrdering());

        [Test]
        public void It_returns_each_of_its_data_stores_once() =>
            _page.Body[0]!["dataStoreIds"]!
                .AsArray()
                .Select(id => id!.GetValue<int>())
                .Order()
                .Should()
                .Equal(_dataStores[1], _dataStores[2]);
    }

    [TestFixture]
    public class Given_application_additions_for_narrowed_first_clients
        : ApiClientEducationOrganizationApiTests
    {
        private const long A5 = 255905;
        private int _subsetApplication;
        private int _fullListApplication;
        private (int Id, string ClientUuid) _subsetClient;
        private (int Id, string ClientUuid) _fullListClient;
        private HttpStatusCode _subsetStatus;
        private HttpStatusCode _fullListStatus;

        [SetUp]
        public async Task Setup()
        {
            _subsetApplication = await CreateApplicationAsync("Subset Application", [A1, A2, A3]);
            _fullListApplication = await CreateApplicationAsync("Full List Application", [A1, A2, A3]);
            _subsetClient = await CreateClientAsync(_subsetApplication, List(A2));
            _fullListClient = await CreateClientAsync(_fullListApplication, List(A1, A2, A3));

            (_subsetStatus, _) = await PutApplicationAsync(_subsetApplication, [A1, A2, A3, A5]);
            (_fullListStatus, _) = await PutApplicationAsync(_fullListApplication, [A1, A2, A3, A5]);
        }

        [Test]
        public void It_updates_both_Applications()
        {
            _subsetStatus.Should().Be(HttpStatusCode.NoContent);
            _fullListStatus.Should().Be(HttpStatusCode.NoContent);
        }

        [Test]
        public void It_sends_each_first_client_its_unchanged_narrowed_claim()
        {
            ClaimsSentFor(_subsetClient.ClientUuid).Should().Equal("255902");
            ClaimsSentFor(_fullListClient.ClientUuid).Should().Equal("255901,255902,255903");
        }

        [Test]
        public async Task It_keeps_each_client_narrowed_without_the_added_id()
        {
            ShouldMatch(await ClientScopeAsync(_subsetClient.Id), Scope(_subsetApplication, false, A2));
            ShouldMatch(
                await ClientScopeAsync(_fullListClient.Id),
                Scope(_fullListApplication, false, A1, A2, A3)
            );
            (await ReadByIdAsync(_fullListClient.Id))
                .Should()
                .BeEquivalentTo(new ReadScope(false, [A1, A2, A3]), o => o.WithStrictOrdering());
        }
    }

    [TestFixture]
    public class Given_an_application_removal_that_empties_a_narrowed_first_client
        : ApiClientEducationOrganizationApiTests
    {
        private (int Id, string ClientUuid) _client;
        private HttpStatusCode _status;

        [SetUp]
        public async Task Setup()
        {
            _client = await CreateClientAsync(ApplicationA, List(A2));
            (_status, _) = await PutApplicationAsync(ApplicationA, [A1, A3]);
        }

        [Test]
        public void It_updates_the_Application() => _status.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_sends_an_empty_claim() => ClaimsSentFor(_client.ClientUuid).Should().Equal("");

        [Test]
        public async Task It_leaves_the_client_narrowed_to_nothing_and_approved()
        {
            ShouldMatch(await ClientScopeAsync(_client.Id), Scope(ApplicationA, false));
            var (_, apiClient) = await SendAsync(HttpMethod.Get, $"/v3/apiClients/{_client.Id}");
            ReadScopeOf(apiClient).Should().BeEquivalentTo(new ReadScope(false, []));
            apiClient["isApproved"]!.GetValue<bool>().Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_an_application_addition_for_an_inheriting_first_client_with_lagging_rows
        : ApiClientEducationOrganizationApiTests
    {
        private const long A5 = 255905;
        private const long A6 = 255906;
        private (int Id, string ClientUuid) _client;
        private HttpStatusCode _status;

        [SetUp]
        public async Task Setup()
        {
            _client = await CreateClientAsync(ApplicationA);

            // An earlier addition already left the client's rows behind its Application
            await AddApplicationEducationOrganizationAsync(ApplicationA, A5);
            (_status, _) = await PutApplicationAsync(ApplicationA, [A1, A2, A3, A5, A6]);
        }

        [Test]
        public void It_updates_the_Application() => _status.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_sends_the_requested_list() =>
            ClaimsSentFor(_client.ClientUuid).Should().Equal("255901,255902,255903,255905,255906");

        [Test]
        public async Task It_keeps_the_client_rows_and_returns_the_full_effective_list()
        {
            ShouldMatch(await ClientScopeAsync(_client.Id), Scope(ApplicationA, true, A1, A2, A3));
            (await ReadByIdAsync(_client.Id))
                .Should()
                .BeEquivalentTo(new ReadScope(true, [A1, A2, A3, A5, A6]), o => o.WithStrictOrdering());
        }
    }

    [TestFixture]
    public class Given_an_application_addition_with_a_narrowed_first_client_and_an_inheriting_second_client
        : ApiClientEducationOrganizationApiTests
    {
        private const long A5 = 255905;
        private (int Id, string ClientUuid) _first;
        private (int Id, string ClientUuid) _second;
        private HttpStatusCode _status;

        [SetUp]
        public async Task Setup()
        {
            _first = await CreateClientAsync(ApplicationA, List(A2));
            _second = await CreateClientAsync(ApplicationA);
            (_status, _) = await PutApplicationAsync(ApplicationA, [A1, A2, A3, A5]);
        }

        [Test]
        public void It_updates_the_Application() => _status.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_sends_only_the_first_client_its_narrowed_claim() =>
            ProviderUpdates.Should().Equal(new ProviderUpdate(_first.ClientUuid, "255902"));

        [Test]
        public async Task It_keeps_the_first_client_narrowed() =>
            (await ReadByIdAsync(_first.Id)).Should().BeEquivalentTo(new ReadScope(false, [A2]));

        [Test]
        public async Task It_reports_the_second_client_inheriting_the_new_list() =>
            (await ReadByIdAsync(_second.Id))
                .Should()
                .BeEquivalentTo(new ReadScope(true, [A1, A2, A3, A5]), o => o.WithStrictOrdering());
    }

    [TestFixture]
    public class Given_an_application_update_that_fails_after_the_narrowed_first_client_provider_update
        : ApiClientEducationOrganizationApiTests
    {
        private const long A5 = 255905;
        private (int Id, string ClientUuid) _client;
        private (HttpStatusCode Status, JsonNode Body) _response;

        [SetUp]
        public async Task Setup()
        {
            _client = await CreateClientAsync(ApplicationA, List(A2));

            // Application names are checked only by the save, after the provider update
            _response = await PutApplicationAsync(
                ApplicationA,
                [A1, A2, A3, A5],
                await ApplicationNameAsync(ApplicationB)
            );
        }

        [Test]
        public void It_reports_the_duplicate_name() => ShouldBeDataValidation(_response, "ApplicationName");

        [Test]
        public void It_restores_the_original_narrowed_claim() =>
            ClaimsSentFor(_client.ClientUuid).Should().Equal("255902", "255902");

        [Test]
        public async Task It_leaves_the_Application_and_the_client_scope()
        {
            (await ApplicationEducationOrganizationIdsAsync(ApplicationA)).Should().Equal(A1, A2, A3);
            ShouldMatch(await ClientScopeAsync(_client.Id), Scope(ApplicationA, false, A2));
        }
    }
}
