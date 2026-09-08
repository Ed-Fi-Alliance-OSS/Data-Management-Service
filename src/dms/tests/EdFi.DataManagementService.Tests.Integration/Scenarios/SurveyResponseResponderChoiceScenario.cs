// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using static EdFi.DataManagementService.Tests.Integration.Scenarios.PeopleRelationshipGetManyScenarioHelpers;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class SurveyResponseResponderChoiceScenario
{
    private const long SchoolId = 1206;
    private const int SchoolYear = 2026;
    private const string CalendarCode = "dms-1206-calendar";
    private const string SessionName = "DMS-1206 Session";
    private const string SurveyNamespace = "uri://ed-fi.org/DMS-1206/Survey";
    private const string SurveyIdentifier = "dms-1206-survey";
    private const string ContactUniqueId = "dms-1206-contact";
    private const string StaffUniqueId = "dms-1206-staff";
    private const string StudentUniqueId = "dms-1206-student";

    private const string CalendarTypeDescriptorsEndpoint = "/data/ed-fi/calendarTypeDescriptors";
    private const string ContactsEndpoint = "/data/ed-fi/contacts";
    private const string EducationOrganizationCategoryDescriptorsEndpoint =
        "/data/ed-fi/educationOrganizationCategoryDescriptors";
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";
    private const string CalendarsEndpoint = "/data/ed-fi/calendars";
    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string SchoolYearTypesEndpoint = "/data/ed-fi/schoolYearTypes";
    private const string SessionsEndpoint = "/data/ed-fi/sessions";
    private const string StaffsEndpoint = "/data/ed-fi/staffs";
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string SurveyCategoryDescriptorsEndpoint = "/data/ed-fi/surveyCategoryDescriptors";
    private const string SurveyResponsesEndpoint = "/data/ed-fi/surveyResponses";
    private const string SurveysEndpoint = "/data/ed-fi/surveys";
    private const string TermDescriptorsEndpoint = "/data/ed-fi/termDescriptors";

    private const string CalendarTypeNamespace = "uri://ed-fi.org/CalendarTypeDescriptor";
    private const string EducationOrganizationCategoryNamespace =
        "uri://ed-fi.org/EducationOrganizationCategoryDescriptor";
    private const string GradeLevelNamespace = "uri://ed-fi.org/GradeLevelDescriptor";
    private const string SurveyCategoryNamespace = "uri://ed-fi.org/SurveyCategoryDescriptor";
    private const string TermNamespace = "uri://ed-fi.org/TermDescriptor";

    private const string CalendarTypeDescriptor = $"{CalendarTypeNamespace}#Instructional day";
    private const string EducationOrganizationCategoryDescriptor =
        $"{EducationOrganizationCategoryNamespace}#School";
    private const string GradeLevelDescriptor = $"{GradeLevelNamespace}#Tenth grade";
    private const string SurveyCategoryDescriptor = $"{SurveyCategoryNamespace}#School Climate";
    private const string TermDescriptor = $"{TermNamespace}#Fall Semester";

    private static readonly SurveyResponseCase[] _cases =
    [
        new("contact responder choice", "dms-1206-response-contact", SurveyResponderChoiceBranch.Contact),
        new("staff responder choice", "dms-1206-response-staff", SurveyResponderChoiceBranch.Staff),
        new("student responder choice", "dms-1206-response-student", SurveyResponderChoiceBranch.Student),
        new("no responder choice", "dms-1206-response-none", SurveyResponderChoiceBranch.None),
    ];

    public static IReadOnlyList<SurveyResponseCase> Cases => _cases;

    public static async Task It_creates_survey_responses_for_responder_choice_cases(
        ApiIntegrationHarness harness
    )
    {
        await PreparePrerequisitesAsync(harness);

        foreach (SurveyResponseCase scenarioCase in Cases)
        {
            await CreateAndAssertSurveyResponseAsync(harness, scenarioCase);
        }
    }

    public static async Task It_creates_survey_response_for_responder_choice_case(
        ApiIntegrationHarness harness,
        SurveyResponseCase scenarioCase
    )
    {
        await PreparePrerequisitesAsync(harness);
        await CreateAndAssertSurveyResponseAsync(harness, scenarioCase);
    }

    public static JsonObject CreateSurveyResponsePayload(SurveyResponseCase scenarioCase)
    {
        var payload = new JsonObject
        {
            ["surveyResponseIdentifier"] = scenarioCase.SurveyResponseIdentifier,
            ["surveyReference"] = new JsonObject
            {
                ["namespace"] = SurveyNamespace,
                ["surveyIdentifier"] = SurveyIdentifier,
            },
            ["responseDate"] = "2026-02-20",
        };

        switch (scenarioCase.Branch)
        {
            case SurveyResponderChoiceBranch.Contact:
                payload["contactReference"] = new JsonObject { ["contactUniqueId"] = ContactUniqueId };
                break;
            case SurveyResponderChoiceBranch.Staff:
                payload["staffReference"] = new JsonObject { ["staffUniqueId"] = StaffUniqueId };
                break;
            case SurveyResponderChoiceBranch.Student:
                payload["studentReference"] = new JsonObject { ["studentUniqueId"] = StudentUniqueId };
                break;
            case SurveyResponderChoiceBranch.None:
                break;
        }

        return payload;
    }

    public static async Task AssertResponderChoiceColumnsAsync(
        ApiIntegrationHarness harness,
        SurveyResponseCase scenarioCase
    )
    {
        ResponderChoiceColumnState state = await ReadResponderChoiceColumnStateAsync(
            harness,
            scenarioCase.SurveyResponseIdentifier
        );

        state
            .ContactPresent.Should()
            .Be(
                scenarioCase.Branch is SurveyResponderChoiceBranch.Contact,
                "only the selected contact responder-choice reference group should be stored"
            );
        state
            .StaffPresent.Should()
            .Be(
                scenarioCase.Branch is SurveyResponderChoiceBranch.Staff,
                "only the selected staff responder-choice reference group should be stored"
            );
        state
            .StudentPresent.Should()
            .Be(
                scenarioCase.Branch is SurveyResponderChoiceBranch.Student,
                "only the selected student responder-choice reference group should be stored"
            );
    }

    private static async Task CreateAndAssertSurveyResponseAsync(
        ApiIntegrationHarness harness,
        SurveyResponseCase scenarioCase
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            SurveyResponsesEndpoint,
            CreateSurveyResponsePayload(scenarioCase)
        );
        string body = await response.Content.ReadAsStringAsync();

        body.Should()
            .NotContain(
                "unrecognized final write failure",
                "the relational write path must accept nullable unselected responder-choice branches"
            );
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);

        await AssertResponderChoiceColumnsAsync(harness, scenarioCase);
    }

    private static async Task PreparePrerequisitesAsync(ApiIntegrationHarness harness)
    {
        await CreateDescriptorAsync(
            harness,
            CalendarTypeDescriptorsEndpoint,
            CalendarTypeNamespace,
            "Instructional day"
        );
        await CreateDescriptorAsync(
            harness,
            EducationOrganizationCategoryDescriptorsEndpoint,
            EducationOrganizationCategoryNamespace,
            "School"
        );
        await CreateDescriptorAsync(
            harness,
            GradeLevelDescriptorsEndpoint,
            GradeLevelNamespace,
            "Tenth grade"
        );
        await CreateDescriptorAsync(
            harness,
            SurveyCategoryDescriptorsEndpoint,
            SurveyCategoryNamespace,
            "School Climate"
        );
        await CreateDescriptorAsync(harness, TermDescriptorsEndpoint, TermNamespace, "Fall Semester");

        await CreateSchoolYearTypeAsync(harness, SchoolYearTypesEndpoint, SchoolYear);
        await CreateSchoolAsync(
            harness,
            SchoolsEndpoint,
            SchoolId,
            "DMS-1206 School",
            EducationOrganizationCategoryDescriptor,
            GradeLevelDescriptor
        );
        await CreateCalendarAsync(harness);
        await CreateSessionAsync(harness);
        await CreateSurveyAsync(harness);
        await CreateContactAsync(harness);
        await CreateStaffAsync(harness);
        await CreateStudentAsync(harness, StudentsEndpoint, StudentUniqueId, "SurveyResponse");
    }

    private static async Task CreateCalendarAsync(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            CalendarsEndpoint,
            new JsonObject
            {
                ["calendarCode"] = CalendarCode,
                ["calendarTypeDescriptor"] = CalendarTypeDescriptor,
                ["schoolReference"] = new JsonObject { ["schoolId"] = SchoolId },
                ["schoolYearTypeReference"] = new JsonObject { ["schoolYear"] = SchoolYear },
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task CreateSessionAsync(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            SessionsEndpoint,
            new JsonObject
            {
                ["sessionName"] = SessionName,
                ["schoolReference"] = new JsonObject { ["schoolId"] = SchoolId },
                ["schoolYearTypeReference"] = new JsonObject { ["schoolYear"] = SchoolYear },
                ["beginDate"] = "2026-01-05",
                ["endDate"] = "2026-05-29",
                ["termDescriptor"] = TermDescriptor,
                ["totalInstructionalDays"] = 90,
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task CreateSurveyAsync(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            SurveysEndpoint,
            new JsonObject
            {
                ["surveyIdentifier"] = SurveyIdentifier,
                ["namespace"] = SurveyNamespace,
                ["surveyTitle"] = "DMS-1206 responder-choice survey",
                ["schoolYearTypeReference"] = new JsonObject { ["schoolYear"] = SchoolYear },
                ["sessionReference"] = new JsonObject
                {
                    ["schoolId"] = SchoolId,
                    ["schoolYear"] = SchoolYear,
                    ["sessionName"] = SessionName,
                },
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["educationOrganizationId"] = SchoolId,
                },
                ["surveyCategoryDescriptor"] = SurveyCategoryDescriptor,
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task CreateContactAsync(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            ContactsEndpoint,
            new JsonObject
            {
                ["contactUniqueId"] = ContactUniqueId,
                ["firstName"] = "DMS-1206",
                ["lastSurname"] = "Contact",
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task CreateStaffAsync(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            StaffsEndpoint,
            new JsonObject
            {
                ["staffUniqueId"] = StaffUniqueId,
                ["firstName"] = "DMS-1206",
                ["lastSurname"] = "Staff",
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    private static async Task<ResponderChoiceColumnState> ReadResponderChoiceColumnStateAsync(
        ApiIntegrationHarness harness,
        string surveyResponseIdentifier
    )
    {
        string sql = IsMssql(harness.DbConnection)
            ? """
                SELECT
                    CASE WHEN [SurveyResponderChoiceContact_DocumentId] IS NOT NULL
                           AND [SurveyResponderChoiceContact_ContactUniqueId] IS NOT NULL THEN 1 ELSE 0 END AS [ContactPresent],
                    CASE WHEN [SurveyResponderChoiceStaff_DocumentId] IS NOT NULL
                           AND [SurveyResponderChoiceStaff_StaffUniqueId] IS NOT NULL THEN 1 ELSE 0 END AS [StaffPresent],
                    CASE WHEN [SurveyResponderChoiceStudent_DocumentId] IS NOT NULL
                           AND [SurveyResponderChoiceStudent_StudentUniqueId] IS NOT NULL THEN 1 ELSE 0 END AS [StudentPresent]
                FROM [edfi].[SurveyResponse]
                WHERE [SurveyResponseIdentifier] = @surveyResponseIdentifier;
                """
            : """
                SELECT
                    CASE WHEN "SurveyResponderChoiceContact_DocumentId" IS NOT NULL
                           AND "SurveyResponderChoiceContact_ContactUniqueId" IS NOT NULL THEN 1 ELSE 0 END AS "ContactPresent",
                    CASE WHEN "SurveyResponderChoiceStaff_DocumentId" IS NOT NULL
                           AND "SurveyResponderChoiceStaff_StaffUniqueId" IS NOT NULL THEN 1 ELSE 0 END AS "StaffPresent",
                    CASE WHEN "SurveyResponderChoiceStudent_DocumentId" IS NOT NULL
                           AND "SurveyResponderChoiceStudent_StudentUniqueId" IS NOT NULL THEN 1 ELSE 0 END AS "StudentPresent"
                FROM "edfi"."SurveyResponse"
                WHERE "SurveyResponseIdentifier" = @surveyResponseIdentifier;
                """;

        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, ("@surveyResponseIdentifier", surveyResponseIdentifier));

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync())
            .Should()
            .BeTrue(
                "SurveyResponse row '{0}' should have been created before checking responder-choice columns",
                surveyResponseIdentifier
            );

        return new(
            ContactPresent: Convert.ToInt32(reader["ContactPresent"], CultureInfo.InvariantCulture) == 1,
            StaffPresent: Convert.ToInt32(reader["StaffPresent"], CultureInfo.InvariantCulture) == 1,
            StudentPresent: Convert.ToInt32(reader["StudentPresent"], CultureInfo.InvariantCulture) == 1
        );
    }
}

internal sealed record SurveyResponseCase(
    string Name,
    string SurveyResponseIdentifier,
    SurveyResponderChoiceBranch Branch
)
{
    public override string ToString() => Name;
}

internal enum SurveyResponderChoiceBranch
{
    Contact,
    Staff,
    Student,
    None,
}

internal sealed record ResponderChoiceColumnState(
    bool ContactPresent,
    bool StaffPresent,
    bool StudentPresent
);
