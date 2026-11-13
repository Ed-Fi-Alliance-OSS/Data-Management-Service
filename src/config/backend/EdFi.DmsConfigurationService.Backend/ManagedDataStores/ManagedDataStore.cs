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
    /// <summary>Restore stored state without replaying lifecycle events or regenerating identity or timestamps.</summary>
    public static ManagedDataStore Rehydrate(ManagedDataStorePersistedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidatePersistedState(state);
        return new(state);
    }

    private ManagedDataStore(ManagedDataStorePersistedState state)
    {
        Id = state.Id;
        TenantId = state.TenantId;
        Name = state.Name;
        Template = state.Template;
        TargetName = state.TargetName;
        ExpectedSourceIdentity = state.ExpectedSourceIdentity;
        Endpoint = state.Endpoint;
        PinnedArtifact = state.PinnedArtifact;
        Status = state.Status;
        DataStoreId = state.DataStoreId;
        HistoricalDataStoreId = state.HistoricalDataStoreId;
        HistoricalDataStoreName = state.HistoricalDataStoreName;
        CreateJobId = state.CreateJobId;
        DeleteJobId = state.DeleteJobId;
        LastAttempt = state.LastAttempt;
        LastFailure = state.LastFailure;
        CreatedAt = state.CreatedAt;
        UpdatedAt = state.UpdatedAt;
        DeletedAt = state.DeletedAt;
        LastRefreshed = state.LastRefreshed;
        LastModifiedDate = state.LastModifiedDate;
    }

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

    private static void ValidatePersistedState(ManagedDataStorePersistedState state)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(state.Id);
        ArgumentNullException.ThrowIfNull(state.Template);
        ArgumentNullException.ThrowIfNull(state.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.CreateJobId);
        _ = ManagedDataStoreEndpoint.CatalogProvider(state.Endpoint.Provider);
        if (
            !CatalogName.IsValidManagedName(state.Name)
            || state.Name != CatalogName.Trim(state.Name)
            || state.ExpectedSourceIdentity == Guid.Empty
            || state.TargetName == default
            || !Enum.IsDefined(state.Status)
            || string.IsNullOrWhiteSpace(state.Endpoint.Transport)
            || string.IsNullOrWhiteSpace(state.Endpoint.Host)
            || string.IsNullOrWhiteSpace(state.Endpoint.MaintenanceDatabase)
            || state.Endpoint.Port is <= 0 or > 65535
        )
        {
            throw new ArgumentException(
                "Invalid persisted managed data store identity or target.",
                nameof(state)
            );
        }

        bool pending =
            state.Status is ManagedDataStoreStatus.PendingCreate or ManagedDataStoreStatus.PendingDelete;
        bool retry =
            state.Status is ManagedDataStoreStatus.CreateFailed or ManagedDataStoreStatus.DeleteFailed;
        bool error = state.Status is ManagedDataStoreStatus.CreateError or ManagedDataStoreStatus.DeleteError;
        if (
            state.LastAttempt < 0
            || (pending && state.LastAttempt != 0)
            || (!pending && !error && state.LastAttempt == 0)
            || (
                retry || error
                    ? state.LastFailure is null
                        || retry != (state.LastFailure.Category == DmsProvisioningFailureCategory.Transient)
                    : state.LastFailure is not null
            )
        )
        {
            throw new ArgumentException(
                "Persisted attempt metadata does not match the lifecycle state.",
                nameof(state)
            );
        }

        bool delete =
            state.Status
            is ManagedDataStoreStatus.PendingDelete
                or ManagedDataStoreStatus.DeleteInProgress
                or ManagedDataStoreStatus.DeleteFailed
                or ManagedDataStoreStatus.DeleteError
                or ManagedDataStoreStatus.Deleted;
        bool published = delete || state.Status == ManagedDataStoreStatus.Created;
        bool deleted = state.Status == ManagedDataStoreStatus.Deleted;
        bool invalidCurrentLink = deleted
            ? state.DataStoreId is not null
            : state.DataStoreId != state.HistoricalDataStoreId;
        if (
            (delete ? string.IsNullOrWhiteSpace(state.DeleteJobId) : state.DeleteJobId is not null)
            || deleted != (state.DeletedAt is not null)
            || (
                published
                    ? state.HistoricalDataStoreId is null or <= 0
                        || state.HistoricalDataStoreName != state.Name
                        || invalidCurrentLink
                    : state.DataStoreId is not null
                        || state.HistoricalDataStoreId is not null
                        || state.HistoricalDataStoreName is not null
            )
        )
        {
            throw new ArgumentException(
                "Persisted catalog links or delete metadata do not match the lifecycle state.",
                nameof(state)
            );
        }

        if (state.PinnedArtifact is not { } pin)
        {
            if (published)
            {
                throw new ArgumentException(
                    "Published state requires the persisted artifact pin.",
                    nameof(state)
                );
            }
            return;
        }

        string[] pinFields =
        [
            pin.PackageId,
            pin.PackageVersion,
            pin.PackageSha256,
            pin.ArtifactSha256,
            pin.DataStandardVersion,
            pin.EffectiveSchemaHash,
            pin.InventorySha256,
            pin.ApiSchemaFormatVersion,
            pin.RelationalMappingVersion,
        ];
        if (
            state.Status == ManagedDataStoreStatus.PendingCreate
            || pin.ContractVersion != DmsTrustedArtifactPin.CurrentContractVersion
            || pin.Provider != state.Endpoint.Provider
            || pin.TemplateKind != state.Template.TemplateKind
            || Array.Exists(pinFields, string.IsNullOrWhiteSpace)
        )
        {
            throw new ArgumentException(
                "The persisted artifact pin is incomplete or inconsistent.",
                nameof(state)
            );
        }
    }

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
