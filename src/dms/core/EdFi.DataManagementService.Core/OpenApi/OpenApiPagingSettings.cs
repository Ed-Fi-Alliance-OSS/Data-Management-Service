// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Core.OpenApi;

/// <summary>
/// The runtime paging values published into an assembled OpenAPI document. Published metadata that
/// disagrees with runtime enforcement is a defect, so these values flow from the same configuration the
/// request pipeline reads rather than being restated in the assembly code.
/// </summary>
/// <remarks>
/// Bounds are not validated here. AppSettingsValidator already refuses host start for an out-of-range
/// MaximumPageSize or DefaultPartitionCount, and a second copy of those bounds would be a second thing
/// to drift.
/// </remarks>
public sealed record OpenApiPagingSettings(int MaximumPageSize, int DefaultPartitionCount)
{
    /// <summary>
    /// The MaximumPageSize the service ships with in appsettings.json.
    /// </summary>
    public const int MaximumPageSizeDefault = 500;

    /// <summary>
    /// The shipped defaults, for callers with no runtime configuration source of their own.
    /// </summary>
    public static OpenApiPagingSettings Default { get; } =
        new(MaximumPageSizeDefault, AppSettings.DefaultPartitionCountDefault);
}
