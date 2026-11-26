// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// The provider-independent documents both providers seed to execute identification-code collection
/// filters over the authoritative Data Standard 5.2 schema. Each call builds new payloads; the provider
/// fixture supplies the upsert operation and keeps every database concern.
/// </summary>
internal static class IdentificationCodeQuerySeeds
{
    public const string RootCourseCatalogUrl = "https://root.example.org/catalog";
    public const string ItemCourseCatalogUrl = "https://item.example.org/catalog";

    public static readonly Guid CourseTranscriptUuid = Guid.Parse("d8100000-0000-0000-0000-0000000000c7");

    public static JsonNode SchoolBody(long schoolId, string name, string? identificationCodes)
    {
        var codes = identificationCodes is null ? "" : $"\"identificationCodes\": {identificationCodes},";

        return JsonNode.Parse(
            $$"""
            {
              "schoolId": {{schoolId}},
              "nameOfInstitution": "{{name}}",
              {{codes}}
              "educationOrganizationCategories": [
                { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" }
              ],
              "gradeLevels": [{ "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade" }]
            }
            """
        )!;
    }

    public static JsonNode StaffBody(string staffUniqueId, string identificationCodes) =>
        JsonNode.Parse(
            $$"""
            {
              "staffUniqueId": "{{staffUniqueId}}",
              "firstName": "{{staffUniqueId}}",
              "lastSurname": "Staff",
              "identificationCodes": {{identificationCodes}}
            }
            """
        )!;

    /// <summary>
    /// A transcript whose root catalog URL and assigning organization differ from those of its
    /// alternative course identification code, with the documents it references, upserted one at a time
    /// in reference order through the supplied provider operation.
    /// </summary>
    public static async Task SeedCourseTranscriptAsync(Func<string, Guid, JsonNode, Task> upsertAsync)
    {
        await upsertAsync(
            "Student",
            Guid.Parse("d8100000-0000-0000-0000-0000000000c1"),
            JsonNode.Parse(
                """{ "studentUniqueId": "ST-1", "firstName": "Student", "lastSurname": "One", "birthDate": "2010-01-01" }"""
            )!
        );
        await upsertAsync(
            "SchoolYearType",
            Guid.Parse("d8100000-0000-0000-0000-0000000000c2"),
            JsonNode.Parse(
                """{ "schoolYear": 2026, "currentSchoolYear": true, "schoolYearDescription": "2025-2026" }"""
            )!
        );
        await upsertAsync(
            "Course",
            Guid.Parse("d8100000-0000-0000-0000-0000000000c3"),
            JsonNode.Parse(
                """
                {
                  "courseCode": "ALG-1",
                  "courseTitle": "Algebra I",
                  "numberOfParts": 1,
                  "educationOrganizationReference": { "educationOrganizationId": 810001 },
                  "identificationCodes": [
                    { "identificationCode": "ALG-1", "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#LEA course code" }
                  ]
                }
                """
            )!
        );
        await upsertAsync(
            "StudentAcademicRecord",
            Guid.Parse("d8100000-0000-0000-0000-0000000000c4"),
            JsonNode.Parse(
                """
                {
                  "studentReference": { "studentUniqueId": "ST-1" },
                  "educationOrganizationReference": { "educationOrganizationId": 810001 },
                  "schoolYearTypeReference": { "schoolYear": 2026 },
                  "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall Semester"
                }
                """
            )!
        );
        await upsertAsync(
            "CourseTranscript",
            CourseTranscriptUuid,
            JsonNode.Parse(
                $$"""
                {
                  "courseReference": { "courseCode": "ALG-1", "educationOrganizationId": 810001 },
                  "studentAcademicRecordReference": {
                    "educationOrganizationId": 810001,
                    "schoolYear": 2026,
                    "studentUniqueId": "ST-1",
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall Semester"
                  },
                  "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                  "courseCatalogURL": "{{RootCourseCatalogUrl}}",
                  "assigningOrganizationIdentificationCode": "ROOT-ORG",
                  "alternativeCourseIdentificationCodes": [
                    {
                      "identificationCode": "ALT-1",
                      "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#State course code",
                      "courseCatalogURL": "{{ItemCourseCatalogUrl}}",
                      "assigningOrganizationIdentificationCode": "ITEM-ORG"
                    }
                  ]
                }
                """
            )!
        );
    }
}
