// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profiles.Model;

/// <summary>
/// Represents a collection reference within a profile content type.
/// </summary>
/// <param name="Name">The name of the collection (e.g., "EducationOrganizationAddresses")</param>
/// <param name="MemberSelection">How to filter items within the collection</param>
public record ProfileCollection(string Name, MemberSelection MemberSelection);
