// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// A flattened representation of a row from the dms.ResourceKey table,
/// used for validation comparison between the database and expected seed.
/// </summary>
/// <param name="ResourceKeyId">The smallint resource key identifier.</param>
/// <param name="ProjectName">The logical project name (e.g., <c>Ed-Fi</c>).</param>
/// <param name="ResourceName">The logical resource name (e.g., <c>School</c>).</param>
/// <param name="ResourceVersion">The resource version label.</param>
public sealed record ResourceKeyRow(
    short ResourceKeyId,
    string ProjectName,
    string ResourceName,
    string ResourceVersion
);
