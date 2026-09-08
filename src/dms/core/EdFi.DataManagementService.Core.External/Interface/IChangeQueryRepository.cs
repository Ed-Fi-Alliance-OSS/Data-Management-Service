// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core.External.Interface;

/// <summary>
/// Backend reads for Change Queries. Relational backend only.
/// </summary>
public interface IChangeQueryRepository
{
    /// <summary>
    /// Returns the current backend change-version high-water mark.
    /// </summary>
    Task<long> GetNewestChangeVersion(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns resource-scoped tracked changes from relational tracked-change tables.
    /// </summary>
    Task<TrackedChangeQueryResult> QueryTrackedChanges(
        ITrackedChangeQueryRequest request,
        CancellationToken cancellationToken = default
    );
}
