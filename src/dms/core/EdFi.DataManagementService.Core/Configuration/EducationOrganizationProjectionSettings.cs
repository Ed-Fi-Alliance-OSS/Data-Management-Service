// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Limits for the education-organization projection endpoint, bound from
/// <c>AppSettings:EducationOrganizationProjection</c>.
/// </summary>
/// <remarks>
/// Each value has an inclusive range enforced by <see cref="AppSettingsValidator"/> at startup,
/// whether or not <see cref="AppSettings.EnableEducationOrganizationProjection"/> is on.
/// </remarks>
public sealed record EducationOrganizationProjectionSettings
{
    public const int MaximumPageSizeDefault = 2000;
    public const int MaximumPageSizeMinimum = 1;
    public const int MaximumPageSizeMaximum = 10000;

    public const int MaxProjectionRowsDefault = 50000;
    public const int MaxProjectionRowsMinimum = 1000;
    public const int MaxProjectionRowsMaximum = 1000000;

    public const int CursorLifetimeMinutesDefault = 60;
    public const int CursorLifetimeMinutesMinimum = 1;
    public const int CursorLifetimeMinutesMaximum = 1440;

    public const int ReadLockTimeoutSecondsDefault = 5;
    public const int ReadLockTimeoutSecondsMinimum = 1;
    public const int ReadLockTimeoutSecondsMaximum = 60;

    public const int ReadCommandTimeoutSecondsDefault = 60;
    public const int ReadCommandTimeoutSecondsMinimum = 5;
    public const int ReadCommandTimeoutSecondsMaximum = 600;

    /// <summary>
    /// Upper bound for the <c>limit</c> query parameter, and the page size applied when it is
    /// omitted.
    /// </summary>
    public int MaximumPageSize { get; init; } = MaximumPageSizeDefault;

    /// <summary>
    /// Largest projected set a data store may hold; a larger set is refused rather than paged.
    /// </summary>
    public int MaxProjectionRows { get; init; } = MaxProjectionRowsDefault;

    /// <summary>
    /// How long a continuation cursor stays valid, measured from the start of the walk that
    /// issued it.
    /// </summary>
    public int CursorLifetimeMinutes { get; init; } = CursorLifetimeMinutesDefault;

    /// <summary>
    /// How long a per-page read waits for a lock before failing.
    /// </summary>
    public int ReadLockTimeoutSeconds { get; init; } = ReadLockTimeoutSecondsDefault;

    /// <summary>
    /// Command timeout for a per-page read.
    /// </summary>
    public int ReadCommandTimeoutSeconds { get; init; } = ReadCommandTimeoutSecondsDefault;
}
