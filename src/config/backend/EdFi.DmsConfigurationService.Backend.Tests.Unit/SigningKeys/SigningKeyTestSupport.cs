// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

/// <summary>
/// A faked key repository whose active-public-key read is scripted per test and records the fake time and token of
/// every call.
/// </summary>
internal sealed class KeyRepositoryHarness
{
    private static readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    private readonly List<(DateTimeOffset At, CancellationToken Token)> _calls = [];

    public KeyRepositoryHarness(TimeProvider timeProvider)
    {
        A.CallTo(() => Repository.GetActivePublicKeysAsync(A<CancellationToken>._))
            .ReturnsLazily(
                (CancellationToken cancellationToken) =>
                {
                    lock (_calls)
                    {
                        _calls.Add((timeProvider.GetUtcNow(), cancellationToken));
                    }

                    return Behavior(cancellationToken);
                }
            );
    }

    public IOpenIddictTokenRepository Repository { get; } = A.Fake<IOpenIddictTokenRepository>();

    /// <summary>What the next read does; by default it returns no rows.</summary>
    public Func<CancellationToken, Task<IEnumerable<PublicKeyInfo>>> Behavior { get; set; } =
        _ => Task.FromResult(Enumerable.Empty<PublicKeyInfo>());

    public IReadOnlyList<(DateTimeOffset At, CancellationToken Token)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>A row for a key id; the same id always carries the same RSA public key.</summary>
    public static PublicKeyInfo Row(string keyId) =>
        new()
        {
            KeyId = keyId,
            PublicKey = _keys.GetOrAdd(
                keyId,
                static _ =>
                {
                    using RSA rsa = RSA.Create(2048);
                    return rsa.ExportSubjectPublicKeyInfo();
                }
            ),
        };

    /// <summary>A row whose key material is in no supported format.</summary>
    public static PublicKeyInfo Garbage(string keyId) => new() { KeyId = keyId, PublicKey = [1, 2, 3] };

    public void Returns(params PublicKeyInfo[] rows) =>
        Behavior = _ => Task.FromResult<IEnumerable<PublicKeyInfo>>(rows);

    public void ReturnsKeys(params string[] keyIds) => Returns([.. keyIds.Select(Row)]);

    public void Fails(Exception exception) =>
        Behavior = _ => Task.FromException<IEnumerable<PublicKeyInfo>>(exception);

    /// <summary>Holds every read until the returned gate is opened; the read honors cancellation.</summary>
    public TaskCompletionSource<IEnumerable<PublicKeyInfo>> Gate()
    {
        TaskCompletionSource<IEnumerable<PublicKeyInfo>> gate = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Behavior = cancellationToken =>
        {
            cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
            return gate.Task;
        };
        return gate;
    }
}

/// <summary>Always returns the same sample, so a 0.5 sample makes the backoff jitter factor exactly 1.</summary>
internal sealed class FixedRandom(double sample) : Random
{
    public override double NextDouble() => sample;
}

internal static class SigningKeyTestSupport
{
    public static readonly DateTimeOffset Start = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    public static FakeTimeProvider NewTime() => new(Start);

    public static SigningKeySnapshotProvider Provider(
        KeyRepositoryHarness harness,
        TimeProvider timeProvider,
        IdentityOptions? options = null,
        ILogger<SigningKeySnapshotProvider>? logger = null
    ) =>
        new(
            new DatabaseSigningKeySource(harness.Repository, NullLogger<DatabaseSigningKeySource>.Instance),
            Options.Create(options ?? new IdentityOptions()),
            timeProvider,
            logger ?? NullLogger<SigningKeySnapshotProvider>.Instance,
            new FixedRandom(0.5)
        );

    /// <summary>Awaits with a real-time guard so a broken implementation fails instead of hanging the run.</summary>
    public static Task<T> Bounded<T>(this Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    public static IReadOnlyList<string> MessagesAt<T>(ILogger<T> logger, LogLevel level) =>
        Fake.GetCalls(logger)
            .Where(call => call.Method.Name == nameof(ILogger.Log) && call.GetArgument<LogLevel>(0) == level)
            .Select(call => call.Arguments[2]?.ToString() ?? string.Empty)
            .ToList();
}
