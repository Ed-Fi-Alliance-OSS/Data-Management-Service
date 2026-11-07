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
/// Resolves <c>AdminConnectionString</c> (F-C6): a value without references is used as configured; each
/// reference is resolved through the registered <see cref="ISecretResolver"/> and the shared
/// <see cref="SecretValueCache"/> (tenant-less: the administrative connection belongs to the deployment, not to
/// a tenant), and substituted into the value of the key that held it, so a resolved value that needs quoting
/// is quoted by the connection-string syntax rather than spliced into the text. One resolution may take at
/// most <c>SecretsSettings:ResolveTimeoutSeconds</c> in all; the resolver is called on a task the host owns,
/// so a resolver that blocks before returning is bounded too, and its token is cancelled at the deadline.
/// Nothing the resolver returns or raises is logged or carried: a failure reports a fixed operation and,
/// when an exception caused it, the exception type chain.
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

    private volatile string? _current;

    public string Current =>
        _current
        ?? throw new InvalidOperationException(
            "The DMS provisioning administrative connection string has not been resolved."
        );

    public async Task<DmsAdminConnectionResolution> ResolveAsync(CancellationToken cancellationToken)
    {
        string configured =
            options.Value.AdminConnectionString
            ?? throw new InvalidOperationException(
                "DmsDataStoreProvisioning:AdminConnectionString is not configured; static validation should have refused it."
            );

        if (SecretReferenceTokens.Find(configured).Count == 0)
        {
            _current = configured;
            return new DmsAdminConnectionResolution.Resolved();
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

        using CancellationTokenSource deadline = new(_resolveTimeout, timeProvider);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token
        );
        if (
            await SubstituteEveryKeyAsync(builder, secretResolver, stop.Token, cancellationToken) is
            { } failure
        )
        {
            return failure;
        }

        _current = builder.ConnectionString;
        return new DmsAdminConnectionResolution.Resolved();
    }

    /// <summary>
    /// Resolves the references in every value of <paramref name="builder"/> and writes the result back
    /// through it. The keys are taken up front because each assignment writes through the same builder; a
    /// keyword assigned twice keeps its last value, so a reference the syntax discarded is never resolved.
    /// </summary>
    private async Task<DmsAdminConnectionResolution?> SubstituteEveryKeyAsync(
        DbConnectionStringBuilder builder,
        ISecretResolver resolver,
        CancellationToken stop,
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
                stop,
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
        CancellationToken stop,
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
                stop,
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
        CancellationToken stop,
        CancellationToken cancellationToken
    )
    {
        if (stop.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (null, Failed(TimedOutStep));
        }

        Task<string> fetch = cache.GetOrFetchAsync(
            null,
            name,
            () => Task.Run(() => resolver.ResolveAsync(new SecretReference(name, null), stop).AsTask(), stop)
        );

        string value;
        try
        {
            value = await fetch.WaitAsync(stop);
        }
        catch (Exception) when (stop.IsCancellationRequested)
        {
            // Observed so a fault the resolver raises later is not reported as unobserved.
            _ = fetch.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default
            );
            cancellationToken.ThrowIfCancellationRequested();
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

    private static DmsAdminConnectionResolution.Failed Failed(string operation) =>
        new(DmsProvisioningDiagnostic.ForOperation(operation));
}
