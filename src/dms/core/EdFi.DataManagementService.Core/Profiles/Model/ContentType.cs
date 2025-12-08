// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profiles.Model;

/// <summary>
/// Defines filtering rules for either reading or writing a resource.
/// </summary>
/// <param name="MemberSelection">The overall strategy for including/excluding members</param>
/// <param name="Properties">Explicitly mentioned properties</param>
/// <param name="Collections">Explicitly mentioned collections</param>
public record ContentType(
    MemberSelection MemberSelection,
    ProfileProperty[] Properties,
    ProfileCollection[] Collections
);
