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
    /// for a derivative read as part of a data store whose reference cannot be resolved.
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
            throw new ConnectionStringReadException(
                $"The stored connection string for {row} could not be decrypted."
            );
        }

        IReadOnlyList<SecretReferenceToken> tokens = plainText is null
            ? []
            : SecretReferenceTokens.Find(plainText);

        if (tokens.Count == 0)
        {
            return Convert.ToBase64String(stored);
        }

        DbConnectionStringBuilder builder = Parse(plainText!, row);

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

            builder[key] = resolved;
        }

        // Rendered even when every reference was discarded, so the stored text that carried one is
        // never returned as it was.
        return Convert.ToBase64String(encryptionService.Encrypt(builder.ConnectionString)!);
    }

    private DbConnectionStringBuilder Parse(string plainText, ConnectionStringRow row)
    {
        try
        {
            return builderSource.CreateBuilder(plainText);
        }
        catch (Exception)
        {
            // The provider's message repeats the text it could not parse.
            throw ParseFailure(row);
        }
    }

    private static ConnectionStringReadException ParseFailure(ConnectionStringRow row) =>
        new($"The stored connection string for {row} could not be parsed by the configured database engine.");

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
        string? tenant = tenantContextProvider.Context is TenantContext.Multitenant multitenant
            ? multitenant.TenantName
            : null;

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

        try
        {
            value = await cache.GetOrFetchAsync(
                tenant,
                name,
                () => FetchAsync(secretResolver, new SecretReference(name, tenant))
            );
            outcome = "could not be resolved: the resolver returned no value";
        }
        catch (SecretResolveTimeoutException)
        {
            value = null;
            outcome =
                $"could not be resolved: the resolver did not return within {secretsOptions.Value.ResolveTimeoutSeconds} seconds";
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

    /// <summary>
    /// A derivative read as part of a data store is optional there, so an unresolvable one reads as not
    /// configured and the data store is unaffected. Anything read as a resource fails the read.
    /// </summary>
    private string? Unresolved(ConnectionStringRow row, string name, string? tenant, string outcome)
    {
        // The name came from the parser, which admits only the grammar's characters, so it is logged
        // exactly as the resolver was asked for it. The shared sanitizer would strip '@' and '+'.
        string token = name;
        string tenantForLog = tenant is null ? "(none)" : LoggingUtility.SanitizeForLog(tenant);

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
    /// Calls the resolver on a task the host owns and races that task against the timeout, so a
    /// resolver that blocks before returning its <see cref="ValueTask{TResult}"/> is bounded as well as
    /// one that returns an incomplete one. The token passed to the resolver is cancelled at the
    /// deadline so a cooperative resolver stops work nobody is waiting for; a thread an uncooperative
    /// one blocks is released only when it returns.
    /// </summary>
    private async Task<string> FetchAsync(ISecretResolver resolver, SecretReference reference)
    {
        // The deadline is armed before the resolver is invoked, so the window covers the whole call.
        using CancellationTokenSource deadlineCancellation = new();
        Task deadline = Task.Delay(_resolveTimeout, _timeProvider, deadlineCancellation.Token);

        CancellationTokenSource cancellation = new();
        Task<string> call = Task.Run(
            () => resolver.ResolveAsync(reference, cancellation.Token).AsTask(),
            CancellationToken.None
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
        // callbacks are done with it.
        Task cancelled = cancellation.CancelAsync();
        _ = Task.WhenAll(call, cancelled)
            .ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    cancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default
            );

        throw new SecretResolveTimeoutException();
    }

    /// <summary>
    /// The host's own deadline passing, kept apart from any <see cref="TimeoutException"/> a resolver
    /// raises itself so the two are reported differently.
    /// </summary>
    public sealed class SecretResolveTimeoutException : Exception;
}
