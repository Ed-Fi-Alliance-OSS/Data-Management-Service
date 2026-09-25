// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// A registered public error code and its fixed message (spec D-7). A job's public <c>ErrorMessage</c> is
/// only ever the <see cref="Message"/> of a registered code, never exception text.
/// </summary>
public sealed record JobErrorCode(string Code, string Message)
{
    public static JobErrorCode UnsupportedJobType { get; } =
        new(nameof(UnsupportedJobType), "The job type is not supported by this service.");

    public static JobErrorCode UnsupportedPayloadVersion { get; } =
        new(nameof(UnsupportedPayloadVersion), "The job payload version is not supported by this service.");

    public static JobErrorCode InvalidPayload { get; } =
        new(nameof(InvalidPayload), "The job payload is not valid for its job type.");

    public static JobErrorCode TenantUnavailable { get; } =
        new(nameof(TenantUnavailable), "The tenant the job belongs to is not available.");

    public static JobErrorCode AttemptsExhausted { get; } =
        new(nameof(AttemptsExhausted), "The job exceeded the maximum number of attempts.");

    public static JobErrorCode HandlerFailed { get; } = new(nameof(HandlerFailed), "The job handler failed.");

    /// <summary>The infrastructure codes, which consumers may not redefine.</summary>
    public static IReadOnlyList<JobErrorCode> Infrastructure { get; } =
    [
        UnsupportedJobType,
        UnsupportedPayloadVersion,
        InvalidPayload,
        TenantUnavailable,
        AttemptsExhausted,
        HandlerFailed,
    ];
}
