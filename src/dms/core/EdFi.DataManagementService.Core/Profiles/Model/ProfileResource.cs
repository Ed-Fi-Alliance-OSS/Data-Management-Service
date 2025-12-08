// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profiles.Model;

/// <summary>
/// Represents filtering rules for a specific API resource within a profile.
/// </summary>
/// <param name="Name">The name of the resource (e.g., "Student", "School")</param>
/// <param name="ReadContentType">Rules for GET operations, or null if not specified</param>
/// <param name="WriteContentType">Rules for POST/PUT operations, or null if not specified</param>
public record ProfileResource(
    string Name,
    ContentType? ReadContentType,
    ContentType? WriteContentType
);
