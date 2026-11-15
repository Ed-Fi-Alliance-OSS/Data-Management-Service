// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;

namespace EdFi.DmsConfigurationService.Backend.ManagedDataStores;

public sealed class DeleteManagedDataStoreHandler(
    IDataStoreManageRepository repository,
    IDmsDataStoreProvisioner provisioner
) : IJobHandler<DeleteManagedDataStorePayload>
{
    public async Task ExecuteAsync(
        JobExecutionContext context,
        DeleteManagedDataStorePayload payload,
        CancellationToken cancellationToken
    )
    {
        var operation = await repository.StartDeleteAsync(payload.ManageId, context, cancellationToken);
        if (operation.AlreadyDeleted)
        {
            return;
        }
        var identity = operation.Command.Identity;
        var result = await provisioner.DeleteAsync(
            new(identity.TargetName, identity.ExpectedSourceIdentity),
            identity.Endpoint,
            (actual, token) => repository.AdmitDeleteAsync(operation, actual, context, token),
            cancellationToken
        );
        if (result is DmsDataStoreDeleteResult.Failure failure)
        {
            throw new ManagedDataStoreProvisioningException(failure.Detail);
        }
        if (result is not (DmsDataStoreDeleteResult.Dropped or DmsDataStoreDeleteResult.AlreadyAbsent))
        {
            throw new InvalidOperationException("Unknown managed delete result.");
        }
        // A failed CMS commit leaves the physical target absent. A fresh attempt must re-admit that
        // endpoint and observe AlreadyAbsent before finalization; never recreate the dropped target.
        await repository.CompleteDeleteAsync(operation.Command, context, cancellationToken);
    }
}
