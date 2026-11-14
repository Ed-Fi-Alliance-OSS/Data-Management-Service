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
    Task<DataStoreManageQueryResult> QueryAsync(
        DataStoreManageQuery query,
        CancellationToken cancellationToken
    );

    Task<DataStoreManageGetResult> GetByIdAsync(int id, CancellationToken cancellationToken);

    Task<DataStoreManageCreateResult> AcceptCreateAsync(
        ManagedDataStoreCreateCommand command,
        CancellationToken cancellationToken
    );

    Task<DataStoreManageDeleteResult> AcceptDeleteAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes through the execution's fence. Failures, including lease loss, fence unavailability and
    /// cancellation, propagate for rollback; the handler must use the existing safe job diagnostics.
    /// </summary>
    Task<int> PublishCreateAsync(
        ManagedDataStorePublishCommand command,
        JobExecutionContext execution,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Immutable operation identity and the already validated runtime connection snapshot. The caller must
/// resolve that snapshot for this endpoint and target before publication; publication performs no resolution
/// or physical database work. This secret-bearing command must never be logged or returned to clients.
/// </summary>
public sealed record ManagedDataStorePublishCommand(
    int Id,
    ManagedDataStoreCreateCommand Identity,
    DmsTrustedArtifactPin PinnedArtifact,
    string RuntimeConnectionString
)
{
    public override string ToString() => nameof(ManagedDataStorePublishCommand);

    public bool IsValid(ProtectedDatabaseNames protectedNames) =>
        Id > 0
        && Identity is not null
        && Identity.IsValid(protectedNames)
        && !string.IsNullOrWhiteSpace(RuntimeConnectionString)
        && PinnedArtifact is { } pin
        && pin.ContractVersion == DmsTrustedArtifactPin.CurrentContractVersion
        && pin.Provider == Identity.Endpoint.Provider
        && pin.TemplateKind == Identity.Template.TemplateKind
        && !Array.Exists(
            new[]
            {
                pin.PackageId,
                pin.PackageVersion,
                pin.PackageSha256,
                pin.ArtifactSha256,
                pin.DataStandardVersion,
                pin.EffectiveSchemaHash,
                pin.InventorySha256,
                pin.ApiSchemaFormatVersion,
                pin.RelationalMappingVersion,
            },
            string.IsNullOrWhiteSpace
        );
}

public abstract record DataStoreManageQueryResult
{
    public sealed record Success(IReadOnlyList<DataStoreManageResponse> DataStores)
        : DataStoreManageQueryResult;

    public sealed record InvalidRequest() : DataStoreManageQueryResult;

    public sealed record FailureUnknown(JobFailureDiagnostic Diagnostic) : DataStoreManageQueryResult;
}

public abstract record DataStoreManageGetResult
{
    public sealed record Success(DataStoreManageResponse DataStore) : DataStoreManageGetResult;

    public sealed record NotFound() : DataStoreManageGetResult;

    public sealed record FailureUnknown(JobFailureDiagnostic Diagnostic) : DataStoreManageGetResult;
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

/// <summary>Internal acceptance outcomes, never a management API response body.</summary>
public abstract record DataStoreManageDeleteResult
{
    public sealed record Success(string JobId) : DataStoreManageDeleteResult;

    public sealed record NotFound() : DataStoreManageDeleteResult;

    public sealed record InvalidState(ManagedDataStoreStatus Status) : DataStoreManageDeleteResult;

    public sealed record ProvisioningUnavailable() : DataStoreManageDeleteResult;

    public sealed record EnqueueFailed(JobEnqueueResult Failure) : DataStoreManageDeleteResult;

    public sealed record FailureUnknown(JobFailureDiagnostic Diagnostic) : DataStoreManageDeleteResult;
}
