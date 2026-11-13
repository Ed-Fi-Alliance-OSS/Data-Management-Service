// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.ManagedDataStores;

public enum ManagedDataStoreStatus
{
    PendingCreate,
    CreateInProgress,
    CreateFailed,
    CreateError,
    Created,
    PendingDelete,
    DeleteInProgress,
    DeleteFailed,
    DeleteError,
    Deleted,
}

public static class ManagedDataStoreStatusRules
{
    public static bool CanTransitionTo(this ManagedDataStoreStatus current, ManagedDataStoreStatus next) =>
        (current, next) switch
        {
            (
                ManagedDataStoreStatus.PendingCreate
                    or ManagedDataStoreStatus.CreateFailed
                    or ManagedDataStoreStatus.CreateInProgress,
                ManagedDataStoreStatus.CreateInProgress
                    or ManagedDataStoreStatus.CreateFailed
                    or ManagedDataStoreStatus.CreateError
            ) => true,
            (ManagedDataStoreStatus.CreateInProgress, ManagedDataStoreStatus.Created) => true,
            (ManagedDataStoreStatus.Created, ManagedDataStoreStatus.PendingDelete) => true,
            (
                ManagedDataStoreStatus.PendingDelete
                    or ManagedDataStoreStatus.DeleteFailed
                    or ManagedDataStoreStatus.DeleteInProgress,
                ManagedDataStoreStatus.DeleteInProgress
                    or ManagedDataStoreStatus.DeleteFailed
                    or ManagedDataStoreStatus.DeleteError
            ) => true,
            (ManagedDataStoreStatus.DeleteInProgress, ManagedDataStoreStatus.Deleted) => true,
            _ => false,
        };
}
