// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IDataStoreManageRepository
{
    Task<DataStoreManageCreateResult> AcceptCreateAsync(
        ManagedDataStoreCreateCommand command,
        CancellationToken cancellationToken
    );
}

/// <summary>Resolved, non-secret acceptance data. The repository validates direct callers before writing.</summary>
public sealed record ManagedDataStoreCreateCommand(
    string Name,
    DmsDatabaseTemplate Template,
    DmsTargetDatabaseName TargetName,
    Guid ExpectedSourceIdentity,
    ManagedDataStoreEndpoint Endpoint
)
{
    public bool IsValid(ProtectedDatabaseNames protectedNames)
    {
        if (
            !CatalogName.IsValidManagedName(Name)
            || Name != CatalogName.Trim(Name)
            || Template is null
            || TargetName == default
            || ExpectedSourceIdentity == Guid.Empty
            || Endpoint is null
            || !Endpoint.IsCanonical()
        )
        {
            return false;
        }

        try
        {
            return TargetName == ManagedDataStoreNameFormatter.Format(Name, Template, protectedNames);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>Internal acceptance outcomes, never a management API response body.</summary>
public abstract record DataStoreManageCreateResult
{
    public sealed record Success(int Id, string JobId) : DataStoreManageCreateResult;

    public sealed record InvalidRequest() : DataStoreManageCreateResult;

    public sealed record ProvisioningUnavailable() : DataStoreManageCreateResult;

    public sealed record ConnectionResolutionFailed() : DataStoreManageCreateResult;

    public sealed record FailureUnknown(JobFailureDiagnostic Diagnostic) : DataStoreManageCreateResult;

    public sealed record NameReserved() : DataStoreManageCreateResult;

    public sealed record TargetReserved() : DataStoreManageCreateResult;

    public sealed record EnqueueFailed(JobEnqueueResult Failure) : DataStoreManageCreateResult;
}
