// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.ManagedDataStores;

public abstract record ManagedDataStoreRuntimeConnectionResult
{
    /// <summary>Plaintext for the ordinary repository's existing encryption boundary. Never log or serialize.</summary>
    public sealed record Success(string ConnectionString, string CatalogProvider)
        : ManagedDataStoreRuntimeConnectionResult
    {
        public override string ToString() => nameof(Success);
    }

    public sealed record Failure(DmsProvisioningFailure Detail) : ManagedDataStoreRuntimeConnectionResult;
}

/// <summary>Captures and validates one runtime snapshot outside CMS transactions. Does not connect or encrypt.</summary>
public sealed class ManagedDataStoreRuntimeConnectionBuilder(
    IOptions<ManagedDataStoreRuntimeOptions> options,
    IManagedDataStoreRuntimeConnectionProvider provider,
    SecretValueCache cache,
    IOptions<SecretsOptions> secretsOptions,
    TimeProvider timeProvider,
    ISecretResolver? secretResolver = null
)
{
    public const string ResolveStep = "ResolveRuntimeConnection";
    public const string ValidateStep = "ValidateRuntimeConnection";

    public async Task<ManagedDataStoreRuntimeConnectionResult> BuildAsync(
        ManagedDataStoreEndpoint expected,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Value);
        cancellationToken.ThrowIfCancellationRequested();
        if (provider.Provider != expected.Provider)
        {
            return Failed(DmsProvisioningFailureCode.TargetEndpointChanged);
        }

        string? configured = options.Value.RuntimeConnectionString;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Failed(DmsProvisioningFailureCode.TargetEndpointUnverifiable);
        }

        // Reuse the bounded, key-aware secret substitution and cache rules. This private source
        // receives ONLY the runtime setting, never the provisioner's administrative options, and
        // is never bound into an administrative operation.
        DmsAdminConnectionSource source = new(
            Options.Create(new DmsDataStoreProvisioningOptions { AdminConnectionString = configured }),
            cache,
            secretsOptions,
            timeProvider,
            secretResolver
        );
        var resolution = await source.ResolveAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (resolution is DmsAdminConnectionResolution.Failed failed)
        {
            return new ManagedDataStoreRuntimeConnectionResult.Failure(
                DmsProvisioningFailure.Create(
                    DmsProvisioningFailureCode.SecretResolutionFailed,
                    new DmsProvisioningDiagnostic(
                        failed.Diagnostic.ExceptionTypeChain,
                        failed.Diagnostic.ProviderErrorCode,
                        ResolveStep
                    )
                )
            );
        }
        string snapshot = ((DmsAdminConnectionResolution.Resolved)resolution).Connection.Value;
        try
        {
            // Resolved values are not recursively expanded. Refuse any remaining reference rather
            // than allowing a later catalog read to resolve a different routing/credential value.
            if (
                snapshot.Contains("${secret:", StringComparison.Ordinal)
                || !provider.TryParse(snapshot, out var actual)
            )
            {
                return Failed(DmsProvisioningFailureCode.TargetEndpointUnverifiable);
            }
            if (actual != expected)
            {
                return Failed(DmsProvisioningFailureCode.TargetEndpointChanged);
            }

            var builder = provider.CreateBuilder(snapshot);
            builder["Database"] = target.Value;
            string final = builder.ConnectionString;
            // Inspect the final native rendering, not another options read or resolution.
            var validated = provider.CreateBuilder(final);
            if (
                !provider.TryParse(final, out var finalEndpoint)
                || !validated.TryGetValue("Database", out object? database)
                || !string.Equals(database as string, target.Value, StringComparison.Ordinal)
            )
            {
                return Failed(DmsProvisioningFailureCode.TargetEndpointUnverifiable);
            }
            if (finalEndpoint != expected)
            {
                return Failed(DmsProvisioningFailureCode.TargetEndpointChanged);
            }
            return new ManagedDataStoreRuntimeConnectionResult.Success(
                final,
                ManagedDataStoreEndpoint.CatalogProvider(actual.Provider)
            );
        }
        catch (Exception exception)
            when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            // Provider parser exceptions can contain credentials or the original connection string.
            return Failed(DmsProvisioningFailureCode.TargetEndpointUnverifiable);
        }
    }

    private static ManagedDataStoreRuntimeConnectionResult.Failure Failed(DmsProvisioningFailureCode code) =>
        new(DmsProvisioningFailure.Create(code, DmsProvisioningDiagnostic.ForOperation(ValidateStep)));
}
