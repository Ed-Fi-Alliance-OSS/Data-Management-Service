// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;

namespace EdFi.DmsConfigurationService.Backend.ManagedDataStores;

/// <summary>
/// The administrative endpoint one delete attempt resolved before its catalog transaction. It holds no
/// credential. Acceptance consults it after the lifecycle, link and readiness outcomes, immediately before
/// writing.
/// </summary>
public abstract record ManagedDataStoreAdminEndpoint
{
    /// <summary>
    /// Null when this is the record's canonical endpoint; credentials may differ. Any other endpoint leaves
    /// the record unchanged. This is a snapshot: the worker still verifies the endpoint and ownership while it
    /// holds the physical target lock.
    /// </summary>
    public virtual DataStoreManageDeleteResult? RefusalFor(ManagedDataStoreEndpoint recorded) =>
        new DataStoreManageDeleteResult.ProvisioningUnavailable();

    /// <summary>Readiness was closed, so nothing was resolved.</summary>
    public sealed record Unresolved() : ManagedDataStoreAdminEndpoint;

    public sealed record Resolved(ManagedDataStoreEndpoint Endpoint) : ManagedDataStoreAdminEndpoint
    {
        public override DataStoreManageDeleteResult? RefusalFor(ManagedDataStoreEndpoint recorded) =>
            Endpoint == recorded ? null : new DataStoreManageDeleteResult.ProvisioningUnavailable();
    }

    /// <summary>Resolution or parsing failed; only the fixed, value-free diagnostic is kept.</summary>
    public sealed record Failed(JobFailureDiagnostic Diagnostic) : ManagedDataStoreAdminEndpoint
    {
        public override DataStoreManageDeleteResult? RefusalFor(ManagedDataStoreEndpoint recorded) =>
            new DataStoreManageDeleteResult.FailureUnknown(Diagnostic);
    }
}

public sealed class ManagedDataStoreDeleteAcceptance(
    IDmsDataStoreProvisionerReadiness readiness,
    IDmsAdminConnectionSource adminConnections,
    ManagedDataStoreEndpointParser parseEndpoint,
    IDataStoreManageRepository repository,
    JobClaimPolicy claimPolicy
)
{
    public async Task<DataStoreManageDeleteResult> AcceptAsync(int id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (claimPolicy.ExcludesManagedDataStores)
        {
            return new DataStoreManageDeleteResult.ManagementDisabled();
        }
        // Resolve at most once, before the repository opens a CMS transaction. A failure is carried in, so
        // not-found and lifecycle outcomes keep their precedence over it.
        ManagedDataStoreAdminEndpoint current =
            readiness.Current is DmsProvisionerReadiness.Ready
                ? await ResolveAsync(cancellationToken)
                : new ManagedDataStoreAdminEndpoint.Unresolved();
        return await repository.AcceptDeleteAsync(id, current, cancellationToken);
    }

    private async Task<ManagedDataStoreAdminEndpoint> ResolveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await adminConnections.ResolveAsync(cancellationToken);
            if (resolution is DmsAdminConnectionResolution.Failed failed)
            {
                return new ManagedDataStoreAdminEndpoint.Failed(
                    new(
                        failed.Diagnostic.ExceptionTypeChain,
                        failed.Diagnostic.ProviderErrorCode,
                        failed.Diagnostic.Operation
                    )
                );
            }
            return
                resolution is DmsAdminConnectionResolution.Resolved resolved
                && parseEndpoint(resolved.Connection.Value, out var endpoint)
                ? new ManagedDataStoreAdminEndpoint.Resolved(endpoint)
                : new ManagedDataStoreAdminEndpoint.Failed(
                    new(string.Empty, null, "ParseManagedDataStoreEndpoint")
                );
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new ManagedDataStoreAdminEndpoint.Failed(
                JobDiagnostics.From(exception, "ResolveManagedDataStoreEndpoint")
            );
        }
    }
}
