// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;

namespace EdFi.DmsConfigurationService.Backend.ManagedDataStores;

/// <summary>Canonicalizes one resolved administrative snapshot using the target provider's pure parser, independently of the CMS datastore.</summary>
public delegate bool ManagedDataStoreEndpointParser(
    string snapshot,
    [NotNullWhen(true)] out ManagedDataStoreEndpoint? endpoint
);

public sealed class ManagedDataStoreCreateAcceptance(
    IDmsDataStoreProvisionerReadiness readiness,
    IDmsAdminConnectionSource adminConnections,
    ManagedDataStoreEndpointParser parseEndpoint,
    ProtectedDatabaseNames protectedNames,
    IDataStoreManageRepository repository
)
{
    public async Task<DataStoreManageCreateResult> AcceptAsync(
        DataStoreManageInsertCommand command,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (
            !new DataStoreManageInsertCommand.Validator().Validate(command).IsValid
            || !DmsDatabaseTemplate.TryFromWireValue(command.DatabaseTemplate, out var template)
        )
        {
            return new DataStoreManageCreateResult.InvalidRequest();
        }
        DmsTargetDatabaseName target;
        try
        {
            target = ManagedDataStoreNameFormatter.Format(command.Name!, template, protectedNames);
        }
        catch (ArgumentException)
        {
            return new DataStoreManageCreateResult.InvalidRequest();
        }
        if (readiness.Current is not DmsProvisionerReadiness.Ready ready)
        {
            return new DataStoreManageCreateResult.ProvisioningUnavailable();
        }

        try
        {
            // Resolve exactly once, before the repository can open a CMS transaction. The snapshot and
            // its credentials never cross the persistence boundary; only the canonical descriptor does.
            if (
                await adminConnections.ResolveAsync(cancellationToken)
                    is not DmsAdminConnectionResolution.Resolved resolved
                || !parseEndpoint(resolved.Connection.Value, out var endpoint)
                || endpoint.Provider != ready.Provider
            )
            {
                return new DataStoreManageCreateResult.ConnectionResolutionFailed();
            }
            return await repository.AcceptCreateAsync(
                new(command.Name!, template, target, Guid.NewGuid(), endpoint),
                cancellationToken
            );
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A lost commit acknowledgement is ambiguous. Report it safely; never replay acceptance
            // with another source identity, aggregate or job.
            return new DataStoreManageCreateResult.FailureUnknown(
                JobDiagnostics.From(exception, "AcceptManagedDataStoreCreate")
            );
        }
    }
}
