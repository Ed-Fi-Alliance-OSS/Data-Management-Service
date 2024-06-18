// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions;

public record School(
    int? schoolId,
    string? nameOfInstitution,
    List<GradeLevel>? gradeLevels,
    List<EducationOrganizationCategory>? educationOrganizationCategories
);

public record GradeLevel(string? gradeLevelDescriptor);

public record EducationOrganizationCategory(string? educationOrganizationCategoryDescriptor);
