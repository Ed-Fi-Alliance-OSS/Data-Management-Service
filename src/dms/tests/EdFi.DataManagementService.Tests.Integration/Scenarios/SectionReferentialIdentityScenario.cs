// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Public HTTP regression proving the relational ReferentialIdentity trigger stores the same
/// referential id Core computes for a resource whose identity contains a key-unified reference
/// path. Section's <c>$.courseOfferingReference.schoolId</c> resolves to two unified storage
/// aliases on the Section root; the trigger must hash that identity path exactly once (one hash
/// element per identity JSON path) or the stored referential id diverges from Core's and every
/// Section-referencing POST fails reference validation with a 409.
///
/// Concrete shape: the full Section dependency chain is created through the public API
/// (descriptors, School, SchoolYearType, Course, Session, CourseOffering, Section), then a
/// StudentSectionAssociation referencing the Section must resolve and create successfully.
///
/// Bound to <c>FixtureKey.AuthoritativeDs52</c> because the defect requires the production
/// CourseOffering/Section shapes and the key unification their references drive.
/// </summary>
internal static class SectionReferentialIdentityScenario
{
    private const string StandardJsonContentType = "application/json";
    private const string EducationOrganizationCategoryDescriptorsEndpoint =
        "/data/ed-fi/educationOrganizationCategoryDescriptors";
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";
    private const string TermDescriptorsEndpoint = "/data/ed-fi/termDescriptors";
    private const string CourseIdentificationSystemDescriptorsEndpoint =
        "/data/ed-fi/courseIdentificationSystemDescriptors";
    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string SchoolYearTypesEndpoint = "/data/ed-fi/schoolYearTypes";
    private const string CoursesEndpoint = "/data/ed-fi/courses";
    private const string SessionsEndpoint = "/data/ed-fi/sessions";
    private const string CourseOfferingsEndpoint = "/data/ed-fi/courseOfferings";
    private const string SectionsEndpoint = "/data/ed-fi/sections";
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string StudentSectionAssociationsEndpoint = "/data/ed-fi/studentSectionAssociations";

    private const int SchoolYear = 2024;

    public static async Task It_creates_a_section_referencing_resource_without_reference_conflict(
        ApiIntegrationHarness harness
    )
    {
        // Per-test unique values keep the scenario isolated even if the leased
        // database baseline is shared with other scenarios bound to this fixture.
        // The schoolId derives deterministically from the suffix's own hex value
        // (not string.GetHashCode(), which is process-randomized) and spans a
        // ~1B range that stays int32-safe, keeping collision odds negligible.
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long schoolId = 1_000_000_000L + (Convert.ToInt64(suffix, 16) % 1_000_000_000L);
        string namespaceUri = $"uri://ed-fi.org/SectionRefId/{suffix}";
        string eocDescriptorUri = $"{namespaceUri}/EducationOrganizationCategoryDescriptor#School";
        string gradeLevelDescriptorUri = $"{namespaceUri}/GradeLevelDescriptor#Tenth grade";
        string termDescriptorUri = $"{namespaceUri}/TermDescriptor#Fall Semester";
        string courseIdSystemDescriptorUri =
            $"{namespaceUri}/CourseIdentificationSystemDescriptor#LEA course code";
        string courseCode = $"ALG-1-{suffix}";
        string sessionName = $"Fall {suffix}";
        string localCourseCode = $"LCC-{suffix}";
        string sectionIdentifier = $"SEC-{suffix}";
        string studentUniqueId = $"stu-{suffix}";

        await SeedDescriptorAsync(
            harness,
            EducationOrganizationCategoryDescriptorsEndpoint,
            namespaceUri: $"{namespaceUri}/EducationOrganizationCategoryDescriptor",
            codeValue: "School",
            shortDescription: "School"
        );
        await SeedDescriptorAsync(
            harness,
            GradeLevelDescriptorsEndpoint,
            namespaceUri: $"{namespaceUri}/GradeLevelDescriptor",
            codeValue: "Tenth grade",
            shortDescription: "Tenth grade"
        );
        await SeedDescriptorAsync(
            harness,
            TermDescriptorsEndpoint,
            namespaceUri: $"{namespaceUri}/TermDescriptor",
            codeValue: "Fall Semester",
            shortDescription: "Fall Semester"
        );
        await SeedDescriptorAsync(
            harness,
            CourseIdentificationSystemDescriptorsEndpoint,
            namespaceUri: $"{namespaceUri}/CourseIdentificationSystemDescriptor",
            codeValue: "LEA course code",
            shortDescription: "LEA course code"
        );

        var schoolPayload = new JsonObject
        {
            ["schoolId"] = schoolId,
            ["nameOfInstitution"] = $"SectionRefId School {suffix}",
            ["educationOrganizationCategories"] = new JsonArray(
                new JsonObject { ["educationOrganizationCategoryDescriptor"] = eocDescriptorUri }
            ),
            ["gradeLevels"] = new JsonArray(
                new JsonObject { ["gradeLevelDescriptor"] = gradeLevelDescriptorUri }
            ),
        };
        await PostExpectingCreatedAsync(harness, SchoolsEndpoint, schoolPayload);

        // The school year is a shared global identity; upsert semantics make a repeat POST return
        // 200 instead of 201, so both are acceptable here.
        var schoolYearPayload = new JsonObject
        {
            ["schoolYear"] = SchoolYear,
            ["currentSchoolYear"] = false,
            ["schoolYearDescription"] = $"{SchoolYear}",
        };
        await PostExpectingSuccessAsync(harness, SchoolYearTypesEndpoint, schoolYearPayload);

        var coursePayload = new JsonObject
        {
            ["courseCode"] = courseCode,
            ["courseTitle"] = $"Algebra I {suffix}",
            ["numberOfParts"] = 1,
            ["educationOrganizationReference"] = new JsonObject { ["educationOrganizationId"] = schoolId },
            ["identificationCodes"] = new JsonArray(
                new JsonObject
                {
                    ["identificationCode"] = courseCode,
                    ["courseIdentificationSystemDescriptor"] = courseIdSystemDescriptorUri,
                }
            ),
        };
        await PostExpectingCreatedAsync(harness, CoursesEndpoint, coursePayload);

        var sessionPayload = new JsonObject
        {
            ["sessionName"] = sessionName,
            ["schoolReference"] = new JsonObject { ["schoolId"] = schoolId },
            ["schoolYearTypeReference"] = new JsonObject { ["schoolYear"] = SchoolYear },
            ["beginDate"] = "2024-08-01",
            ["endDate"] = "2024-12-20",
            ["termDescriptor"] = termDescriptorUri,
            ["totalInstructionalDays"] = 90,
        };
        await PostExpectingCreatedAsync(harness, SessionsEndpoint, sessionPayload);

        var courseOfferingPayload = new JsonObject
        {
            ["localCourseCode"] = localCourseCode,
            ["schoolReference"] = new JsonObject { ["schoolId"] = schoolId },
            ["sessionReference"] = new JsonObject
            {
                ["schoolId"] = schoolId,
                ["schoolYear"] = SchoolYear,
                ["sessionName"] = sessionName,
            },
            ["courseReference"] = new JsonObject
            {
                ["courseCode"] = courseCode,
                ["educationOrganizationId"] = schoolId,
            },
        };
        await PostExpectingCreatedAsync(harness, CourseOfferingsEndpoint, courseOfferingPayload);

        // Section's identity contains the key-unified $.courseOfferingReference.schoolId path.
        var sectionPayload = new JsonObject
        {
            ["sectionIdentifier"] = sectionIdentifier,
            ["courseOfferingReference"] = new JsonObject
            {
                ["localCourseCode"] = localCourseCode,
                ["schoolId"] = schoolId,
                ["schoolYear"] = SchoolYear,
                ["sessionName"] = sessionName,
            },
        };
        await PostExpectingCreatedAsync(harness, SectionsEndpoint, sectionPayload);

        var studentPayload = new JsonObject
        {
            ["studentUniqueId"] = studentUniqueId,
            ["firstName"] = "Sec",
            ["lastSurname"] = "Tion",
            ["birthDate"] = "2012-01-01",
        };
        await PostExpectingCreatedAsync(harness, StudentsEndpoint, studentPayload);

        // The proof: resolving sectionReference requires the Section's trigger-stored referential
        // id to equal Core's computed referential id (one hash element per identity JSON path).
        // A duplicated key-unified element makes this POST fail reference validation with 409.
        var studentSectionAssociationPayload = new JsonObject
        {
            ["beginDate"] = "2024-08-01",
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = studentUniqueId },
            ["sectionReference"] = new JsonObject
            {
                ["localCourseCode"] = localCourseCode,
                ["schoolId"] = schoolId,
                ["schoolYear"] = SchoolYear,
                ["sectionIdentifier"] = sectionIdentifier,
                ["sessionName"] = sessionName,
            },
        };
        await PostExpectingCreatedAsync(
            harness,
            StudentSectionAssociationsEndpoint,
            studentSectionAssociationPayload
        );
    }

    private static async Task SeedDescriptorAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string namespaceUri,
        string codeValue,
        string shortDescription
    )
    {
        var payload = new JsonObject
        {
            ["namespace"] = namespaceUri,
            ["codeValue"] = codeValue,
            ["shortDescription"] = shortDescription,
        };
        await PostExpectingCreatedAsync(harness, endpoint, payload);
    }

    private static async Task PostExpectingCreatedAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType);
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(endpoint, content);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST {endpoint} body: {body}");
    }

    private static async Task PostExpectingSuccessAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType);
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(endpoint, content);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .BeOneOf(
                [HttpStatusCode.Created, HttpStatusCode.OK],
                $"POST {endpoint} must create or upsert. Body: {body}"
            );
    }
}
