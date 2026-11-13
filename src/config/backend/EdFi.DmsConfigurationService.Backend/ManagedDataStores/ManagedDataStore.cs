// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;

namespace EdFi.DmsConfigurationService.Backend.ManagedDataStores;

/// <summary>
/// Lifecycle decisions only. Callers must persist changes in the specified fenced CMS transactions;
/// these in-memory operations neither authorize physical work nor provide concurrency control.
/// </summary>
public sealed class ManagedDataStore
{
    public ManagedDataStore(
        int id,
        long? tenantId,
        string name,
        DmsDatabaseTemplate template,
        DmsTargetDatabaseName targetName,
        Guid expectedSourceIdentity,
        ManagedDataStoreEndpoint endpoint,
        string createJobId,
        DateTimeOffset acceptedAt
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        if (!CatalogName.IsValidManagedName(name) || expectedSourceIdentity == Guid.Empty)
        {
            throw new ArgumentException("A valid managed name and nonempty source identity are required.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(createJobId);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(endpoint);
        _ = targetName.Value;
        _ = ManagedDataStoreEndpoint.CatalogProvider(endpoint.Provider);
        Id = id;
        TenantId = tenantId;
        Name = CatalogName.Trim(name);
        Template = template;
        TargetName = targetName;
        ExpectedSourceIdentity = expectedSourceIdentity;
        Endpoint = endpoint;
        CreateJobId = createJobId;
        CreatedAt = UpdatedAt = LastRefreshed = LastModifiedDate = acceptedAt.ToUniversalTime();
    }

    public int Id { get; }
    public long? TenantId { get; }
    public string Name { get; }
    public DmsDatabaseTemplate Template { get; }
    public DmsTargetDatabaseName TargetName { get; }
    public Guid ExpectedSourceIdentity { get; }
    public ManagedDataStoreEndpoint Endpoint { get; }
    public DmsTrustedArtifactPin? PinnedArtifact { get; private set; }
    public ManagedDataStoreStatus Status { get; private set; } = ManagedDataStoreStatus.PendingCreate;
    public int? DataStoreId { get; private set; }
    public int? HistoricalDataStoreId { get; private set; }
    public string? HistoricalDataStoreName { get; private set; }
    public string CreateJobId { get; }
    public string? DeleteJobId { get; private set; }
    public int LastAttempt { get; private set; }
    public ManagedDataStoreFailure? LastFailure { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public DateTimeOffset LastRefreshed { get; private set; }
    public DateTimeOffset LastModifiedDate { get; private set; }

    public void PinArtifact(DmsTrustedArtifactPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (
            Status != ManagedDataStoreStatus.CreateInProgress
            || pin.Provider != Endpoint.Provider
            || pin.TemplateKind != Template.TemplateKind
            || pin.ContractVersion != DmsTrustedArtifactPin.CurrentContractVersion
            || (PinnedArtifact is not null && PinnedArtifact != pin)
        )
        {
            throw new InvalidOperationException(
                "The artifact cannot be pinned or replaced for this managed data store."
            );
        }
        PinnedArtifact = pin;
    }

    public void RecordAttempt(
        ManagedDataStoreStatus status,
        DateTimeOffset observedAt,
        int attempt,
        ManagedDataStoreFailure? failure = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        if (
            status
            is ManagedDataStoreStatus.Created
                or ManagedDataStoreStatus.PendingDelete
                or ManagedDataStoreStatus.Deleted
        )
        {
            throw new InvalidOperationException("This transition requires a catalog operation.");
        }
        bool inProgress =
            status is ManagedDataStoreStatus.CreateInProgress or ManagedDataStoreStatus.DeleteInProgress;
        bool retry = status is ManagedDataStoreStatus.CreateFailed or ManagedDataStoreStatus.DeleteFailed;
        if (
            attempt < LastAttempt
            || ((inProgress || retry) && attempt == 0)
            || (
                inProgress
                    ? failure is not null
                    : failure is null
                        || retry != (failure.Category == DmsProvisioningFailureCategory.Transient)
            )
        )
        {
            throw new ArgumentException("Attempt metadata must match the lifecycle observation.");
        }
        Transition(status, observedAt);
        LastAttempt = attempt;
        LastFailure = failure;
    }

    public void Publish(int dataStoreId, DateTimeOffset observedAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dataStoreId);
        if (PinnedArtifact is null)
        {
            throw new InvalidOperationException("Publication requires the persisted artifact pin.");
        }
        Transition(ManagedDataStoreStatus.Created, observedAt);
        DataStoreId = HistoricalDataStoreId = dataStoreId;
        HistoricalDataStoreName = Name;
    }

    public void AcceptDelete(string deleteJobId, DateTimeOffset acceptedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deleteJobId);
        Transition(ManagedDataStoreStatus.PendingDelete, acceptedAt);
        DeleteJobId = deleteJobId;
        LastAttempt = 0;
        LastFailure = null;
    }

    public void CompleteDelete(DateTimeOffset observedAt)
    {
        Transition(ManagedDataStoreStatus.Deleted, observedAt);
        DataStoreId = null;
        DeletedAt = observedAt.ToUniversalTime();
    }

    public DataStoreManageResponse ToResponse() =>
        new(
            Id,
            Name,
            HistoricalDataStoreId,
            HistoricalDataStoreName,
            Status.ToString(),
            Template.WireValue,
            HistoricalDataStoreId is null ? null : TargetName.Value,
            LastRefreshed,
            LastModifiedDate
        );

    private void Transition(ManagedDataStoreStatus next, DateTimeOffset observedAt)
    {
        if (!Status.CanTransitionTo(next))
        {
            throw new InvalidOperationException("Invalid managed data store lifecycle transition.");
        }
        Status = next;
        UpdatedAt = LastModifiedDate = observedAt.ToUniversalTime();
        if (next != ManagedDataStoreStatus.PendingDelete)
        {
            LastRefreshed = LastModifiedDate;
        }
    }
}
