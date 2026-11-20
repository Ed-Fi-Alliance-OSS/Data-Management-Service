// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;

namespace EdFi.DmsConfigurationService.Backend.ManagedDataStores;

/// <summary>Both operations load identity, target and pin by ID under the job envelope's tenant and fence.</summary>
public static class ManagedJobTypes
{
    public const string Create = "ManagedDataStore.Create";
    public const string Delete = "ManagedDataStore.Delete";
    public const short PayloadVersion = 1;
}

public sealed record CreateManagedDataStorePayload(int ManageId);

public sealed class CreateManagedDataStorePayloadValidator
    : IJobPayloadValidator<CreateManagedDataStorePayload>
{
    public IReadOnlyList<string> Validate(CreateManagedDataStorePayload payload) =>
        payload.ManageId > 0 ? [] : ["InvalidManageId"];
}

public sealed record DeleteManagedDataStorePayload(int ManageId);

public sealed class DeleteManagedDataStorePayloadValidator
    : IJobPayloadValidator<DeleteManagedDataStorePayload>
{
    public IReadOnlyList<string> Validate(DeleteManagedDataStorePayload payload) =>
        payload.ManageId > 0 ? [] : ["InvalidManageId"];
}
