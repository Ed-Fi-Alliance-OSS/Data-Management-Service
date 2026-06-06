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
/// Public HTTP regression proving that relational GET-many supports <c>studentUniqueId</c> query
/// fields whose ApiSchema paths are reference-derived aliases — that is, paths that do not map
/// directly to a column on the resource's own root table but are inherited from a parent or
/// through a reference hop.
///
/// <para>
/// Two distinct alias shapes are exercised:
/// <list type="bullet">
///   <item><description>
///     <b>Direct-site superclass alias</b>: <c>StudentProgramAssociation.studentUniqueId</c> maps to
///     <c>$.studentReference.generalStudentProgramAssociationUniqueId</c>, the superclass-mangled path
///     that DMS propagates from the Student identity column on the SPA root table.
///   </description></item>
///   <item><description>
///     <b>Through-reference alias</b>: <c>CourseTranscript.studentUniqueId</c> maps to
///     <c>$.studentReference.studentAcademicRecordUniqueId</c>, which predicates the
///     StudentUniqueId column propagated from the StudentAcademicRecord reference.
///   </description></item>
/// </list>
/// </para>
///
/// Without compiled query capability for these alias paths, the relational GET-many handler
/// returns 501 on every request that carries a <c>studentUniqueId</c> query parameter for these
/// resources. Each method first proves that plain GET-all returns 200, then proves that the
/// filtered query selects exactly the matching association and excludes the other.
///
/// Bound to <c>FixtureKey.AuthoritativeDs52</c> because the alias propagation requires the
/// production DS 5.2 superclass/reference shapes.
/// </summary>
internal static class ReferenceDerivedStudentUniqueIdQueryScenario
{
    private const string StandardJsonContentType = "application/json";

    private const string EducationOrganizationCategoryDescriptorsEndpoint =
        "/data/ed-fi/educationOrganizationCategoryDescriptors";
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";
    private const string ProgramTypeDescriptorsEndpoint = "/data/ed-fi/programTypeDescriptors";
    private const string TermDescriptorsEndpoint = "/data/ed-fi/termDescriptors";
    private const string CourseAttemptResultDescriptorsEndpoint =
        "/data/ed-fi/courseAttemptResultDescriptors";
    private const string CourseIdentificationSystemDescriptorsEndpoint =
        "/data/ed-fi/courseIdentificationSystemDescriptors";
    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string SchoolYearTypesEndpoint = "/data/ed-fi/schoolYearTypes";
    private const string CoursesEndpoint = "/data/ed-fi/courses";
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string ProgramsEndpoint = "/data/ed-fi/programs";
    private const string StudentProgramAssociationsEndpoint = "/data/ed-fi/studentProgramAssociations";
    private const string StudentAcademicRecordsEndpoint = "/data/ed-fi/studentAcademicRecords";
    private const string CourseTranscriptsEndpoint = "/data/ed-fi/courseTranscripts";

    private const int SchoolYear = 2024;

    public static async Task It_filters_student_program_associations_by_student_unique_id(
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
        string namespaceUri = $"uri://ed-fi.org/SpaQuery/{suffix}";
        string eocDescriptorUri = $"{namespaceUri}/EducationOrganizationCategoryDescriptor#School";
        string gradeLevelDescriptorUri = $"{namespaceUri}/GradeLevelDescriptor#Tenth grade";
        string programTypeDescriptorUri = $"{namespaceUri}/ProgramTypeDescriptor#Athletics";
        string programName = $"Prog-{suffix}";
        string matchStudentUniqueId = $"spa-match-{suffix}";
        string otherStudentUniqueId = $"spa-other-{suffix}";
        string noneStudentUniqueId = $"spa-none-{suffix}";

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
            ProgramTypeDescriptorsEndpoint,
            namespaceUri: $"{namespaceUri}/ProgramTypeDescriptor",
            codeValue: "Athletics",
            shortDescription: "Athletics"
        );

        var schoolPayload = new JsonObject
        {
            ["schoolId"] = schoolId,
            ["nameOfInstitution"] = $"SpaQuery School {suffix}",
            ["educationOrganizationCategories"] = new JsonArray(
                new JsonObject { ["educationOrganizationCategoryDescriptor"] = eocDescriptorUri }
            ),
            ["gradeLevels"] = new JsonArray(
                new JsonObject { ["gradeLevelDescriptor"] = gradeLevelDescriptorUri }
            ),
        };
        await PostExpectingCreatedAsync(harness, SchoolsEndpoint, schoolPayload);

        var programPayload = new JsonObject
        {
            ["programName"] = programName,
            ["programTypeDescriptor"] = programTypeDescriptorUri,
            ["educationOrganizationReference"] = new JsonObject { ["educationOrganizationId"] = schoolId },
        };
        await PostExpectingCreatedAsync(harness, ProgramsEndpoint, programPayload);

        var matchStudentPayload = new JsonObject
        {
            ["studentUniqueId"] = matchStudentUniqueId,
            ["firstName"] = "Match",
            ["lastSurname"] = "Student",
            ["birthDate"] = "2010-01-01",
        };
        await PostExpectingCreatedAsync(harness, StudentsEndpoint, matchStudentPayload);

        var otherStudentPayload = new JsonObject
        {
            ["studentUniqueId"] = otherStudentUniqueId,
            ["firstName"] = "Other",
            ["lastSurname"] = "Student",
            ["birthDate"] = "2010-01-01",
        };
        await PostExpectingCreatedAsync(harness, StudentsEndpoint, otherStudentPayload);

        var matchSpaPayload = new JsonObject
        {
            ["beginDate"] = "2024-08-01",
            ["educationOrganizationReference"] = new JsonObject { ["educationOrganizationId"] = schoolId },
            ["programReference"] = new JsonObject
            {
                ["educationOrganizationId"] = schoolId,
                ["programName"] = programName,
                ["programTypeDescriptor"] = programTypeDescriptorUri,
            },
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = matchStudentUniqueId },
        };
        await PostExpectingCreatedAsync(harness, StudentProgramAssociationsEndpoint, matchSpaPayload);

        var otherSpaPayload = new JsonObject
        {
            ["beginDate"] = "2024-08-01",
            ["educationOrganizationReference"] = new JsonObject { ["educationOrganizationId"] = schoolId },
            ["programReference"] = new JsonObject
            {
                ["educationOrganizationId"] = schoolId,
                ["programName"] = programName,
                ["programTypeDescriptor"] = programTypeDescriptorUri,
            },
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = otherStudentUniqueId },
        };
        await PostExpectingCreatedAsync(harness, StudentProgramAssociationsEndpoint, otherSpaPayload);

        // Plain GET-all must succeed (proves capability is compiled and handler does not 501).
        JsonArray allAssociations = await GetExpectingOkArrayAsync(
            harness,
            StudentProgramAssociationsEndpoint
        );
        allAssociations.Count.Should().BeGreaterThanOrEqualTo(2, "both seeded associations must be present");

        // Filter by the matching student — the reference-derived alias predicate must select exactly
        // the one association whose studentReference.studentUniqueId equals the queried value.
        JsonArray matchResult = await GetExpectingOkArrayAsync(
            harness,
            $"{StudentProgramAssociationsEndpoint}?studentUniqueId={matchStudentUniqueId}"
        );
        matchResult
            .Count.Should()
            .Be(1, $"filter ?studentUniqueId={matchStudentUniqueId} must return exactly 1 result");
        matchResult[0]!["studentReference"]!["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be(matchStudentUniqueId);

        // Filter by a non-existent student — the result must be an empty array, not a 501 or error.
        JsonArray noneResult = await GetExpectingOkArrayAsync(
            harness,
            $"{StudentProgramAssociationsEndpoint}?studentUniqueId={noneStudentUniqueId}"
        );
        noneResult
            .Count.Should()
            .Be(0, $"filter ?studentUniqueId={noneStudentUniqueId} must return empty array");
    }

    public static async Task It_filters_course_transcripts_by_student_unique_id(ApiIntegrationHarness harness)
    {
        // Same per-test isolation approach as the student program association method above.
        // The 2B base keeps the two methods' schoolId ranges disjoint within the shared
        // leased database baseline.
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long schoolId = 2_000_000_000L + (Convert.ToInt64(suffix, 16) % 1_000_000_000L);
        string namespaceUri = $"uri://ed-fi.org/CtQuery/{suffix}";
        string eocDescriptorUri = $"{namespaceUri}/EducationOrganizationCategoryDescriptor#School";
        string gradeLevelDescriptorUri = $"{namespaceUri}/GradeLevelDescriptor#Tenth grade";
        string termDescriptorUri = $"{namespaceUri}/TermDescriptor#Fall Semester";
        string courseAttemptResultDescriptorUri = $"{namespaceUri}/CourseAttemptResultDescriptor#Pass";
        string courseIdentificationSystemDescriptorUri =
            $"{namespaceUri}/CourseIdentificationSystemDescriptor#LEA course code";
        string courseCode = $"CT-{suffix}";
        string matchStudentUniqueId = $"ct-match-{suffix}";
        string otherStudentUniqueId = $"ct-other-{suffix}";
        string noneStudentUniqueId = $"ct-none-{suffix}";

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
            CourseAttemptResultDescriptorsEndpoint,
            namespaceUri: $"{namespaceUri}/CourseAttemptResultDescriptor",
            codeValue: "Pass",
            shortDescription: "Pass"
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
            ["nameOfInstitution"] = $"CtQuery School {suffix}",
            ["educationOrganizationCategories"] = new JsonArray(
                new JsonObject { ["educationOrganizationCategoryDescriptor"] = eocDescriptorUri }
            ),
            ["gradeLevels"] = new JsonArray(
                new JsonObject { ["gradeLevelDescriptor"] = gradeLevelDescriptorUri }
            ),
        };
        await PostExpectingCreatedAsync(harness, SchoolsEndpoint, schoolPayload);

        // SchoolYearType is a global singleton; a repeat POST returns 200 on upsert.
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
            ["courseTitle"] = $"Course {suffix}",
            ["numberOfParts"] = 1,
            ["educationOrganizationReference"] = new JsonObject { ["educationOrganizationId"] = schoolId },
            ["identificationCodes"] = new JsonArray(
                new JsonObject
                {
                    ["identificationCode"] = courseCode,
                    ["courseIdentificationSystemDescriptor"] = courseIdentificationSystemDescriptorUri,
                }
            ),
        };
        await PostExpectingCreatedAsync(harness, CoursesEndpoint, coursePayload);

        var matchStudentPayload = new JsonObject
        {
            ["studentUniqueId"] = matchStudentUniqueId,
            ["firstName"] = "Match",
            ["lastSurname"] = "Transcript",
            ["birthDate"] = "2010-01-01",
        };
        await PostExpectingCreatedAsync(harness, StudentsEndpoint, matchStudentPayload);

        var otherStudentPayload = new JsonObject
        {
            ["studentUniqueId"] = otherStudentUniqueId,
            ["firstName"] = "Other",
            ["lastSurname"] = "Transcript",
            ["birthDate"] = "2010-01-01",
        };
        await PostExpectingCreatedAsync(harness, StudentsEndpoint, otherStudentPayload);

        // StudentAcademicRecord per student — the StudentUniqueId column is propagated from the
        // Student reference and is the column that CourseTranscript's studentUniqueId alias predicate
        // ultimately targets through the studentAcademicRecordReference hop.
        var matchSarPayload = new JsonObject
        {
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = matchStudentUniqueId },
            ["educationOrganizationReference"] = new JsonObject { ["educationOrganizationId"] = schoolId },
            ["schoolYearTypeReference"] = new JsonObject { ["schoolYear"] = SchoolYear },
            ["termDescriptor"] = termDescriptorUri,
        };
        await PostExpectingCreatedAsync(harness, StudentAcademicRecordsEndpoint, matchSarPayload);

        var otherSarPayload = new JsonObject
        {
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = otherStudentUniqueId },
            ["educationOrganizationReference"] = new JsonObject { ["educationOrganizationId"] = schoolId },
            ["schoolYearTypeReference"] = new JsonObject { ["schoolYear"] = SchoolYear },
            ["termDescriptor"] = termDescriptorUri,
        };
        await PostExpectingCreatedAsync(harness, StudentAcademicRecordsEndpoint, otherSarPayload);

        var matchCtPayload = new JsonObject
        {
            ["courseAttemptResultDescriptor"] = courseAttemptResultDescriptorUri,
            ["courseReference"] = new JsonObject
            {
                ["courseCode"] = courseCode,
                ["educationOrganizationId"] = schoolId,
            },
            ["studentAcademicRecordReference"] = new JsonObject
            {
                ["educationOrganizationId"] = schoolId,
                ["schoolYear"] = SchoolYear,
                ["studentUniqueId"] = matchStudentUniqueId,
                ["termDescriptor"] = termDescriptorUri,
            },
        };
        await PostExpectingCreatedAsync(harness, CourseTranscriptsEndpoint, matchCtPayload);

        var otherCtPayload = new JsonObject
        {
            ["courseAttemptResultDescriptor"] = courseAttemptResultDescriptorUri,
            ["courseReference"] = new JsonObject
            {
                ["courseCode"] = courseCode,
                ["educationOrganizationId"] = schoolId,
            },
            ["studentAcademicRecordReference"] = new JsonObject
            {
                ["educationOrganizationId"] = schoolId,
                ["schoolYear"] = SchoolYear,
                ["studentUniqueId"] = otherStudentUniqueId,
                ["termDescriptor"] = termDescriptorUri,
            },
        };
        await PostExpectingCreatedAsync(harness, CourseTranscriptsEndpoint, otherCtPayload);

        // Plain GET-all must succeed (proves capability is compiled and handler does not 501).
        JsonArray allTranscripts = await GetExpectingOkArrayAsync(harness, CourseTranscriptsEndpoint);
        allTranscripts.Count.Should().BeGreaterThanOrEqualTo(2, "both seeded transcripts must be present");

        // Filter by the matching student — the through-reference alias predicate must select exactly
        // the one transcript whose studentAcademicRecordReference.studentUniqueId equals the queried value.
        JsonArray matchResult = await GetExpectingOkArrayAsync(
            harness,
            $"{CourseTranscriptsEndpoint}?studentUniqueId={matchStudentUniqueId}"
        );
        matchResult
            .Count.Should()
            .Be(1, $"filter ?studentUniqueId={matchStudentUniqueId} must return exactly 1 result");
        matchResult[0]!["studentAcademicRecordReference"]!["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be(matchStudentUniqueId);

        // Filter by a non-existent student — the result must be an empty array, not a 501 or error.
        JsonArray noneResult = await GetExpectingOkArrayAsync(
            harness,
            $"{CourseTranscriptsEndpoint}?studentUniqueId={noneStudentUniqueId}"
        );
        noneResult
            .Count.Should()
            .Be(0, $"filter ?studentUniqueId={noneStudentUniqueId} must return empty array");
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

    private static async Task<JsonArray> GetExpectingOkArrayAsync(
        ApiIntegrationHarness harness,
        string requestUri
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(requestUri);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {requestUri} body: {body}");

        return JsonNode.Parse(body) as JsonArray
            ?? throw new InvalidOperationException($"GET {requestUri} did not return a JSON array: {body}");
    }
}
