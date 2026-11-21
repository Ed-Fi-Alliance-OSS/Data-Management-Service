// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;

/// <summary>
/// The job error codes of the education-organization refresh (DMS-1441 spec §6.3). Each message is fixed text that
/// names no tenant, data store, URL or DMS response value.
/// </summary>
public static class EducationOrganizationRefreshJobErrorCodes
{
    /// <summary>A refresh of one data store found that the data store no longer exists.</summary>
    public const string DataStoreNotFound = "EdOrgRefreshDataStoreNotFound";

    /// <summary>A transient failure (DMS, the data store, or the Configuration Service database) outlasted every attempt.</summary>
    public const string TemporarilyUnavailable = "EdOrgRefreshTemporarilyUnavailable";

    /// <summary>Another job held the data store's refresh lease on every attempt.</summary>
    public const string TargetBusy = "EdOrgRefreshTargetBusy";

    /// <summary>
    /// The composite code of a refresh of every data store that did not refresh them all. It is registered as a
    /// composite with <see cref="IncompleteCategories"/>, not with <see cref="All"/>.
    /// </summary>
    public const string Incomplete = "EdOrgRefreshIncomplete";

    public const string IncompleteMessage =
        "Education organizations could not be refreshed for one or more data stores, whose previously refreshed "
        + "education organizations were kept.";

    /// <summary>Every single code with its fixed public message, in registration order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> All { get; } =
    [
        new(
            DataStoreNotFound,
            "The data store no longer exists; its education organizations were not refreshed."
        ),
        new(
            TemporarilyUnavailable,
            "DMS, the data store, or the Configuration Service database was temporarily unavailable."
        ),
        new(TargetBusy, "The data store was being refreshed by another job."),
    ];

    /// <summary>
    /// The categories an <see cref="Incomplete"/> failure may list: every permanent projection code, then the
    /// refresh's two transient codes.
    /// </summary>
    public static IReadOnlyList<string> IncompleteCategories { get; } =
    [
        .. EducationOrganizationProjectionJobErrorCodes.All.Select(pair => pair.Key),
        TemporarilyUnavailable,
        TargetBusy,
    ];
}
