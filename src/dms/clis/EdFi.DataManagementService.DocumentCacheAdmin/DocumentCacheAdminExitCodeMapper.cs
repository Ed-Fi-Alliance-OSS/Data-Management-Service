// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

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

    /// <summary>
    /// Maps one enablement or source-replacement admission onto an exit code.
    /// </summary>
    /// <remarks>
    /// Anything short of <see cref="CdcAdmissionState.Admitted"/> is
    /// <see cref="DocumentCacheAdminExitCodes.IncompleteRetryable"/> rather than a rejection: the
    /// sequence makes the binding durable before it creates any external artifact, so a run that did
    /// not admit writes may still have mutated deployment state, and the operation is built to be
    /// reissued unchanged. <see cref="CdcAdmissionState.Unknown"/> is not a softer verdict than
    /// <see cref="CdcAdmissionState.NotAdmitted"/> — both leave write admission closed — so both map to
    /// the same code; the shared contract carries which one it was.
    /// </remarks>
    public static int ForAdmission(CdcAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);

        return ForAdmissionState(admission.AdmissionState);
    }

    public static int ForAdmissionState(CdcAdmissionState admissionState) =>
        admissionState switch
        {
            CdcAdmissionState.Admitted => DocumentCacheAdminExitCodes.Success,
            CdcAdmissionState.NotAdmitted => DocumentCacheAdminExitCodes.IncompleteRetryable,
            CdcAdmissionState.Unknown => DocumentCacheAdminExitCodes.IncompleteRetryable,
            _ => DocumentCacheAdminExitCodes.UnexpectedFailure,
        };

    /// <summary>
    /// Maps one collected CDC status onto an exit code. A status read is not a mutation, so a
    /// not-ready or unknown binding reports <see cref="DocumentCacheAdminExitCodes.Success"/>: the
    /// command answered, and the answer is in the shared contract. Only a status that could not be
    /// produced at all is a failure, which the caller reports rather than this mapping.
    /// </summary>
    public static int ForStatus(CdcStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return ForReadiness(status.Readiness);
    }

    public static int ForReadiness(CdcReadiness readiness) =>
        readiness switch
        {
            CdcReadiness.Ready => DocumentCacheAdminExitCodes.Success,
            CdcReadiness.NotReady => DocumentCacheAdminExitCodes.Success,
            CdcReadiness.Unknown => DocumentCacheAdminExitCodes.Success,
            _ => DocumentCacheAdminExitCodes.UnexpectedFailure,
        };
}
