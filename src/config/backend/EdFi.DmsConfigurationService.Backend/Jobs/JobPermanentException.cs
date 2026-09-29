// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Thrown by a job handler to fail its job terminally with a registered error code (spec D-7). The exception
/// message is the code, never input text; the job's public message is the code's registered message.
/// </summary>
public sealed class JobPermanentException(JobErrorCode errorCode) : Exception(errorCode.Code)
{
    public JobErrorCode ErrorCode { get; } = errorCode;
}
