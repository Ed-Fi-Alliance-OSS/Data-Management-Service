// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// The switch and limits of the education-organization projection endpoint, bound from the
/// <c>DataManagement:EducationOrganizationProjection</c> section.
/// </summary>
/// <remarks>
/// Each limit has an inclusive range enforced at startup by
/// <see cref="EducationOrganizationProjectionSettingsValidator"/>, whether or not <see cref="Enabled"/>
/// is on.
/// </remarks>
public sealed record EducationOrganizationProjectionSettings
{
    public const string SectionName = "DataManagement:EducationOrganizationProjection";

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
    /// If true, maps the projection endpoint under <c>/management</c> and advertises it in Discovery.
    /// Defaults to true.
    /// </summary>
    public bool Enabled { get; init; } = true;

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
