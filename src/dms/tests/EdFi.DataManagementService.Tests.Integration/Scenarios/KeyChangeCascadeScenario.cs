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
/// Public HTTP regression proving a key-changing PUT on a resource with
/// <c>allowIdentityUpdates</c> cascades the changed identity column into a referrer whose
/// foreign key converges with another reference from the same identity origin. On SQL Server,
/// the foreign-key pruning pass keeps only one <c>ON UPDATE CASCADE</c> edge per convergence;
/// when it cuts the edge to a directly mutable intermediate, the PUT fails with error 547 and a
/// 500 instead of cascading.
///
/// Concrete shapes:
/// <list type="bullet">
/// <item>A Section moves to another Session while a StudentSectionAssociation references it
/// (<c>FK_StudentSectionAssociation_Section_RefKey</c> converges with the optional
/// DualCreditEducationOrganization reference).</item>
/// <item>A StudentSchoolAssociation changes its entryDate while a StudentAssessmentRegistration
/// references it (<c>FK_StudentAssessmentRegistration_StudentSchoolAssociation_RefKey</c>
/// converges with the AssessmentAdministration reference).</item>
/// </list>
///
/// Bound to <c>FixtureKey.AuthoritativeDs52</c> because the defect requires the production
/// reference topology that drives those convergences.
/// </summary>
internal static class KeyChangeCascadeScenario
{
    private const string StandardJsonContentType = "application/json";
    private const string EducationOrganizationCategoryDescriptorsEndpoint =
        "/data/ed-fi/educationOrganizationCategoryDescriptors";
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";
    private const string TermDescriptorsEndpoint = "/data/ed-fi/termDescriptors";
    private const string CourseIdentificationSystemDescriptorsEndpoint =
        "/data/ed-fi/courseIdentificationSystemDescriptors";
    private const string AcademicSubjectDescriptorsEndpoint = "/data/ed-fi/academicSubjectDescriptors";
    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string SchoolYearTypesEndpoint = "/data/ed-fi/schoolYearTypes";
    private const string CoursesEndpoint = "/data/ed-fi/courses";
    private const string SessionsEndpoint = "/data/ed-fi/sessions";
    private const string CourseOfferingsEndpoint = "/data/ed-fi/courseOfferings";
    private const string SectionsEndpoint = "/data/ed-fi/sections";
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string StudentSectionAssociationsEndpoint = "/data/ed-fi/studentSectionAssociations";
    private const string StudentSchoolAssociationsEndpoint = "/data/ed-fi/studentSchoolAssociations";
    private const string StudentEducationOrganizationAssociationsEndpoint =
        "/data/ed-fi/studentEducationOrganizationAssociations";
    private const string AssessmentsEndpoint = "/data/ed-fi/assessments";
    private const string AssessmentAdministrationsEndpoint = "/data/ed-fi/assessmentAdministrations";
    private const string StudentAssessmentRegistrationsEndpoint =
        "/data/ed-fi/studentAssessmentRegistrations";

    private const int SchoolYear = 2024;

    public static async Task It_cascades_a_section_session_change_to_the_student_section_association(
        ApiIntegrationHarness harness
    )
    {
        string suffix = NewSuffix();
        long schoolId = SchoolIdFor(suffix);
        string namespaceUri = $"uri://ed-fi.org/KeyChangeCascade/{suffix}";
        string termDescriptorUri = $"{namespaceUri}/TermDescriptor#Fall Semester";
        string courseIdSystemDescriptorUri =
            $"{namespaceUri}/CourseIdentificationSystemDescriptor#LEA course code";
        string courseCode = $"ALG-1-{suffix}";
        string fallSessionName = $"Fall {suffix}";
        string springSessionName = $"Spring {suffix}";
        string localCourseCode = $"LCC-{suffix}";
        string sectionIdentifier = $"SEC-{suffix}";
        string studentUniqueId = $"stu-{suffix}";

        await SeedSchoolAsync(harness, suffix, schoolId, namespaceUri);
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

        foreach (string sessionName in new[] { fallSessionName, springSessionName })
        {
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

            // One CourseOffering per session, sharing one localCourseCode, so the Section can move
            // between them by changing only its sessionName.
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
        }

        string sectionLocationPath = await PostExpectingCreatedAsync(
            harness,
            SectionsEndpoint,
            SectionPayload(sectionIdentifier, localCourseCode, schoolId, fallSessionName)
        );

        await SeedStudentAsync(harness, studentUniqueId);

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
                ["sessionName"] = fallSessionName,
            },
        };
        string studentSectionAssociationLocationPath = await PostExpectingCreatedAsync(
            harness,
            StudentSectionAssociationsEndpoint,
            studentSectionAssociationPayload
        );

        JsonObject sectionUpdatePayload = SectionPayload(
            sectionIdentifier,
            localCourseCode,
            schoolId,
            springSessionName
        );
        sectionUpdatePayload["id"] = ResourceIdOf(sectionLocationPath);
        await PutExpectingNoContentAsync(harness, sectionLocationPath, sectionUpdatePayload);

        JsonObject sectionReference = (
            await GetJsonObjectAsync(harness, studentSectionAssociationLocationPath)
        )["sectionReference"]!.AsObject();
        sectionReference["sessionName"]!.GetValue<string>().Should().Be(springSessionName);
        sectionReference["sectionIdentifier"]!.GetValue<string>().Should().Be(sectionIdentifier);
        sectionReference["localCourseCode"]!.GetValue<string>().Should().Be(localCourseCode);
        sectionReference["schoolId"]!.GetValue<long>().Should().Be(schoolId);
        sectionReference["schoolYear"]!.GetValue<int>().Should().Be(SchoolYear);
    }

    public static async Task It_cascades_a_student_school_association_entry_date_change_to_the_student_assessment_registration(
        ApiIntegrationHarness harness
    )
    {
        string suffix = NewSuffix();
        long schoolId = SchoolIdFor(suffix);
        string namespaceUri = $"uri://ed-fi.org/KeyChangeCascade/{suffix}";
        string gradeLevelDescriptorUri = $"{namespaceUri}/GradeLevelDescriptor#Tenth grade";
        string academicSubjectDescriptorUri = $"{namespaceUri}/AcademicSubjectDescriptor#Mathematics";
        string studentUniqueId = $"stu-{suffix}";
        string assessmentIdentifier = $"ASMT-{suffix}";
        string assessmentNamespace = $"{namespaceUri}/Assessment";
        string administrationIdentifier = $"ADM-{suffix}";
        const string OriginalEntryDate = "2024-08-01";
        const string ChangedEntryDate = "2024-08-15";

        await SeedSchoolAsync(harness, suffix, schoolId, namespaceUri);
        await SeedDescriptorAsync(
            harness,
            AcademicSubjectDescriptorsEndpoint,
            namespaceUri: $"{namespaceUri}/AcademicSubjectDescriptor",
            codeValue: "Mathematics",
            shortDescription: "Mathematics"
        );
        await SeedStudentAsync(harness, studentUniqueId);

        string studentSchoolAssociationLocationPath = await PostExpectingCreatedAsync(
            harness,
            StudentSchoolAssociationsEndpoint,
            StudentSchoolAssociationPayload(
                studentUniqueId,
                schoolId,
                OriginalEntryDate,
                gradeLevelDescriptorUri
            )
        );

        var studentEducationOrganizationAssociationPayload = new JsonObject
        {
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = studentUniqueId },
            ["educationOrganizationReference"] = new JsonObject { ["educationOrganizationId"] = schoolId },
        };
        await PostExpectingCreatedAsync(
            harness,
            StudentEducationOrganizationAssociationsEndpoint,
            studentEducationOrganizationAssociationPayload
        );

        var assessmentPayload = new JsonObject
        {
            ["assessmentIdentifier"] = assessmentIdentifier,
            ["namespace"] = assessmentNamespace,
            ["assessmentTitle"] = $"Assessment {suffix}",
            ["academicSubjects"] = new JsonArray(
                new JsonObject { ["academicSubjectDescriptor"] = academicSubjectDescriptorUri }
            ),
        };
        await PostExpectingCreatedAsync(harness, AssessmentsEndpoint, assessmentPayload);

        var assessmentAdministrationPayload = new JsonObject
        {
            ["administrationIdentifier"] = administrationIdentifier,
            ["assessmentReference"] = new JsonObject
            {
                ["assessmentIdentifier"] = assessmentIdentifier,
                ["namespace"] = assessmentNamespace,
            },
            ["assigningEducationOrganizationReference"] = new JsonObject
            {
                ["educationOrganizationId"] = schoolId,
            },
        };
        await PostExpectingCreatedAsync(
            harness,
            AssessmentAdministrationsEndpoint,
            assessmentAdministrationPayload
        );

        var studentAssessmentRegistrationPayload = new JsonObject
        {
            ["assessmentAdministrationReference"] = new JsonObject
            {
                ["administrationIdentifier"] = administrationIdentifier,
                ["assessmentIdentifier"] = assessmentIdentifier,
                ["namespace"] = assessmentNamespace,
                ["assigningEducationOrganizationId"] = schoolId,
            },
            ["studentEducationOrganizationAssociationReference"] = new JsonObject
            {
                ["educationOrganizationId"] = schoolId,
                ["studentUniqueId"] = studentUniqueId,
            },
            ["studentSchoolAssociationReference"] = new JsonObject
            {
                ["entryDate"] = OriginalEntryDate,
                ["schoolId"] = schoolId,
                ["studentUniqueId"] = studentUniqueId,
            },
        };
        string studentAssessmentRegistrationLocationPath = await PostExpectingCreatedAsync(
            harness,
            StudentAssessmentRegistrationsEndpoint,
            studentAssessmentRegistrationPayload
        );

        JsonObject studentSchoolAssociationUpdatePayload = StudentSchoolAssociationPayload(
            studentUniqueId,
            schoolId,
            ChangedEntryDate,
            gradeLevelDescriptorUri
        );
        studentSchoolAssociationUpdatePayload["id"] = ResourceIdOf(studentSchoolAssociationLocationPath);
        await PutExpectingNoContentAsync(
            harness,
            studentSchoolAssociationLocationPath,
            studentSchoolAssociationUpdatePayload
        );

        JsonObject studentSchoolAssociationReference = (
            await GetJsonObjectAsync(harness, studentAssessmentRegistrationLocationPath)
        )["studentSchoolAssociationReference"]!.AsObject();
        studentSchoolAssociationReference["entryDate"]!.GetValue<string>().Should().Be(ChangedEntryDate);
    }

    // Per-test unique values keep each case isolated even if the leased database baseline is
    // shared with other scenarios bound to this fixture. The schoolId derives deterministically
    // from the suffix's own hex value (not string.GetHashCode(), which is process-randomized) and
    // spans a ~1B range that stays int32-safe.
    private static string NewSuffix() => Guid.NewGuid().ToString("N")[..8];

    private static long SchoolIdFor(string suffix) =>
        1_000_000_000L + (Convert.ToInt64(suffix, 16) % 1_000_000_000L);

    private static string ResourceIdOf(string locationPath) => locationPath.Split('/')[^1];

    private static JsonObject SectionPayload(
        string sectionIdentifier,
        string localCourseCode,
        long schoolId,
        string sessionName
    ) =>
        new()
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

    private static JsonObject StudentSchoolAssociationPayload(
        string studentUniqueId,
        long schoolId,
        string entryDate,
        string gradeLevelDescriptorUri
    ) =>
        new()
        {
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = studentUniqueId },
            ["schoolReference"] = new JsonObject { ["schoolId"] = schoolId },
            ["entryDate"] = entryDate,
            ["entryGradeLevelDescriptor"] = gradeLevelDescriptorUri,
        };

    private static async Task SeedSchoolAsync(
        ApiIntegrationHarness harness,
        string suffix,
        long schoolId,
        string namespaceUri
    )
    {
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

        var schoolPayload = new JsonObject
        {
            ["schoolId"] = schoolId,
            ["nameOfInstitution"] = $"KeyChangeCascade School {suffix}",
            ["educationOrganizationCategories"] = new JsonArray(
                new JsonObject
                {
                    ["educationOrganizationCategoryDescriptor"] =
                        $"{namespaceUri}/EducationOrganizationCategoryDescriptor#School",
                }
            ),
            ["gradeLevels"] = new JsonArray(
                new JsonObject
                {
                    ["gradeLevelDescriptor"] = $"{namespaceUri}/GradeLevelDescriptor#Tenth grade",
                }
            ),
        };
        await PostExpectingCreatedAsync(harness, SchoolsEndpoint, schoolPayload);
    }

    private static async Task SeedStudentAsync(ApiIntegrationHarness harness, string studentUniqueId)
    {
        var studentPayload = new JsonObject
        {
            ["studentUniqueId"] = studentUniqueId,
            ["firstName"] = "Key",
            ["lastSurname"] = "Change",
            ["birthDate"] = "2012-01-01",
        };
        await PostExpectingCreatedAsync(harness, StudentsEndpoint, studentPayload);
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

    private static async Task<string> PostExpectingCreatedAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType);
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(endpoint, content);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST {endpoint} body: {body}");
        response.Headers.Location.Should().NotBeNull($"POST {endpoint} must return a Location header");

        Uri location = response.Headers.Location!;
        return location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
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

    private static async Task PutExpectingNoContentAsync(
        ApiIntegrationHarness harness,
        string locationPath,
        JsonObject payload
    )
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType);
        using HttpResponseMessage response = await harness.HttpClient.PutAsync(locationPath, content);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.NoContent,
                $"the key-changing PUT {locationPath} must succeed and cascade to its referrer. Body: {body}"
            );
    }

    private static async Task<JsonObject> GetJsonObjectAsync(ApiIntegrationHarness harness, string endpoint)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(endpoint);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {endpoint} body: {body}");
        JsonNode? node = JsonNode.Parse(body);
        node.Should().NotBeNull($"GET {endpoint} must return a JSON document");
        return node!.AsObject();
    }
}
