// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.E2E.Keycloak;

// Offline tests (no Keycloak needed) for the revocation observer's public-client cleanup (DMS-1327
// P3.2): every created resource is tracked at once, each deletion is attempted even when another
// fails, an undeleted resource is retried at feature teardown and a deleted one never is, and the
// setup failure survives its cleanup without secrets in what is reported.

/// <summary>
/// A scripted Keycloak admin API and realm. Created resources get sequential ids; a DELETE fails
/// with 500 while its path has failures left, and a DELETE of something already deleted answers 404
/// as Keycloak does, so a needless retry surfaces as a failure.
/// </summary>
public sealed class ScriptedKeycloak : HttpMessageHandler
{
    public const string BaseUrl = "http://keycloak.test";
    public const string AdminToken = "SECRET-ADMIN-TOKEN-SENTINEL";
    public const string PublicAccessToken = "SECRET-PUBLIC-TOKEN-SENTINEL";

    private readonly HashSet<string> _deleted = [];
    private int _clients;
    private int _users;

    public bool FailPasswordGrant { get; init; }

    /// <summary>DELETE path → number of 500 answers before it succeeds.</summary>
    public Dictionary<string, int> DeleteFailures { get; } = [];

    public List<string> Requests { get; } = [];

    public List<string> CreatedClientPaths { get; } = [];

    public List<string> CreatedUserPaths { get; } = [];

    /// <summary>Every password the fixture sent in a user representation.</summary>
    public List<string> UserPasswords { get; } = [];

    public int RequestCount(string method, string path) =>
        Requests.Count(request => request == $"{method} {path}");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        string path = request.RequestUri!.AbsolutePath;
        string method = request.Method.Method;
        Requests.Add($"{method} {path}");
        string body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return (method, path) switch
        {
            ("POST", "/realms/master/protocol/openid-connect/token") => Json(
                HttpStatusCode.OK,
                new JsonObject { ["access_token"] = AdminToken }
            ),
            ("POST", "/realms/edfi/protocol/openid-connect/token") => FailPasswordGrant
                ? Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_grant" })
                : Json(HttpStatusCode.OK, new JsonObject { ["access_token"] = PublicAccessToken }),
            ("POST", "/admin/realms/edfi/clients") => Created(
                CreatedClientPaths,
                $"{path}/client-{++_clients}"
            ),
            ("POST", "/admin/realms/edfi/client-scopes") => Created([], $"{path}/scope-1"),
            ("POST", "/admin/realms/edfi/users") => CreatedUser(body, $"{path}/user-{++_users}"),
            ("PUT", _) => new HttpResponseMessage(HttpStatusCode.NoContent),
            ("DELETE", _) => Delete(path),
            _ => new HttpResponseMessage(HttpStatusCode.NotImplemented),
        };
    }

    private HttpResponseMessage CreatedUser(string body, string location)
    {
        foreach (JsonNode? credential in JsonNode.Parse(body)!["credentials"]!.AsArray())
        {
            UserPasswords.Add(credential!["value"]!.GetValue<string>());
        }

        return Created(CreatedUserPaths, location);
    }

    private static HttpResponseMessage Created(List<string> created, string location)
    {
        created.Add(location);
        HttpResponseMessage response = new(HttpStatusCode.Created);
        response.Headers.Location = new Uri($"{BaseUrl}{location}");
        return response;
    }

    private HttpResponseMessage Delete(string path)
    {
        if (DeleteFailures.TryGetValue(path, out int remaining) && remaining > 0)
        {
            DeleteFailures[path] = remaining - 1;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }

        return _deleted.Add(path)
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}

public abstract class ObserverCleanupFixture
{
    protected ScriptedKeycloak Keycloak { get; private set; } = null!;

    protected StringWriter Diagnostics { get; private set; } = null!;

    protected KeycloakRevocationObserver Observer { get; private set; } = null!;

    /// <summary>Requests sent before feature teardown started.</summary>
    protected List<string> RequestsBeforeTeardown { get; private set; } = [];

    /// <summary>Requests sent by feature teardown.</summary>
    protected List<string> TeardownRequests { get; private set; } = [];

    protected Exception? TeardownFailure { get; private set; }

    protected abstract ScriptedKeycloak CreateKeycloak();

    /// <summary>The scenario part, run between provisioning and feature teardown.</summary>
    protected abstract Task ActAsync();

    [SetUp]
    public async Task Setup()
    {
        Keycloak = CreateKeycloak();
        Diagnostics = new StringWriter();
        Observer = await KeycloakRevocationObserver.CreateAsync(
            [],
            new KeycloakCharacterizationApi(
                ScriptedKeycloak.BaseUrl,
                "edfi",
                "master",
                new SensitiveValueRegistry(),
                Keycloak
            ),
            Diagnostics
        );

        await ActAsync();

        RequestsBeforeTeardown = [.. Keycloak.Requests];
        TeardownFailure = await Caught.ExceptionAsync(async () => await Observer.DisposeAsync());
        TeardownRequests = Keycloak.Requests.Skip(RequestsBeforeTeardown.Count).ToList();
    }

    [TearDown]
    public void TearDown()
    {
        Diagnostics?.Dispose();
        Keycloak?.Dispose();
    }

    // The observer client is the first client created; the scenario's public client the second.
    protected string PublicClientPath => Keycloak.CreatedClientPaths[1];

    protected string UserPath => Keycloak.CreatedUserPaths.Single();

    protected static string Delete(string path) => $"DELETE {path}";
}

[TestFixture]
public class Given_a_public_client_setup_that_fails_after_creating_the_user_and_whose_user_deletion_fails
    : ObserverCleanupFixture
{
    private Exception? _setupFailure;

    protected override ScriptedKeycloak CreateKeycloak()
    {
        ScriptedKeycloak keycloak = new() { FailPasswordGrant = true };
        keycloak.DeleteFailures["/admin/realms/edfi/users/user-1"] = 1;
        return keycloak;
    }

    protected override async Task ActAsync() =>
        _setupFailure = await Caught.ExceptionAsync(() => Observer.CreatePublicClientAsync());

    [Test]
    public void It_reports_the_original_setup_failure() =>
        _setupFailure
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .StartWith("The token request answered HTTP 400");

    [Test]
    public void It_attempts_the_user_deletion_during_setup_cleanup() =>
        RequestsBeforeTeardown.Should().Contain(Delete(UserPath));

    [Test]
    public void It_still_deletes_the_client_although_the_user_deletion_failed() =>
        RequestsBeforeTeardown.Should().Contain(Delete(PublicClientPath));

    [Test]
    public void It_reports_the_resource_left_for_teardown_by_exception_type_only()
    {
        string report = Diagnostics.ToString();
        report.Should().Contain("left 1 resource(s) for feature teardown");
        report.Should().Contain(typeof(InvalidOperationException).FullName);
        report.Should().NotContain("HTTP 500");
    }

    [Test]
    public void It_keeps_secrets_out_of_the_setup_failure_and_the_report()
    {
        string reported = Diagnostics + ExceptionText.Of(_setupFailure!);
        reported.Should().NotContain(ScriptedKeycloak.AdminToken);
        Keycloak.UserPasswords.Should().ContainSingle();
        reported.Should().NotContain(Keycloak.UserPasswords[0]);
    }

    [Test]
    public void It_retries_the_unresolved_user_at_feature_teardown() =>
        TeardownRequests.Should().Contain(Delete(UserPath));

    [Test]
    public void It_does_not_retry_the_already_deleted_client() =>
        TeardownRequests.Should().NotContain(Delete(PublicClientPath));

    [Test]
    public void It_completes_feature_teardown_without_a_failure() => TeardownFailure.Should().BeNull();
}

[TestFixture]
public class Given_a_scenario_public_client_whose_user_and_client_deletions_both_fail_once
    : ObserverCleanupFixture
{
    private Exception? _scenarioCleanupFailure;

    protected override ScriptedKeycloak CreateKeycloak()
    {
        ScriptedKeycloak keycloak = new();
        keycloak.DeleteFailures["/admin/realms/edfi/users/user-1"] = 1;
        keycloak.DeleteFailures["/admin/realms/edfi/clients/client-2"] = 1;
        return keycloak;
    }

    protected override async Task ActAsync()
    {
        KeycloakPublicClient publicClient = await Observer.CreatePublicClientAsync();
        _scenarioCleanupFailure = await Caught.ExceptionAsync(() =>
            Observer.DeletePublicClientAsync(publicClient)
        );
    }

    [Test]
    public void It_attempts_the_client_deletion_although_the_user_deletion_failed()
    {
        RequestsBeforeTeardown.Should().Contain(Delete(UserPath));
        RequestsBeforeTeardown.Should().Contain(Delete(PublicClientPath));
    }

    [Test]
    public void It_reports_both_failures_from_the_scenario_cleanup() =>
        _scenarioCleanupFailure
            .Should()
            .BeOfType<AggregateException>()
            .Which.InnerExceptions.Should()
            .HaveCount(2);

    [Test]
    public void It_retries_both_resources_at_feature_teardown()
    {
        TeardownRequests.Should().Contain(Delete(UserPath));
        TeardownRequests.Should().Contain(Delete(PublicClientPath));
    }

    [Test]
    public void It_completes_feature_teardown_without_a_failure() => TeardownFailure.Should().BeNull();
}

[TestFixture]
public class Given_a_scenario_public_client_deleted_successfully : ObserverCleanupFixture
{
    protected override ScriptedKeycloak CreateKeycloak() => new();

    protected override async Task ActAsync() =>
        await Observer.DeletePublicClientAsync(await Observer.CreatePublicClientAsync());

    [Test]
    public void It_deletes_the_user_and_the_client_once_each()
    {
        Keycloak.RequestCount("DELETE", UserPath).Should().Be(1);
        Keycloak.RequestCount("DELETE", PublicClientPath).Should().Be(1);
    }

    [Test]
    public void It_leaves_nothing_of_the_public_client_for_feature_teardown()
    {
        TeardownRequests.Should().NotContain(Delete(UserPath));
        TeardownRequests.Should().NotContain(Delete(PublicClientPath));
    }

    [Test]
    public void It_completes_feature_teardown_without_a_failure() => TeardownFailure.Should().BeNull();
}
