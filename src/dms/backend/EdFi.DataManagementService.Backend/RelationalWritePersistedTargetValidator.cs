// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend;

internal static class RelationalWritePersistedTargetValidator
{
    public static void Validate(
        RelationalWriteTargetContext targetContext,
        RelationalWritePersistResult persistedTarget
    )
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentNullException.ThrowIfNull(persistedTarget);

        if (persistedTarget.DocumentId <= 0)
        {
            throw new InvalidOperationException(
                "Relational write persistence completed without returning a valid committed DocumentId."
            );
        }

        switch (targetContext)
        {
            case RelationalWriteTargetContext.CreateNew(var documentUuid)
                when persistedTarget.DocumentUuid != documentUuid:
                throw new InvalidOperationException(
                    $"Relational write create completed for document uuid '{documentUuid.Value}', "
                        + $"but persistence returned committed uuid '{persistedTarget.DocumentUuid.Value}'."
                );

            case RelationalWriteTargetContext.ExistingDocument(var documentId, var documentUuid, _)
                when persistedTarget.DocumentId != documentId || persistedTarget.DocumentUuid != documentUuid:
                throw new InvalidOperationException(
                    $"Relational write targeted existing document id {documentId} / uuid '{documentUuid.Value}', "
                        + "but persistence returned a different committed target identity."
                );
        }

        if (persistedTarget.ContentVersion <= 0)
        {
            throw new InvalidOperationException(
                "Relational write persistence completed without returning a positive committed ContentVersion."
            );
        }
    }
}
