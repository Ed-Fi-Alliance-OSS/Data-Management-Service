// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profiles.Model;

/// <summary>
/// Represents a complete API profile with filtering rules for one or more resources.
/// </summary>
/// <param name="Name">The name of the profile (e.g., "Student-Exclude-BirthDate")</param>
/// <param name="Resources">Resources and their filtering rules</param>
public record ApiProfile(string Name, ProfileResource[] Resources);
