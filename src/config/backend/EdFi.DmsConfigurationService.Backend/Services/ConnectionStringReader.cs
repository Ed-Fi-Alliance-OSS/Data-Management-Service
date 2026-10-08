// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// Whether a derivative row is the resource a client asked for, or an optional part of the data
/// store being read.
/// </summary>
public enum DerivativeReadMode
{
    Resource,
    PartOfDataStore,
}

/// <summary>
/// The row a stored connection string belongs to, which is all the read rule needs to know about it
/// and all its diagnostics name. The row's <c>Provider</c> column is deliberately absent: the builder
/// is the configured engine's, because derivative rows carry no provider.
/// </summary>
public abstract record ConnectionStringRow
{
    private ConnectionStringRow() { }

    public sealed record DataStore(long DataStoreId) : ConnectionStringRow
    {
        public override string ToString() => $"data store {DataStoreId}";
    }

    public sealed record Derivative(
        long DerivativeId,
        long DataStoreId,
        string DerivativeType,
        DerivativeReadMode ReadAs
    ) : ConnectionStringRow
    {
        public override string ToString() =>
            $"data store derivative {DerivativeId} ({LoggingUtility.SanitizeForLog(DerivativeType)}) of data store {DataStoreId}";
    }
}

/// <summary>
/// A stored connection string could not be turned into a response value. The message is the host's
/// own, names the row and, where one is involved, the secret reference, and never carries the text of
/// the stored value, a resolved value, or an exception raised below this point. No inner exception
/// is attached, because the repositories log a caught exception with its whole chain.
/// </summary>
public sealed class ConnectionStringReadException(string message) : Exception(message);

/// <summary>
/// The one read rule for a stored connection string, called wherever a stored value becomes a
/// response value, so the data store and derivative reads on both engines cannot drift apart.
/// </summary>
public interface IConnectionStringReader
{
    /// <summary>
    /// Returns the Base64 cipher text a response carries for the stored value, resolving any
    /// <c>${secret:&lt;name&gt;}</c> reference it holds. Throws
    /// <see cref="ConnectionStringReadException"/> when the value cannot be returned; returns null
    /// instead for a derivative read as part of a data store, whose value cannot be decrypted, parsed
    /// or resolved, or cannot take a value it resolved.
    /// </summary>
    Task<string?> ReadAsync(byte[]? stored, ConnectionStringRow row);
}

/// <summary>
/// Decrypts every stored value, because whether it carries a secret reference is not knowable
/// otherwise. A value with no reference is returned as the Base64 of the bytes it was given, so the
/// response is byte-identical to one produced without this rule. A value with references is parsed
/// by the configured engine's builder, each reference is replaced inside the keyword value that holds
/// it, and the builder's rendering is re-encrypted. Substitution goes through the builder rather than
/// through the text, because the builder applies the provider's own quoting to a value an operator
/// chose, and a resolved value is never scanned again.
///
/// Transient, like the repositories that call it, because it reads the request's tenant from the
/// scoped tenant provider. The cache it consults is the singleton, and takes the tenant as an
/// argument.
///
/// Being transient also makes one instance one repository read, which is what bounds a slow or hung
/// resolver. Rows are read one at a time, so a timeout on each call alone would let a read of many
/// rows wait out one timeout per row, and a store that answers each call just inside it would cost
/// one wait per distinct uncached name. The timeout is therefore the read's: it is armed at the first
/// reference the read resolves and covers every later one. Once it passes, every later reference the read
/// meets is served from the cache or reported unresolved without asking again, so a read waits at
/// most one timeout. A data store read nests one derivative read, so it waits at most two. The
/// allowance is measured on the monotonic clock, so a step in the system clock cannot shorten or
/// stretch it, and it belongs to the instance, so a caller that kept one repository across reads would
/// share one allowance among them and, once it was spent, serve only cached values; no caller does.
///
/// A read that joins a call another read started waits on that call's own deadline, which may pass
/// before the joining read's allowance does. That read reports the reference unresolved and keeps
/// what is left of its allowance for the references after it.
/// </summary>
public sealed class ConnectionStringReader(
    IConnectionStringEncryptionService encryptionService,
    IDataStoreConnectionStringBuilderSource builderSource,
    SecretValueCache cache,
    IOptions<SecretsOptions> secretsOptions,
    ITenantContextProvider tenantContextProvider,
    ILogger<ConnectionStringReader> logger,
    ISecretResolver? secretResolver = null,
    TimeProvider? timeProvider = null
) : IConnectionStringReader
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _resolveTimeout = TimeSpan.FromSeconds(
        secretsOptions.Value.ResolveTimeoutSeconds
    );

    private long? _readStarted;

    // Set once this read has waited out its allowance, so a timer that fires a little early cannot
    // leave a sliver of time that sends a later reference back to a resolver that may be hung.
    private bool _allowanceSpent;

    public async Task<string?> ReadAsync(byte[]? stored, ConnectionStringRow row)
    {
        if (stored is null)
        {
            return null;
        }

        string? plainText;

        try
        {
            plainText = encryptionService.Decrypt(stored);
        }
        catch (Exception)
        {
            // The exception says nothing an operator can act on beyond this, and its text is not ours.
            return Unreadable(row, "could not be decrypted");
        }

        IReadOnlyList<SecretReferenceToken> tokens = plainText is null
            ? []
            : SecretReferenceTokens.Find(plainText);

        if (tokens.Count == 0)
        {
            return Convert.ToBase64String(stored);
        }

        if (Parse(plainText!) is not { } builder)
        {
            return Unreadable(row, "could not be parsed by the configured database engine");
        }

        // Resolution works on the values the provider kept: a keyword assigned twice keeps its last
        // value, so a reference the provider discarded is never resolved and cannot reach the
        // rendering below. The keys are taken up front because each assignment writes through the
        // same builder.
        foreach (string key in builder.Keys.Cast<string>().ToList())
        {
            if (
                builder[key] is not string value
                || SecretReferenceTokens.Find(value) is not { Count: > 0 } valueTokens
            )
            {
                continue;
            }

            string? resolved = await SubstituteAsync(value, valueTokens, row);

            if (resolved is null)
            {
                return null;
            }

            try
            {
                builder[key] = resolved;
            }
            catch (Exception)
            {
                // The provider refuses some values, such as one holding a NUL, and its message may
                // repeat the value it refused.
                return Unreadable(row, "could not take a resolved secret value");
            }
        }

        // Rendered even when every reference was discarded, so the stored text that carried one is
        // never returned as it was.
        return Convert.ToBase64String(encryptionService.Encrypt(builder.ConnectionString)!);
    }

    private DbConnectionStringBuilder? Parse(string plainText)
    {
        try
        {
            return builderSource.CreateBuilder(plainText);
        }
        catch (Exception)
        {
            // The provider's message repeats the text it could not parse.
            return null;
        }
    }

    private string? Tenant =>
        tenantContextProvider.Context is TenantContext.Multitenant multitenant
            ? multitenant.TenantName
            : null;

    private static string TenantForLog(string? tenant) =>
        tenant is null ? "(none)" : LoggingUtility.SanitizeForLog(tenant);

    /// <summary>
    /// A stored value that cannot be decrypted or parsed, or that cannot take a resolved value. A
    /// derivative read as part of a data store reads as not configured, as DMS already treats a
    /// derivative it cannot decrypt, so one stale row cannot empty every data store's derivatives.
    /// It is logged as an error, as DMS logs that case: the null this returns takes DMS's silent
    /// not-configured path, so this log is the only report of a defect in the stored value. Anything
    /// read as a resource fails the read.
    /// </summary>
    private string? Unreadable(ConnectionStringRow row, string problem)
    {
        if (row is ConnectionStringRow.Derivative { ReadAs: DerivativeReadMode.PartOfDataStore } derivative)
        {
            logger.LogError(
                "Derivative {DerivativeId} ({DerivativeType}) of data store {DataStoreId} in tenant {Tenant} is treated as not configured: its stored connection string {Problem}",
                derivative.DerivativeId,
                LoggingUtility.SanitizeForLog(derivative.DerivativeType),
                derivative.DataStoreId,
                TenantForLog(Tenant),
                problem
            );
            return null;
        }

        throw new ConnectionStringReadException($"The stored connection string for {row} {problem}.");
    }

    /// <summary>
    /// Rebuilds one keyword value from its original segments, replacing each reference with its
    /// resolved value. Returns null when a derivative read as part of a data store cannot be resolved.
    /// </summary>
    private async Task<string?> SubstituteAsync(
        string value,
        IReadOnlyList<SecretReferenceToken> tokens,
        ConnectionStringRow row
    )
    {
        StringBuilder result = new(value.Length);
        int position = 0;

        foreach (SecretReferenceToken token in tokens)
        {
            string? resolved = await ResolveAsync(token.Name, row);

            if (resolved is null)
            {
                return null;
            }

            result.Append(value, position, token.Index - position).Append(resolved);
            position = token.Index + token.Length;
        }

        return result.Append(value, position, value.Length - position).ToString();
    }

    private async Task<string?> ResolveAsync(string name, ConnectionStringRow row)
    {
        string? tenant = Tenant;

        if (secretResolver is null)
        {
            return Unresolved(
                row,
                name,
                tenant,
                "needs a Configuration Service plugin registering ISecretResolver, and none is registered"
            );
        }

        string? value;
        string outcome;

        _readStarted ??= _timeProvider.GetTimestamp();
        TimeSpan remaining = RemainingAllowance();

        if (remaining <= TimeSpan.Zero)
        {
            // Asking again would wait beyond the time this read may spend on the resolver, so only a
            // value already cached is used.
            return cache.TryGetFresh(tenant, name, out string? cached)
                ? cached
                : Unresolved(
                    row,
                    name,
                    tenant,
                    $"was not resolved: this read had already waited the {secretsOptions.Value.ResolveTimeoutSeconds} seconds it may spend on the resolver"
                );
        }

        // The cache invokes the factory only for the read that starts the call, and before it first
        // yields, so this tells a call this read started from one it joined.
        bool startedCall = false;

        try
        {
            value = await WithinReadDeadlineAsync(
                cache.GetOrFetchAsync(
                    tenant,
                    name,
                    () =>
                    {
                        startedCall = true;
                        return FetchAsync(secretResolver, new SecretReference(name, tenant));
                    }
                ),
                remaining
            );
            outcome = "could not be resolved: the resolver returned no value";
        }
        catch (ReadAllowanceSpentException)
        {
            value = null;
            outcome = AllowanceSpentOutcome();
        }
        catch (SecretResolveTimeoutException) when (startedCall)
        {
            // A call this read started is armed no later than the read's own deadline, so reaching its
            // deadline first means the read's allowance is spent too.
            _allowanceSpent = true;
            value = null;
            outcome = AllowanceSpentOutcome();
        }
        catch (SecretResolveTimeoutException)
        {
            // The call this read joined reached its own deadline first, and the read still has time
            // for the references after this one.
            value = null;
            outcome =
                $"could not be resolved: the call this read joined, which another read started, did not return within its {secretsOptions.Value.ResolveTimeoutSeconds} seconds";
        }
        catch (Exception exception)
        {
            // Reached after the shared fetch, so every caller reports its own row. The exception's
            // type is the host's to report; its message is the plugin's and may describe the secret
            // it was fetching.
            value = null;
            outcome =
                exception is OperationCanceledException
                    ? $"could not be resolved: the resolver was cancelled ({exception.GetType().FullName})"
                    : $"could not be resolved: the resolver failed ({exception.GetType().FullName})";
        }

        return string.IsNullOrEmpty(value) ? Unresolved(row, name, tenant, outcome) : value;
    }

    private TimeSpan RemainingAllowance() =>
        _allowanceSpent ? TimeSpan.Zero : _resolveTimeout - _timeProvider.GetElapsedTime(_readStarted!.Value);

    private string AllowanceSpentOutcome() =>
        $"could not be resolved: the resolver did not return within the {secretsOptions.Value.ResolveTimeoutSeconds} seconds this read may spend on it";

    /// <summary>
    /// A derivative read as part of a data store is optional there, so an unresolvable one reads as not
    /// configured and the data store is unaffected. Anything read as a resource fails the read.
    /// </summary>
    private string? Unresolved(ConnectionStringRow row, string name, string? tenant, string outcome)
    {
        // The name came from the parser, which admits only the grammar's characters, so it is logged
        // exactly as the resolver was asked for it. The shared sanitizer would strip '@' and '+'.
        string token = name;
        string tenantForLog = TenantForLog(tenant);

        if (row is ConnectionStringRow.Derivative { ReadAs: DerivativeReadMode.PartOfDataStore } derivative)
        {
            logger.LogWarning(
                "Derivative {DerivativeId} ({DerivativeType}) of data store {DataStoreId} in tenant {Tenant} is treated as not configured: secret {Token} {Outcome}",
                derivative.DerivativeId,
                LoggingUtility.SanitizeForLog(derivative.DerivativeType),
                derivative.DataStoreId,
                tenantForLog,
                token,
                outcome
            );
            return null;
        }

        logger.LogError(
            "The stored connection string for {Row} in tenant {Tenant} cannot be read: secret {Token} {Outcome}",
            row.ToString(),
            tenantForLog,
            token,
            outcome
        );

        throw new ConnectionStringReadException(
            $"The stored connection string for {row} cannot be read: secret {token} {outcome}."
        );
    }

    /// <summary>
    /// Waits for a value no longer than the read has left. The fetch may be shared with other reads,
    /// so its own deadline stays the full timeout and is not shortened here; a fetch this read stops
    /// waiting for runs on, and caches its value if it returns one within that deadline. Which task
    /// finished first decides the outcome, not a later look at the clock: a timer may fire a little
    /// before the allowance is used up.
    /// </summary>
    private async Task<string> WithinReadDeadlineAsync(Task<string> fetch, TimeSpan remaining)
    {
        // A cached value, or a fetch that finished on the caller's thread, needs no timer.
        if (fetch.IsCompleted)
        {
            return await fetch;
        }

        using CancellationTokenSource deadlineCancellation = new();
        Task deadline = Task.Delay(remaining, _timeProvider, deadlineCancellation.Token);

        if (await Task.WhenAny(fetch, deadline) == fetch)
        {
            await deadlineCancellation.CancelAsync();
            return await fetch;
        }

        // Observed so a fault the fetch raises later is not reported as unobserved.
        _ = fetch.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default
        );

        _allowanceSpent = true;
        throw new ReadAllowanceSpentException();
    }

    /// <summary>
    /// Calls the resolver on a task the host owns and races that task against the timeout, so a
    /// resolver that blocks before returning its <see cref="ValueTask{TResult}"/> is bounded as well as
    /// one that returns an incomplete one. The token passed to the resolver is cancelled at the
    /// deadline so a cooperative resolver stops work nobody is waiting for; a thread an uncooperative
    /// one blocks is released only when it returns. A call still queued when the deadline passes is
    /// never started.
    /// </summary>
    private async Task<string> FetchAsync(ISecretResolver resolver, SecretReference reference)
    {
        // The deadline is armed before the resolver is invoked, so the window covers the whole call.
        using CancellationTokenSource deadlineCancellation = new();
        Task deadline = Task.Delay(_resolveTimeout, _timeProvider, deadlineCancellation.Token);

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

        // Cancellation callbacks are the resolver's, so they run off this path and cannot delay the
        // timeout. The late outcome is observed and dropped: this fetch has already failed, so nothing
        // the call returns can reach the cache. The source is disposed only once both the call and its
        // callbacks are done with it. Until a call that never completes does, this continuation, the
        // source and the call's state stay reachable from whatever the resolver keeps pending.
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

        throw new SecretResolveTimeoutException();
    }

    private TaskCompletionSource? _abandonedCallObserved;

    /// <summary>
    /// Completes once the outcome of a call this reader started, and abandoned at that call's own
    /// deadline, has been observed and dropped, so a test can wait for it instead of guessing when it
    /// ran. It completes once, for the first such call. The signal is created on first use by either
    /// side, so it is the same one whichever comes first.
    /// </summary>
    internal Task AbandonedCallObserved => AbandonedCallSignal().Task;

    private TaskCompletionSource AbandonedCallSignal() =>
        LazyInitializer.EnsureInitialized(
            ref _abandonedCallObserved,
            () => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        );

    /// <summary>
    /// The host's own deadline passing, kept apart from any <see cref="TimeoutException"/> a resolver
    /// raises itself so the two are reported differently.
    /// </summary>
    public sealed class SecretResolveTimeoutException : Exception;

    /// <summary>This read's own allowance passed before the call it was waiting on finished.</summary>
    public sealed class ReadAllowanceSpentException : Exception;
}
