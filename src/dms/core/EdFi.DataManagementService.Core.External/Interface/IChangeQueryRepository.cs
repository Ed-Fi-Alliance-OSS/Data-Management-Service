// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Interface;

/// <summary>
/// Backend read for the Change Queries availableChangeVersions endpoint. Relational backend only.
/// </summary>
public interface IChangeQueryRepository
{
    /// <summary>
    /// Returns the current value of dms.ChangeVersionSequence via dms.GetMaxChangeVersion().
    /// </summary>
    Task<long> GetNewestChangeVersion(CancellationToken cancellationToken = default);
}
