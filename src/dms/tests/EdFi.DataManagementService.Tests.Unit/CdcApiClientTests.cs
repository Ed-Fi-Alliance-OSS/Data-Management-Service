// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Tests.E2E;
using EdFi.DataManagementService.Tests.E2E.Cdc;
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
    private static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-000000000123");

    [SetUp]
    public async Task Setup()
    {
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
                return Response(HttpStatusCode.OK, """{"access_token":"dms-token"}""");
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
            _cms
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
        requests.Select(r => r.Authorization).Should().OnlyContain(a => a == "Bearer dms-token");
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
        using var client = new CdcApiClient(
            new("http://localhost:18093/"),
            new("http://localhost:18094/"),
            73,
            new RecordingHandler(_ => Response(HttpStatusCode.BadRequest, "sensitive-body-and-credentials")),
            new RecordingHandler(_ => Response(HttpStatusCode.OK, "{}"))
        );
        Func<Task> act = () =>
            client.PostAsync(
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
            return respond(request);
        }
    }
}

[TestFixture]
public class Given_CdcDocumentObserver
{
    [Test]
    public void It_selects_the_handoff_mssql_database_despite_postgresql_defaults()
    {
        AppSettings.Create(new ConfigurationBuilder().Build()).DatabaseEngine.Should().Be("postgresql");
        var observer = new CdcDocumentObserver(
            CdcProvider.SqlServer,
            "Server=localhost,18433;Database=admitted_mssql;User Id=sa;Password=private;"
        );
        using var connection = observer.CreateConnection();
        connection.Should().BeOfType<SqlConnection>();
        connection.Database.Should().Be("admitted_mssql");
        connection.DataSource.Should().Be("localhost,18433");
    }

    [Test]
    public void It_selects_the_explicit_postgresql_database()
    {
        var observer = new CdcDocumentObserver(
            CdcProvider.Postgresql,
            "Host=localhost;Port=18543;Database=admitted_pg;Username=postgres;Password=private;"
        );
        using var connection = observer.CreateConnection();
        connection.Should().BeOfType<NpgsqlConnection>();
        connection.Database.Should().Be("admitted_pg");
        new NpgsqlConnectionStringBuilder(connection.ConnectionString).Port.Should().Be(18543);
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
