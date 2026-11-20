// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;

namespace EdFi.DmsConfigurationService.Backend.ManagedDataStores;

public sealed class CreateManagedDataStoreHandler(
    IDataStoreManageRepository repository,
    IDmsDataStoreProvisioner provisioner,
    ManagedDataStoreRuntimeConnectionBuilder runtime
) : IJobHandler<CreateManagedDataStorePayload>
{
    public async Task ExecuteAsync(
        JobExecutionContext context,
        CreateManagedDataStorePayload payload,
        CancellationToken cancellationToken
    )
    {
        var operation = await repository.StartCreateAsync(payload.ManageId, context, cancellationToken);
        if (operation.AlreadyCreated)
        {
            // Start verifies the durable ordinary link under the fence; no secrets or physical work
            // are needed to recover a committed publication whose job completion was interrupted.
            return;
        }
        if (operation.PinnedArtifact is null)
        {
            var resolution = await provisioner.ResolveArtifactAsync(
                operation.Identity.Template,
                cancellationToken
            );
            if (resolution is DmsArtifactResolution.Failure failed)
            {
                throw new ManagedDataStoreProvisioningException(ForAcceptedCreate(failed.Detail));
            }
            operation = await repository.PinCreateAsync(
                operation,
                ((DmsArtifactResolution.Resolved)resolution).Pin,
                context,
                cancellationToken
            );
        }

        var identity = operation.Identity;
        var pin = operation.PinnedArtifact!;
        // Capture and validate the runtime snapshot before physical work, so an invalid or
        // mismatched runtime endpoint fails without restoring a target. Publication receives
        // this same value; it is never resolved again after the physical operation.
        var connection = await runtime.BuildAsync(identity.Endpoint, identity.TargetName, cancellationToken);
        if (connection is ManagedDataStoreRuntimeConnectionResult.Failure runtimeFailure)
        {
            throw new ManagedDataStoreProvisioningException(runtimeFailure.Detail);
        }
        string runtimeConnectionString = (
            (ManagedDataStoreRuntimeConnectionResult.Success)connection
        ).ConnectionString;
        var result = await provisioner.CreateAsync(
            new(identity.Template, identity.TargetName, identity.ExpectedSourceIdentity, pin),
            identity.Endpoint,
            (actual, token) => repository.AdmitCreateAsync(operation, actual, context, token),
            cancellationToken
        );
        if (result is DmsDataStoreCreateResult.Failure failure)
        {
            throw new ManagedDataStoreProvisioningException(ForAcceptedCreate(failure.Detail));
        }
        var actualPin = result switch
        {
            DmsDataStoreCreateResult.Created created => created.Artifact,
            DmsDataStoreCreateResult.Reconciled reconciled => reconciled.Artifact,
            _ => throw new InvalidOperationException("Unknown managed create result."),
        };
        if (actualPin != pin)
        {
            throw new ManagedDataStoreProvisioningException(
                DmsProvisioningFailure.Create(
                    DmsProvisioningFailureCode.ArtifactChangedSinceCreate,
                    DmsProvisioningDiagnostic.ForOperation("VerifyCreateResult")
                )
            );
        }
        await repository.PublishCreateAsync(
            new(operation.Id, identity, pin, runtimeConnectionString),
            context,
            cancellationToken
        );
    }

    /// <summary>
    /// An accepted create whose allowlisted package or attestation is absent retries within the job's attempts: the
    /// absence is an outage of the artifact store, and every retry repeats the trust, hash, manifest, and persisted-pin
    /// checks before any physical work. Every other failure, an untrusted, tampered, or changed artifact included,
    /// keeps its own category.
    /// </summary>
    private static DmsProvisioningFailure ForAcceptedCreate(DmsProvisioningFailure failure) =>
        failure.Code == DmsProvisioningFailureCode.ArtifactMissing
            ? DmsProvisioningFailure.Create(
                failure.Code,
                DmsProvisioningFailureCategory.Transient,
                failure.Diagnostic
            )
            : failure;
}

/// <summary>A typed signal to the executor, which commits the job and lifecycle outcome together.</summary>
public sealed class ManagedDataStoreProvisioningException(DmsProvisioningFailure failure)
    : Exception("Managed data store provisioning failed.")
{
    public DmsProvisioningFailure Failure { get; } = failure;
}
