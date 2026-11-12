// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
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
/// Drives the Application and API client endpoints through the HTTP pipeline against the
/// integration database, with real repositories and lock manager. Only the identity provider is
/// faked, so each test can prove which provider calls a zero-client Application does or does not
/// make.
/// </summary>
public abstract class ApplicationZeroClientApiTests : DatabaseTestBase
{
    protected const string RotatedSecret = "rotated-secret-value";

    private WebApplicationFactory<Program> _factory = null!;

    protected IIdentityProviderRepository IdentityProvider { get; private set; } = null!;

    [SetUp]
    public void StartHost()
    {
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
            .ReturnsLazily(() => new ClientCreateResult.Success(Guid.NewGuid()));
        A.CallTo(() => IdentityProvider.DeleteClientAsync(A<string>.Ignored))
            .Returns(new ClientDeleteResult.Success());
        A.CallTo(() => IdentityProvider.ResetCredentialsAsync(A<string>.Ignored))
            .Returns(new ClientResetResult.Success(RotatedSecret));

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
                services.AddTransient(_ => IdentityProvider);
            });
        });
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

    protected Task<(HttpStatusCode Status, JsonNode Body)> Send(HttpMethod method, string path) =>
        SendRequest(new HttpRequestMessage(method, path));

    protected Task<(HttpStatusCode Status, JsonNode Body)> Send(
        HttpMethod method,
        string path,
        object body
    ) => SendRequest(new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) });

    /// <summary>Sends the request; an empty response body is returned as an empty object.</summary>
    private async Task<(HttpStatusCode Status, JsonNode Body)> SendRequest(HttpRequestMessage request)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage disposableRequest = request;
        request.Headers.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        using HttpResponseMessage response = await client.SendAsync(request);
        string content = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, content.Length == 0 ? new JsonObject() : JsonNode.Parse(content)!);
    }

    /// <summary>
    /// Posts a resource and returns its id, read from the body when the endpoint returns one and
    /// otherwise from the Location header (vendor creation returns no body).
    /// </summary>
    protected async Task<int> Create(string path, object body)
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        using HttpResponseMessage response = await client.PostAsync(path, JsonContent.Create(body));
        string content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, content);
        return content.Length > 0
            ? JsonNode.Parse(content)!["id"]!.GetValue<int>()
            : int.Parse(response.Headers.Location!.Segments[^1]);
    }

    protected Task<int> CreateVendor() =>
        Create(
            "/v3/vendors/",
            new
            {
                company = $"Zero Client Vendor {Guid.NewGuid():N}",
                contactName = "Test Contact",
                contactEmailAddress = "test@test.test",
                namespacePrefixes = "uri://zero-client",
            }
        );

    protected Task<int> CreateDataStore() =>
        Create(
            "/v3/dataStores/",
            new
            {
                dataStoreType = "Production",
                name = $"ZeroClient-{Guid.NewGuid():N}",
                connectionString = "Server=db;Database=edfi;User Id=edfi;Password=secret",
            }
        );

    protected Task<int> CreateApiClient(int applicationId, string name, bool isApproved, int dataStoreId) =>
        Create(
            "/v3/apiClients/",
            new
            {
                applicationId,
                name,
                isApproved,
                dataStoreIds = new[] { dataStoreId },
            }
        );

    protected async Task<JsonNode> GetApplicationFromCollection(int applicationId)
    {
        var (status, applications) = await Send(HttpMethod.Get, "/v3/applications/?offset=0&limit=25");
        status.Should().Be(HttpStatusCode.OK);
        return applications.AsArray().Single(a => a!["id"]!.GetValue<int>() == applicationId)!;
    }

    protected async Task<JsonNode> GetApplicationFromVendor(int vendorId, int applicationId)
    {
        var (status, applications) = await Send(HttpMethod.Get, $"/v3/vendors/{vendorId}/applications");
        status.Should().Be(HttpStatusCode.OK);
        return applications.AsArray().Single(a => a!["id"]!.GetValue<int>() == applicationId)!;
    }

    protected static int[] DataStoreIds(JsonNode application) =>
        [.. application["dataStoreIds"]!.AsArray().Select(id => id!.GetValue<int>()).Order()];

    protected void AssertNoProviderResetHappened() =>
        A.CallTo(() => IdentityProvider.ResetCredentialsAsync(A<string>.Ignored)).MustNotHaveHappened();

    [TestFixture]
    public class Given_an_application_created_without_default_credentials : ApplicationZeroClientApiTests
    {
        private int _applicationId;
        private (HttpStatusCode Status, JsonNode Body) _created;
        private (HttpStatusCode Status, JsonNode Body) _item;
        private JsonNode _collectionItem = null!;
        private JsonNode _vendorItem = null!;
        private (HttpStatusCode Status, JsonNode Body) _reset;
        private (HttpStatusCode Status, JsonNode Body) _missingReset;
        private (HttpStatusCode Status, JsonNode Body) _delete;
        private (HttpStatusCode Status, JsonNode Body) _itemAfterDelete;

        [SetUp]
        public async Task Setup()
        {
            int vendorId = await CreateVendor();
            _created = await Send(
                HttpMethod.Post,
                "/v3/applications/",
                new
                {
                    applicationName = "Zero Client Application",
                    vendorId,
                    claimSetName = "ZeroClientClaimSet",
                    educationOrganizationIds = new[] { 255901 },
                    createDefaultCredentials = false,
                }
            );
            _applicationId = _created.Body["id"]!.GetValue<int>();

            _item = await Send(HttpMethod.Get, $"/v3/applications/{_applicationId}");
            _collectionItem = await GetApplicationFromCollection(_applicationId);
            _vendorItem = await GetApplicationFromVendor(vendorId, _applicationId);
            _reset = await Send(HttpMethod.Put, $"/v3/applications/{_applicationId}/reset-credential");
            _missingReset = await Send(
                HttpMethod.Put,
                $"/v3/applications/{_applicationId + 100000}/reset-credential"
            );
            _delete = await Send(HttpMethod.Delete, $"/v3/applications/{_applicationId}");
            _itemAfterDelete = await Send(HttpMethod.Get, $"/v3/applications/{_applicationId}");
        }

        [Test]
        public void It_creates_the_application_with_null_credentials()
        {
            _created.Status.Should().Be(HttpStatusCode.Created);
            _created.Body.AsObject().ContainsKey("key").Should().BeTrue();
            _created.Body["key"].Should().BeNull();
            _created.Body.AsObject().ContainsKey("secret").Should().BeTrue();
            _created.Body["secret"].Should().BeNull();
        }

        [Test]
        public void It_creates_no_identity_provider_client() =>
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
                .MustNotHaveHappened();

        [Test]
        public void It_reports_the_application_disabled_with_no_data_stores_by_id()
        {
            _item.Status.Should().Be(HttpStatusCode.OK);
            _item.Body["enabled"]!.GetValue<bool>().Should().BeFalse();
            DataStoreIds(_item.Body).Should().BeEmpty();
            _item.Body["educationOrganizationIds"]!
                .AsArray()
                .Select(id => id!.GetValue<long>())
                .Should()
                .Equal(255901L);
        }

        [Test]
        public void It_reports_the_application_disabled_with_no_data_stores_in_the_collection()
        {
            _collectionItem["enabled"]!.GetValue<bool>().Should().BeFalse();
            DataStoreIds(_collectionItem).Should().BeEmpty();
        }

        [Test]
        public void It_reports_the_application_disabled_with_no_data_stores_in_the_vendor_applications()
        {
            _vendorItem["enabled"]!.GetValue<bool>().Should().BeFalse();
            DataStoreIds(_vendorItem).Should().BeEmpty();
        }

        [Test]
        public void It_rejects_an_application_credential_reset_as_a_conflict()
        {
            _reset.Status.Should().Be(HttpStatusCode.Conflict);
            _reset.Body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:conflict");
            _reset.Body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("The application has no API clients. Create one with POST /v3/apiClients.");
        }

        [Test]
        public void It_returns_not_found_for_a_reset_of_a_missing_application() =>
            _missingReset.Status.Should().Be(HttpStatusCode.NotFound);

        [Test]
        public void It_resets_no_identity_provider_client() => AssertNoProviderResetHappened();

        [Test]
        public void It_deletes_the_application()
        {
            _delete.Status.Should().Be(HttpStatusCode.NoContent);
            _itemAfterDelete.Status.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [TestFixture]
    public class Given_an_application_whose_last_api_client_is_deleted : ApplicationZeroClientApiTests
    {
        private int _applicationId;
        private int _firstDataStoreId;
        private int _secondDataStoreId;
        private (HttpStatusCode Status, JsonNode Body) _withDefaultClient;
        private (HttpStatusCode Status, JsonNode Body) _clientDelete;
        private (HttpStatusCode Status, JsonNode Body) _withNoClients;
        private JsonNode _collectionWithNoClients = null!;
        private JsonNode _vendorWithNoClients = null!;
        private (HttpStatusCode Status, JsonNode Body) _resetWithNoClients;
        private string _replacementKey = "";
        private (HttpStatusCode Status, JsonNode Body) _withReplacementClient;
        private (HttpStatusCode Status, JsonNode Body) _resetWithOneClient;
        private (HttpStatusCode Status, JsonNode Body) _withMixedClients;
        private (HttpStatusCode Status, JsonNode Body) _resetWithTwoClients;
        private (HttpStatusCode Status, JsonNode Body) _applicationDelete;
        private (HttpStatusCode Status, JsonNode Body) _afterApplicationDelete;

        [SetUp]
        public async Task Setup()
        {
            int vendorId = await CreateVendor();
            _firstDataStoreId = await CreateDataStore();
            _secondDataStoreId = await CreateDataStore();

            _applicationId = await Create(
                "/v3/applications/",
                new
                {
                    applicationName = "Last Client Application",
                    vendorId,
                    claimSetName = "LastClientClaimSet",
                    educationOrganizationIds = new[] { 255901 },
                    dataStoreIds = new[] { _firstDataStoreId },
                }
            );
            _withDefaultClient = await Send(HttpMethod.Get, $"/v3/applications/{_applicationId}");

            var (_, clients) = await Send(HttpMethod.Get, "/v3/apiClients/?offset=0&limit=25");
            int defaultClientId = clients
                .AsArray()
                .Single(c => c!["applicationId"]!.GetValue<int>() == _applicationId)!["id"]!.GetValue<int>();

            _clientDelete = await Send(HttpMethod.Delete, $"/v3/apiClients/{defaultClientId}");
            _withNoClients = await Send(HttpMethod.Get, $"/v3/applications/{_applicationId}");
            _collectionWithNoClients = await GetApplicationFromCollection(_applicationId);
            _vendorWithNoClients = await GetApplicationFromVendor(vendorId, _applicationId);
            _resetWithNoClients = await Send(
                HttpMethod.Put,
                $"/v3/applications/{_applicationId}/reset-credential"
            );

            var (replacementStatus, replacement) = await Send(
                HttpMethod.Post,
                "/v3/apiClients/",
                new
                {
                    applicationId = _applicationId,
                    name = "Replacement",
                    isApproved = true,
                    dataStoreIds = new[] { _firstDataStoreId },
                }
            );
            replacementStatus.Should().Be(HttpStatusCode.Created, replacement.ToJsonString());
            _replacementKey = replacement["key"]!.GetValue<string>();
            _withReplacementClient = await Send(HttpMethod.Get, $"/v3/applications/{_applicationId}");
            _resetWithOneClient = await Send(
                HttpMethod.Put,
                $"/v3/applications/{_applicationId}/reset-credential"
            );

            await CreateApiClient(_applicationId, "Unapproved", isApproved: false, _secondDataStoreId);
            _withMixedClients = await Send(HttpMethod.Get, $"/v3/applications/{_applicationId}");
            _resetWithTwoClients = await Send(
                HttpMethod.Put,
                $"/v3/applications/{_applicationId}/reset-credential"
            );

            _applicationDelete = await Send(HttpMethod.Delete, $"/v3/applications/{_applicationId}");
            _afterApplicationDelete = await Send(HttpMethod.Get, $"/v3/applications/{_applicationId}");
        }

        [Test]
        public void It_reports_the_default_client_before_deletion()
        {
            _withDefaultClient.Body["enabled"]!.GetValue<bool>().Should().BeTrue();
            DataStoreIds(_withDefaultClient.Body).Should().Equal(_firstDataStoreId);
        }

        [Test]
        public void It_deletes_the_last_client() =>
            _clientDelete.Status.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_keeps_the_application_readable_as_disabled_with_no_data_stores()
        {
            _withNoClients.Status.Should().Be(HttpStatusCode.OK);
            _withNoClients.Body["applicationName"]!.GetValue<string>().Should().Be("Last Client Application");
            _withNoClients.Body["enabled"]!.GetValue<bool>().Should().BeFalse();
            DataStoreIds(_withNoClients.Body).Should().BeEmpty();
        }

        [Test]
        public void It_reports_the_application_disabled_with_no_data_stores_in_the_collection()
        {
            _collectionWithNoClients["enabled"]!.GetValue<bool>().Should().BeFalse();
            DataStoreIds(_collectionWithNoClients).Should().BeEmpty();
        }

        [Test]
        public void It_reports_the_application_disabled_with_no_data_stores_in_the_vendor_applications()
        {
            _vendorWithNoClients["enabled"]!.GetValue<bool>().Should().BeFalse();
            DataStoreIds(_vendorWithNoClients).Should().BeEmpty();
        }

        [Test]
        public void It_rejects_a_reset_with_no_clients_as_a_conflict()
        {
            _resetWithNoClients.Status.Should().Be(HttpStatusCode.Conflict);
            _resetWithNoClients.Body["type"]!
                .GetValue<string>()
                .Should()
                .Be("urn:ed-fi:management-api:conflict");
        }

        [Test]
        public void It_accepts_a_new_client_and_reports_it()
        {
            _withReplacementClient.Body["enabled"]!.GetValue<bool>().Should().BeTrue();
            DataStoreIds(_withReplacementClient.Body).Should().Equal(_firstDataStoreId);
        }

        [Test]
        public void It_rotates_the_only_client_on_reset()
        {
            _resetWithOneClient.Status.Should().Be(HttpStatusCode.OK);
            _resetWithOneClient.Body["id"]!.GetValue<int>().Should().Be(_applicationId);
            _resetWithOneClient.Body["key"]!.GetValue<string>().Should().Be(_replacementKey);
            _resetWithOneClient.Body["secret"]!.GetValue<string>().Should().Be(RotatedSecret);
        }

        [Test]
        public void It_calls_the_provider_reset_only_for_the_single_client_reset() =>
            A.CallTo(() => IdentityProvider.ResetCredentialsAsync(A<string>.Ignored))
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_reports_mixed_approval_as_disabled_with_the_union_of_data_stores()
        {
            _withMixedClients.Body["enabled"]!.GetValue<bool>().Should().BeFalse();
            DataStoreIds(_withMixedClients.Body)
                .Should()
                .Equal(new[] { _firstDataStoreId, _secondDataStoreId }.Order());
        }

        [Test]
        public void It_rejects_a_reset_with_two_clients_as_a_conflict() =>
            _resetWithTwoClients.Status.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_deletes_the_application()
        {
            _applicationDelete.Status.Should().Be(HttpStatusCode.NoContent);
            _afterApplicationDelete.Status.Should().Be(HttpStatusCode.NotFound);
        }
    }
}
