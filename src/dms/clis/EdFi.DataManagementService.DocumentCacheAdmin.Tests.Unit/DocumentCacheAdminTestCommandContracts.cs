// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.DocumentCacheAdmin;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

internal static class DocumentCacheAdminTestCommandContracts
{
    public static string ExpectedConfirmationJsonValue(string commandName)
    {
        if (!DocumentCacheAdminMutatingCommandContracts.TryGet(commandName, out var contract))
        {
            throw new ArgumentException(
                $"Command '{commandName}' does not have a mutating command contract.",
                nameof(commandName)
            );
        }

        return contract.ExpectedConfirmationJsonValue;
    }
}
