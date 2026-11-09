// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.Security.ConfigurationServiceTokenHandlerTests;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

/// <summary>
/// The seven authenticated Configuration Service reads through the registered GET and grant
/// pipelines, each with its own scripted primary transport. A first protected 401 obtains one
/// replacement bearer for the generation actually sent and replays an equivalent request once.
/// </summary>
public class ConfigurationServiceAuthenticatedReadTests
{
    internal const string ReadTenant = "tenant-north";
    internal const string ReadClientId = "client-1";

    /// <summary>One protected read as its primary transport saw it.</summary>
    internal sealed record ProtectedRead(
        HttpMethod Method,
        Uri RequestUri,
        string? Bearer,
        string? Tenant,
        bool? AllowNotFound
    );

    /// <summary>
    /// The protected-read primary transport. It records each request, including its opted-in 404
    /// option, and answers from a script given the request and its 1-based attempt number.
    /// </summary>
    internal sealed class ProtectedReadTransport(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        private int _attempt;

        public ConcurrentQueue<ProtectedRead> Reads { get; } = new();

        public ConcurrentQueue<HttpStatusCode> Statuses { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Reads.Enqueue(
                new ProtectedRead(
                    request.Method,
                    request.RequestUri!,
                    request.Headers.Authorization?.Parameter,
                    request.Headers.TryGetValues("Tenant", out IEnumerable<string>? tenants)
                        ? tenants.Single()
                        : null,
                    request.Options.TryGetValue(
                        ConfigurationServiceResponseHandler.AllowNotFoundResponse,
                        out bool allowNotFound
                    )
                        ? allowNotFound
                        : null
                )
            );

            HttpResponseMessage response = respond(request, Interlocked.Increment(ref _attempt));
            Statuses.Enqueue(response.StatusCode);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            // Outlives the factory's handler chain, as ScriptedTransport does.
        }
    }

    /// <summary>The four providers over one registered Core graph.</summary>
    public sealed class ReadServices(ServiceProvider provider)
    {
        private ConfigurationServiceApiClient ApiClient =>
            provider.GetRequiredService<ConfigurationServiceApiClient>();

        private IConfigurationServiceTokenHandler TokenHandler =>
            provider.GetRequiredService<IConfigurationServiceTokenHandler>();

        private ConfigurationServiceContext Context =>
            provider.GetRequiredService<ConfigurationServiceContext>();

        public ConfigurationServiceDataStoreProvider DataStores =>
            provider.GetRequiredService<ConfigurationServiceDataStoreProvider>();

        public ConfigurationServiceApplicationProvider Applications =>
            new(
                ApiClient,
                TokenHandler,
                Context,
                provider.GetRequiredService<ILogger<ConfigurationServiceApplicationProvider>>()
            );

        public ConfigurationServiceClaimSetProvider ClaimSets => new(ApiClient, TokenHandler, Context);

        public ConfigurationServiceProfileProvider Profiles =>
            new(
                ApiClient,
                TokenHandler,
                Context,
                provider.GetRequiredService<ILogger<ConfigurationServiceProfileProvider>>()
            );
    }

    internal static readonly string ApplicationContextBody = $$"""
        {"id":1,"applicationId":100,"clientId":"{{ReadClientId}}","clientUuid":"5b8f6c2e-0f43-4d0a-9e6a-2d8f1b7c4a11","dataStoreIds":[1],"ownershipTokenIds":[]}
        """;

    internal const string ApplicationProfileInfoBody =
        """{"id":5,"applicationName":"app","vendorId":1,"claimSetName":"cs","educationOrganizationIds":[],"dataStoreIds":[],"profileIds":[7]}""";

    internal const string ProfileBody =
        """{"id":7,"name":"profile-7","definition":"<Profile name=\"profile-7\" />"}""";

    /// <summary>
    /// One authenticated read: its route and request shape, how to invoke it, and the provider's
    /// existing success and terminal outcomes.
    /// </summary>
    public sealed record ReadCase(
        string Name,
        string Path,
        string? Tenant,
        bool? AllowNotFound,
        string SuccessBody,
        Func<ReadServices, Task<object?>> Invoke,
        Action<object?> AssertSuccess,
        Action<object?, Exception?> AssertTerminal
    )
    {
        public override string ToString() => Name;
    }

    private static void AssertWrapped401(Exception? failure) =>
        failure
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.InnerException.Should()
            .BeOfType<HttpRequestException>()
            .Which.StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);

    private static void AssertProfileUnavailable(Exception? failure) =>
        failure.Should().BeOfType<ProfileDataUnavailableException>();

    internal static readonly ReadCase DataStoresRead = new(
        "data stores",
        "v3/dataStores/",
        ReadTenant,
        null,
        "[]",
        async services => await services.DataStores.LoadDataStores(ReadTenant),
        result => result.Should().BeAssignableTo<IList<DataStore>>().Which.Should().BeEmpty(),
        (_, failure) => AssertWrapped401(failure)
    );

    internal static readonly ReadCase TenantsRead = new(
        "tenants",
        "v3/tenants/",
        null,
        null,
        "[]",
        async services => await services.DataStores.LoadTenants(),
        result => result.Should().BeAssignableTo<IList<string>>().Which.Should().BeEmpty(),
        (_, failure) => AssertWrapped401(failure)
    );

    internal static readonly ReadCase ApplicationRead = new(
        "application context",
        $"v3/apiClients/{ReadClientId}",
        ReadTenant,
        true,
        ApplicationContextBody,
        async services => await services.Applications.GetApplicationByClientIdAsync(ReadClientId, ReadTenant),
        result =>
            result
                .Should()
                .BeOfType<ApplicationContextResult.Success>()
                .Which.ApplicationContext.ClientId.Should()
                .Be(ReadClientId),
        (result, failure) =>
        {
            failure.Should().BeNull();
            result.Should().BeOfType<ApplicationContextResult.Unavailable>();
        }
    );

    internal static readonly ReadCase ClaimSetsRead = new(
        "claim sets",
        "v3/authorizationMetadata",
        ReadTenant,
        null,
        "[]",
        async services => await services.ClaimSets.GetAllClaimSets(ReadTenant),
        result => result.Should().BeAssignableTo<IList<ClaimSet>>().Which.Should().BeEmpty(),
        (_, failure) =>
            failure
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .Be(HttpStatusCode.Unauthorized)
    );

    internal static readonly ReadCase ApplicationProfileInfoRead = new(
        "application profile info",
        "v3/applications/5",
        ReadTenant,
        true,
        ApplicationProfileInfoBody,
        async services => await services.Profiles.GetApplicationProfileInfoAsync(5, ReadTenant),
        result => result.Should().BeOfType<ApplicationProfileInfo>().Which.ProfileIds.Should().Equal(7),
        (_, failure) => AssertProfileUnavailable(failure)
    );

    internal static readonly ReadCase ProfileRead = new(
        "profile",
        "v3/profiles/7",
        ReadTenant,
        true,
        ProfileBody,
        async services => await services.Profiles.GetProfileAsync(7, ReadTenant),
        result => result.Should().BeOfType<CmsProfileResponse>().Which.Name.Should().Be("profile-7"),
        (_, failure) => AssertProfileUnavailable(failure)
    );

    internal static readonly ReadCase ProfileCatalogRead = new(
        "profile catalog",
        "v3/profiles",
        ReadTenant,
        null,
        "[]",
        async services => await services.Profiles.GetProfilesAsync(ReadTenant),
        result =>
            result.Should().BeAssignableTo<IReadOnlyList<CmsProfileResponse>>().Which.Should().BeEmpty(),
        (_, failure) => AssertProfileUnavailable(failure)
    );

    internal static readonly ReadCase[] AllReads =
    [
        DataStoresRead,
        TenantsRead,
        ApplicationRead,
        ClaimSetsRead,
        ApplicationProfileInfoRead,
        ProfileRead,
        ProfileCatalogRead,
    ];

    internal static async Task<(object? Result, Exception? Failure)> Capture(Task<object?> call)
    {
        try
        {
            return (await call.WaitAsync(Failsafe), null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    /// <summary>Grants that issue <c>{TokenSentinel}-{n}</c>, recording when each was issued.</summary>
    internal static ScriptedTransport IssuingGrants(
        TimeProvider timeProvider,
        int lifetimeSeconds,
        ConcurrentDictionary<string, DateTimeOffset>? issuedAt = null
    ) =>
        AnsweringByAttempt(attempt =>
        {
            string bearer = $"{TokenSentinel}-{attempt}";
            issuedAt?.TryAdd(bearer, timeProvider.GetUtcNow());
            return JsonResponse(HttpStatusCode.OK, ValidGrantBody(bearer, lifetimeSeconds));
        });

    internal static HttpResponseMessage Rejected() =>
        JsonResponse(HttpStatusCode.Unauthorized, TokenShapedBody);

    internal static IEnumerable<TestFixtureData> RejectionCases() =>
        AllReads.SelectMany(read =>
            new[]
            {
                new TestFixtureData(read, false).SetArgDisplayNames(read.Name, "401 then success"),
                new TestFixtureData(read, true).SetArgDisplayNames(read.Name, "401 then 401"),
            }
        );

    /// <summary>
    /// Each read, rejected once and then answered or rejected again: one replacement grant, exactly
    /// two reads with equivalent routing and options, and the provider's existing outcome.
    /// </summary>
    [TestFixtureSource(nameof(RejectionCases))]
    [Parallelizable]
    public class Given_A_Rejected_Protected_Read(ReadCase read, bool replayRejected)
        : ConfigurationServiceAuthenticatedReadTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private ScriptedTransport _grants = null!;
        private ProtectedReadTransport _reads = null!;
        private object? _result;
        private Exception? _failure;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _logs = new RecordingLoggerProvider();
            _grants = IssuingGrants(fakeTime, 600);
            _reads = new ProtectedReadTransport(
                (_, attempt) =>
                    attempt == 1 || replayRejected
                        ? Rejected()
                        : JsonResponse(HttpStatusCode.OK, read.SuccessBody)
            );
            _provider = BuildRegisteredProvider(_logs, fakeTime, _grants, _reads);

            (_result, _failure) = await Capture(read.Invoke(new ReadServices(_provider)));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_obtains_one_replacement_grant() => _grants.Requests.Should().HaveCount(2);

        [Test]
        public void It_sends_exactly_two_reads() => _reads.Reads.Should().HaveCount(2);

        [Test]
        public void It_replays_with_the_replacement_bearer()
        {
            ProtectedRead[] reads = [.. _reads.Reads];
            reads[0].Bearer.Should().Be($"{TokenSentinel}-1");
            reads[^1].Bearer.Should().Be($"{TokenSentinel}-2");
        }

        [Test]
        public void It_replays_an_equivalent_request()
        {
            ProtectedRead[] reads = [.. _reads.Reads];
            reads.Should().HaveCount(2);

            foreach (ProtectedRead sent in reads)
            {
                sent.Method.Should().Be(HttpMethod.Get);
                sent.RequestUri.Should().Be(new Uri(new Uri(CmsBaseAddress), read.Path));
                sent.Tenant.Should().Be(read.Tenant);
                sent.AllowNotFound.Should().Be(read.AllowNotFound);
            }
        }

        [Test]
        public void It_keeps_the_provider_outcome()
        {
            if (replayRejected)
            {
                read.AssertTerminal(_result, _failure);
            }
            else
            {
                _failure.Should().BeNull();
                read.AssertSuccess(_result);
            }
        }

        [Test]
        public void It_keeps_bearers_and_echoed_content_out_of_diagnostics()
        {
            AssertNoSentinels(_logs.Rendered);

            if (_failure is not null)
            {
                AssertNoSentinels(_failure.ToString());
            }
        }

        [Test]
        public void It_identifies_the_failed_endpoint()
        {
            IReadOnlyList<CapturedLog> failures = _logs.For(
                typeof(ConfigurationServiceResponseHandler).FullName!
            );
            failures.Should().HaveCount(replayRejected ? 2 : 1);
            failures
                .Should()
                .OnlyContain(entry => entry.Message.Contains($"endpoint v3/{read.Path.Split('/')[1]}"));
            failures.Should().OnlyContain(entry => !entry.Message.Contains(ReadClientId));
        }
    }

    internal static IEnumerable<TestFixtureData> ResponseFailureCases =>
        AllReads.SelectMany(read =>
            new[] { false, true }.Select(malformedJson => new TestFixtureData(read, malformedJson))
        );

    /// <summary>
    /// Successful HTTP responses can still fail during charset decoding or JSON parsing. Those
    /// failures must keep echoed credentials out of provider logs and propagated exception chains.
    /// </summary>
    [TestFixtureSource(nameof(ResponseFailureCases))]
    [Parallelizable]
    public class Given_An_Unreadable_Protected_Response(ReadCase read, bool malformedJson)
        : ConfigurationServiceAuthenticatedReadTests
    {
        private ServiceProvider _provider = null!;
        private RecordingLoggerProvider _logs = null!;
        private ScriptedTransport _grants = null!;
        private ProtectedReadTransport _reads = null!;
        private object? _result;
        private Exception? _failure;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _logs = new RecordingLoggerProvider();
            _grants = IssuingGrants(fakeTime, 600);
            _reads = new ProtectedReadTransport(
                (_, _) =>
                {
                    string body = malformedJson ? $$"""{"{{TokenSentinel}}": }""" : read.SuccessBody;
                    if (malformedJson && read.SuccessBody.StartsWith('['))
                    {
                        body = $"[{body}]";
                    }

                    HttpResponseMessage response = JsonResponse(HttpStatusCode.OK, body);
                    if (!malformedJson)
                    {
                        response.Content.Headers.ContentType!.CharSet = $"\"{TokenSentinel}\"";
                    }

                    return response;
                }
            );
            _provider = BuildRegisteredProvider(_logs, fakeTime, _grants, _reads);

            (_result, _failure) = await Capture(read.Invoke(new ReadServices(_provider)));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_preserves_dependency_unavailability()
        {
            if (ReferenceEquals(read, ApplicationRead))
            {
                _failure.Should().BeNull();
                _result.Should().BeOfType<ApplicationContextResult.Unavailable>();
            }
            else if (
                ReferenceEquals(read, ProfileRead)
                || ReferenceEquals(read, ProfileCatalogRead)
                || ReferenceEquals(read, ApplicationProfileInfoRead)
            )
            {
                _failure.Should().BeOfType<ProfileDataUnavailableException>();
            }
            else if (ReferenceEquals(read, ClaimSetsRead))
            {
                if (malformedJson)
                {
                    _failure.Should().BeOfType<System.Text.Json.JsonException>();
                }
                else
                {
                    _failure.Should().BeOfType<HttpRequestException>().Which.StatusCode.Should().BeNull();
                }
            }
            else
            {
                _failure.Should().BeOfType<InvalidOperationException>();
            }
        }

        [Test]
        public void It_does_not_recover_or_replay()
        {
            _grants.Requests.Should().ContainSingle();
            _reads.Reads.Should().ContainSingle();
        }

        [Test]
        public void It_keeps_response_text_out_of_diagnostics()
        {
            AssertNoSentinels(_logs.Rendered);
            if (_failure is not null)
            {
                AssertNoSentinels(_failure.ToString());
            }
        }
    }

    /// <summary>
    /// A recovery grant that fails - here a 429 whose body cannot be read - sends no replay, and a
    /// later read inside the 30-second window sends neither a grant nor a read.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Failed_Recovery_Grant : ConfigurationServiceAuthenticatedReadTests
    {
        private ServiceProvider _provider = null!;
        private ScriptedTransport _grants = null!;
        private ProtectedReadTransport _reads = null!;
        private Exception? _recoveryFailure;
        private int _readsAfterRecoveryFailure;
        private Exception? _laterFailure;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            int grantAttempt = 0;
            _grants = new ScriptedTransport(
                (_, _) =>
                {
                    if (Interlocked.Increment(ref grantAttempt) == 1)
                    {
                        return Task.FromResult(
                            JsonResponse(HttpStatusCode.OK, ValidGrantBody($"{TokenSentinel}-1", 600))
                        );
                    }

                    return Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                        {
                            Content = new StreamContent(new ThrowingStream()),
                        }
                    );
                }
            );
            _reads = new ProtectedReadTransport((_, _) => Rejected());
            _provider = BuildRegisteredProvider(new RecordingLoggerProvider(), fakeTime, _grants, _reads);
            var services = new ReadServices(_provider);

            (_, _recoveryFailure) = await Capture(ClaimSetsRead.Invoke(services));
            _readsAfterRecoveryFailure = _reads.Reads.Count;

            fakeTime.Advance(TimeSpan.FromSeconds(10));
            (_, _laterFailure) = await Capture(ClaimSetsRead.Invoke(services));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_fails_with_the_recovery_grant_status() =>
            _recoveryFailure
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .Be(HttpStatusCode.TooManyRequests);

        [Test]
        public void It_sends_no_replay() => _readsAfterRecoveryFailure.Should().Be(1);

        [Test]
        public void It_sends_no_grant_for_a_later_read_in_the_window() =>
            _grants.Requests.Should().HaveCount(2);

        [Test]
        public void It_sends_no_read_for_a_later_read_in_the_window() => _reads.Reads.Should().HaveCount(1);

        [Test]
        public void It_returns_the_window_failure_to_the_later_read() =>
            _laterFailure
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .Be(HttpStatusCode.TooManyRequests);
    }

    /// <summary>A 401 from the grant POST is a failed grant, never a rejected read.</summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Rejected_Grant : ConfigurationServiceAuthenticatedReadTests
    {
        private ServiceProvider _provider = null!;
        private ScriptedTransport _grants = null!;
        private ProtectedReadTransport _reads = null!;
        private Exception? _failure;

        [SetUp]
        public async Task Setup()
        {
            _grants = Answering(HttpStatusCode.Unauthorized, TokenShapedBody);
            _reads = new ProtectedReadTransport((_, _) => JsonResponse(HttpStatusCode.OK, "[]"));
            _provider = BuildRegisteredProvider(
                new RecordingLoggerProvider(),
                new FakeTimeProvider(),
                _grants,
                _reads
            );

            (_, _failure) = await Capture(ClaimSetsRead.Invoke(new ReadServices(_provider)));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_fails_with_the_grant_status() =>
            _failure
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .Be(HttpStatusCode.Unauthorized);

        [Test]
        public void It_sends_one_grant() => _grants.Requests.Should().ContainSingle();

        [Test]
        public void It_sends_no_read() => _reads.Reads.Should().BeEmpty();
    }

    /// <summary>A protected failure other than 401 is terminal: no replacement grant and no replay.</summary>
    [TestFixture(HttpStatusCode.Forbidden)]
    [TestFixture(HttpStatusCode.ServiceUnavailable)]
    [Parallelizable]
    public class Given_A_Terminal_Protected_Failure(HttpStatusCode statusCode)
        : ConfigurationServiceAuthenticatedReadTests
    {
        private ServiceProvider _provider = null!;
        private ScriptedTransport _grants = null!;
        private ProtectedReadTransport _reads = null!;
        private Exception? _claimSetFailure;
        private object? _applicationResult;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _grants = IssuingGrants(fakeTime, 600);
            _reads = new ProtectedReadTransport((_, _) => JsonResponse(statusCode, TokenShapedBody));
            _provider = BuildRegisteredProvider(new RecordingLoggerProvider(), fakeTime, _grants, _reads);
            var services = new ReadServices(_provider);

            (_, _claimSetFailure) = await Capture(ClaimSetsRead.Invoke(services));
            (_applicationResult, _) = await Capture(ApplicationRead.Invoke(services));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_propagates_the_claim_set_failure() =>
            _claimSetFailure
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .Be(statusCode);

        [Test]
        public void It_reports_the_application_as_unavailable() =>
            _applicationResult.Should().BeOfType<ApplicationContextResult.Unavailable>();

        [Test]
        public void It_sends_one_read_per_call() => _reads.Reads.Should().HaveCount(2);

        [Test]
        public void It_sends_no_replacement_grant() => _grants.Requests.Should().ContainSingle();
    }

    /// <summary>
    /// The opted-in detail 404 reaches the provider as not found, whether it is the first answer or
    /// the answer to the replay.
    /// </summary>
    [TestFixture(false)]
    [TestFixture(true)]
    [Parallelizable]
    public class Given_An_Opted_In_Not_Found(bool afterRejection) : ConfigurationServiceAuthenticatedReadTests
    {
        private ServiceProvider _provider = null!;
        private ScriptedTransport _grants = null!;
        private ProtectedReadTransport _reads = null!;
        private object? _applicationResult;
        private Exception? _applicationFailure;
        private object? _profileResult;
        private Exception? _profileFailure;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _grants = IssuingGrants(fakeTime, 600);
            var rejectedPaths = new ConcurrentDictionary<string, bool>();
            _reads = new ProtectedReadTransport(
                (request, _) =>
                    afterRejection && rejectedPaths.TryAdd(request.RequestUri!.AbsolutePath, true)
                        ? Rejected()
                        : JsonResponse(HttpStatusCode.NotFound, TokenShapedBody)
            );
            _provider = BuildRegisteredProvider(new RecordingLoggerProvider(), fakeTime, _grants, _reads);
            var services = new ReadServices(_provider);

            (_applicationResult, _applicationFailure) = await Capture(ApplicationRead.Invoke(services));

            (_profileResult, _profileFailure) = await Capture(ProfileRead.Invoke(services));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_reports_the_application_as_not_found()
        {
            _applicationFailure.Should().BeNull();
            _applicationResult.Should().BeOfType<ApplicationContextResult.NotFound>();
        }

        [Test]
        public void It_reports_the_profile_as_absent()
        {
            _profileFailure.Should().BeNull();
            _profileResult.Should().BeNull();
        }

        [Test]
        public void It_replays_only_after_a_rejection() =>
            _reads.Reads.Should().HaveCount(afterRejection ? 4 : 2);

        [Test]
        public void It_sends_grants_only_for_the_rejection() =>
            _grants.Requests.Should().HaveCount(afterRejection ? 3 : 1);
    }

    /// <summary>
    /// A replay rejected again leaves its bearer current, so a later call that the Configuration
    /// Service accepts again reuses it without another grant.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Later_Call_After_A_Terminal_Rejection : ConfigurationServiceAuthenticatedReadTests
    {
        private ServiceProvider _provider = null!;
        private ScriptedTransport _grants = null!;
        private ProtectedReadTransport _reads = null!;
        private Exception? _terminalFailure;
        private object? _laterResult;
        private Exception? _laterFailure;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _grants = IssuingGrants(fakeTime, 600);
            _reads = new ProtectedReadTransport(
                (_, attempt) => attempt <= 2 ? Rejected() : JsonResponse(HttpStatusCode.OK, "[]")
            );
            _provider = BuildRegisteredProvider(new RecordingLoggerProvider(), fakeTime, _grants, _reads);
            var services = new ReadServices(_provider);

            (_, _terminalFailure) = await Capture(ClaimSetsRead.Invoke(services));
            (_laterResult, _laterFailure) = await Capture(ClaimSetsRead.Invoke(services));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_ends_the_first_call_with_the_rejection() =>
            _terminalFailure
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .Be(HttpStatusCode.Unauthorized);

        [Test]
        public void It_reuses_the_replacement_for_the_later_call()
        {
            _grants.Requests.Should().HaveCount(2);
            _reads.Reads.Should().HaveCount(3);
            _reads.Reads.Last().Bearer.Should().Be($"{TokenSentinel}-2");
        }

        [Test]
        public void It_completes_the_later_call()
        {
            _laterFailure.Should().BeNull();
            ClaimSetsRead.AssertSuccess(_laterResult);
        }
    }

    /// <summary>
    /// The Configuration Service rejects every bearer it issues, although grants succeed. Each
    /// call gets one replacement and one replay, including calls immediately after a terminal 401.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Every_Issued_Bearer_Is_Rejected : ConfigurationServiceAuthenticatedReadTests
    {
        private ServiceProvider _provider = null!;
        private ScriptedTransport _grants = null!;
        private ProtectedReadTransport _reads = null!;
        private readonly List<Exception?> _failures = [];
        private readonly List<int> _grantCounts = [];
        private readonly List<int> _readCounts = [];
        private Exception? _failureAfterRecovery;

        [SetUp]
        public async Task Setup()
        {
            _failures.Clear();
            _grantCounts.Clear();
            _readCounts.Clear();

            var fakeTime = new FakeTimeProvider();
            bool accepting = false;
            _grants = IssuingGrants(fakeTime, 600);
            _reads = new ProtectedReadTransport(
                (_, _) => Volatile.Read(ref accepting) ? JsonResponse(HttpStatusCode.OK, "[]") : Rejected()
            );
            _provider = BuildRegisteredProvider(new RecordingLoggerProvider(), fakeTime, _grants, _reads);
            var services = new ReadServices(_provider);

            for (int call = 0; call < 3; call++)
            {
                _failures.Add((await Capture(ClaimSetsRead.Invoke(services))).Failure);
                _grantCounts.Add(_grants.Requests.Count);
                _readCounts.Add(_reads.Reads.Count);
            }

            Volatile.Write(ref accepting, true);
            _failureAfterRecovery = (await Capture(ClaimSetsRead.Invoke(services))).Failure;
        }

        private static void AssertRejected(Exception? failure) =>
            failure
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .Be(HttpStatusCode.Unauthorized);

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_reports_each_calls_second_rejection() =>
            _failures.Should().HaveCount(3).And.AllSatisfy(AssertRejected);

        [Test]
        public void It_obtains_one_replacement_per_call() => _grantCounts.Should().Equal(2, 3, 4);

        [Test]
        public void It_replays_each_call_once() => _readCounts.Should().Equal(2, 4, 6);

        [Test]
        public void It_reads_with_the_current_bearer_once_it_is_accepted()
        {
            _failureAfterRecovery.Should().BeNull();
            _grants.Requests.Should().HaveCount(4);
            _reads.Reads.Should().HaveCount(7);
            _reads.Reads.Last().Bearer.Should().Be($"{TokenSentinel}-4");
        }
    }

    /// <summary>
    /// A caller canceled while its recovery grant is pending ends without a replay, and the grant it
    /// left keeps running for others.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Cancellation_Before_The_Replay : ConfigurationServiceAuthenticatedReadTests
    {
        private ServiceProvider _provider = null!;
        private GatedTransport _grants = null!;
        private ProtectedReadTransport _reads = null!;
        private Exception? _failure;
        private bool _recoveryGrantSent;
        private bool _recoveryGrantCanceled;

        [SetUp]
        public async Task Setup()
        {
            _grants = new GatedTransport();
            _reads = new ProtectedReadTransport(
                (_, attempt) => attempt == 1 ? Rejected() : JsonResponse(HttpStatusCode.OK, "[]")
            );
            _provider = BuildRegisteredProvider(
                new RecordingLoggerProvider(),
                new FakeTimeProvider(),
                _grants,
                _reads
            );
            var services = new ReadServices(_provider);
            using var caller = new CancellationTokenSource();

            Task<IList<ClaimSet>> read = services.ClaimSets.GetAllClaimSets(ReadTenant, caller.Token);
            PendingRequest initial = await _grants.NextAsync() ?? throw new TimeoutException();
            initial.Answer(HttpStatusCode.OK, ValidGrantBody($"{TokenSentinel}-1", 600));

            PendingRequest? recovery = await _grants.NextAsync();
            _recoveryGrantSent = recovery is not null;
            await caller.CancelAsync();
            (_, _failure) = await Capture(read.ContinueWith(task => (object?)task.GetAwaiter().GetResult()));
            _recoveryGrantCanceled = recovery?.CancellationToken.IsCancellationRequested ?? false;
            recovery?.Answer(HttpStatusCode.OK, ValidGrantBody($"{TokenSentinel}-2", 600));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_sends_the_recovery_grant() => _recoveryGrantSent.Should().BeTrue();

        [Test]
        public void It_ends_the_canceled_call() =>
            _failure.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_sends_no_replay() => _reads.Reads.Should().ContainSingle();

        [Test]
        public void It_leaves_the_recovery_grant_running() => _recoveryGrantCanceled.Should().BeFalse();
    }

    /// <summary>
    /// Protected reads around the nominal 540- and 270-second cutoffs of 600- and 300-second bearers.
    /// The scripted issuer rejects a bearer once its own lifetime has passed; the replacement is sent
    /// at the cutoff, before any such rejection could occur.
    /// </summary>
    [TestFixture(600, 540)]
    [TestFixture(300, 270)]
    [Parallelizable]
    public class Given_Protected_Reads_Around_The_Cutoff(int lifetimeSeconds, int cutoffSeconds)
        : ConfigurationServiceAuthenticatedReadTests
    {
        private ServiceProvider _provider = null!;
        private ScriptedTransport _grants = null!;
        private ProtectedReadTransport _reads = null!;
        private readonly List<Exception?> _failures = [];

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            var issuedAt = new ConcurrentDictionary<string, DateTimeOffset>();
            _grants = IssuingGrants(fakeTime, lifetimeSeconds, issuedAt);
            _reads = new ProtectedReadTransport(
                (request, _) =>
                    fakeTime.GetUtcNow()
                    >= issuedAt[request.Headers.Authorization!.Parameter!].AddSeconds(lifetimeSeconds)
                        ? Rejected()
                        : JsonResponse(HttpStatusCode.OK, "[]")
            );
            _provider = BuildRegisteredProvider(new RecordingLoggerProvider(), fakeTime, _grants, _reads);
            var services = new ReadServices(_provider);

            _failures.Add((await Capture(ClaimSetsRead.Invoke(services))).Failure);
            fakeTime.Advance(TimeSpan.FromSeconds(cutoffSeconds) - OneTick);
            _failures.Add((await Capture(ClaimSetsRead.Invoke(services))).Failure);
            fakeTime.Advance(OneTick);
            _failures.Add((await Capture(ClaimSetsRead.Invoke(services))).Failure);
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_completes_every_read() =>
            _failures.Should().AllSatisfy(failure => failure.Should().BeNull());

        [Test]
        public void It_uses_the_original_bearer_just_before_the_cutoff() =>
            _reads
                .Reads.Select(sent => sent.Bearer)
                .Take(2)
                .Should()
                .Equal($"{TokenSentinel}-1", $"{TokenSentinel}-1");

        [Test]
        public void It_sends_the_replacement_at_the_cutoff() =>
            _reads.Reads.Last().Bearer.Should().Be($"{TokenSentinel}-2");

        [Test]
        public void It_meets_no_expiry_rejection() =>
            _reads.Statuses.Should().NotContain(HttpStatusCode.Unauthorized);

        [Test]
        public void It_sends_one_replacement_grant() => _grants.Requests.Should().HaveCount(2);
    }

    /// <summary>
    /// A 3,600-second bearer is reused by one grant until the 3,540-second cutoff, then replaced once
    /// under concurrent demand, so it overlaps its replacement by at most the 60-second margin.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Concurrent_Reads_At_The_Long_Cutoff : ConfigurationServiceAuthenticatedReadTests
    {
        private const int Readers = 10;

        private ServiceProvider _provider = null!;
        private ScriptedTransport _grants = null!;
        private ProtectedReadTransport _reads = null!;
        private readonly ConcurrentDictionary<string, DateTimeOffset> _issuedAt = new();
        private int _grantsJustBeforeCutoff;
        private (object? Result, Exception? Failure)[] _atCutoff = [];

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _grants = IssuingGrants(fakeTime, 3600, _issuedAt);
            _reads = new ProtectedReadTransport((_, _) => JsonResponse(HttpStatusCode.OK, "[]"));
            _provider = BuildRegisteredProvider(new RecordingLoggerProvider(), fakeTime, _grants, _reads);
            var services = new ReadServices(_provider);

            await Capture(ClaimSetsRead.Invoke(services));
            fakeTime.Advance(TimeSpan.FromSeconds(3540) - OneTick);
            await Capture(ClaimSetsRead.Invoke(services));
            _grantsJustBeforeCutoff = _grants.Requests.Count;

            fakeTime.Advance(OneTick);
            _atCutoff = await Task.WhenAll(
                Enumerable.Range(0, Readers).Select(_ => Capture(ClaimSetsRead.Invoke(services)))
            );
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_sends_one_grant_before_the_cutoff() => _grantsJustBeforeCutoff.Should().Be(1);

        [Test]
        public void It_sends_one_replacement_under_concurrent_demand() =>
            _grants.Requests.Should().HaveCount(2);

        [Test]
        public void It_completes_every_concurrent_read() =>
            _atCutoff.Should().AllSatisfy(outcome => outcome.Failure.Should().BeNull());

        [Test]
        public void It_sends_the_replacement_with_every_concurrent_read() =>
            _reads.Reads.Skip(2).Should().OnlyContain(sent => sent.Bearer == $"{TokenSentinel}-2");

        [Test]
        public void It_overlaps_the_replacement_by_at_most_the_margin()
        {
            TimeSpan nominalOverlap =
                _issuedAt[$"{TokenSentinel}-1"].AddSeconds(3600) - _issuedAt[$"{TokenSentinel}-2"];
            nominalOverlap.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Generation races against the token handler's single-flight state.
    // ---------------------------------------------------------------------------------------------

    internal static Task<ConfigurationServiceTokenLease> Lease(IConfigurationServiceTokenHandler handler) =>
        handler.GetTokenAsync(GrantClientId, SecretSentinel, GrantScope);

    internal static Task<ConfigurationServiceTokenLease> Refresh(
        IConfigurationServiceTokenHandler handler,
        ConfigurationServiceTokenLease rejected
    ) => handler.RefreshRejectedTokenAsync(rejected, GrantClientId, SecretSentinel, GrantScope);

    internal static async Task<Exception?> FailureOf(Task task)
    {
        try
        {
            await task.WaitAsync(Failsafe);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// The issuer returns the same bearer text for a replacement. A late rejection of the older lease
    /// leaves the newer generation in place and sends no grant.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Late_Rejection_Of_An_Older_Lease_With_The_Same_Text
        : ConfigurationServiceAuthenticatedReadTests
    {
        private ServiceProvider _provider = null!;
        private ScriptedTransport _grants = null!;
        private ConfigurationServiceTokenLease _older = null!;
        private ConfigurationServiceTokenLease _newer = null!;
        private ConfigurationServiceTokenLease _afterLateRefresh = null!;
        private ConfigurationServiceTokenLease _currentAfterLateRefresh = null!;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _grants = AnsweringByAttempt(_ =>
                JsonResponse(HttpStatusCode.OK, ValidGrantBody("same-bearer", 600))
            );
            _provider = BuildRegisteredProvider(new RecordingLoggerProvider(), fakeTime, _grants, _grants);
            var handler = _provider.GetRequiredService<IConfigurationServiceTokenHandler>();

            _older = await Lease(handler).WaitAsync(Failsafe);
            fakeTime.Advance(TimeSpan.FromSeconds(540));
            _newer = await Lease(handler).WaitAsync(Failsafe);

            _afterLateRefresh = await Refresh(handler, _older).WaitAsync(Failsafe);
            _currentAfterLateRefresh = await Lease(handler).WaitAsync(Failsafe);
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_gives_the_replacement_a_new_generation_with_the_same_text()
        {
            _newer.AccessToken.Should().Be(_older.AccessToken);
            _newer.Generation.Should().NotBe(_older.Generation);
        }

        [Test]
        public void It_returns_the_newer_generation_to_a_late_refresh() =>
            _afterLateRefresh.Generation.Should().Be(_newer.Generation);

        [Test]
        public void It_keeps_the_newer_generation_current_after_a_late_refresh() =>
            _currentAfterLateRefresh.Generation.Should().Be(_newer.Generation);

        [Test]
        public void It_sends_no_grant_for_the_late_rejections() => _grants.Requests.Should().HaveCount(2);
    }

    /// <summary>
    /// While a replacement for a rejected lease is pending, a duplicate late rejection of that lease
    /// and a cold caller join the same grant and receive the same generation.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Rejection_While_A_Replacement_Is_Pending : ConfigurationServiceAuthenticatedReadTests
    {
        private ServiceProvider _provider = null!;
        private GatedTransport _grants = null!;
        private int _grantsWhilePending;
        private ConfigurationServiceTokenLease[] _joined = [];

        [SetUp]
        public async Task Setup()
        {
            _grants = new GatedTransport();
            _provider = BuildRegisteredProvider(
                new RecordingLoggerProvider(),
                new FakeTimeProvider(),
                _grants,
                _grants
            );
            var handler = _provider.GetRequiredService<IConfigurationServiceTokenHandler>();

            Task<ConfigurationServiceTokenLease> first = Lease(handler);
            (await _grants.NextAsync() ?? throw new TimeoutException()).Answer(
                HttpStatusCode.OK,
                ValidGrantBody("bearer-1", 600)
            );
            ConfigurationServiceTokenLease rejected = await first.WaitAsync(Failsafe);

            Task<ConfigurationServiceTokenLease> recovery = Refresh(handler, rejected);
            PendingRequest replacement = await _grants.NextAsync() ?? throw new TimeoutException();
            Task<ConfigurationServiceTokenLease> duplicateRejection = Refresh(handler, rejected);
            Task<ConfigurationServiceTokenLease> coldCaller = Lease(handler);
            _grantsWhilePending = _grants.Count;

            replacement.Answer(HttpStatusCode.OK, ValidGrantBody("bearer-2", 600));
            _joined = await Task.WhenAll(recovery, duplicateRejection, coldCaller).WaitAsync(Failsafe);
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_sends_one_replacement_grant()
        {
            _grantsWhilePending.Should().Be(2);
            _grants.Count.Should().Be(2);
        }

        [Test]
        public void It_gives_every_joiner_the_replacement_generation()
        {
            _joined.Select(lease => lease.AccessToken).Should().Equal("bearer-2", "bearer-2", "bearer-2");
            _joined.Select(lease => lease.Generation).Distinct().Should().ContainSingle();
        }
    }

    /// <summary>
    /// A replacement grant that fails starts its window. Further rejections of the same lease and
    /// cold callers inside it receive the safe failure without a grant; one grant is admitted after it.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Rejection_During_A_Failed_Replacement_Window
        : ConfigurationServiceAuthenticatedReadTests
    {
        private ServiceProvider _provider = null!;
        private ScriptedTransport _grants = null!;
        private Exception? _recoveryFailure;
        private Exception? _repeatedRejectionFailure;
        private Exception? _coldCallerFailure;
        private int _grantsInsideWindow;
        private ConfigurationServiceTokenLease _afterWindow = null!;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _grants = AnsweringByAttempt(attempt =>
                attempt == 2
                    ? JsonResponse(HttpStatusCode.InternalServerError, TokenShapedBody)
                    : JsonResponse(HttpStatusCode.OK, ValidGrantBody($"bearer-{attempt}", 600))
            );
            _provider = BuildRegisteredProvider(new RecordingLoggerProvider(), fakeTime, _grants, _grants);
            var handler = _provider.GetRequiredService<IConfigurationServiceTokenHandler>();

            ConfigurationServiceTokenLease rejected = await Lease(handler).WaitAsync(Failsafe);
            _recoveryFailure = await FailureOf(Refresh(handler, rejected));
            _repeatedRejectionFailure = await FailureOf(Refresh(handler, rejected));
            _coldCallerFailure = await FailureOf(Lease(handler));
            _grantsInsideWindow = _grants.Requests.Count;

            fakeTime.Advance(TimeSpan.FromSeconds(5));
            _afterWindow = await Lease(handler).WaitAsync(Failsafe);
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_fails_the_recovery_with_the_grant_status() =>
            _recoveryFailure
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_answers_later_rejections_and_cold_callers_with_the_same_failure()
        {
            _repeatedRejectionFailure
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .Be(HttpStatusCode.InternalServerError);
            _coldCallerFailure
                .Should()
                .BeOfType<HttpRequestException>()
                .Which.StatusCode.Should()
                .Be(HttpStatusCode.InternalServerError);
        }

        [Test]
        public void It_sends_no_grant_inside_the_window() => _grantsInsideWindow.Should().Be(2);

        [Test]
        public void It_admits_one_grant_after_the_window()
        {
            _grants.Requests.Should().HaveCount(3);
            _afterWindow.AccessToken.Should().Be("bearer-3");
        }
    }

    /// <summary>
    /// After one successful recovery, a separate call through any provider rejects its replacement
    /// within five seconds. That call must still obtain a new bearer and succeed after one replay.
    /// </summary>
    [TestFixtureSource(typeof(ConfigurationServiceAuthenticatedReadTests), nameof(AllReads))]
    [Parallelizable]
    public class Given_A_Rejection_After_A_Successful_Recovery(ReadCase read)
        : ConfigurationServiceAuthenticatedReadTests
    {
        private ServiceProvider _provider = null!;
        private ScriptedTransport _grants = null!;
        private ProtectedReadTransport _reads = null!;
        private object? _firstResult;
        private Exception? _firstFailure;
        private object? _laterResult;
        private Exception? _laterFailure;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _grants = IssuingGrants(fakeTime, 600);
            _reads = new ProtectedReadTransport(
                (_, attempt) =>
                {
                    string successBody = attempt == 2 ? "[]" : read.SuccessBody;
                    return attempt is 1 or 3 ? Rejected() : JsonResponse(HttpStatusCode.OK, successBody);
                }
            );
            _provider = BuildRegisteredProvider(new RecordingLoggerProvider(), fakeTime, _grants, _reads);
            var services = new ReadServices(_provider);

            (_firstResult, _firstFailure) = await Capture(ClaimSetsRead.Invoke(services));
            fakeTime.Advance(TimeSpan.FromSeconds(1));
            (_laterResult, _laterFailure) = await Capture(read.Invoke(services));
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_completes_the_first_recovery()
        {
            _firstFailure.Should().BeNull();
            ClaimSetsRead.AssertSuccess(_firstResult);
        }

        [Test]
        public void It_obtains_another_replacement_for_the_later_call() =>
            _grants.Requests.Should().HaveCount(3);

        [Test]
        public void It_replays_each_call_with_its_replacement() =>
            _reads
                .Reads.Select(sent => sent.Bearer)
                .Should()
                .Equal(
                    $"{TokenSentinel}-1",
                    $"{TokenSentinel}-2",
                    $"{TokenSentinel}-2",
                    $"{TokenSentinel}-3"
                );

        [Test]
        public void It_completes_the_later_call()
        {
            _laterFailure.Should().BeNull();
            read.AssertSuccess(_laterResult);
        }
    }

    /// <summary>A lease never prints its bearer through default formatting.</summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Formatted_Lease : ConfigurationServiceAuthenticatedReadTests
    {
        [Test]
        public void It_keeps_the_bearer_out_of_its_text() =>
            new ConfigurationServiceTokenLease(TokenSentinel, 1)
                .ToString()
                .Should()
                .NotContain(TokenSentinel);
    }
}
