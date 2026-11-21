// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Thrown by a job handler to fail its job terminally with a registered error code (spec D-7). The exception
/// message is the code, never input text; the job's public message is the code's registered message.
/// </summary>
/// <remarks>
/// A composite failure names a registered composite code and the codes of its problem categories. Only the
/// <see cref="JobErrorCode.Code"/> of each argument is kept: the public message is composed from registered text by
/// <see cref="IJobErrorCodeRegistry.TryCompose"/>, so no caller-supplied message is ever published. A composite that
/// cannot be composed fails the job with <see cref="JobErrorCode.HandlerFailed"/>.
/// </remarks>
public sealed class JobPermanentException(JobErrorCode errorCode) : Exception(errorCode.Code)
{
    public JobPermanentException(JobErrorCode errorCode, IReadOnlyList<JobErrorCode> categories)
        : this(errorCode) =>
        CategoryCodes = categories?.Select(category => category?.Code ?? "").ToArray() ?? [];

    public JobErrorCode ErrorCode { get; } = errorCode;

    /// <summary>The category codes of a composite failure, as given; null for a single-code failure.</summary>
    public IReadOnlyList<string>? CategoryCodes { get; }
}
