// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Keycloak;
using FluentAssertions;
using Flurl.Http;
using Flurl.Http.Testing;
using Keycloak.Net.Models.Clients;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// The client-type read used by the Keycloak revocation gate (DMS-1327 D-11.2, D-11.4). The bounded
/// wait is exercised with a fake clock; the read itself runs the real Keycloak.Net package against
/// Flurl's test transport, so the claims about the package (A-01) are checked against the shipped
/// assembly rather than assumed.
/// </summary>
public class KeycloakClientFacadeTests
{
    private const string Sentinel = "SECRET-EX-SENTINEL";
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [TestFixture]
    public class Given_RunBoundedAsync_with_an_operation_that_completes : KeycloakClientFacadeTests
    {
        private string _result = "";
        private CancellationToken _operationToken;

        [SetUp]
        public async Task Setup()
        {
            _result = await KeycloakClientFacade.RunBoundedAsync(
                token =>
                {
                    _operationToken = token;
                    return Task.FromResult("answer");
                },
                _timeout,
                new FakeTimeProvider(),
                CancellationToken.None
            );
        }

        [Test]
        public void It_returns_the_operation_result() => _result.Should().Be("answer");

        [Test]
        public void It_hands_the_operation_a_cancellable_token() =>
            _operationToken.CanBeCanceled.Should().BeTrue();
    }

    [TestFixture]
    public class Given_RunBoundedAsync_when_the_timeout_expires : KeycloakClientFacadeTests
    {
        private Exception? _exception;
        private CancellationToken _operationToken;

        [SetUp]
        public async Task Setup()
        {
            var clock = new FakeTimeProvider();
            var operationStarted = new TaskCompletionSource<CancellationToken>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            Task<string> run = KeycloakClientFacade.RunBoundedAsync(
                token =>
                {
                    operationStarted.SetResult(token);
                    return new TaskCompletionSource<string>().Task;
                },
                _timeout,
                clock,
                CancellationToken.None
            );

            _operationToken = await operationStarted.Task;
            clock.Advance(_timeout);
            _exception = await Catch(run);
        }

        [Test]
        public void It_throws_a_timeout() => _exception.Should().BeOfType<TimeoutException>();

        [Test]
        public void It_cancels_the_token_the_operation_observes() =>
            _operationToken.IsCancellationRequested.Should().BeTrue();
    }

    [TestFixture]
    public class Given_RunBoundedAsync_before_the_timeout_expires : KeycloakClientFacadeTests
    {
        private bool _completedEarly;

        [SetUp]
        public async Task Setup()
        {
            var clock = new FakeTimeProvider();
            var operationStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var release = new TaskCompletionSource<string>();
            Task<string> run = KeycloakClientFacade.RunBoundedAsync(
                _ =>
                {
                    operationStarted.SetResult();
                    return release.Task;
                },
                _timeout,
                clock,
                CancellationToken.None
            );

            await operationStarted.Task;
            clock.Advance(_timeout - TimeSpan.FromTicks(1));
            _completedEarly = run.IsCompleted;
            release.SetResult("answer");
            await run;
        }

        [Test]
        public void It_keeps_waiting_until_the_full_budget_has_elapsed() =>
            _completedEarly.Should().BeFalse();
    }

    /// <summary>
    /// Keycloak.Net fetches its admin token synchronously before a call returns a task. A wait that
    /// ran the operation on the caller's thread would block there, out of reach of the timeout.
    /// </summary>
    [TestFixture]
    public class Given_RunBoundedAsync_with_an_operation_that_blocks_its_thread : KeycloakClientFacadeTests
    {
        private ManualResetEventSlim _release = null!;
        private bool _returnedWhileBlocked;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _release = new ManualResetEventSlim(false);
            var clock = new FakeTimeProvider();
            var blocking = new ManualResetEventSlim(false);
            Task<string> run = KeycloakClientFacade.RunBoundedAsync(
                _ =>
                {
                    blocking.Set();
                    _release.Wait();
                    return Task.FromResult("late");
                },
                _timeout,
                clock,
                CancellationToken.None
            );

            // Reached only because RunBoundedAsync returned while the operation is still blocked.
            _returnedWhileBlocked = !run.IsCompleted;
            blocking.Wait();
            clock.Advance(_timeout);
            _exception = await Catch(run);
        }

        [TearDown]
        public void TearDown()
        {
            _release.Set();
            _release.Dispose();
        }

        [Test]
        public void It_returns_to_the_caller_while_the_operation_blocks() =>
            _returnedWhileBlocked.Should().BeTrue();

        [Test]
        public void It_still_times_out() => _exception.Should().BeOfType<TimeoutException>();
    }

    [TestFixture]
    public class Given_RunBoundedAsync_when_the_caller_cancels : KeycloakClientFacadeTests
    {
        private CancellationTokenSource _caller = null!;
        private Exception? _exception;
        private CancellationToken _operationToken;

        [SetUp]
        public async Task Setup()
        {
            _caller = new CancellationTokenSource();
            var operationStarted = new TaskCompletionSource<CancellationToken>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            Task<string> run = KeycloakClientFacade.RunBoundedAsync(
                async token =>
                {
                    operationStarted.SetResult(token);
                    // Fails the way Flurl does when its token is cancelled: a wrapped exception
                    // whose message carries request content.
                    await Task.Delay(Timeout.Infinite, token)
                        .ContinueWith(
                            _ => throw new InvalidOperationException($"wrapped {Sentinel}"),
                            TaskScheduler.Default
                        );
                    return "unreachable";
                },
                Timeout.InfiniteTimeSpan,
                new FakeTimeProvider(),
                _caller.Token
            );

            _operationToken = await operationStarted.Task;
            await _caller.CancelAsync();
            _exception = await Catch(run);
        }

        [TearDown]
        public void TearDown() => _caller.Dispose();

        [Test]
        public void It_throws_an_operation_cancelled_exception_for_the_caller_token() =>
            _exception
                .Should()
                .BeAssignableTo<OperationCanceledException>()
                .Which.CancellationToken.Should()
                .Be(_caller.Token);

        [Test]
        public void It_carries_no_operation_content() =>
            ExceptionText(_exception!).Should().NotContain(Sentinel);

        [Test]
        public void It_cancels_the_token_the_operation_observes() =>
            _operationToken.IsCancellationRequested.Should().BeTrue();
    }

    [TestFixture]
    public class Given_RunBoundedAsync_with_a_caller_that_already_cancelled : KeycloakClientFacadeTests
    {
        private bool _operationInvoked;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            using var caller = new CancellationTokenSource();
            await caller.CancelAsync();
            _exception = await Catch(
                KeycloakClientFacade.RunBoundedAsync(
                    _ =>
                    {
                        _operationInvoked = true;
                        return Task.FromResult("answer");
                    },
                    _timeout,
                    new FakeTimeProvider(),
                    caller.Token
                )
            );
        }

        [Test]
        public void It_throws_an_operation_cancelled_exception() =>
            _exception.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_never_starts_the_operation() => _operationInvoked.Should().BeFalse();
    }

    [TestFixture]
    public class Given_RunBoundedAsync_with_an_operation_that_fails : KeycloakClientFacadeTests
    {
        private readonly InvalidOperationException _failure = new("failure");
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _exception = await Catch(
                KeycloakClientFacade.RunBoundedAsync<string>(
                    _ => Task.FromException<string>(_failure),
                    _timeout,
                    new FakeTimeProvider(),
                    CancellationToken.None
                )
            );
        }

        [Test]
        public void It_lets_the_failure_escape_unchanged_for_the_caller_to_classify() =>
            _exception.Should().BeSameAs(_failure);
    }

    /// <summary>
    /// A-01, resolved against the shipped package: the read goes through Keycloak.Net's own
    /// <c>GetClientsAsync(realm, clientId, …)</c>, authenticated with the service credentials.
    /// </summary>
    public class FacadeAgainstThePackage : KeycloakClientFacadeTests
    {
        protected const string ServiceClientId = "cms-service";
        protected const string ServiceClientSecret = "service-secret";
        protected HttpTest _httpTest = null!;

        protected static KeycloakClientFacade CreateFacade() =>
            new(
                new KeycloakContext(
                    "http://keycloak.test",
                    "edfi",
                    ServiceClientId,
                    ServiceClientSecret,
                    "role"
                )
            );

        protected void RespondToAdminToken() =>
            _httpTest
                .ForCallsTo("*/realms/edfi/protocol/openid-connect/token")
                .RespondWithJson(new { access_token = "admin-access-token" });

        [SetUp]
        public void CreateHttpTest() => _httpTest = new HttpTest();

        [TearDown]
        public void DisposeHttpTest() => _httpTest.Dispose();
    }

    [TestFixture]
    public class Given_a_client_list_that_states_both_flags : FacadeAgainstThePackage
    {
        private List<Client> _clients = [];

        [SetUp]
        public async Task Setup()
        {
            RespondToAdminToken();
            _httpTest
                .ForCallsTo("*/admin/realms/edfi/clients*")
                .RespondWith("""[{"clientId":"caller","publicClient":false,"bearerOnly":true}]""");

            _clients =
            [
                .. await CreateFacade().GetClientsByClientIdAsync("edfi", "caller", _timeout, default),
            ];
        }

        [Test]
        public void It_reads_publicClient_as_stated() => _clients.Single().PublicClient.Should().BeFalse();

        [Test]
        public void It_reads_bearerOnly_as_stated() => _clients.Single().BearerOnly.Should().BeTrue();

        [Test]
        public void It_queries_by_client_id() =>
            _httpTest
                .ShouldHaveCalled("http://keycloak.test/admin/realms/edfi/clients?*")
                .WithVerb(HttpMethod.Get)
                .WithQueryParam("clientId", "caller")
                .WithOAuthBearerToken("admin-access-token")
                .Times(1);

        [Test]
        public void It_authenticates_the_read_with_the_service_credentials() =>
            _httpTest
                .ShouldHaveCalled("http://keycloak.test/realms/edfi/protocol/openid-connect/token")
                .WithVerb(HttpMethod.Post)
                .WithRequestBody("*grant_type=client_credentials*")
                .WithRequestBody($"*client_id={ServiceClientId}*")
                .WithRequestBody($"*client_secret={ServiceClientSecret}*")
                .Times(1);
    }

    [TestFixture]
    public class Given_a_client_list_that_omits_both_flags : FacadeAgainstThePackage
    {
        private List<Client> _clients = [];

        [SetUp]
        public async Task Setup()
        {
            RespondToAdminToken();
            _httpTest.ForCallsTo("*/admin/realms/edfi/clients*").RespondWith("""[{"clientId":"caller"}]""");

            _clients =
            [
                .. await CreateFacade().GetClientsByClientIdAsync("edfi", "caller", _timeout, default),
            ];
        }

        [Test]
        public void It_keeps_publicClient_absent_rather_than_false() =>
            _clients.Single().PublicClient.Should().BeNull();

        [Test]
        public void It_keeps_bearerOnly_absent_rather_than_false() =>
            _clients.Single().BearerOnly.Should().BeNull();
    }

    [TestFixture]
    public class Given_a_client_id_with_query_syntax : FacadeAgainstThePackage
    {
        [SetUp]
        public async Task Setup()
        {
            RespondToAdminToken();
            _httpTest.ForCallsTo("*/admin/realms/edfi/clients*").RespondWith("[]");

            await CreateFacade().GetClientsByClientIdAsync("edfi", "a b&search=true", _timeout, default);
        }

        [Test]
        public void It_sends_the_id_as_one_encoded_value_and_adds_no_search_parameter() =>
            _httpTest
                .ShouldHaveCalled("http://keycloak.test/admin/realms/edfi/clients?*")
                .WithQueryParam("clientId", "a b&search=true")
                .WithoutQueryParam("search")
                .Times(1);
    }

    [TestFixture]
    public class Given_the_admin_api_refuses_the_read : FacadeAgainstThePackage
    {
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            RespondToAdminToken();
            _httpTest.ForCallsTo("*/admin/realms/edfi/clients*").RespondWith("", 403);

            _exception = await Catch(
                CreateFacade().GetClientsByClientIdAsync("edfi", "caller", _timeout, default)
            );
        }

        [Test]
        public void It_raises_the_status_for_the_gate_to_classify() =>
            _exception.Should().BeAssignableTo<FlurlHttpException>().Which.StatusCode.Should().Be(403);
    }

    [TestFixture]
    public class Given_the_service_credentials_are_rejected : FacadeAgainstThePackage
    {
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _httpTest
                .ForCallsTo("*/realms/edfi/protocol/openid-connect/token")
                .RespondWith("""{"error":"unauthorized_client"}""", 401);

            _exception = await Catch(
                CreateFacade().GetClientsByClientIdAsync("edfi", "caller", _timeout, default)
            );
        }

        [Test]
        public void It_raises_the_token_endpoint_status() =>
            _exception.Should().BeAssignableTo<FlurlHttpException>().Which.StatusCode.Should().Be(401);

        [Test]
        public void It_never_reads_the_client_list() =>
            _httpTest.ShouldNotHaveCalled("http://keycloak.test/admin/realms/edfi/clients*");
    }

    protected static async Task<Exception?> Catch(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    protected static string ExceptionText(Exception exception)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message).AppendLine(current.ToString());
        }

        return text.ToString();
    }
}
