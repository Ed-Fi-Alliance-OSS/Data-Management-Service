// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// With the host's real rate limiter allowing one request per sixty-second window, an identity
/// request after the permit is spent answers <c>429</c> with <c>no-store</c> and the existing
/// too-many-requests problem type, and the provider is never invoked.
/// </summary>
/// <remarks>
/// The permit is spent on <c>GET /metadata</c>, not <c>/health</c>, whose database check would dial.
/// The limiter partitions by host, and every in-process request shares one partition. The negative
/// control is a host at the default limit: the same identity request reaches the provider.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheRealRateLimiterRejectsAnIdentityRequest
{
    private const string Create = """{ "LastSurname": "Rivera" }""";

    private IdentityHttpRun? _limited;
    private IdentityHttpRun? _unlimited;
    private IdentityHttpOutcome _permitSpender = null!;
    private IdentityHttpOutcome _rejected = null!;
    private IdentityHttpOutcome _unlimitedCreate = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _limited = await IdentityHttpRun.StartAsync(
            settings: new Dictionary<string, string>
            {
                ["RateLimit:PermitLimit"] = "1",
                ["RateLimit:Window"] = "60",
                ["RateLimit:QueueLimit"] = "0",
            }
        );
        _permitSpender = await _limited.GetAsync("http://localhost/metadata");
        _rejected = await _limited.PostAsync("identities", Create);

        _unlimited = await IdentityHttpRun.StartAsync();
        _ = await _unlimited.GetAsync("http://localhost/metadata");
        _unlimitedCreate = await _unlimited.PostAsync("identities", Create);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_limited is not null)
        {
            await _limited.DisposeAsync();
        }

        if (_unlimited is not null)
        {
            await _unlimited.DisposeAsync();
        }
    }

    [Test]
    public void It_spent_the_permit_on_a_metadata_request_that_was_answered()
    {
        _permitSpender.Status.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_answers_the_identity_request_with_too_many_requests()
    {
        _rejected.Status.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public void It_marks_the_rejection_no_store_exactly_once()
    {
        _rejected.HeaderValues("Cache-Control").Should().ContainSingle().Which.Should().Contain("no-store");
    }

    [Test]
    public void It_reports_the_existing_too_many_requests_problem_type()
    {
        _rejected.ProblemType.Should().Be("urn:ed-fi:api:too-many-requests");
    }

    [Test]
    public void It_tells_the_client_when_to_retry_within_the_sixty_second_window()
    {
        string retryAfter = _rejected.HeaderValues("Retry-After").Should().ContainSingle().Which;

        int.Parse(retryAfter, CultureInfo.InvariantCulture).Should().BeInRange(1, 60);
    }

    [Test]
    public void It_never_invoked_the_provider()
    {
        _limited!.Stub.InvocationCount().Should().Be(0);
    }

    [Test]
    public void It_invokes_the_provider_for_the_same_request_without_the_low_limit()
    {
        _unlimitedCreate.Status.Should().Be(HttpStatusCode.OK);
        _unlimited!.Stub.InvocationCount("create").Should().Be(1);
    }
}
