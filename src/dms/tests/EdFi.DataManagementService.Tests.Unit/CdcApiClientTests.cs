// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.SchemaTools.Cdc;
using EdFi.DataManagementService.Tests.E2E;
using EdFi.DataManagementService.Tests.E2E.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_CdcApiClient
{
    private RecordingHandler _cms = null!;
    private RecordingHandler _dms = null!;
    private CdcApiClient _client = null!;
    private ManualTimeProvider _time = null!;
    private int _tokenRequests;
    private Func<HttpResponseMessage> _tokenResponse = null!;
    private static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-000000000123");

    [SetUp]
    public async Task Setup()
    {
        _time = new();
        _tokenRequests = 0;
        _tokenResponse = () =>
            Response(
                HttpStatusCode.OK,
                $$"""{"access_token":"dms-token-{{++_tokenRequests}}","expires_in":1800}"""
            );
        _cms = new(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/connect/register" => Response(HttpStatusCode.OK, "{}"),
                "/connect/token" => Response(
                    HttpStatusCode.OK,
                    """{"access_token":"cms-token","expires_in":1800}"""
                ),
                "/v3/dataStores/73" => Response(HttpStatusCode.OK, """{"id":73}"""),
                "/v3/vendors" => Created("http://container-cms:8080/v3/vendors/8"),
                "/v3/vendors/8" => Response(HttpStatusCode.OK, """{"id":8}"""),
                "/v3/applications" => Response(
                    HttpStatusCode.Created,
                    """{"key":"app-key","secret":"app-secret"}"""
                ),
                _ => Response(HttpStatusCode.NotFound, "unexpected CMS route"),
            }
        );
        _dms = new(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("oauth/token", StringComparison.Ordinal))
            {
                return _tokenResponse();
            }
            if (request.Method == HttpMethod.Post)
            {
                return Created($"http://container-dms:8080/data/ed-fi/students/{Id:D}");
            }
            return request.Method == HttpMethod.Get
                ? Response(HttpStatusCode.OK, """{"firstName":"Read"}""")
                : Response(HttpStatusCode.NoContent, "");
        });
        _client = new(
            new Uri("http://localhost:18093/context"),
            new Uri("http://localhost:18094/"),
            73,
            _dms,
            _cms,
            _time
        );
        await _client.AuthenticateAsync(CancellationToken.None);
    }

    [TearDown]
    public void Teardown() => _client.Dispose();

    [Test]
    public void It_uses_only_resolved_endpoints_and_the_admitted_data_store()
    {
        var defaults = AppSettings.Create(new ConfigurationBuilder().Build());
        defaults.DatabaseEngine.Should().Be("postgresql");
        defaults.DmsPort.Should().NotBe("18093");
        defaults.ConfigServicePort.Should().NotBe("18094");
        _cms.Requests.Select(r => r.Uri.Port).Should().OnlyContain(p => p == 18094);
        _dms.Requests.Single().Uri.Should().Be(new Uri("http://localhost:18093/context/oauth/token"));
        _cms.Requests.Select(r => r.Uri.AbsolutePath)
            .Should()
            .Equal(
                "/connect/register",
                "/connect/token",
                "/v3/dataStores/73",
                "/v3/vendors",
                "/v3/vendors/8",
                "/v3/applications"
            );
        var application = JsonNode.Parse(_cms.Requests[^1].Body)!;
        application["dataStoreIds"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(73);
        application["vendorId"]!.GetValue<int>().Should().Be(8);
        _cms.Requests.Skip(2).Select(r => r.Authorization).Should().OnlyContain(a => a == "Bearer cms-token");
        _dms.Requests.Single().Authorization.Should().StartWith("Basic ");
    }

    [TestCase("Student", "students")]
    [TestCase("SchoolTypeDescriptor", "schoolTypeDescriptors")]
    public async Task It_mutates_and_reads_both_resources_through_authenticated_http(
        string resourceName,
        string route
    )
    {
        CdcApiResource resource = Enum.Parse<CdcApiResource>(resourceName);
        JsonObject body =
            resource == CdcApiResource.Student
                ? CdcApiClient.NewStudent("Created")
                : CdcApiClient.NewSchoolTypeDescriptor("Created");
        (await _client.PostAsync(resource, body, CancellationToken.None)).Should().Be(Id);
        await _client.PutAsync(resource, Id, body, CancellationToken.None);
        (await _client.GetAsync(resource, Id, CancellationToken.None))["firstName"]!
            .GetValue<string>()
            .Should()
            .Be("Read");
        await _client.DeleteAsync(resource, Id, CancellationToken.None);
        var requests = _dms.Requests.Skip(1).ToArray();
        requests
            .Select(r => r.Method)
            .Should()
            .Equal(HttpMethod.Post, HttpMethod.Put, HttpMethod.Get, HttpMethod.Delete);
        requests
            .Select(r => r.Uri.AbsolutePath)
            .Should()
            .Equal(
                $"/context/data/ed-fi/{route}",
                $"/context/data/ed-fi/{route}/{Id:D}",
                $"/context/data/ed-fi/{route}/{Id:D}",
                $"/context/data/ed-fi/{route}/{Id:D}"
            );
        requests.Select(r => r.Authorization).Should().OnlyContain(a => a == "Bearer dms-token-1");
        requests.Select(r => r.CanCancel).Should().OnlyContain(c => c);
        JsonNode.Parse(requests[1].Body)!["id"]!.GetValue<string>().Should().Be(Id.ToString("D"));
        body.ContainsKey("id").Should().BeFalse();
    }

    [Test]
    public void It_creates_unique_resource_identities()
    {
        var students = Enumerable
            .Range(0, 5)
            .Select(_ => CdcApiClient.NewStudent("Test")["studentUniqueId"]!.GetValue<string>())
            .ToArray();
        students.Should().OnlyHaveUniqueItems().And.OnlyContain(id => id.Length == 32);
        Enumerable
            .Range(0, 5)
            .Select(_ => CdcApiClient.NewSchoolTypeDescriptor("Test")["codeValue"]!.GetValue<string>())
            .Should()
            .OnlyHaveUniqueItems();
    }

    [Test]
    public async Task It_omits_server_payloads_from_http_failure_diagnostics()
    {
        _dms.Respond = _ => Response(HttpStatusCode.BadRequest, "sensitive-body-and-credentials");
        Func<Task> act = () =>
            _client.PostAsync(
                CdcApiResource.Student,
                CdcApiClient.NewStudent("Sensitive name"),
                CancellationToken.None
            );
        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Be("DMS create failed: HTTP 400; expected 201.");
    }

    [Test]
    public async Task It_honors_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Func<Task> act = () => _client.GetAsync(CdcApiResource.Student, Id, cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestCase("POST")]
    [TestCase("PUT")]
    [TestCase("GET")]
    [TestCase("DELETE")]
    public async Task It_renews_before_each_resource_method_after_expiration(string method)
    {
        _time.Advance(TimeSpan.FromMinutes(31));
        await SendResourceAsync(method, CancellationToken.None);

        _dms.Requests.Select(r => r.Uri.AbsolutePath)
            .Take(2)
            .Should()
            .Equal("/context/oauth/token", "/context/oauth/token");
        _dms.Requests[1].Authorization.Should().Be(_dms.Requests[0].Authorization);
        _dms.Requests[1].Body.Should().Be("grant_type=client_credentials");
        _dms.Requests[1].CanCancel.Should().BeTrue();
        _dms.Requests[1].Uri.Should().Be(_dms.Requests[0].Uri);
        _dms.Requests.Should().HaveCount(3);
        _dms.Requests[^1].Method.Method.Should().Be(method);
        _dms.Requests[^1].Authorization.Should().Be("Bearer dms-token-2");
        _cms.Requests.Should().HaveCount(6);
    }

    [Test]
    public async Task It_reuses_tokens_until_the_renewal_boundary_and_tracks_replacement_expiration()
    {
        _time.Advance(TimeSpan.FromSeconds(1769));
        await SendResourceAsync("GET", CancellationToken.None);
        _dms.Requests[^1].Authorization.Should().Be("Bearer dms-token-1");
        _tokenRequests.Should().Be(1);

        _time.Advance(TimeSpan.FromSeconds(1));
        await SendResourceAsync("POST", CancellationToken.None);
        _dms.Requests[^1].Authorization.Should().Be("Bearer dms-token-2");
        _tokenRequests.Should().Be(2);

        _time.Advance(TimeSpan.FromSeconds(1769));
        await SendResourceAsync("PUT", CancellationToken.None);
        _dms.Requests[^1].Authorization.Should().Be("Bearer dms-token-2");
        _tokenRequests.Should().Be(2);

        _time.Advance(TimeSpan.FromSeconds(1));
        await SendResourceAsync("DELETE", CancellationToken.None);
        _dms.Requests[^1].Authorization.Should().Be("Bearer dms-token-3");
        _tokenRequests.Should().Be(3);
    }

    [TestCase("POST")]
    [TestCase("PUT")]
    [TestCase("GET")]
    [TestCase("DELETE")]
    public async Task It_does_not_send_the_pending_resource_request_when_renewal_fails(string method)
    {
        _time.Advance(TimeSpan.FromMinutes(31));
        _tokenResponse = () => Response(HttpStatusCode.Unauthorized, "app-secret sensitive-token-body");
        Func<Task> act = () => SendResourceAsync(method, CancellationToken.None);
        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Be("DMS token acquisition failed.");
        failure.Which.InnerException.Should().BeNull();
        _dms.Requests.Should().HaveCount(2).And.OnlyContain(r => r.Uri.AbsolutePath.EndsWith("oauth/token"));
        _cms.Requests.Should().HaveCount(6);
    }

    [TestCase("POST")]
    [TestCase("PUT")]
    [TestCase("GET")]
    [TestCase("DELETE")]
    public async Task It_does_not_send_the_pending_resource_request_when_renewal_is_cancelled(string method)
    {
        _time.Advance(TimeSpan.FromMinutes(31));
        using var cancellation = new CancellationTokenSource();
        _tokenResponse = () =>
        {
            cancellation.Cancel();
            return Response(HttpStatusCode.OK, """{"access_token":"private-token","expires_in":1800}""");
        };
        Func<Task> act = () => SendResourceAsync(method, cancellation.Token);
        var failure = await act.Should().ThrowAsync<OperationCanceledException>();
        failure.Which.Message.Should().Be("DMS token acquisition cancelled or timed out.");
        failure.Which.InnerException.Should().BeNull();
        _dms.Requests.Should().HaveCount(2).And.OnlyContain(r => r.Uri.AbsolutePath.EndsWith("oauth/token"));
    }

    [TestCase("invalid-sensitive-token-body")]
    [TestCase("[]")]
    [TestCase("{}")]
    [TestCase("{\"access_token\":\"private-token\"}")]
    [TestCase("{\"access_token\":\"private-token\",\"expires_in\":0}")]
    [TestCase("{\"access_token\":\"private-token\",\"expires_in\":-1}")]
    [TestCase("{\"access_token\":\"private-token\",\"expires_in\":\"private-value\"}")]
    [TestCase("{\"access_token\":\"private-token\",\"expires_in\":1.5}")]
    [TestCase("{\"access_token\":\"private-token\",\"expires_in\":999999999999}")]
    [TestCase("{\"access_token\":null,\"expires_in\":1800}")]
    [TestCase("{\"access_token\":42,\"expires_in\":1800}")]
    [TestCase("{\"access_token\":\" \",\"expires_in\":1800}")]
    public async Task It_rejects_invalid_token_metadata_without_exposing_the_response(string body)
    {
        _time.Advance(TimeSpan.FromMinutes(31));
        _tokenResponse = () => Response(HttpStatusCode.OK, body);
        Func<Task> act = () => SendResourceAsync("POST", CancellationToken.None);
        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Be("DMS token acquisition failed.");
        failure.Which.InnerException.Should().BeNull();
        _dms.Requests.Should().HaveCount(2);
    }

    [Test]
    public async Task It_sanitizes_token_transport_failures()
    {
        _time.Advance(TimeSpan.FromMinutes(31));
        _tokenResponse = () => throw new HttpRequestException("private-token app-secret");
        Func<Task> act = () => SendResourceAsync("POST", CancellationToken.None);
        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Be("DMS token acquisition failed.");
        failure.Which.InnerException.Should().BeNull();
        _dms.Requests.Should().HaveCount(2);
    }

    [Test]
    public async Task It_counts_token_request_latency_against_expiration()
    {
        _time.Advance(TimeSpan.FromMinutes(31));
        _tokenResponse = () =>
        {
            _time.Advance(TimeSpan.FromSeconds(6));
            return Response(HttpStatusCode.OK, """{"access_token":"private-token","expires_in":10}""");
        };
        Func<Task> act = () => SendResourceAsync("POST", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        _dms.Requests.Should().HaveCount(2);
    }

    [TestCase("POST")]
    [TestCase("PUT")]
    [TestCase("GET")]
    [TestCase("DELETE")]
    public async Task It_does_not_retry_a_transmitted_domain_request_after_unauthorized(string method)
    {
        _dms.Respond = _ => Response(HttpStatusCode.Unauthorized, "private-response");
        Func<Task> act = () => SendResourceAsync(method, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        _dms.Requests.Should().HaveCount(2);
        _dms.Requests[^1].Method.Method.Should().Be(method);
    }

    private async Task SendResourceAsync(string method, CancellationToken token)
    {
        switch (method)
        {
            case "POST":
                await _client.PostAsync(CdcApiResource.Student, CdcApiClient.NewStudent("Created"), token);
                break;
            case "PUT":
                await _client.PutAsync(CdcApiResource.Student, Id, CdcApiClient.NewStudent("Updated"), token);
                break;
            case "GET":
                await _client.GetAsync(CdcApiResource.Student, Id, token);
                break;
            case "DELETE":
                await _client.DeleteAsync(CdcApiResource.Student, Id, token);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(method));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private static HttpResponseMessage Created(string location)
    {
        var response = Response(HttpStatusCode.Created, "{}");
        response.Headers.Location = new(location);
        return response;
    }

    private sealed record Request(
        Uri Uri,
        HttpMethod Method,
        string Body,
        string Authorization,
        bool CanCancel
    );

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(
                new(
                    request.RequestUri!,
                    request.Method,
                    request.Content is not null
                        ? await request.Content.ReadAsStringAsync(cancellationToken)
                        : "",
                    request.Headers.Authorization?.ToString() ?? "",
                    cancellationToken.CanBeCanceled
                )
            );
            return Respond(request);
        }
    }
}

[TestFixture]
public class Given_CdcDocumentObserver
{
    private CdcDocumentObserver _observer = null!;
    private readonly List<DbConnection> _connections = [];
    private readonly InvalidOperationException _openFailure = new("Observation open boundary");

    [SetUp]
    public void Setup() => _connections.Clear();

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public void It_retains_the_admitted_provider_and_database_on_fresh_connections(CdcProvider provider)
    {
        AppSettings.Create(new ConfigurationBuilder().Build()).DatabaseEngine.Should().Be("postgresql");
        var settings = new ConfigurationBuilder().AddInMemoryCollection().Build();
        using var settingsOwner = (IDisposable)settings;
        settings["Cdc:Provider"] = provider == CdcProvider.Postgresql ? "postgresql" : "sqlserver";
        settings["Cdc:SetupConnectionString"] =
            provider == CdcProvider.Postgresql
                ? "Host=localhost;Port=18543;Database=admitted_pg;Username=postgres;Password=private;"
                : "Server=localhost,18433;Database=admitted_mssql;User Id=sa;Password=private;";
        var config = new CdcCommandConfiguration(settings);
        _observer = new(provider, config.CreateConnection);
        using var first = _observer.CreateConnection();
        using var second = _observer.CreateConnection();
        second.Should().NotBeSameAs(first);
        foreach (var connection in new[] { first, second })
        {
            if (provider == CdcProvider.Postgresql)
            {
                connection.Should().BeOfType<NpgsqlConnection>();
                connection.Database.Should().Be("admitted_pg");
                var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
                builder.Port.Should().Be(18543);
                builder.PersistSecurityInfo.Should().BeFalse();
                (builder.Password == "private").Should().BeTrue();
            }
            else
            {
                connection.Should().BeOfType<SqlConnection>();
                connection.Database.Should().Be("admitted_mssql");
                connection.DataSource.Should().Be("localhost,18433");
                var builder = new SqlConnectionStringBuilder(connection.ConnectionString);
                builder.PersistSecurityInfo.Should().BeFalse();
                (builder.Password == "private").Should().BeTrue();
            }
        }
    }

    [TestCase(CdcProvider.Postgresql, "source")]
    [TestCase(CdcProvider.Postgresql, "work")]
    [TestCase(CdcProvider.Postgresql, "cache")]
    [TestCase(CdcProvider.Postgresql, "sequence")]
    [TestCase(CdcProvider.SqlServer, "source")]
    [TestCase(CdcProvider.SqlServer, "work")]
    [TestCase(CdcProvider.SqlServer, "cache")]
    [TestCase(CdcProvider.SqlServer, "sequence")]
    public async Task It_opens_and_disposes_a_fresh_factory_connection_for_each_observation(
        CdcProvider provider,
        string observation
    )
    {
        // Stop at OpenAsync: exercise the observer handoff without a live driver/authentication harness.
        _observer = new(
            provider,
            () =>
            {
                var connection = A.Fake<DbConnection>();
                A.CallTo(() => connection.ConnectionString)
                    .Throws(new InvalidOperationException("Do not copy connection strings"));
                A.CallTo(() => connection.OpenAsync(A<CancellationToken>._)).ThrowsAsync(_openFailure);
                _connections.Add(connection);
                return connection;
            }
        );
        using var cancellation = new CancellationTokenSource();
        Func<Task> read = observation switch
        {
            "source" => () => _observer.ReadSourceAsync(Guid.NewGuid(), cancellation.Token),
            "work" => () => _observer.ReadWorkAsync(73, cancellation.Token),
            "cache" => () => _observer.ReadCacheAsync(73, cancellation.Token),
            "sequence" => () => _observer.ReadChangeVersionSequenceAsync(cancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(observation)),
        };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            (await read.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should()
                .BeSameAs(_openFailure);
        }
        _connections.Should().HaveCount(2);
        _connections[1].Should().NotBeSameAs(_connections[0]);
        foreach (var connection in _connections)
        {
            A.CallTo(() =>
                    connection.OpenAsync(A<CancellationToken>.That.Matches(token => token.CanBeCanceled))
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => connection.DisposeAsync()).MustHaveHappenedOnceExactly();
            A.CallTo(() => connection.ConnectionString).MustNotHaveHappened();
        }
    }

    [Test]
    public void It_reads_provider_timestamp_shapes_as_utc()
    {
        var expected = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        CdcDocumentObserver
            .ReadTimestamp(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Unspecified))
            .Should()
            .Be(expected);
        CdcDocumentObserver.ReadTimestamp(expected.UtcDateTime).Should().Be(expected);
        CdcDocumentObserver.ReadTimestamp(expected.ToOffset(TimeSpan.FromHours(-5))).Should().Be(expected);
    }
}
