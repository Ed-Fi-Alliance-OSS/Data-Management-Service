// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.Profile;
using FakeItEasy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OpenIddictIdentityOptions = EdFi.DmsConfigurationService.Backend.OpenIddict.Models.IdentityOptions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// The key table and the token table behind the real token manager and snapshot provider: signing keys are held per
/// key id, the active ones are served as public-key rows, and every read is counted. A key read can be held behind a
/// gate or made to fail; a status read can be made to fail.
/// </summary>
internal sealed class PipelineTokenStore
{
    private readonly ConcurrentDictionary<string, RSA> _signingKeys = new(StringComparer.Ordinal);
    private readonly List<string> _activeKeyIds = [];
    private readonly ConcurrentDictionary<Guid, string> _statuses = new();
    private int _keyReads;
    private int _statusReads;

    public PipelineTokenStore()
    {
        A.CallTo(() => Repository.GetActivePublicKeysAsync(A<CancellationToken>._))
            .ReturnsLazily(
                (CancellationToken cancellationToken) =>
                {
                    Interlocked.Increment(ref _keyReads);
                    return KeyReadBehavior is { } behavior ? behavior(cancellationToken) : ActiveRowsAsync();
                }
            );
        A.CallTo(() => Repository.GetTokenStatusAsync(A<Guid>._))
            .ReturnsLazily(
                (Guid tokenId) =>
                {
                    Interlocked.Increment(ref _statusReads);
                    if (StatusFailure is { } failure)
                    {
                        return Task.FromException<string?>(failure);
                    }

                    return Task.FromResult(_statuses.TryGetValue(tokenId, out var status) ? status : null);
                }
            );
    }

    public IOpenIddictTokenRepository Repository { get; } = A.Fake<IOpenIddictTokenRepository>();

    /// <summary>What a key read does instead of returning the active rows; <see langword="null"/> returns them.</summary>
    public Func<CancellationToken, Task<IEnumerable<PublicKeyInfo>>>? KeyReadBehavior { get; set; }

    /// <summary>When set, every status read fails with it.</summary>
    public Exception? StatusFailure { get; set; }

    public int KeyReads => Volatile.Read(ref _keyReads);

    public int StatusReads => Volatile.Read(ref _statusReads);

    /// <summary>Creates a signing key and makes it active.</summary>
    public void AddKey(string keyId)
    {
        _signingKeys[keyId] = RSA.Create(2048);
        lock (_activeKeyIds)
        {
            _activeKeyIds.Add(keyId);
        }
    }

    /// <summary>Makes a key inactive; tokens can still be signed with it.</summary>
    public void RetireKey(string keyId)
    {
        lock (_activeKeyIds)
        {
            _activeKeyIds.Remove(keyId);
        }
    }

    public RSA SigningKey(string keyId) => _signingKeys[keyId];

    public void FailKeyReads(Exception exception) =>
        KeyReadBehavior = _ => Task.FromException<IEnumerable<PublicKeyInfo>>(exception);

    /// <summary>Holds every key read until the returned gate is opened; a held read honors cancellation.</summary>
    public TaskCompletionSource GateKeyReads()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyReadBehavior = async cancellationToken =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return await ActiveRowsAsync();
        };
        return gate;
    }

    public void SetStatus(Guid tokenId, string status) => _statuses[tokenId] = status;

    /// <summary>
    /// A token for the host's issuer and audience with the scope and role every profile endpoint requires, signed by
    /// <paramref name="signer"/> (by default the key named <paramref name="keyId"/>), whose <c>kid</c> header is
    /// <paramref name="keyId"/> (omitted when <see langword="null"/>), and stored with <paramref name="status"/>.
    /// </summary>
    public (string Token, Guid TokenId) Mint(
        string? keyId,
        RSA? signer = null,
        string issuer = BearerPipelineHost.Issuer,
        string audience = BearerPipelineHost.Audience,
        DateTime? expires = null,
        string status = "valid"
    )
    {
        Guid tokenId = Guid.NewGuid();
        DateTime expiresAt = expires ?? DateTime.UtcNow.AddMinutes(10);
        string token = new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                IssuedAt = expiresAt.AddMinutes(-30),
                NotBefore = expiresAt.AddMinutes(-30),
                Expires = expiresAt,
                Claims = new Dictionary<string, object>
                {
                    [JwtRegisteredClaimNames.Jti] = tokenId.ToString(),
                    ["scope"] = AuthorizationScopes.AdminScope.Name,
                    [BearerPipelineHost.RoleClaimType] = BearerPipelineHost.ConfigServiceRole,
                },
                SigningCredentials = new SigningCredentials(
                    new RsaSecurityKey(signer ?? SigningKey(keyId!)) { KeyId = keyId },
                    SecurityAlgorithms.RsaSha256
                ),
            }
        );
        SetStatus(tokenId, status);
        return (token, tokenId);
    }

    private Task<IEnumerable<PublicKeyInfo>> ActiveRowsAsync()
    {
        string[] active;
        lock (_activeKeyIds)
        {
            active = [.. _activeKeyIds];
        }

        return Task.FromResult<IEnumerable<PublicKeyInfo>>([
            .. active.Select(keyId => new PublicKeyInfo
            {
                KeyId = keyId,
                PublicKey = _signingKeys[keyId].ExportSubjectPublicKeyInfo(),
            }),
        ]);
    }
}

/// <summary>Counts the requests for a usable snapshot, so a test can see how many callers are waiting on a load.</summary>
internal sealed class CountingSnapshotProvider(ISigningKeySnapshotProvider inner)
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

/// <summary>Counts the handler's requests for its configuration.</summary>
internal sealed class CountingConfigurationManager(IConfigurationManager<OpenIdConnectConfiguration> inner)
    : IConfigurationManager<OpenIdConnectConfiguration>
{
    private int _calls;

    public IConfigurationManager<OpenIdConnectConfiguration> Inner => inner;

    public int Calls => Volatile.Read(ref _calls);

    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        Interlocked.Increment(ref _calls);
        return inner.GetConfigurationAsync(cancel);
    }

    public void RequestRefresh() => inner.RequestRefresh();
}

/// <summary>Records every request sent over the scheme's backchannel and answers 404 (I-5: there should be none).</summary>
internal sealed class RecordingBackchannelHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Uri?> _sent = new();

    public IReadOnlyCollection<Uri?> Sent => [.. _sent];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        _sent.Enqueue(request.RequestUri);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}

/// <summary>
/// The application with its real <c>Bearer</c> scheme, real token manager, real snapshot provider and real refresh
/// service, over <see cref="PipelineTokenStore"/>, a faked profile repository and fake time (spec §5 step 3.1). The
/// provider is wrapped by <see cref="CountingSnapshotProvider"/> and the scheme's configuration manager by
/// <see cref="CountingConfigurationManager"/>; the scheme's backchannel handler is
/// <see cref="RecordingBackchannelHandler"/>. The manager is wrapped after every other post-configuration, so a scheme
/// left without a manager would get the framework's HTTP manager, wrapped, and it would send over the backchannel.
/// Arrange <see cref="Store"/> before <see cref="StartAsync"/>: the refresh service's startup load begins with the host.
/// </summary>
internal sealed class BearerPipelineHost : IDisposable
{
    public const string Issuer = "http://localhost/realms/dms";
    public const string Audience = "account";
    public const string ConfigServiceRole = "test-role";

    /// <summary>
    /// The role claim as <c>JwtTokenGenerator</c> issues it and the production settings expect it. The Test settings'
    /// short <c>role</c> would be renamed by the handler's inbound claim mapping, so the host restores this one.
    /// </summary>
    public const string RoleClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

    public const string ProfileDefinition =
        "<Profile name=\"TestProfile\"><Resource name=\"Resource1\"></Resource></Profile>";

    private const string UnreachableDatabase =
        "host=127.0.0.1;port=1;database=unreachable;username=none;timeout=1";

    private readonly WebApplicationFactory<Program> _factory;
    private HttpClient? _client;
    private CountingConfigurationManager? _manager;

    public BearerPipelineHost()
    {
        A.CallTo(() => Profiles.GetProfile(A<int>._))
            .ReturnsLazily(
                (int id) =>
                    new ProfileGetResult.Success(
                        new ProfileResponse
                        {
                            Id = id,
                            Name = "TestProfile",
                            Definition = ProfileDefinition,
                        }
                    )
            );

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("DatabaseSettings:DatabaseConnection", UnreachableDatabase);
            builder.UseSetting("IdentitySettings:RoleClaimType", RoleClaimType);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Time);
                services.RemoveAll<IOpenIddictTokenRepository>();
                services.AddSingleton(Store.Repository);
                services.RemoveAll<IProfileRepository>();
                services.AddSingleton(Profiles);

                services.RemoveAll<ISigningKeySnapshotProvider>();
                services.AddSingleton(serviceProvider => new SigningKeySnapshotProvider(
                    serviceProvider.GetRequiredService<ISigningKeySource>(),
                    serviceProvider.GetRequiredService<IOptions<OpenIddictIdentityOptions>>(),
                    Time,
                    serviceProvider.GetRequiredService<ILogger<SigningKeySnapshotProvider>>()
                ));
                services.AddSingleton<ISigningKeySnapshotProvider>(
                    serviceProvider => new CountingSnapshotProvider(
                        serviceProvider.GetRequiredService<SigningKeySnapshotProvider>()
                    )
                );

                services.Configure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options => options.BackchannelHttpHandler = Backchannel
                );
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options =>
                    {
                        _manager = new CountingConfigurationManager(options.ConfigurationManager!);
                        options.ConfigurationManager = _manager;
                    }
                );
            });
        });
    }

    /// <summary>Starts at the same instant in every fixture; only the provider and the refresh service read it.</summary>
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));

    public PipelineTokenStore Store { get; } = new();

    public IProfileRepository Profiles { get; } = A.Fake<IProfileRepository>();

    public RecordingBackchannelHandler Backchannel { get; } = new();

    /// <summary>The scheme's configuration manager, once the scheme's options exist.</summary>
    public CountingConfigurationManager Manager =>
        _manager ?? throw new InvalidOperationException("The Bearer scheme's options have not been built.");

    public CountingSnapshotProvider Provider =>
        (CountingSnapshotProvider)_factory.Services.GetRequiredService<ISigningKeySnapshotProvider>();

    public IServiceProvider Services => _factory.Services;

    private HttpClient Client =>
        _client ?? throw new InvalidOperationException("Start the host before sending requests.");

    /// <summary>Starts the host, and with it the refresh service's startup load, and waits for that load's key read.</summary>
    public async Task StartAsync()
    {
        _client = _factory.CreateClient();
        await WaitUntilAsync(() => Store.KeyReads >= 1, () => "The startup load never read the key table.");
    }

    /// <summary>Waits for the startup load to publish, so later fake-time moves cannot time it out.</summary>
    public async Task StartWarmAsync()
    {
        await StartAsync();
        await WaitUntilAsync(
            () => Provider.Current is not null,
            () => "The startup load never published a snapshot."
        );
    }

    public Task<HttpResponseMessage> GetProfileAsync(string? token) =>
        SendAsync(token is null ? null : new AuthenticationHeaderValue("Bearer", token));

    public Task<HttpResponseMessage> SendAsync(AuthenticationHeaderValue? authorization)
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/v3/profiles/7");
        request.Headers.Authorization = authorization;
        return Client.SendAsync(request);
    }

    /// <summary>Polls (in real time, bounded) until <paramref name="condition"/> holds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, Func<string> failure)
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

    public void Dispose()
    {
        _client?.Dispose();
        _factory.Dispose();
    }
}
