// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// The documents both providers seed to show identification-code filters composing with row-level
/// authorization over the authoritative Data Standard 5.2 schema. Each resource gets a matching document the
/// caller is authorized for, a matching document it is not, and an authorized document that does not match.
/// </summary>
/// <remarks>
/// <para>
/// StudentEducationOrganizationAssociation is secured by its education organization. The authorized and
/// unauthorized associations differ only in that school, and the authorized matching one carries the code on
/// two elements, so a filter that joined instead of testing existence would return or count it twice.
/// </para>
/// <para>
/// Assessment is secured by its namespace. The authorized and unauthorized assessments differ only in their
/// namespace.
/// </para>
/// </remarks>
internal static class IdentificationCodeAuthorizationSeeds
{
    public const long ClaimEducationOrganizationId = 900;
    public const int AuthorizedSchoolId = 810100;
    public const int UnauthorizedSchoolId = 810200;
    public const string AuthorizedNamespacePrefix = "uri://authorized.example.org";

    public const string MatchingStudentCode = "K-1";
    public const string MatchingAssigningOrganization = "ORG-1";
    public const string MatchingAssessmentCode = "Q-1";

    public const string StateStudentSystem = "uri://ed-fi.org/StudentIdentificationSystemDescriptor#State";
    public const string DistrictStudentSystem =
        "uri://ed-fi.org/StudentIdentificationSystemDescriptor#District";
    public const string ContractorAssessmentSystem =
        "uri://ed-fi.org/AssessmentIdentificationSystemDescriptor#Test Contractor";

    // Seeded in this order, so DocumentId increases in this order within each resource.
    public static readonly Guid AuthorizedMatchingSeoaUuid = Guid.Parse(
        "d8102700-0000-0000-0000-000000000001"
    );
    public static readonly Guid UnauthorizedMatchingSeoaUuid = Guid.Parse(
        "d8102700-0000-0000-0000-000000000002"
    );
    public static readonly Guid AuthorizedNonMatchingSeoaUuid = Guid.Parse(
        "d8102700-0000-0000-0000-000000000003"
    );
    public static readonly Guid AuthorizedMatchingAssessmentUuid = Guid.Parse(
        "d8102700-0000-0000-0000-000000000011"
    );
    public static readonly Guid UnauthorizedMatchingAssessmentUuid = Guid.Parse(
        "d8102700-0000-0000-0000-000000000012"
    );
    public static readonly Guid AuthorizedNonMatchingAssessmentUuid = Guid.Parse(
        "d8102700-0000-0000-0000-000000000013"
    );

    /// <summary>
    /// Seeds the descriptors, schools, students, associations and assessments through the supplied provider
    /// context operations. The caller adds the authorization edge to <see cref="AuthorizedSchoolId" />.
    /// </summary>
    public static async Task SeedAsync(
        Func<Task> seedSchoolDescriptorDataAsync,
        Func<Guid, string, string, string, string, Task> seedDescriptorAsync,
        Func<string, string, JsonNode, DocumentUuid, string, Task<UpsertResult>> upsertAsync
    )
    {
        await seedSchoolDescriptorDataAsync();

        (string ResourceName, string CodeValue)[] descriptors =
        [
            ("StudentIdentificationSystemDescriptor", "State"),
            ("StudentIdentificationSystemDescriptor", "District"),
            ("AssessmentIdentificationSystemDescriptor", "Test Contractor"),
            ("AcademicSubjectDescriptor", "Mathematics"),
        ];

        foreach (var (resourceName, codeValue) in descriptors)
        {
            await seedDescriptorAsync(
                Guid.NewGuid(),
                resourceName,
                $"uri://ed-fi.org/{resourceName}",
                codeValue,
                codeValue
            );
        }

        async Task UpsertAsync(string resourceName, Guid documentUuid, JsonNode body) =>
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await upsertAsync(
                    "ed-fi",
                    resourceName,
                    body,
                    new DocumentUuid(documentUuid),
                    $"seed-ident-code-auth-{documentUuid}"
                )
            );

        await UpsertAsync(
            "School",
            Guid.Parse("d8102700-0000-0000-0000-0000000000a1"),
            RelationalQueryAuthorizationRequestBodies.CreateSchoolRequestBody(
                AuthorizedSchoolId,
                "Authorized"
            )
        );
        await UpsertAsync(
            "School",
            Guid.Parse("d8102700-0000-0000-0000-0000000000a2"),
            RelationalQueryAuthorizationRequestBodies.CreateSchoolRequestBody(
                UnauthorizedSchoolId,
                "Unauthorized"
            )
        );
        await UpsertAsync(
            "Student",
            Guid.Parse("d8102700-0000-0000-0000-0000000000b1"),
            StudentBody("ST-810-1")
        );
        await UpsertAsync(
            "Student",
            Guid.Parse("d8102700-0000-0000-0000-0000000000b2"),
            StudentBody("ST-810-2")
        );

        await UpsertAsync(
            "StudentEducationOrganizationAssociation",
            AuthorizedMatchingSeoaUuid,
            SeoaBody(
                "ST-810-1",
                AuthorizedSchoolId,
                (MatchingStudentCode, StateStudentSystem, MatchingAssigningOrganization),
                (MatchingStudentCode, DistrictStudentSystem, "ORG-2")
            )
        );
        await UpsertAsync(
            "StudentEducationOrganizationAssociation",
            UnauthorizedMatchingSeoaUuid,
            SeoaBody(
                "ST-810-1",
                UnauthorizedSchoolId,
                (MatchingStudentCode, StateStudentSystem, MatchingAssigningOrganization)
            )
        );
        await UpsertAsync(
            "StudentEducationOrganizationAssociation",
            AuthorizedNonMatchingSeoaUuid,
            SeoaBody(
                "ST-810-2",
                AuthorizedSchoolId,
                ("K-2", StateStudentSystem, MatchingAssigningOrganization)
            )
        );

        await UpsertAsync(
            "Assessment",
            AuthorizedMatchingAssessmentUuid,
            AssessmentBody("A-1", $"{AuthorizedNamespacePrefix}/assessments", MatchingAssessmentCode)
        );
        await UpsertAsync(
            "Assessment",
            UnauthorizedMatchingAssessmentUuid,
            AssessmentBody("A-1", "uri://other.example.org/assessments", MatchingAssessmentCode)
        );
        await UpsertAsync(
            "Assessment",
            AuthorizedNonMatchingAssessmentUuid,
            AssessmentBody("A-2", $"{AuthorizedNamespacePrefix}/assessments", "Q-2")
        );
    }

    private static JsonNode StudentBody(string studentUniqueId) =>
        new JsonObject
        {
            ["studentUniqueId"] = studentUniqueId,
            ["firstName"] = studentUniqueId,
            ["lastSurname"] = "Student",
            ["birthDate"] = "2010-01-01",
        };

    private static JsonNode SeoaBody(
        string studentUniqueId,
        int educationOrganizationId,
        params (string Code, string System, string AssigningOrganization)[] identificationCodes
    ) =>
        new JsonObject
        {
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = studentUniqueId },
            ["educationOrganizationReference"] = new JsonObject
            {
                ["educationOrganizationId"] = (long)educationOrganizationId,
            },
            ["studentIdentificationCodes"] = new JsonArray([
                .. identificationCodes.Select(static code =>
                    (JsonNode)
                        new JsonObject
                        {
                            ["identificationCode"] = code.Code,
                            ["studentIdentificationSystemDescriptor"] = code.System,
                            ["assigningOrganizationIdentificationCode"] = code.AssigningOrganization,
                        }
                ),
            ]),
        };

    private static JsonNode AssessmentBody(string assessmentIdentifier, string @namespace, string code) =>
        new JsonObject
        {
            ["assessmentIdentifier"] = assessmentIdentifier,
            ["namespace"] = @namespace,
            ["assessmentTitle"] = assessmentIdentifier,
            ["academicSubjects"] = new JsonArray(
                new JsonObject
                {
                    ["academicSubjectDescriptor"] = "uri://ed-fi.org/AcademicSubjectDescriptor#Mathematics",
                }
            ),
            ["identificationCodes"] = new JsonArray(
                new JsonObject
                {
                    ["identificationCode"] = code,
                    ["assessmentIdentificationSystemDescriptor"] = ContractorAssessmentSystem,
                }
            ),
        };
}
