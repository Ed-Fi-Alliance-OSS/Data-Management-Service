// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.Profile;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;
using Respawn;
using OpenIddictIdentityOptions = EdFi.DmsConfigurationService.Backend.OpenIddict.Models.IdentityOptions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Spec §5 step 3.4: the signing-key snapshot through the whole CMS pipeline against a real database. Keys are real
/// <c>OpenIddictKey</c> rows, tokens come from <c>/connect/token</c>, and requests go through the real bearer scheme,
/// snapshot provider and refresh service. <c>OpenIddictKey</c> is reset before and after each test, and only after
/// every host (and with it the refresh service) has been disposed (Q16). Labels as in the spec: [F] demonstrates the
/// fix, [C] is compatibility.
/// </summary>
[NonParallelizable]
public class SigningKeyPipelineTests : DatabaseTest
{
    private const string EncryptionKey = "Dms1556PipelineEncryptionKey32Ch";
    private const string ClientId = "dms1556-pipeline";
    private const string ClientSecret = "ValidClientSecret1234567890!Abcd";
    private const string ProfileName = "Dms1556PipelineProfile";

    /// <summary>The host's connections carry this name, so its backends can be told apart from the fixture's.</summary>
    protected const string HostApplicationName = "dms1556-pipeline-host";

    /// <summary>The role claim as the token generator issues it; the Test settings' short <c>role</c> would not match.</summary>
    private const string RoleClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

    protected const string ProfileDefinition =
        $"""<Profile name="{ProfileName}"><Resource name="School"><ReadContentType memberSelection="IncludeOnly"><Property name="NameOfInstitution" /></ReadContentType></Resource></Profile>""";

    private readonly List<WebApplicationFactory<Program>> _hosts = [];
    private Respawner _keyRespawner = null!;

    [SetUp]
    public async Task ResetKeysBefore()
    {
        _keyRespawner = await Respawner.CreateAsync(
            Connection!,
            new RespawnerOptions
            {
                TablesToInclude = [new("dmscs", "OpenIddictKey")],
                DbAdapter = DbAdapter.Postgres,
            }
        );
        await _keyRespawner.ResetAsync(Connection!);
    }

    [TearDown]
    public async Task DisposeHostsThenResetKeys()
    {
        foreach (WebApplicationFactory<Program> host in _hosts)
        {
            await host.DisposeAsync();
        }

        _hosts.Clear();
        await _keyRespawner.ResetAsync(Connection!);
    }

    /// <summary>
    /// A CMS host on the test database with database signing keys. Its snapshot provider is wrapped by
    /// <see cref="CountingSnapshotProvider"/>; nothing else is replaced. The host starts with its first request.
    /// <paramref name="unknownKeyCooldownSeconds"/>, when given, replaces the 30 s default so a fixture need not wait it.
    /// </summary>
    protected WebApplicationFactory<Program> CreateHost(int? unknownKeyCooldownSeconds = null)
    {
        string connectionString = new NpgsqlConnectionStringBuilder(
            Configuration.DatabaseOptions.Value.DatabaseConnection
        )
        {
            ApplicationName = HostApplicationName,
        }.ToString();

        WebApplicationFactory<Program> host = new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("AppSettings:Datastore", "postgresql");
                builder.UseSetting("AppSettings:MultiTenancy", "false");
                builder.UseSetting("DatabaseSettings:DatabaseConnection", connectionString);
                builder.UseSetting("IdentitySettings:EncryptionKey", EncryptionKey);
                builder.UseSetting("IdentitySettings:UseCertificates", "false");
                builder.UseSetting("IdentitySettings:RoleClaimType", RoleClaimType);
                if (unknownKeyCooldownSeconds is { } cooldown)
                {
                    builder.UseSetting(
                        "IdentitySettings:SigningKeyUnknownKeyRefreshCooldownSeconds",
                        cooldown.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    );
                }

                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ISigningKeySnapshotProvider>();
                    services.AddSingleton(serviceProvider => new SigningKeySnapshotProvider(
                        serviceProvider.GetRequiredService<ISigningKeySource>(),
                        serviceProvider.GetRequiredService<IOptions<OpenIddictIdentityOptions>>(),
                        serviceProvider.GetRequiredService<TimeProvider>(),
                        serviceProvider.GetRequiredService<ILogger<SigningKeySnapshotProvider>>()
                    ));
                    services.AddSingleton<ISigningKeySnapshotProvider>(
                        serviceProvider => new CountingSnapshotProvider(
                            serviceProvider.GetRequiredService<SigningKeySnapshotProvider>()
                        )
                    );
                });
            }
        );
        _hosts.Add(host);
        return host;
    }

    protected static CountingSnapshotProvider ProviderOf(WebApplicationFactory<Program> host) =>
        (CountingSnapshotProvider)host.Services.GetRequiredService<ISigningKeySnapshotProvider>();

    /// <summary>Inserts an active signing key as the setup scripts do: SPKI public key, encrypted PKCS#8 private key.</summary>
    protected async Task InsertKeyAsync(string keyId, RSA key, DateTime createdAt) =>
        await Connection!.ExecuteAsync(
            """
            INSERT INTO "dmscs"."OpenIddictKey" ("KeyId", "PublicKey", "PrivateKey", "IsActive", "CreatedAt")
            VALUES (@KeyId, @PublicKey, pgp_sym_encrypt(@PrivateKey, @EncryptionKey), TRUE, @CreatedAt)
            """,
            new
            {
                KeyId = keyId,
                PublicKey = key.ExportSubjectPublicKeyInfo(),
                PrivateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
                EncryptionKey,
                CreatedAt = createdAt,
            }
        );

    protected async Task RetireKeyAsync(string keyId) =>
        await Connection!.ExecuteAsync(
            "UPDATE \"dmscs\".\"OpenIddictKey\" SET \"IsActive\" = FALSE WHERE \"KeyId\" = @KeyId",
            new { KeyId = keyId }
        );

    protected async Task<int> InsertProfileAsync()
    {
        ProfileRepository repository = new(
            Configuration.DatabaseOptions,
            NullLogger<ProfileRepository>.Instance,
            new TestAuditContext(),
            new TenantContextProvider()
        );
        ProfileInsertResult result = await repository.InsertProfile(
            new ProfileInsertCommand { Name = ProfileName, Definition = ProfileDefinition }
        );
        return result.Should().BeOfType<ProfileInsertResult.Success>().Subject.Id;
    }

    /// <summary>Registers the fixture's client through <c>/connect/register</c>.</summary>
    protected static async Task RegisterClientAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsync(
            "/connect/register",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["ClientId"] = ClientId,
                    ["ClientSecret"] = ClientSecret,
                    ["DisplayName"] = ClientId,
                }
            )
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Obtains an access token for the fixture's client through <c>/connect/token</c>.</summary>
    protected static async Task<string> MintAsync(HttpClient client)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["scope"] = AuthorizationScopes.AdminScope.Name,
                }
            ),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"))
        );

        using HttpResponseMessage response = await client.SendAsync(request);
        string content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        return JsonNode.Parse(content)!["access_token"]!.GetValue<string>();
    }

    protected static string KeyIdOf(string token) => new JsonWebToken(token).Kid;

    protected static Task<HttpResponseMessage> GetProfileAsync(HttpClient client, string token, int profileId)
    {
        HttpRequestMessage request = new(HttpMethod.Get, $"/v3/profiles/{profileId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    /// <summary>Polls (in real time, bounded) until <paramref name="condition"/> holds.</summary>
    protected static async Task WaitUntilAsync(Func<bool> condition, Func<string> failure)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException(failure());
            }

            await Task.Delay(5);
        }
    }

    /// <summary>Counts the requests for a usable snapshot, so a test can see how many callers wait on a load.</summary>
    protected sealed class CountingSnapshotProvider(ISigningKeySnapshotProvider inner)
        : ISigningKeySnapshotProvider
    {
        private int _usableRequests;

        public int UsableRequests => Volatile.Read(ref _usableRequests);

        public SigningKeySnapshot? Current => inner.Current;

        public SigningKeyProviderStatus Status => inner.Status;

        public DateTimeOffset NextAttemptAt => inner.NextAttemptAt;

        public Task AttemptStateChanged => inner.AttemptStateChanged;

        public Task<SigningKeySnapshot> GetUsableAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _usableRequests);
            return inner.GetUsableAsync(cancellationToken);
        }

        public Task<SigningKeyRefreshOutcome> RefreshAsync(
            SigningKeyRefreshTrigger trigger,
            CancellationToken cancellationToken
        ) => inner.RefreshAsync(trigger, cancellationToken);

        public Task<SigningKeyRefreshOutcome> RefreshIfUnchangedAsync(
            SigningKeyRefreshTrigger trigger,
            long observedStateVersion,
            CancellationToken cancellationToken
        ) => inner.RefreshIfUnchangedAsync(trigger, observedStateVersion, cancellationToken);

        public Task<SigningKeyUnknownKeyOutcome> TryRefreshForUnknownKeyAsync(
            string keyId,
            CancellationToken cancellationToken
        ) => inner.TryRefreshForUnknownKeyAsync(keyId, cancellationToken);
    }

    // 3.4-a [F]: a token issued before a restart, and 64 concurrent requests on the restarted, cold instance while its
    // startup key read is held on the server. Every request waits on that one load, which publishes the only
    // snapshot, and every one is answered 200 with the profile once it does. The host's backends are recorded only.
    [TestFixture]
    public class Given_64_concurrent_requests_on_a_cold_instance : SigningKeyPipelineTests
    {
        private const int Requests = 64;
        private int _profileId;
        private int _completedBeforeTheLoad;
        private int _peakHostBackends;
        private CountingSnapshotProvider _provider = null!;
        private HttpResponseMessage[] _responses = [];
        private JsonObject[] _bodies = [];

        [SetUp]
        public async Task Act()
        {
            using RSA key = RSA.Create(2048);
            await InsertKeyAsync("dms1556-key-a", key, DateTime.UtcNow.AddMinutes(-1));
            _profileId = await InsertProfileAsync();

            string token;
            WebApplicationFactory<Program> issuing = CreateHost();
            using (HttpClient client = issuing.CreateClient())
            {
                await RegisterClientAsync(client);
                token = await MintAsync(client);
            }

            await issuing.DisposeAsync();
            _hosts.Remove(issuing);

            await using NpgsqlConnection lockConnection = await DataSource!.OpenConnectionAsync();
            await using NpgsqlTransaction keyTableLock = await lockConnection.BeginTransactionAsync();
            await lockConnection.ExecuteAsync(
                "LOCK TABLE \"dmscs\".\"OpenIddictKey\" IN ACCESS EXCLUSIVE MODE",
                transaction: keyTableLock
            );

            WebApplicationFactory<Program> cold = CreateHost();
            HttpClient coldClient = cold.CreateClient();
            _provider = ProviderOf(cold);
            _provider.Current.Should().BeNull("the startup load is held behind the table lock");

            Task<HttpResponseMessage>[] requests =
            [
                .. Enumerable.Range(0, Requests).Select(_ => GetProfileAsync(coldClient, token, _profileId)),
            ];
            await WaitUntilAsync(
                () => _provider.UsableRequests >= Requests,
                () => $"Only {_provider.UsableRequests} requests reached the provider."
            );
            _completedBeforeTheLoad = requests.Count(request => request.IsCompleted);

            using CancellationTokenSource stopSampling = new();
            Task<int> sampling = SamplePeakHostBackendsAsync(stopSampling.Token);

            await keyTableLock.RollbackAsync();
            _responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(60));
            _bodies = await Task.WhenAll(
                _responses.Select(async response =>
                    JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject()
                )
            );

            await stopSampling.CancelAsync();
            _peakHostBackends = await sampling;
            await TestContext.Out.WriteLineAsync(
                $"3.4-a observational: peak {HostApplicationName} backends during the release = {_peakHostBackends}"
            );
        }

        /// <summary>The largest number of the host's backends seen in <c>pg_stat_activity</c> until canceled.</summary>
        private async Task<int> SamplePeakHostBackendsAsync(CancellationToken cancellationToken)
        {
            await using NpgsqlConnection observer = await DataSource!.OpenConnectionAsync(
                CancellationToken.None
            );
            int peak = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                int current = await observer.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM pg_stat_activity WHERE application_name = @Name",
                    new { Name = HostApplicationName }
                );
                peak = Math.Max(peak, current);
                try
                {
                    await Task.Delay(20, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return peak;
        }

        [TearDown]
        public void DisposeResponses()
        {
            foreach (HttpResponseMessage response in _responses)
            {
                response.Dispose();
            }
        }

        [Test]
        public void It_holds_every_request_until_the_keys_are_loaded() =>
            _completedBeforeTheLoad.Should().Be(0);

        [Test]
        public void It_answers_every_request_with_200() =>
            _responses.Select(response => response.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.OK);

        [Test]
        public void It_returns_the_expected_profile_to_every_request() =>
            _bodies
                .Select(body => (body["id"]!.GetValue<int>(), body["definition"]!.GetValue<string>()))
                .Should()
                .AllBeEquivalentTo((_profileId, ProfileDefinition));

        [Test]
        public void It_served_every_request_from_the_one_startup_snapshot()
        {
            _provider.Current!.Version.Should().Be(1);
            _provider.Status.ConsecutiveFailures.Should().Be(0);
        }
    }

    // 3.4-b [F]: a newer active key inserted while the instance is warm. The next mint signs with it, and the token is
    // accepted on its first sighting: the unknown-key refresh is eligible (the cooldown has passed) and finds the key.
    [TestFixture]
    public class Given_a_newer_active_key_inserted : SigningKeyPipelineTests
    {
        private const int CooldownSeconds = 2;
        private string _firstKeyId = null!;
        private string _secondKeyId = null!;
        private long _versionBeforeTheNewKey;
        private bool _newKeyInTheSnapshotBeforeTheRequest;
        private HttpResponseMessage _response = null!;
        private CountingSnapshotProvider _provider = null!;

        [SetUp]
        public async Task Act()
        {
            using RSA first = RSA.Create(2048);
            using RSA second = RSA.Create(2048);
            await InsertKeyAsync("dms1556-key-a", first, DateTime.UtcNow.AddMinutes(-2));
            int profileId = await InsertProfileAsync();

            WebApplicationFactory<Program> host = CreateHost(CooldownSeconds);
            HttpClient client = host.CreateClient();
            _provider = ProviderOf(host);
            await RegisterClientAsync(client);
            _firstKeyId = KeyIdOf(await MintAsync(client));
            await WaitUntilAsync(
                () => _provider.Current is not null,
                () => "The startup load never published a snapshot."
            );
            _versionBeforeTheNewKey = _provider.Current!.Version;

            await InsertKeyAsync("dms1556-key-b", second, DateTime.UtcNow.AddMinutes(-1));
            string token = await MintAsync(client);
            _secondKeyId = KeyIdOf(token);

            // Past the cooldown, measured from the last completed load: the startup load, the only one so far.
            DateTimeOffset eligibleAt = _provider.Current!.RetrievedAt.AddSeconds(CooldownSeconds + 0.5);
            await WaitUntilAsync(
                () => DateTimeOffset.UtcNow > eligibleAt,
                () => "The cooldown never passed."
            );
            _newKeyInTheSnapshotBeforeTheRequest = _provider.Current.ContainsKeyId("dms1556-key-b");
            _response = await GetProfileAsync(client, token, profileId);
        }

        [TearDown]
        public void DisposeResponse() => _response.Dispose();

        [Test]
        public void It_signed_the_first_token_with_the_only_key() => _firstKeyId.Should().Be("dms1556-key-a");

        [Test]
        public void It_signs_the_next_token_with_the_newer_key() => _secondKeyId.Should().Be("dms1556-key-b");

        [Test]
        public void It_had_not_loaded_the_newer_key_before_the_request() =>
            _newKeyInTheSnapshotBeforeTheRequest.Should().BeFalse();

        [Test]
        public void It_accepts_the_token_on_its_first_sighting() =>
            _response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_published_the_newer_key_through_the_unknown_key_refresh()
        {
            _provider.Current!.Version.Should().BeGreaterThan(_versionBeforeTheNewKey);
            _provider.Current.ContainsKeyId("dms1556-key-b").Should().BeTrue();
        }
    }

    // 3.4-c [F]: the old key retired (IsActive = false) and the snapshot refreshed: a token it signed is rejected with
    // the ordinary 401, not a dependency 503.
    [TestFixture]
    public class Given_the_old_key_retired_and_the_snapshot_refreshed : SigningKeyPipelineTests
    {
        private HttpResponseMessage _beforeRetirement = null!;
        private SigningKeyRefreshOutcome _refresh = null!;
        private HttpResponseMessage _afterRetirement = null!;
        private CountingSnapshotProvider _provider = null!;

        [SetUp]
        public async Task Act()
        {
            using RSA first = RSA.Create(2048);
            using RSA second = RSA.Create(2048);
            await InsertKeyAsync("dms1556-key-a", first, DateTime.UtcNow.AddMinutes(-2));
            int profileId = await InsertProfileAsync();

            WebApplicationFactory<Program> host = CreateHost();
            HttpClient client = host.CreateClient();
            _provider = ProviderOf(host);
            await RegisterClientAsync(client);
            string oldToken = await MintAsync(client);
            KeyIdOf(oldToken).Should().Be("dms1556-key-a");
            _beforeRetirement = await GetProfileAsync(client, oldToken, profileId);

            await InsertKeyAsync("dms1556-key-b", second, DateTime.UtcNow.AddMinutes(-1));
            await RetireKeyAsync("dms1556-key-a");
            _refresh = await _provider.RefreshAsync(SigningKeyRefreshTrigger.Request, CancellationToken.None);

            _afterRetirement = await GetProfileAsync(client, oldToken, profileId);
        }

        [TearDown]
        public void DisposeResponses()
        {
            _beforeRetirement.Dispose();
            _afterRetirement.Dispose();
        }

        [Test]
        public void It_accepted_the_token_before_the_retirement() =>
            _beforeRetirement.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_refreshed_to_a_snapshot_without_the_retired_key()
        {
            _refresh.Should().BeOfType<SigningKeyRefreshOutcome.Succeeded>();
            _provider.Current!.ContainsKeyId("dms1556-key-a").Should().BeFalse();
            _provider.Current.ContainsKeyId("dms1556-key-b").Should().BeTrue();
        }

        [Test]
        public void It_rejects_the_old_token_with_an_ordinary_401()
        {
            _afterRetirement.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            _afterRetirement.Headers.WwwAuthenticate.Select(header => header.Scheme).Should().Equal("Bearer");
            _afterRetirement.Headers.Contains("Retry-After").Should().BeFalse();
        }
    }
}
