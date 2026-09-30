// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

/// <summary>
/// One scripted Configuration Service reply: a status with a JSON body, or an exception thrown in
/// place of a response (a transport failure).
/// </summary>
internal sealed record CmsReply(HttpStatusCode StatusCode, string Body, Exception? Exception)
{
    public static CmsReply Json(string body) => new(HttpStatusCode.OK, body, null);

    public static CmsReply Status(HttpStatusCode statusCode) => new(statusCode, "", null);

    public static CmsReply Throws(Exception exception) => new(default, "", exception);

    public HttpResponseMessage ToResponse() =>
        Exception is not null
            ? throw Exception
            : new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            };
}

/// <summary>
/// Holds the next request on a route until the test releases it with a reply. <see cref="Observed" />
/// completes once the held request has arrived.
/// </summary>
internal sealed class CmsGate
{
    private readonly TaskCompletionSource _observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CmsReply> _released = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public Task Observed => _observed.Task;

    public void Release(CmsReply reply) => _released.TrySetResult(reply);

    internal async Task<CmsReply> WaitAsync(CancellationToken cancellationToken)
    {
        _observed.TrySetResult();
        return await _released.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>
/// A scripted, routing Configuration Service HTTP double for the profile provider. Routes are keyed
/// by (tenant, path); a route scripted without a tenant serves every tenant that has no route of its
/// own, and supplies the healthy reply of a tenant route that scripts only failures. A route can
/// fail a set number of times, or until it is made healthy, before serving its healthy reply, and
/// its next request can be held behind a gate. Requests are counted per (tenant, path). It sits behind the production <see cref="ConfigurationServiceResponseHandler" />,
/// so status-code handling is the real one.
/// </summary>
internal sealed class CmsProfileHttpDouble : HttpMessageHandler
{
    private const string BaseAddress = "https://cms.example/";

    private readonly Lock _lock = new();
    private readonly Dictionary<(string? Tenant, string Path), Route> _routes = [];
    private readonly Dictionary<(string? Tenant, string Path), int> _requestCounts = [];
    private readonly List<CapturedRequest> _requests = [];
    private readonly List<HttpClient> _clients = [];
    private int _inFlight;
    private int _maxInFlight;

    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    public void Serve(string path, CmsReply reply, string? tenant = null)
    {
        lock (_lock)
        {
            GetOrAddRoute(tenant, path).Healthy = reply;
        }
    }

    public void FailTimes(string path, CmsReply failure, int times, string? tenant = null)
    {
        lock (_lock)
        {
            Route route = GetOrAddRoute(tenant, path);
            route.Failure = failure;
            route.RemainingFailures = times;
        }
    }

    public void FailUntilHealthy(string path, CmsReply failure, string? tenant = null) =>
        FailTimes(path, failure, int.MaxValue, tenant);

    public void MakeHealthy(string path, string? tenant = null)
    {
        lock (_lock)
        {
            GetOrAddRoute(tenant, path).RemainingFailures = 0;
        }
    }

    public CmsGate GateNext(string path, string? tenant = null)
    {
        var gate = new CmsGate();
        lock (_lock)
        {
            GetOrAddRoute(tenant, path).Gate = gate;
        }
        return gate;
    }

    /// <summary>
    /// Requests currently inside this double, including any held behind a gate.
    /// </summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// The most requests that were ever inside this double at once.
    /// </summary>
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>
    /// Requests for a path across every tenant.
    /// </summary>
    public int RequestCount(string path)
    {
        lock (_lock)
        {
            return _requestCounts.Where(entry => entry.Key.Path == path).Sum(entry => entry.Value);
        }
    }

    /// <summary>
    /// Requests for a path that carried the given Tenant header (null: no Tenant header).
    /// </summary>
    public int RequestCount(string path, string? tenant)
    {
        lock (_lock)
        {
            return _requestCounts.GetValueOrDefault((tenant, path));
        }
    }

    /// <summary>
    /// The real provider over the production response handler and this double, with a faked token
    /// handler that returns <c>cms-token</c> unless one is supplied.
    /// </summary>
    public ConfigurationServiceProfileProvider CreateProvider(
        ILogger<ConfigurationServiceProfileProvider>? logger = null,
        IConfigurationServiceTokenHandler? tokenHandler = null,
        TimeSpan? timeout = null
    )
    {
        var responseHandler = new ConfigurationServiceResponseHandler(
            NullLogger<ConfigurationServiceResponseHandler>.Instance
        )
        {
            InnerHandler = this,
        };
        var client = new HttpClient(responseHandler, disposeHandler: false)
        {
            BaseAddress = new Uri(BaseAddress),
        };
        if (timeout is { } clientTimeout)
        {
            client.Timeout = clientTimeout;
        }

        lock (_lock)
        {
            _clients.Add(client);
        }

        if (tokenHandler is null)
        {
            tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() =>
                    tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._)
                )
                .Returns("cms-token");
        }

        return new ConfigurationServiceProfileProvider(
            new ConfigurationServiceApiClient(client),
            tokenHandler,
            new ConfigurationServiceContext("cms-client", "cms-secret", "cms-scope"),
            logger ?? NullLogger<ConfigurationServiceProfileProvider>.Instance
        );
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        string path = request.RequestUri!.AbsolutePath;
        string? tenant = request.Headers.TryGetValues("Tenant", out IEnumerable<string>? tenants)
            ? tenants.Single()
            : null;

        CmsReply reply;
        CmsGate? gate;
        lock (_lock)
        {
            _requests.Add(new CapturedRequest(request.Method, path, tenant, request.Headers.Authorization));
            _requestCounts[(tenant, path)] = _requestCounts.GetValueOrDefault((tenant, path)) + 1;

            Route route =
                _routes.GetValueOrDefault((tenant, path))
                ?? _routes.GetValueOrDefault((null, path))
                ?? throw new InvalidOperationException($"No CMS route scripted for {path}");

            gate = route.Gate;
            route.Gate = null;

            if (route.RemainingFailures > 0)
            {
                route.RemainingFailures--;
                reply = route.Failure!;
            }
            else
            {
                // A tenant route that only scripts failures falls back to the shared healthy reply.
                reply =
                    route.Healthy
                    ?? _routes.GetValueOrDefault((null, path))?.Healthy
                    ?? throw new InvalidOperationException($"No healthy CMS reply scripted for {path}");
            }
        }

        int inFlight = Interlocked.Increment(ref _inFlight);
        int observedMax;
        while (inFlight > (observedMax = Volatile.Read(ref _maxInFlight)))
        {
            Interlocked.CompareExchange(ref _maxInFlight, inFlight, observedMax);
        }

        try
        {
            if (gate is not null)
            {
                reply = await gate.WaitAsync(cancellationToken);
            }

            return reply.ToResponse();
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                foreach (HttpClient client in _clients)
                {
                    client.Dispose();
                }
                _clients.Clear();
            }
        }
        base.Dispose(disposing);
    }

    private Route GetOrAddRoute(string? tenant, string path)
    {
        if (!_routes.TryGetValue((tenant, path), out Route? route))
        {
            route = new Route();
            _routes[(tenant, path)] = route;
        }
        return route;
    }

    private sealed class Route
    {
        public CmsReply? Healthy { get; set; }

        public CmsReply? Failure { get; set; }

        public int RemainingFailures { get; set; }

        public CmsGate? Gate { get; set; }
    }
}

internal sealed record CapturedRequest(
    HttpMethod Method,
    string Path,
    string? Tenant,
    AuthenticationHeaderValue? Authorization
);

/// <summary>
/// The provider fetch under test.
/// </summary>
public enum ProfileCmsFetch
{
    ProfileDetail,
    ApplicationProfileInfo,
    ProfileCatalog,
}

/// <summary>
/// A Configuration Service failure that must surface as unavailable, never as absent.
/// </summary>
public enum ProfileCmsFailure
{
    InternalServerError,
    ServiceUnavailable,
    Unauthorized,
    NotFound,
    Transport,
    Timeout,
    MalformedJson,
    NullBody,
    TokenFailure,
}

internal static class ProfileCmsJson
{
    public const string StudentProfileDefinition =
        """<Profile name="StudentProfile"><Resource name="Student"><ReadContentType memberSelection="IncludeOnly" /></Resource></Profile>""";

    public const string StudentProfile =
        """{"id":5,"name":"StudentProfile","definition":"<Profile name=\"StudentProfile\"><Resource name=\"Student\"><ReadContentType memberSelection=\"IncludeOnly\" /></Resource></Profile>"}""";

    public const string Application =
        """{"id":7,"applicationName":"App","vendorId":1,"claimSetName":"SIS","educationOrganizationIds":[255901],"dataStoreIds":[1],"profileIds":[5,6]}""";

    public const string Catalog = """[{"id":5,"name":"StudentProfile"},{"id":6,"name":"SchoolProfile"}]""";
}

public class ConfigurationServiceProfileProviderTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_A_Profile_Returned_By_Cms
    {
        private CmsProfileHttpDouble _cms = null!;
        private CmsProfileResponse? _result;

        [SetUp]
        public async Task Setup()
        {
            _cms = new CmsProfileHttpDouble();
            _cms.Serve("/v3/profiles/5", CmsReply.Json(ProfileCmsJson.StudentProfile));

            _result = await _cms.CreateProvider().GetProfileAsync(5, "tenant-a");
        }

        [TearDown]
        public void TearDown() => _cms.Dispose();

        [Test]
        public void It_maps_the_id_name_and_definition()
        {
            _result
                .Should()
                .Be(new CmsProfileResponse(5, "StudentProfile", ProfileCmsJson.StudentProfileDefinition));
        }

        [Test]
        public void It_requests_the_profile_by_id()
        {
            CapturedRequest request = _cms.Requests.Should().ContainSingle().Subject;
            request.Method.Should().Be(HttpMethod.Get);
            request.Path.Should().Be("/v3/profiles/5");
        }

        [Test]
        public void It_sends_the_bearer_token()
        {
            _cms.Requests.Single()
                .Authorization.Should()
                .Be(new AuthenticationHeaderValue("Bearer", "cms-token"));
        }

        [Test]
        public void It_sends_the_tenant_header()
        {
            _cms.Requests.Single().Tenant.Should().Be("tenant-a");
        }
    }

    /// <summary>
    /// A listed profile deleted before its detail is fetched gets a CMS 404. Through the production
    /// response handler that must still read as "this profile is absent", so the catalog skips it
    /// instead of failing the whole attempt.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Profile_Cms_Reports_Not_Found
    {
        private CmsProfileHttpDouble _cms = null!;
        private CmsProfileResponse? _result;

        [SetUp]
        public async Task Setup()
        {
            _cms = new CmsProfileHttpDouble();
            _cms.Serve("/v3/profiles/5", CmsReply.Status(HttpStatusCode.NotFound));

            _result = await _cms.CreateProvider().GetProfileAsync(5, null);
        }

        [TearDown]
        public void TearDown() => _cms.Dispose();

        [Test]
        public void It_returns_null()
        {
            _result.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Application_Profile_Info_Returned_By_Cms
    {
        private CmsProfileHttpDouble _cms = null!;
        private ApplicationProfileInfo? _result;

        [SetUp]
        public async Task Setup()
        {
            _cms = new CmsProfileHttpDouble();
            _cms.Serve("/v3/applications/7", CmsReply.Json(ProfileCmsJson.Application));

            _result = await _cms.CreateProvider().GetApplicationProfileInfoAsync(7, null);
        }

        [TearDown]
        public void TearDown() => _cms.Dispose();

        [Test]
        public void It_maps_the_application_id()
        {
            _result!.ApplicationId.Should().Be(7);
        }

        [Test]
        public void It_maps_the_profile_ids()
        {
            _result!.ProfileIds.Should().BeEquivalentTo(new long[] { 5, 6 });
        }

        [Test]
        public void It_requests_the_application_by_id()
        {
            CapturedRequest request = _cms.Requests.Should().ContainSingle().Subject;
            request.Method.Should().Be(HttpMethod.Get);
            request.Path.Should().Be("/v3/applications/7");
        }

        [Test]
        public void It_sends_the_bearer_token()
        {
            _cms.Requests.Single()
                .Authorization.Should()
                .Be(new AuthenticationHeaderValue("Bearer", "cms-token"));
        }

        [Test]
        public void It_sends_no_tenant_header_without_a_tenant()
        {
            _cms.Requests.Single().Tenant.Should().BeNull();
        }
    }

    /// <summary>
    /// The application id comes from the client's resolved application context, so a CMS 404 for it
    /// is a disagreement between DMS and CMS, not "no profiles assigned". Reading it as absent would
    /// cache unprofiled access, so it must surface as unavailable.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Application_Cms_Reports_Not_Found
    {
        private CmsProfileHttpDouble _cms = null!;
        private RecordingLogger<ConfigurationServiceProfileProvider> _logger = null!;
        private Exception? _thrown;

        [SetUp]
        public async Task Setup()
        {
            _cms = new CmsProfileHttpDouble();
            _logger = new RecordingLogger<ConfigurationServiceProfileProvider>();
            _cms.Serve("/v3/applications/7", CmsReply.Status(HttpStatusCode.NotFound));

            try
            {
                await _cms.CreateProvider(_logger).GetApplicationProfileInfoAsync(7, null);
            }
            catch (Exception ex)
            {
                _thrown = ex;
            }
        }

        [TearDown]
        public void TearDown() => _cms.Dispose();

        [Test]
        public void It_throws_profile_data_unavailable()
        {
            _thrown.Should().BeOfType<ProfileDataUnavailableException>();
        }

        [Test]
        public void It_logs_the_failure_once()
        {
            _logger.Records.Count(record => record.Level == LogLevel.Error).Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Profile_Catalog_Returned_By_Cms
    {
        private CmsProfileHttpDouble _cms = null!;
        private IReadOnlyList<CmsProfileResponse> _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _cms = new CmsProfileHttpDouble();
            _cms.Serve("/v3/profiles", CmsReply.Json(ProfileCmsJson.Catalog));

            _result = await _cms.CreateProvider().GetProfilesAsync("tenant-a");
        }

        [TearDown]
        public void TearDown() => _cms.Dispose();

        [Test]
        public void It_returns_every_listed_profile()
        {
            _result
                .Select(profile => profile.Name)
                .Should()
                .BeEquivalentTo("StudentProfile", "SchoolProfile");
        }

        [Test]
        public void It_maps_each_id_to_its_name()
        {
            _result.Single(profile => profile.Name == "StudentProfile").Id.Should().Be(5);
            _result.Single(profile => profile.Name == "SchoolProfile").Id.Should().Be(6);
        }

        [Test]
        public void It_requests_the_profile_list()
        {
            CapturedRequest request = _cms.Requests.Should().ContainSingle().Subject;
            request.Method.Should().Be(HttpMethod.Get);
            request.Path.Should().Be("/v3/profiles");
        }

        [Test]
        public void It_sends_the_bearer_token()
        {
            _cms.Requests.Single()
                .Authorization.Should()
                .Be(new AuthenticationHeaderValue("Bearer", "cms-token"));
        }

        [Test]
        public void It_sends_the_tenant_header()
        {
            _cms.Requests.Single().Tenant.Should().Be("tenant-a");
        }
    }

    /// <summary>
    /// Only an empty list from CMS is an empty catalog.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Empty_Profile_Catalog_Returned_By_Cms
    {
        private CmsProfileHttpDouble _cms = null!;
        private IReadOnlyList<CmsProfileResponse> _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _cms = new CmsProfileHttpDouble();
            _cms.Serve("/v3/profiles", CmsReply.Json("[]"));

            _result = await _cms.CreateProvider().GetProfilesAsync(null);
        }

        [TearDown]
        public void TearDown() => _cms.Dispose();

        [Test]
        public void It_returns_an_empty_catalog()
        {
            _result.Should().BeEmpty();
        }
    }

    /// <summary>
    /// Every failure other than a CMS 404 on a detail or application fetch must surface as
    /// <see cref="ProfileDataUnavailableException" />, never as "absent" (null) or "no profiles" (an
    /// empty list). A 404 on the list endpoint is a failure too. The cause is kept, and the failure is
    /// logged once and wrapped once.
    /// </summary>
    [TestFixtureSource(nameof(Cases))]
    [Parallelizable]
    public class Given_A_Profile_Cms_Fetch_That_Fails(ProfileCmsFetch fetch, ProfileCmsFailure failure)
    {
        private static readonly TimeSpan _clientTimeout = TimeSpan.FromMilliseconds(200);

        private CmsProfileHttpDouble _cms = null!;
        private RecordingLogger<ConfigurationServiceProfileProvider> _logger = null!;
        private Exception? _cause;
        private Exception? _thrown;

        private static IEnumerable<TestFixtureData> Cases()
        {
            ProfileCmsFailure[] commonFailures =
            [
                ProfileCmsFailure.InternalServerError,
                ProfileCmsFailure.ServiceUnavailable,
                ProfileCmsFailure.Unauthorized,
                ProfileCmsFailure.Transport,
                ProfileCmsFailure.Timeout,
                ProfileCmsFailure.MalformedJson,
                ProfileCmsFailure.NullBody,
                ProfileCmsFailure.TokenFailure,
            ];

            foreach (ProfileCmsFetch fetch in Enum.GetValues<ProfileCmsFetch>())
            {
                foreach (ProfileCmsFailure failure in commonFailures)
                {
                    yield return new TestFixtureData(fetch, failure);
                }
            }

            yield return new TestFixtureData(ProfileCmsFetch.ProfileCatalog, ProfileCmsFailure.NotFound);
        }

        private string Path =>
            fetch switch
            {
                ProfileCmsFetch.ProfileDetail => "/v3/profiles/5",
                ProfileCmsFetch.ApplicationProfileInfo => "/v3/applications/7",
                _ => "/v3/profiles",
            };

        private string HealthyBody =>
            fetch switch
            {
                ProfileCmsFetch.ProfileDetail => ProfileCmsJson.StudentProfile,
                ProfileCmsFetch.ApplicationProfileInfo => ProfileCmsJson.Application,
                _ => ProfileCmsJson.Catalog,
            };

        [SetUp]
        public async Task Setup()
        {
            _cms = new CmsProfileHttpDouble();
            _logger = new RecordingLogger<ConfigurationServiceProfileProvider>();
            IConfigurationServiceTokenHandler? tokenHandler = null;
            TimeSpan? timeout = null;

            switch (failure)
            {
                case ProfileCmsFailure.InternalServerError:
                    _cms.Serve(Path, CmsReply.Status(HttpStatusCode.InternalServerError));
                    break;
                case ProfileCmsFailure.ServiceUnavailable:
                    _cms.Serve(Path, CmsReply.Status(HttpStatusCode.ServiceUnavailable));
                    break;
                case ProfileCmsFailure.Unauthorized:
                    _cms.Serve(Path, CmsReply.Status(HttpStatusCode.Unauthorized));
                    break;
                case ProfileCmsFailure.NotFound:
                    _cms.Serve(Path, CmsReply.Status(HttpStatusCode.NotFound));
                    break;
                case ProfileCmsFailure.Transport:
                    _cause = new HttpRequestException("Connection refused (cms.example:443)");
                    _cms.Serve(Path, CmsReply.Throws(_cause));
                    break;
                case ProfileCmsFailure.Timeout:
                    // A real HttpClient timeout: the request is held until the client gives up on it.
                    _cms.Serve(Path, CmsReply.Json(HealthyBody));
                    _cms.GateNext(Path);
                    timeout = _clientTimeout;
                    break;
                case ProfileCmsFailure.MalformedJson:
                    _cms.Serve(Path, CmsReply.Json("{not json"));
                    break;
                case ProfileCmsFailure.NullBody:
                    _cms.Serve(Path, CmsReply.Json("null"));
                    break;
                case ProfileCmsFailure.TokenFailure:
                    _cause = new InvalidOperationException("Token endpoint unreachable");
                    _cms.Serve(Path, CmsReply.Json(HealthyBody));
                    tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
                    A.CallTo(() =>
                            tokenHandler.GetTokenAsync(
                                A<string>._,
                                A<string>._,
                                A<string>._,
                                A<CancellationToken>._
                            )
                        )
                        .ThrowsAsync(_cause);
                    break;
            }

            ConfigurationServiceProfileProvider provider = _cms.CreateProvider(
                _logger,
                tokenHandler,
                timeout
            );

            try
            {
                switch (fetch)
                {
                    case ProfileCmsFetch.ProfileDetail:
                        await provider.GetProfileAsync(5, null);
                        break;
                    case ProfileCmsFetch.ApplicationProfileInfo:
                        await provider.GetApplicationProfileInfoAsync(7, null);
                        break;
                    default:
                        await provider.GetProfilesAsync(null);
                        break;
                }
            }
            catch (Exception ex)
            {
                _thrown = ex;
            }
        }

        [TearDown]
        public void TearDown() => _cms.Dispose();

        [Test]
        public void It_throws_profile_data_unavailable()
        {
            _thrown.Should().BeOfType<ProfileDataUnavailableException>();
        }

        [Test]
        public void It_keeps_the_cause()
        {
            Exception? inner = _thrown!.InnerException;

            switch (failure)
            {
                case ProfileCmsFailure.InternalServerError:
                case ProfileCmsFailure.ServiceUnavailable:
                case ProfileCmsFailure.Unauthorized:
                case ProfileCmsFailure.NotFound:
                    // Thrown by the production response handler for the non-success status.
                    inner.Should().BeOfType<HttpRequestException>();
                    inner!.Message.Should().Contain($"StatusCode: {ExpectedStatusCode()}");
                    break;
                case ProfileCmsFailure.Timeout:
                    inner.Should().BeOfType<TaskCanceledException>();
                    inner!.InnerException.Should().BeOfType<TimeoutException>();
                    break;
                case ProfileCmsFailure.MalformedJson:
                    inner.Should().BeAssignableTo<JsonException>();
                    break;
                case ProfileCmsFailure.NullBody:
                    // Thrown directly for the null body; the catch-all must not wrap it a second time.
                    inner.Should().BeNull();
                    break;
                default:
                    inner.Should().BeSameAs(_cause);
                    break;
            }
        }

        [Test]
        public void It_logs_the_failure_once()
        {
            _logger.Records.Count(record => record.Level == LogLevel.Error).Should().Be(1);
        }

        private HttpStatusCode ExpectedStatusCode() =>
            failure switch
            {
                ProfileCmsFailure.InternalServerError => HttpStatusCode.InternalServerError,
                ProfileCmsFailure.ServiceUnavailable => HttpStatusCode.ServiceUnavailable,
                ProfileCmsFailure.Unauthorized => HttpStatusCode.Unauthorized,
                _ => HttpStatusCode.NotFound,
            };
    }
}
