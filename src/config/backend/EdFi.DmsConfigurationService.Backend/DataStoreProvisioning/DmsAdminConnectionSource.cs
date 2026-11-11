// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// Resolves <c>AdminConnectionString</c>: a value without references is used as configured; each
/// reference is resolved through the registered <see cref="ISecretResolver"/> and the shared
/// <see cref="SecretValueCache"/> (tenant-less: the administrative connection belongs to the deployment, not to
/// a tenant), and substituted into the value of the key that held it, so a resolved value that needs quoting
/// is quoted by the connection-string syntax rather than spliced into the text. One resolution may take at
/// most <c>SecretsSettings:ResolveTimeoutSeconds</c> in all. A fetch this resolution starts is bounded by
/// what remains of that allowance on its own, inside the cache, as <c>ConnectionStringReader</c> bounds its
/// fetches: the resolver runs on a task the host owns, its token is cancelled at the deadline, and the fetch
/// then fails, so the cache neither keeps a pending call that later operations would join nor stores what the
/// resolver returns afterwards. A fetch another resolution started is waited on only for this resolution's
/// remaining allowance. Nothing the resolver returns or raises is logged or carried: a failure reports a fixed
/// operation and, when an exception caused it, the exception type chain. Each success is an immutable
/// snapshot bound per asynchronous flow (<see cref="Bind"/>); no resolution changes another flow's binding.
/// </summary>
public sealed class DmsAdminConnectionSource(
    IOptions<DmsDataStoreProvisioningOptions> options,
    SecretValueCache cache,
    IOptions<SecretsOptions> secretsOptions,
    TimeProvider timeProvider,
    ISecretResolver? secretResolver = null
) : IDmsAdminConnectionSource
{
    /// <summary>The <c>Diagnostic.Operation</c> prefix of every resolution failure.</summary>
    public const string ResolveStep = "ResolveAdminConnection";

    /// <summary>The value holds references and no <see cref="ISecretResolver"/> is registered.</summary>
    public const string NoResolverStep = ResolveStep + ".NoResolver";

    /// <summary>The resolver did not return within the resolution's bound.</summary>
    public const string TimedOutStep = ResolveStep + ".TimedOut";

    /// <summary>The resolver raised an exception; the diagnostic carries its type chain.</summary>
    public const string ResolverFailedStep = ResolveStep + ".ResolverFailed";

    /// <summary>The resolver returned an empty value.</summary>
    public const string EmptyValueStep = ResolveStep + ".EmptyValue";

    /// <summary>The configured value could not be parsed, or could not take a resolved value.</summary>
    public const string UnusableValueStep = ResolveStep + ".UnusableValue";

    private readonly TimeSpan _resolveTimeout = TimeSpan.FromSeconds(
        secretsOptions.Value.ResolveTimeoutSeconds
    );

    private readonly AsyncLocal<DmsAdminConnectionString?> _bound = new();

    private TaskCompletionSource? _abandonedCallObserved;

    /// <summary>The fetch's own deadline passing, kept apart from any exception the resolver raises.</summary>
    public sealed class FetchTimedOutException : Exception;

    /// <summary>Restores the binding that was in place when it was made.</summary>
    private sealed class Binding(
        AsyncLocal<DmsAdminConnectionString?> bound,
        DmsAdminConnectionString? previous
    ) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                bound.Value = previous;
            }
        }
    }

    public string Current =>
        _bound.Value?.Value
        ?? throw new InvalidOperationException(
            "No DMS provisioning administrative connection string is bound to this operation."
        );

    public IDisposable Bind(DmsAdminConnectionString connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Binding binding = new(_bound, _bound.Value);
        _bound.Value = connection;
        return binding;
    }

    /// <summary>
    /// Completes once the outcome of a call a fetch abandoned at its deadline has been observed and dropped, so a
    /// test can wait for it instead of guessing when it ran. It completes once, for the first such call.
    /// </summary>
    internal Task AbandonedCallObserved => AbandonedCallSignal().Task;

    public async Task<DmsAdminConnectionResolution> ResolveAsync(CancellationToken cancellationToken)
    {
        string configured =
            options.Value.AdminConnectionString
            ?? throw new InvalidOperationException(
                "DmsDataStoreProvisioning:AdminConnectionString is not configured; static validation should have refused it."
            );

        if (SecretReferenceTokens.Find(configured).Count == 0)
        {
            return new DmsAdminConnectionResolution.Resolved(new DmsAdminConnectionString(configured));
        }

        if (secretResolver is null)
        {
            return Failed(NoResolverStep);
        }

        DbConnectionStringBuilder builder = new();
        try
        {
            builder.ConnectionString = configured;
        }
        catch (Exception)
        {
            // The parser's message repeats the text it could not parse.
            return Failed(UnusableValueStep);
        }

        long started = timeProvider.GetTimestamp();
        if (await SubstituteEveryKeyAsync(builder, secretResolver, started, cancellationToken) is { } failure)
        {
            return failure;
        }

        return new DmsAdminConnectionResolution.Resolved(
            new DmsAdminConnectionString(builder.ConnectionString)
        );
    }

    /// <summary>
    /// Resolves the references in every value of <paramref name="builder"/> and writes the result back
    /// through it. The keys are taken up front because each assignment writes through the same builder; a
    /// keyword assigned twice keeps its last value, so a reference the syntax discarded is never resolved.
    /// </summary>
    private async Task<DmsAdminConnectionResolution?> SubstituteEveryKeyAsync(
        DbConnectionStringBuilder builder,
        ISecretResolver resolver,
        long started,
        CancellationToken cancellationToken
    )
    {
        foreach (string key in builder.Keys.Cast<string>().ToList())
        {
            if (
                builder[key] is not string value
                || SecretReferenceTokens.Find(value) is not { Count: > 0 } tokens
            )
            {
                continue;
            }

            (string? substituted, DmsAdminConnectionResolution? failure) = await SubstituteAsync(
                resolver,
                value,
                tokens,
                started,
                cancellationToken
            );
            if (failure is not null)
            {
                return failure;
            }

            try
            {
                builder[key] = substituted;
            }
            catch (Exception)
            {
                // The builder may refuse a value, and its message may repeat the value it refused.
                return Failed(UnusableValueStep);
            }
        }

        return null;
    }

    /// <summary>Replaces every reference in one value with its resolved text, or reports the first failure.</summary>
    private async Task<(string? Value, DmsAdminConnectionResolution? Failure)> SubstituteAsync(
        ISecretResolver resolver,
        string value,
        IReadOnlyList<SecretReferenceToken> tokens,
        long started,
        CancellationToken cancellationToken
    )
    {
        System.Text.StringBuilder substituted = new(value.Length);
        int position = 0;
        foreach (SecretReferenceToken token in tokens)
        {
            (string? resolved, DmsAdminConnectionResolution? failure) = await ResolveTokenAsync(
                resolver,
                token.Name,
                started,
                cancellationToken
            );
            if (failure is not null)
            {
                return (null, failure);
            }

            substituted.Append(value, position, token.Index - position).Append(resolved);
            position = token.Index + token.Length;
        }

        return (substituted.Append(value, position, value.Length - position).ToString(), null);
    }

    private async Task<(string? Value, DmsAdminConnectionResolution? Failure)> ResolveTokenAsync(
        ISecretResolver resolver,
        string name,
        long started,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan remaining = _resolveTimeout - timeProvider.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
        {
            // Asking again would wait beyond this resolution's allowance, so only a value already cached is used.
            return cache.TryGetFresh(null, name, out string? cached)
                ? (cached, null)
                : (null, Failed(TimedOutStep));
        }

        // The cache invokes the factory only for the resolution that starts the call, and before it first
        // yields, so this tells a call this resolution started from one it joined.
        bool startedCall = false;
        Task<string> fetch = cache.GetOrFetchAsync(
            null,
            name,
            () =>
            {
                startedCall = true;
                return FetchAsync(resolver, new SecretReference(name, null), remaining);
            }
        );

        string value;
        try
        {
            // A call this resolution started is bounded by its own deadline; a joined one only by this
            // resolution's remaining allowance.
            value = startedCall
                ? await fetch.WaitAsync(cancellationToken)
                : await fetch.WaitAsync(remaining, timeProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FetchTimedOutException)
        {
            return (null, Failed(TimedOutStep));
        }
        catch (TimeoutException) when (!startedCall && !fetch.IsCompleted)
        {
            return (null, Failed(TimedOutStep));
        }
        catch (Exception exception)
        {
            return (
                null,
                new DmsAdminConnectionResolution.Failed(
                    DmsProvisioningDiagnostic.From(exception, ResolverFailedStep)
                )
            );
        }

        return string.IsNullOrEmpty(value) ? (null, Failed(EmptyValueStep)) : (value, null);
    }

    /// <summary>
    /// Calls the resolver on a task the host owns and races it against <paramref name="allowance"/>, so a
    /// resolver that blocks before returning its <see cref="ValueTask{TResult}"/> is bounded as well as one that
    /// returns an incomplete one. At the deadline the resolver's token is cancelled and the fetch fails, which
    /// releases the cache's flight for the next caller; the late outcome is observed and dropped, so nothing
    /// the call returns afterwards can reach the cache. The deadline is armed before the resolver is invoked.
    /// </summary>
    private async Task<string> FetchAsync(
        ISecretResolver resolver,
        SecretReference reference,
        TimeSpan allowance
    )
    {
        using CancellationTokenSource deadlineCancellation = new();
        Task deadline = Task.Delay(allowance, timeProvider, deadlineCancellation.Token);

        CancellationTokenSource cancellation = new();
        Task<string> call = Task.Run(
            () => resolver.ResolveAsync(reference, cancellation.Token).AsTask(),
            cancellation.Token
        );

        if (await Task.WhenAny(call, deadline) == call)
        {
            await deadlineCancellation.CancelAsync();
            cancellation.Dispose();
            return await call;
        }

        Task cancelled = cancellation.CancelAsync();
        _ = Task.WhenAll(call, cancelled)
            .ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    cancellation.Dispose();
                    AbandonedCallSignal().TrySetResult();
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default
            );

        throw new FetchTimedOutException();
    }

    private TaskCompletionSource AbandonedCallSignal() =>
        LazyInitializer.EnsureInitialized(
            ref _abandonedCallObserved,
            () => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        );

    private static DmsAdminConnectionResolution.Failed Failed(string operation) =>
        new(DmsProvisioningDiagnostic.ForOperation(operation));
}
