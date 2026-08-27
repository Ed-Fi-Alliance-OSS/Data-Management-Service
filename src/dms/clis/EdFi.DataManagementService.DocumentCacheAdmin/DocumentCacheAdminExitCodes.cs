// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal static class DocumentCacheAdminExitCodes
{
    public const int Success = 0;
    public const int UnexpectedFailure = 1;
    public const int RejectedNoMutation = 10;
    public const int FailedNoMutation = 11;
    public const int IncompleteRetryable = 12;
    public const int ArgumentError = 64;
    public const int ConfigurationError = 78;
}
