// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Sockets;
using System.Text;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Code = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;
using Stage = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionStage;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

public class ProjectionServiceTokenProviderTests
{
    private const string Tenant = "Tenant_255901";
    private const string TenantClient = "tenant-client";
    private const string TenantSecret = "tenant-secret-1440";
    private const string SharedClient = "shared-client";
    private const string SharedSecret = "shared-secret-1440";

    private static readonly Uri _tokenUrl = new(
        "https://dms.example.org/api/Tenant_255901/255901/2026/oauth/token"
    );

    private static readonly DateTimeOffset _start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Real time allowed for a step the test has already released through the fake clock, a token or a handler. A
    /// correct provider finishes without waiting; the bound only turns a regression that leaves a call pending into a
    /// failure instead of a hung run.
    /// </summary>
    private static readonly TimeSpan _hangGuard = TimeSpan.FromSeconds(30);

    private static HttpResponseMessage TokenResponse(
        string accessToken = "token-1",
        long expiresIn = 3600,
        string tokenType = "bearer"
    ) =>
        DmsResponses.Text(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{accessToken}}","token_type":"{{tokenType}}","expires_in":{{expiresIn}},"scope":"edfi_admin_api/full_access"}"""
        );

    /// <summary>A handler issuing token-1, token-2, ... in order.</summary>
    private static FakeDmsHandler Issuing(long expiresIn = 3600)
    {
        int issued = 0;
        return FakeDmsHandler.Answering(() =>
            TokenResponse("token-" + Interlocked.Increment(ref issued), expiresIn)
        );
    }

    /// <summary>The client id and secret as the CMS token endpoint reads an HTTP Basic header.</summary>
    private static (string ClientId, string ClientSecret) DecodeBasic(string? authorization)
    {
        authorization.Should().StartWith("Basic ");
        string[] parts = Encoding
            .UTF8.GetString(Convert.FromBase64String(authorization!["Basic ".Length..]))
            .Split(':', 2);
        return (Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]));
    }

    /// <summary>A token provider over a fake DMS and a fake clock.</summary>
    public sealed class Harness
    {
        public Harness(
            FakeDmsHandler handler,
            Action<DmsEducationOrganizationProjectionSettings>? configure = null,
            bool manualTimers = false
        )
        {
            Handler = handler;
            DmsEducationOrganizationProjectionSettings settings = new()
            {
                DmsBaseUrl = "https://dms.example.org/api",
                Credentials = new() { ClientId = SharedClient, ClientSecret = SharedSecret },
            };
            settings.TenantCredentials[Tenant] = new()
            {
                ClientId = TenantClient,
                ClientSecret = TenantSecret,
            };
            configure?.Invoke(settings);
            ManualTimers = manualTimers ? new ManualTimersTimeProvider(Time) : null;
            Provider = new ProjectionServiceTokenProvider(
                new SingleHandlerHttpClientFactory(
                    handler,
                    DmsEducationOrganizationProjectionHttpClient.Name
                ),
                Options.Create(settings),
                ManualTimers ?? (TimeProvider)Time
            );
        }

        public FakeTimeProvider Time { get; } = new(_start);

        public ManualTimersTimeProvider? ManualTimers { get; }

        public FakeDmsHandler Handler { get; }

        public ProjectionServiceTokenProvider Provider { get; }

        public Task<ProjectionServiceTokenResult> GetAsync(
            string? tenant = Tenant,
            Uri? tokenUrl = null,
            DateTimeOffset? deadline = null,
            CancellationToken cancellationToken = default
        ) =>
            Provider.GetTokenAsync(
                tenant,
                tokenUrl ?? _tokenUrl,
                deadline ?? Time.GetUtcNow().AddSeconds(600),
                cancellationToken
            );
    }

    private static ProjectionServiceToken ShouldBeIssued(ProjectionServiceTokenResult result) =>
        result.Should().BeOfType<ProjectionServiceTokenResult.Issued>().Which.Token;

    /// <summary>A Token-stage failure with the code's own category and nothing read.</summary>
    private static void ShouldFailWith(ProjectionServiceTokenResult result, Code code, int? httpStatus)
    {
        EducationOrganizationProjectionFailure failure = result
            .Should()
            .BeOfType<ProjectionServiceTokenResult.Failed>()
            .Which.Failure;
        failure.Code.Should().Be(code);
        failure.Category.Should().Be(EducationOrganizationProjectionFailure.CategoryOf(code));
        failure.Stage.Should().Be(Stage.Token);
        failure.HttpStatus.Should().Be(httpStatus);
        (failure.PagesRead, failure.Restarts).Should().Be((0, 0));
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [TestFixture]
    public class Given_a_token_request
    {
        private Harness _harness = null!;
        private ProjectionServiceToken _token = null!;
        private RecordedRequest _request = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(Issuing());
            _token = ShouldBeIssued(await _harness.GetAsync());
            _request = _harness.Handler.Requests.Single();
        }

        [Test]
        public void It_posts_to_the_resolved_token_url() =>
            (_request.Method, _request.Uri).Should().Be((HttpMethod.Post, _tokenUrl));

        [Test]
        public void It_sends_the_client_credentials_grant_as_a_form() =>
            (_request.ContentType, _request.Body)
                .Should()
                .Be(("application/x-www-form-urlencoded", "grant_type=client_credentials"));

        [Test]
        public void It_asks_for_json() => _request.Accept.Should().Be("application/json");

        [Test]
        public void It_authenticates_with_the_tenant_credential() =>
            DecodeBasic(_request.Authorization).Should().Be((TenantClient, TenantSecret));

        [Test]
        public void It_sends_no_cookie() => _request.HasCookie.Should().BeFalse();

        [Test]
        public void It_returns_the_access_token() => _token.AccessToken.Should().Be("token-1");
    }

    [TestFixture]
    public class Given_credentials_with_reserved_characters
    {
        private const string ClientId = "client:id é+/";
        private const string ClientSecret = "s:e%c+r et/=&";
        private RecordedRequest _request = null!;

        [SetUp]
        public async Task Setup()
        {
            Harness harness = new(
                Issuing(),
                settings => settings.Credentials = new() { ClientId = ClientId, ClientSecret = ClientSecret }
            );
            ShouldBeIssued(await harness.GetAsync(tenant: null));
            _request = harness.Handler.Requests.Single();
        }

        [Test]
        public void It_percent_encodes_each_value_before_base64() =>
            _request
                .Authorization.Should()
                .Be(
                    "Basic "
                        + Convert.ToBase64String(
                            Encoding.UTF8.GetBytes(
                                Uri.EscapeDataString(ClientId) + ":" + Uri.EscapeDataString(ClientSecret)
                            )
                        )
                );

        [Test]
        public void It_round_trips_through_the_cms_token_endpoint_decoding() =>
            DecodeBasic(_request.Authorization).Should().Be((ClientId, ClientSecret));
    }

    [TestFixture(Tenant, TenantClient, TenantSecret, false)]
    [TestFixture("TENANT_255901", TenantClient, TenantSecret, false)]
    [TestFixture("TENANT_255901", TenantClient, TenantSecret, true)]
    [TestFixture("Other", SharedClient, SharedSecret, false)]
    [TestFixture(null, SharedClient, SharedSecret, false)]
    public class Given_a_tenant_to_choose_a_credential_for(
        string? tenant,
        string expectedClient,
        string expectedSecret,
        bool ordinalDictionary
    )
    {
        private RecordedRequest _request = null!;

        [SetUp]
        public async Task Setup()
        {
            Harness harness = new(
                Issuing(),
                settings =>
                {
                    if (ordinalDictionary)
                    {
                        // A dictionary the binder did not create keeps its own comparer.
                        settings.TenantCredentials = new Dictionary<
                            string,
                            DmsEducationOrganizationProjectionCredentials
                        >(settings.TenantCredentials, StringComparer.Ordinal);
                    }
                }
            );
            ShouldBeIssued(await harness.GetAsync(tenant));
            _request = harness.Handler.Requests.Single();
        }

        [Test]
        public void It_uses_the_expected_credential() =>
            DecodeBasic(_request.Authorization).Should().Be((expectedClient, expectedSecret));
    }

    [TestFixture("no shared credential, tenant without an entry")]
    [TestFixture("no shared credential, single-tenant")]
    [TestFixture("incomplete tenant entry")]
    [TestFixture("incomplete shared credential")]
    public class Given_no_usable_credential(string situation)
    {
        private Harness _harness = null!;
        private ProjectionServiceTokenResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(
                Issuing(),
                settings =>
                {
                    switch (situation)
                    {
                        case "incomplete tenant entry":
                            settings.TenantCredentials[Tenant] = new()
                            {
                                ClientId = TenantClient,
                                ClientSecret = " ",
                            };
                            break;
                        case "incomplete shared credential":
                            settings.Credentials = new() { ClientId = SharedClient };
                            break;
                        default:
                            settings.Credentials = null;
                            break;
                    }
                }
            );
            string? tenant = situation switch
            {
                "no shared credential, tenant without an entry" => "Other",
                "incomplete tenant entry" => Tenant,
                _ => null,
            };
            _result = await _harness.GetAsync(tenant);
        }

        [Test]
        public void It_is_not_configured() => ShouldFailWith(_result, Code.NotConfigured, null);

        [Test]
        public void It_sends_no_request() => _harness.Handler.Requests.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_cached_token
    {
        private Harness _harness = null!;
        private ProjectionServiceToken _first = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(
                Issuing(expiresIn: 3600),
                settings => settings.TokenExpirySafetyMarginSeconds = 60
            );
            _first = ShouldBeIssued(await _harness.GetAsync());
        }

        [Test]
        public async Task It_reuses_it_for_another_token_url_of_the_tenant()
        {
            ShouldBeIssued(
                    await _harness.GetAsync(
                        tokenUrl: new Uri("https://dms.example.org/api/Tenant_255901/1/2025/oauth/token")
                    )
                )
                .Should()
                .BeSameAs(_first);
            _harness.Handler.Requests.Should().ContainSingle();
        }

        [Test]
        public async Task It_reuses_it_until_the_safety_margin_before_expiry()
        {
            _harness.Time.Advance(TimeSpan.FromSeconds(3540) - TimeSpan.FromTicks(1));
            ShouldBeIssued(await _harness.GetAsync()).Should().BeSameAs(_first);

            _harness.Time.Advance(TimeSpan.FromTicks(1));
            ShouldBeIssued(await _harness.GetAsync()).AccessToken.Should().Be("token-2");
            _harness.Handler.Requests.Should().HaveCount(2);
        }

        [Test]
        public async Task It_requests_a_new_one_after_invalidation()
        {
            _harness.Provider.Invalidate(Tenant, _first);
            ShouldBeIssued(await _harness.GetAsync()).AccessToken.Should().Be("token-2");
        }

        [Test]
        public async Task It_keeps_it_when_another_token_is_invalidated()
        {
            _harness.Provider.Invalidate(Tenant, new ProjectionServiceToken(_first.AccessToken));
            _harness.Provider.Invalidate("Other", _first);
            ShouldBeIssued(await _harness.GetAsync()).Should().BeSameAs(_first);
            _harness.Handler.Requests.Should().ContainSingle();
        }

        [Test]
        public async Task It_keeps_a_replacement_when_the_old_token_is_invalidated_late()
        {
            _harness.Provider.Invalidate(Tenant, _first);
            ProjectionServiceToken second = ShouldBeIssued(await _harness.GetAsync());
            _harness.Provider.Invalidate(Tenant, _first);
            ShouldBeIssued(await _harness.GetAsync()).Should().BeSameAs(second);
        }
    }

    [TestFixture]
    public class Given_two_tenants_with_the_same_client_id
    {
        private Harness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(
                Issuing(),
                settings =>
                    settings.TenantCredentials["Tenant_2"] = new()
                    {
                        ClientId = TenantClient,
                        ClientSecret = TenantSecret,
                    }
            );
            await _harness.GetAsync(Tenant);
            await _harness.GetAsync("Tenant_2");
            await _harness.GetAsync(Tenant);
            await _harness.GetAsync("Tenant_2");
        }

        [Test]
        public void It_caches_a_token_per_tenant() => _harness.Handler.Requests.Should().HaveCount(2);
    }

    [TestFixture(60, 60)]
    [TestFixture(30, 60)]
    [TestFixture(1, 0)]
    public class Given_a_token_whose_lifetime_does_not_exceed_the_margin(int expiresIn, int margin)
    {
        private Harness _harness = null!;
        private ProjectionServiceToken _first = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(
                Issuing(expiresIn),
                settings => settings.TokenExpirySafetyMarginSeconds = margin
            );
            _first = ShouldBeIssued(await _harness.GetAsync());
            if (margin == 0)
            {
                _harness.Time.Advance(TimeSpan.FromSeconds(expiresIn));
            }
        }

        [Test]
        public void It_returns_the_token() => _first.AccessToken.Should().Be("token-1");

        [Test]
        public async Task It_does_not_reuse_it()
        {
            ShouldBeIssued(await _harness.GetAsync()).AccessToken.Should().Be("token-2");
            _harness.Handler.Requests.Should().HaveCount(2);
        }
    }

    [TestFixture]
    public class Given_no_safety_margin
    {
        [Test]
        public async Task It_reuses_the_token_until_it_expires()
        {
            Harness harness = new(
                Issuing(expiresIn: 100),
                settings => settings.TokenExpirySafetyMarginSeconds = 0
            );
            ProjectionServiceToken first = ShouldBeIssued(await harness.GetAsync());

            harness.Time.Advance(TimeSpan.FromSeconds(100) - TimeSpan.FromTicks(1));
            ShouldBeIssued(await harness.GetAsync()).Should().BeSameAs(first);
            harness.Handler.Requests.Should().ContainSingle();
        }
    }

    [TestFixture]
    public class Given_time_passing_while_the_token_is_requested
    {
        [Test]
        public async Task It_counts_the_lifetime_from_when_the_request_started()
        {
            Harness? harness = null;
            int calls = 0;
            harness = new Harness(
                FakeDmsHandler.Answering(() =>
                    DmsResponses.Stream(
                        HttpStatusCode.OK,
                        new EndOfBodyStream(
                            Encoding.UTF8.GetBytes(
                                $$"""{"access_token":"token-{{Interlocked.Increment(ref calls)}}","token_type":"bearer","expires_in":3600}"""
                            ),
                            () => harness!.Time.Advance(TimeSpan.FromSeconds(10))
                        )
                    )
                ),
                settings => settings.TokenExpirySafetyMarginSeconds = 60
            );
            DateTimeOffset requestStart = harness.Time.GetUtcNow();
            ProjectionServiceToken first = ShouldBeIssued(await harness.GetAsync());

            harness.Time.Advance(
                requestStart.AddSeconds(3540) - harness.Time.GetUtcNow() - TimeSpan.FromTicks(1)
            );
            ShouldBeIssued(await harness.GetAsync()).Should().BeSameAs(first);

            harness.Time.Advance(TimeSpan.FromTicks(1));
            ShouldBeIssued(await harness.GetAsync()).AccessToken.Should().Be("token-2");
        }
    }

    [TestFixture]
    public class Given_a_lifetime_beyond_int_max_seconds
    {
        [Test]
        public async Task It_caches_the_token_without_overflow()
        {
            Harness harness = new(Issuing(expiresIn: long.MaxValue));
            ProjectionServiceToken first = ShouldBeIssued(await harness.GetAsync());

            harness.Time.Advance(TimeSpan.FromDays(3650));
            ShouldBeIssued(await harness.GetAsync()).Should().BeSameAs(first);
            harness.Handler.Requests.Should().ContainSingle();
        }
    }

    [TestFixture]
    public class Given_concurrent_callers_for_one_tenant
    {
        private Harness _harness = null!;
        private int _requestsBeforeAnswer;
        private ProjectionServiceTokenResult[] _results = null!;

        [SetUp]
        public async Task Setup()
        {
            TaskCompletionSource<HttpResponseMessage> answer = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _harness = new Harness(new FakeDmsHandler((_, _) => answer.Task));
            Task<ProjectionServiceTokenResult>[] calls =
            [
                .. Enumerable.Range(0, 5).Select(_ => _harness.GetAsync()),
            ];
            await _harness.Handler.RequestReceived.Task.WaitAsync(_hangGuard);
            _requestsBeforeAnswer = _harness.Handler.Requests.Count;

            answer.SetResult(TokenResponse());
            _results = await Task.WhenAll(calls).WaitAsync(_hangGuard);
        }

        [Test]
        public void It_sends_one_request_while_one_is_outstanding() => _requestsBeforeAnswer.Should().Be(1);

        [Test]
        public void It_sends_one_request_in_all() => _harness.Handler.Requests.Should().ContainSingle();

        [Test]
        public void It_gives_every_caller_the_same_token() =>
            _results.Select(ShouldBeIssued).Distinct().Should().ContainSingle();
    }

    [TestFixture]
    public class Given_concurrent_callers_for_two_tenants
    {
        [Test]
        public async Task It_does_not_make_one_wait_for_the_other()
        {
            TaskCompletionSource answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Harness harness = new(
                new FakeDmsHandler(
                    async (_, _) =>
                    {
                        await answer.Task;
                        return TokenResponse();
                    }
                )
            );
            Task<ProjectionServiceTokenResult> first = harness.GetAsync(Tenant);
            Task<ProjectionServiceTokenResult> second = harness.GetAsync("Other");

            // Both requests reach DMS while neither has an answer.
            await harness.Handler.WaitForRequestsAsync(2).WaitAsync(_hangGuard);
            answer.SetResult();
            (await Task.WhenAll(first, second).WaitAsync(_hangGuard))
                .Should()
                .AllSatisfy(result => ShouldBeIssued(result));
        }
    }

    [TestFixture]
    public class Given_the_outstanding_request_fails
    {
        private Harness _harness = null!;
        private ProjectionServiceTokenResult _first = null!;
        private ProjectionServiceTokenResult _waiter = null!;

        [SetUp]
        public async Task Setup()
        {
            TaskCompletionSource<HttpResponseMessage> answer = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            int calls = 0;
            _harness = new Harness(
                new FakeDmsHandler(
                    (_, _) =>
                        Interlocked.Increment(ref calls) == 1 ? answer.Task : Task.FromResult(TokenResponse())
                )
            );
            Task<ProjectionServiceTokenResult> first = _harness.GetAsync();
            Task<ProjectionServiceTokenResult> waiter = _harness.GetAsync();
            await _harness.Handler.RequestReceived.Task.WaitAsync(_hangGuard);

            answer.SetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            _first = await first.WaitAsync(_hangGuard);
            _waiter = await waiter.WaitAsync(_hangGuard);
        }

        [Test]
        public void It_reports_the_failure_to_its_caller() =>
            ShouldFailWith(_first, Code.ServiceUnavailable, 503);

        [Test]
        public void It_lets_the_waiter_make_its_own_request()
        {
            ShouldBeIssued(_waiter);
            _harness.Handler.Requests.Should().HaveCount(2);
        }
    }

    [TestFixture]
    public class Given_a_waiting_caller_that_cancels
    {
        private Harness _harness = null!;
        private CancellationTokenSource _caller = null!;
        private Exception? _exception;
        private ProjectionServiceTokenResult _first = null!;

        [SetUp]
        public async Task Setup()
        {
            _caller = new CancellationTokenSource();
            TaskCompletionSource<HttpResponseMessage> answer = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _harness = new Harness(new FakeDmsHandler((_, _) => answer.Task));
            Task<ProjectionServiceTokenResult> first = _harness.GetAsync();
            Task<ProjectionServiceTokenResult> waiter = _harness.GetAsync(cancellationToken: _caller.Token);
            await _harness.Handler.RequestReceived.Task.WaitAsync(_hangGuard);

            await _caller.CancelAsync();
            _exception = await CaptureAsync(() => waiter.WaitAsync(_hangGuard));
            answer.SetResult(TokenResponse());
            _first = await first.WaitAsync(_hangGuard);
        }

        [TearDown]
        public void TearDown() => _caller.Dispose();

        [Test]
        public void It_throws_with_the_waiter_token() =>
            _exception
                .Should()
                .BeAssignableTo<OperationCanceledException>()
                .Which.CancellationToken.Should()
                .Be(_caller.Token);

        [Test]
        public void It_leaves_the_outstanding_request_to_finish()
        {
            ShouldBeIssued(_first);
            _harness.Handler.Requests.Should().ContainSingle();
        }
    }

    [TestFixture(false)]
    [TestFixture(true)]
    public class Given_a_waiting_caller_whose_read_deadline_passes(bool deadlineTimerRuns)
    {
        private Harness _harness = null!;
        private ProjectionServiceTokenResult _waiter = null!;
        private ProjectionServiceTokenResult _first = null!;

        [SetUp]
        public async Task Setup()
        {
            TaskCompletionSource<HttpResponseMessage> answer = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            // With manual timers the waiter's deadline timer never runs: the waiter learns of its deadline only when
            // its turn comes, from the clock.
            _harness = new Harness(
                new FakeDmsHandler((_, _) => answer.Task),
                manualTimers: !deadlineTimerRuns
            );
            Task<ProjectionServiceTokenResult> first = _harness.GetAsync();
            Task<ProjectionServiceTokenResult> waiter = _harness.GetAsync(
                deadline: _harness.Time.GetUtcNow().AddSeconds(5)
            );
            await _harness.Handler.RequestReceived.Task.WaitAsync(_hangGuard);

            _harness.Time.Advance(TimeSpan.FromSeconds(5));
            if (deadlineTimerRuns)
            {
                _waiter = await waiter.WaitAsync(_hangGuard);
            }
            answer.SetResult(TokenResponse());
            _first = await first.WaitAsync(_hangGuard);
            if (!deadlineTimerRuns)
            {
                _waiter = await waiter.WaitAsync(_hangGuard);
            }
        }

        [Test]
        public void It_times_out() => ShouldFailWith(_waiter, Code.Timeout, null);

        [Test]
        public void It_does_not_send_a_request_or_take_the_new_token()
        {
            ShouldBeIssued(_first);
            _harness.Handler.Requests.Should().ContainSingle();
        }
    }

    [TestFixture(0)]
    [TestFixture(-1)]
    public class Given_a_read_deadline_already_reached(int secondsFromNow)
    {
        private Harness _harness = null!;
        private ProjectionServiceTokenResult _cold = null!;
        private ProjectionServiceTokenResult _cached = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(Issuing());
            _cold = await _harness.GetAsync(
                tenant: "Other",
                deadline: _harness.Time.GetUtcNow().AddSeconds(secondsFromNow)
            );
            ShouldBeIssued(await _harness.GetAsync());
            _cached = await _harness.GetAsync(deadline: _harness.Time.GetUtcNow().AddSeconds(secondsFromNow));
        }

        [Test]
        public void It_times_out_without_a_cached_token() => ShouldFailWith(_cold, Code.Timeout, null);

        [Test]
        public void It_times_out_with_a_cached_token() => ShouldFailWith(_cached, Code.Timeout, null);

        [Test]
        public void It_sends_no_request_beyond_the_one_that_filled_the_cache() =>
            _harness.Handler.Requests.Should().ContainSingle();
    }

    [TestFixture]
    public class Given_a_cached_token_a_reached_read_deadline_and_a_cancelled_caller
    {
        [Test]
        public async Task It_reports_caller_cancellation_first()
        {
            using CancellationTokenSource caller = new();
            Harness harness = new(Issuing());
            ShouldBeIssued(await harness.GetAsync());
            await caller.CancelAsync();

            Exception? exception = await CaptureAsync(() =>
                harness.GetAsync(deadline: harness.Time.GetUtcNow(), cancellationToken: caller.Token)
            );

            exception
                .Should()
                .BeAssignableTo<OperationCanceledException>()
                .Which.CancellationToken.Should()
                .Be(caller.Token);
            harness.Handler.Requests.Should().ContainSingle();
        }
    }

    [TestFixture(400, "application/json", """{"error":"invalid_client"}""", Code.TokenRejected)]
    [TestFixture(
        400,
        "application/problem+json",
        """{"type":"urn:ed-fi:api:bad-request"}""",
        Code.TokenRejected
    )]
    [TestFixture(401, "application/json", """{"error":"unauthorized_client"}""", Code.TokenRejected)]
    [TestFixture(401, "", "", Code.TokenRejected)]
    [TestFixture(403, "", "", Code.UnexpectedResponse)]
    [TestFixture(
        404,
        "application/problem+json",
        """{"type":"urn:ed-fi:api:not-found"}""",
        Code.UnexpectedResponse
    )]
    [TestFixture(405, "", "", Code.UnexpectedResponse)]
    [TestFixture(415, "", "", Code.UnexpectedResponse)]
    [TestFixture(201, "application/json", "{}", Code.UnexpectedResponse)]
    [TestFixture(204, "", "", Code.UnexpectedResponse)]
    [TestFixture(301, "", "", Code.DiscoveryInvalid)]
    [TestFixture(307, "", "", Code.DiscoveryInvalid)]
    [TestFixture(429, "", "", Code.RateLimited)]
    [TestFixture(
        500,
        "application/problem+json",
        """{"type":"urn:ed-fi:api:system:configuration:security"}""",
        Code.Forbidden
    )]
    [TestFixture(
        500,
        "application/json",
        """{"type":"urn:ed-fi:api:system:configuration:security"}""",
        Code.ServiceUnavailable
    )]
    [TestFixture(500, "application/json", """{"message":"m","traceId":"t"}""", Code.ServiceUnavailable)]
    [TestFixture(502, "", "", Code.ServiceUnavailable)]
    [TestFixture(503, "", "", Code.ServiceUnavailable)]
    public class Given_a_token_status_other_than_200(int status, string mediaType, string body, Code expected)
    {
        private Harness _harness = null!;
        private ProjectionServiceTokenResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(
                FakeDmsHandler.Answering(() =>
                    mediaType.Length == 0
                        ? new HttpResponseMessage((HttpStatusCode)status)
                        : DmsResponses.Text((HttpStatusCode)status, body, mediaType)
                )
            );
            _result = await _harness.GetAsync();
        }

        [Test]
        public void It_is_classified() => ShouldFailWith(_result, expected, status);

        [Test]
        public async Task It_caches_nothing()
        {
            await _harness.GetAsync();
            _harness.Handler.Requests.Should().HaveCount(2);
        }
    }

    [TestFixture("not json")]
    [TestFixture("")]
    [TestFixture("[]")]
    [TestFixture("""{}""")]
    [TestFixture("""{"token_type":"bearer","expires_in":3600}""")]
    [TestFixture("""{"access_token":"","token_type":"bearer","expires_in":3600}""")]
    [TestFixture("""{"access_token":7,"token_type":"bearer","expires_in":3600}""")]
    [TestFixture("""{"access_token":null,"token_type":"bearer","expires_in":3600}""")]
    [TestFixture("""{"access_token":"a b","token_type":"bearer","expires_in":3600}""")]
    [TestFixture("""{"access_token":"abc\r\nInjected: 1","token_type":"bearer","expires_in":3600}""")]
    [TestFixture("""{"access_token":"=abc","token_type":"bearer","expires_in":3600}""")]
    [TestFixture("""{"access_token":"ab=c","token_type":"bearer","expires_in":3600}""")]
    [TestFixture("""{"access_token":"abc,def","token_type":"bearer","expires_in":3600}""")]
    [TestFixture("""{"access_token":"t","expires_in":3600}""")]
    [TestFixture("""{"access_token":"t","token_type":"mac","expires_in":3600}""")]
    [TestFixture("""{"access_token":"t","token_type":"bearer ","expires_in":3600}""")]
    [TestFixture("""{"access_token":"t","token_type":7,"expires_in":3600}""")]
    [TestFixture("""{"access_token":"t","token_type":"bearer"}""")]
    [TestFixture("""{"access_token":"t","token_type":"bearer","expires_in":0}""")]
    [TestFixture("""{"access_token":"t","token_type":"bearer","expires_in":-1}""")]
    [TestFixture("""{"access_token":"t","token_type":"bearer","expires_in":"3600"}""")]
    [TestFixture("""{"access_token":"t","token_type":"bearer","expires_in":3600.5}""")]
    [TestFixture("""{"access_token":"t","token_type":"bearer","expires_in":1e400}""")]
    [TestFixture("""{"access_token":"t","token_type":"bearer","expires_in":null}""")]
    [TestFixture("""{"access_token":"t","token_type":"bearer","expires_in":3600""")]
    public class Given_a_malformed_token_response(string body)
    {
        private Harness _harness = null!;
        private ProjectionServiceTokenResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(
                FakeDmsHandler.Answering(() => DmsResponses.Text(HttpStatusCode.OK, body))
            );
            _result = await _harness.GetAsync();
        }

        [Test]
        public void It_is_malformed() => ShouldFailWith(_result, Code.MalformedResponse, 200);
    }

    [TestFixture]
    public class Given_a_token_response_with_invalid_utf8
    {
        [Test]
        public async Task It_is_malformed()
        {
            byte[] body =
            [
                .. "{\"access_token\":\"t"u8,
                0xFF,
                .. "\",\"token_type\":\"bearer\",\"expires_in\":3600}"u8,
            ];
            Harness harness = new(
                FakeDmsHandler.Answering(() =>
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                )
            );
            ShouldFailWith(await harness.GetAsync(), Code.MalformedResponse, 200);
        }
    }

    [TestFixture(true)]
    [TestFixture(false)]
    public class Given_token_responses_around_the_size_limit(bool declaredLength)
    {
        private static byte[] Padded(int length)
        {
            string prefix = """{"access_token":"t","token_type":"bearer","expires_in":3600,"pad":" """;
            return Encoding.UTF8.GetBytes(
                prefix + new string('a', length - Encoding.UTF8.GetByteCount(prefix) - 2) + "\"}"
            );
        }

        private HttpResponseMessage Response(byte[] body) =>
            declaredLength
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : DmsResponses.Stream(HttpStatusCode.OK, new UnknownLengthStream(body));

        [Test]
        public async Task It_reads_a_response_of_exactly_the_limit()
        {
            byte[] body = Padded(ProjectionServiceTokenProvider.MaxTokenResponseBytes);
            body.Length.Should().Be(ProjectionServiceTokenProvider.MaxTokenResponseBytes);
            ShouldBeIssued(await new Harness(FakeDmsHandler.Answering(() => Response(body))).GetAsync());
        }

        [Test]
        public async Task It_refuses_a_response_over_the_limit()
        {
            byte[] body = Padded(ProjectionServiceTokenProvider.MaxTokenResponseBytes + 1);
            ShouldFailWith(
                await new Harness(FakeDmsHandler.Answering(() => Response(body))).GetAsync(),
                Code.MalformedResponse,
                200
            );
        }
    }

    [TestFixture("""{"access_token":"t","token_type":"Bearer","expires_in":3600}""", "t")]
    [TestFixture("""{"access_token":"t","token_type":"BEARER","expires_in":3600}""", "t")]
    [TestFixture(
        """{"access_token":"aZ09-._~+/==","token_type":"bearer","expires_in":1,"refresh_token":null,"x":{"y":[1]}}""",
        "aZ09-._~+/=="
    )]
    [TestFixture(
        """{"expires_in":3600,"token_type":"bearer","access_token":"eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln"}""",
        "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln"
    )]
    public class Given_a_valid_token_response(string body, string expectedToken)
    {
        [Test]
        public async Task It_returns_the_access_token() =>
            ShouldBeIssued(
                await new Harness(
                    FakeDmsHandler.Answering(() => DmsResponses.Text(HttpStatusCode.OK, body))
                ).GetAsync()
            )
                .AccessToken.Should()
                .Be(expectedToken);
    }

    [TestFixture("http")]
    [TestFixture("io")]
    [TestFixture("socket")]
    public class Given_a_transport_failure(string kind)
    {
        [Test]
        public async Task It_is_a_network_error()
        {
            Exception failure = kind switch
            {
                "http" => new HttpRequestException("connect " + TenantSecret),
                "io" => new IOException("reset " + TenantSecret),
                _ => new SocketException((int)SocketError.ConnectionRefused),
            };
            ShouldFailWith(
                await new Harness(new FakeDmsHandler((_, _) => throw failure)).GetAsync(),
                Code.NetworkError,
                null
            );
        }
    }

    [TestFixture(30, null)]
    [TestFixture(30, 5)]
    public class Given_a_token_request_that_does_not_answer_in_time(
        int tokenTimeout,
        int? readDeadlineSeconds
    )
    {
        private bool _completedEarly;
        private ProjectionServiceTokenResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            Harness harness = new(
                FakeDmsHandler.NeverAnswering(),
                settings => settings.TokenRequestTimeoutSeconds = tokenTimeout
            );
            Task<ProjectionServiceTokenResult> pending = harness.GetAsync(
                deadline: readDeadlineSeconds is { } seconds
                    ? harness.Time.GetUtcNow().AddSeconds(seconds)
                    : null
            );
            await harness.Handler.RequestReceived.Task.WaitAsync(_hangGuard);

            TimeSpan limit = TimeSpan.FromSeconds(readDeadlineSeconds ?? tokenTimeout);
            harness.Time.Advance(limit - TimeSpan.FromTicks(1));
            _completedEarly = pending.IsCompleted;
            harness.Time.Advance(TimeSpan.FromTicks(1));
            _result = await pending.WaitAsync(_hangGuard);
        }

        [Test]
        public void It_waits_for_the_earlier_limit() => _completedEarly.Should().BeFalse();

        [Test]
        public void It_times_out() => ShouldFailWith(_result, Code.Timeout, null);
    }

    [TestFixture]
    public class Given_caller_cancellation_while_the_request_is_outstanding
    {
        [Test]
        public async Task It_throws_with_the_caller_token()
        {
            using CancellationTokenSource caller = new();
            Harness harness = new(FakeDmsHandler.NeverAnswering());
            Task<ProjectionServiceTokenResult> pending = harness.GetAsync(cancellationToken: caller.Token);
            await harness.Handler.RequestReceived.Task.WaitAsync(_hangGuard);

            await caller.CancelAsync();
            Exception? exception = await CaptureAsync(() => pending.WaitAsync(_hangGuard));

            exception
                .Should()
                .BeAssignableTo<OperationCanceledException>()
                .Which.CancellationToken.Should()
                .Be(caller.Token);
        }
    }

    [TestFixture]
    public class Given_a_body_that_ends_normally_after_the_caller_cancels
    {
        private Harness _harness = null!;
        private CancellationTokenSource _caller = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _caller = new CancellationTokenSource();
            int calls = 0;
            _harness = new Harness(
                FakeDmsHandler.Answering(() =>
                    Interlocked.Increment(ref calls) == 1
                        ? DmsResponses.Stream(
                            HttpStatusCode.OK,
                            new EndOfBodyStream(
                                Encoding.UTF8.GetBytes(
                                    """{"access_token":"late","token_type":"bearer","expires_in":3600}"""
                                ),
                                () => _caller.Cancel()
                            )
                        )
                        : TokenResponse("token-2")
                )
            );
            _exception = await CaptureAsync(() =>
                _harness.GetAsync(cancellationToken: _caller.Token).WaitAsync(_hangGuard)
            );
        }

        [TearDown]
        public void TearDown() => _caller.Dispose();

        [Test]
        public void It_throws_with_the_caller_token() =>
            _exception
                .Should()
                .BeAssignableTo<OperationCanceledException>()
                .Which.CancellationToken.Should()
                .Be(_caller.Token);

        [Test]
        public async Task It_does_not_cache_the_token() =>
            ShouldBeIssued(await _harness.GetAsync()).AccessToken.Should().Be("token-2");
    }

    [TestFixture("token timeout")]
    [TestFixture("read deadline")]
    public class Given_a_body_that_ends_normally_after_the_timeout(string limit)
    {
        private Harness _harness = null!;
        private ProjectionServiceTokenResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            bool readDeadline = limit == "read deadline";
            int calls = 0;
            _harness = new Harness(
                FakeDmsHandler.Answering(() =>
                    Interlocked.Increment(ref calls) == 1
                        ? DmsResponses.Stream(
                            HttpStatusCode.OK,
                            new EndOfBodyStream(
                                Encoding.UTF8.GetBytes(
                                    """{"access_token":"late","token_type":"bearer","expires_in":3600}"""
                                ),
                                () => _harness.Time.Advance(TimeSpan.FromSeconds(readDeadline ? 5 : 30))
                            )
                        )
                        : TokenResponse("token-2")
                ),
                settings => settings.TokenRequestTimeoutSeconds = 30
            );
            _result = await _harness
                .GetAsync(deadline: readDeadline ? _harness.Time.GetUtcNow().AddSeconds(5) : null)
                .WaitAsync(_hangGuard);
        }

        [Test]
        public void It_times_out() => ShouldFailWith(_result, Code.Timeout, null);

        [Test]
        public async Task It_does_not_cache_the_token() =>
            ShouldBeIssued(await _harness.GetAsync()).AccessToken.Should().Be("token-2");
    }

    [TestFixture(false)]
    [TestFixture(true)]
    public class Given_a_transport_failure_after_the_timeout(bool callerCancelsToo)
    {
        [Test]
        public async Task It_reports_the_timeout_unless_the_caller_cancelled()
        {
            using CancellationTokenSource caller = new();
            Harness? harness = null;
            harness = new Harness(
                FakeDmsHandler.Answering(() =>
                    DmsResponses.Stream(
                        HttpStatusCode.OK,
                        new StalledStream(
                            new IOException("reset"),
                            () =>
                            {
                                harness!.Time.Advance(TimeSpan.FromSeconds(30));
                                if (callerCancelsToo)
                                {
                                    caller.Cancel();
                                }
                            }
                        )
                    )
                )
            );

            Exception? exception = null;
            ProjectionServiceTokenResult? result = null;
            try
            {
                result = await harness.GetAsync(cancellationToken: caller.Token).WaitAsync(_hangGuard);
            }
            catch (Exception caught)
            {
                exception = caught;
            }

            if (callerCancelsToo)
            {
                exception
                    .Should()
                    .BeAssignableTo<OperationCanceledException>()
                    .Which.CancellationToken.Should()
                    .Be(caller.Token);
            }
            else
            {
                ShouldFailWith(result!, Code.Timeout, null);
            }
        }
    }

    [TestFixture]
    public class Given_the_production_registration_and_hostile_values
    {
        // DMS text is carried, sanitized, in the failure's problem fields; it does not repeat a credential, so the
        // credential checks below cover only what the provider itself could add.
        private const string HostileJson = "FORGED\\r\\nline <b>";
        private RecordingLoggerProvider _recorder = null!;
        private List<ProjectionServiceTokenResult> _results = null!;
        private string _basicParameter = null!;

        [SetUp]
        public async Task Setup()
        {
            _recorder = new RecordingLoggerProvider();
            _basicParameter = ProjectionServiceTokenProvider.BasicParameter(TenantClient, TenantSecret);
            int call = 0;
            Func<HttpResponseMessage> respond = () =>
                Interlocked.Increment(ref call) switch
                {
                    1 => DmsResponses.Text(
                        HttpStatusCode.Unauthorized,
                        $$"""{"type":"{{HostileJson}}","detail":"{{HostileJson}}","correlationId":"c-1"}""",
                        "application/problem+json"
                    ),
                    2 => throw new HttpRequestException(
                        "send failed " + TenantSecret,
                        new IOException("Basic " + _basicParameter)
                    ),
                    _ => TokenResponse("SECRET-ACCESS-TOKEN-1440"),
                };

            ServiceCollection services = new();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(_recorder));
            services.AddDmsEducationOrganizationProjectionReader(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["DmsEducationOrganizationProjectionSettings:DmsBaseUrl"] =
                                "https://dms.example.org/api",
                            [
                                $"DmsEducationOrganizationProjectionSettings:TenantCredentials:{Tenant}:ClientId"
                            ] = TenantClient,
                            [
                                $"DmsEducationOrganizationProjectionSettings:TenantCredentials:{Tenant}:ClientSecret"
                            ] = TenantSecret,
                        }
                    )
                    .Build()
            );
            services
                .AddHttpClient(DmsEducationOrganizationProjectionHttpClient.Name)
                .ConfigurePrimaryHttpMessageHandler(() =>
                    new FakeDmsHandler((_, _) => Task.FromResult(respond()))
                );

            await using ServiceProvider provider = services.BuildServiceProvider();
            IProjectionServiceTokenProvider tokens =
                provider.GetRequiredService<IProjectionServiceTokenProvider>();
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(10);
            _results = [];
            for (int i = 0; i < 3; i++)
            {
                _results.Add(await tokens.GetTokenAsync(Tenant, _tokenUrl, deadline, CancellationToken.None));
            }
        }

        [TearDown]
        public void TearDown() => _recorder.Dispose();

        [Test]
        public void It_classifies_each_request() =>
            _results
                .Select(result =>
                    result is ProjectionServiceTokenResult.Failed failed
                        ? failed.Failure.Code.ToString()
                        : "Issued"
                )
                .Should()
                .Equal("TokenRejected", "NetworkError", "Issued");

        [Test]
        public void It_logs_only_the_projection_http_records() =>
            _recorder
                .Records.Select(record => record.Category)
                .Should()
                .OnlyContain(category => category == typeof(ProjectionHttpClientLogger).FullName);

        [Test]
        public void It_logs_no_credential_token_or_dms_value_at_any_level() =>
            _recorder
                .Records.Should()
                .NotContain(record =>
                    record.AllText.Contains(TenantSecret)
                    || record.AllText.Contains(TenantClient)
                    || record.AllText.Contains(_basicParameter)
                    || record.AllText.Contains("Basic")
                    || record.AllText.Contains("SECRET-ACCESS-TOKEN-1440")
                    || record.AllText.Contains("FORGED")
                );

        [Test]
        public void It_attaches_no_exception_to_any_record() =>
            _recorder
                .Records.Select(record => record.Exception)
                .Should()
                .AllSatisfy(exception => exception.Should().BeNull());

        [Test]
        public void It_keeps_the_token_and_credentials_out_of_every_result_text() =>
            _results
                .Select(result => result.ToString())
                .Should()
                .NotContain(text =>
                    text.Contains("SECRET-ACCESS-TOKEN-1440")
                    || text.Contains(TenantSecret)
                    || text.Contains(_basicParameter)
                    || text.Contains('\n')
                );

        [Test]
        public void It_returns_the_token_itself_to_the_caller() =>
            ShouldBeIssued(_results[2]).AccessToken.Should().Be("SECRET-ACCESS-TOKEN-1440");
    }
}
