// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal static class DocumentCacheAdminExitCodeMapper
{
    public static int ForAdministrativeCommandResult(DocumentCacheAdministrativeCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return ForAdministrativeCommandStatus(result.Status);
    }

    public static int ForAdministrativeCommandStatus(DocumentCacheAdministrativeCommandStatus status) =>
        status switch
        {
            DocumentCacheAdministrativeCommandStatus.Completed => DocumentCacheAdminExitCodes.Success,
            DocumentCacheAdministrativeCommandStatus.RejectedNoMutation =>
                DocumentCacheAdminExitCodes.RejectedNoMutation,
            DocumentCacheAdministrativeCommandStatus.FailedNoMutation =>
                DocumentCacheAdminExitCodes.FailedNoMutation,
            DocumentCacheAdministrativeCommandStatus.IncompleteRetryable =>
                DocumentCacheAdminExitCodes.IncompleteRetryable,
            _ => DocumentCacheAdminExitCodes.UnexpectedFailure,
        };
}
