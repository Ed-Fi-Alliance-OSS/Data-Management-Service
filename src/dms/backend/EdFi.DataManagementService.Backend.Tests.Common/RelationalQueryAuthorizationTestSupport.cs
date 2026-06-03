// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal sealed record ClassPeriodSeed(DocumentUuid DocumentUuid, int SchoolId, string ClassPeriodName);

internal sealed record AuthorizationAndSeed(
    DocumentUuid DocumentUuid,
    int AuthorizationAndId,
    string Name,
    int PrimarySchoolId,
    int SecondarySchoolId
);

internal sealed record AuthorizationRootChildSeed(
    DocumentUuid DocumentUuid,
    int AuthorizationRootChildId,
    string Name,
    int SchoolId,
    IReadOnlyList<ClassPeriodReferenceSeed> ClassPeriods
);

internal sealed record AuthorizationChildOnlySeed(
    DocumentUuid DocumentUuid,
    int AuthorizationChildOnlyId,
    string Name,
    IReadOnlyList<ClassPeriodReferenceSeed> ClassPeriods
);

internal sealed record AuthorizationNullableSeed(
    DocumentUuid DocumentUuid,
    int AuthorizationNullableId,
    string Name,
    int? NullableSchoolId = null
);

internal sealed record StudentSeed(
    DocumentUuid DocumentUuid,
    string StudentUniqueId,
    string FirstName,
    string LastSurname
);

internal sealed record SchoolYearTypeSeed(
    DocumentUuid DocumentUuid,
    int SchoolYear,
    bool CurrentSchoolYear,
    string SchoolYearDescription
);

internal sealed record StudentSchoolAssociationSeed(
    DocumentUuid DocumentUuid,
    string StudentUniqueId,
    int SchoolId,
    int SchoolYear,
    string EntryGradeLevelDescriptor,
    DateOnly EntryDate
);

internal sealed record StudentAcademicRecordSeed(
    DocumentUuid DocumentUuid,
    int EducationOrganizationId,
    int SchoolYear,
    string StudentUniqueId,
    string TermDescriptor
);

internal sealed record AuthorizationStudentAcademicRecordSeed(
    DocumentUuid DocumentUuid,
    int AuthorizationStudentAcademicRecordId,
    string Name,
    int EducationOrganizationId,
    int SchoolYear,
    string StudentUniqueId,
    string TermDescriptor
);

internal sealed record AuthorizationStudentSchoolSeed(
    DocumentUuid DocumentUuid,
    int AuthorizationStudentSchoolId,
    string Name,
    int SchoolId,
    string? StudentUniqueId
);

internal sealed record ContactSeed(
    DocumentUuid DocumentUuid,
    string ContactUniqueId,
    string FirstName,
    string LastSurname
);

internal sealed record StaffSeed(
    DocumentUuid DocumentUuid,
    string StaffUniqueId,
    string FirstName,
    string LastSurname
);

internal sealed record StudentContactAssociationSeed(
    DocumentUuid DocumentUuid,
    string StudentUniqueId,
    string ContactUniqueId,
    bool EmergencyContactStatus
);

internal sealed record StaffEducationOrganizationAssignmentAssociationSeed(
    DocumentUuid DocumentUuid,
    string StaffUniqueId,
    int EducationOrganizationId,
    string StaffClassificationDescriptor,
    DateOnly BeginDate
);

internal sealed record StudentEducationOrganizationResponsibilityAssociationSeed(
    DocumentUuid DocumentUuid,
    string StudentUniqueId,
    int EducationOrganizationId,
    string ResponsibilityDescriptor,
    DateOnly BeginDate
);

internal sealed record ClassPeriodReferenceSeed(string ClassPeriodName, int SchoolId);

internal sealed class RelationalQueryAuthorizationAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

internal static class RelationalQueryAuthorizationRequestBodies
{
    public static JsonNode CreateSchoolRequestBody(int schoolId, string nameOfInstitution)
    {
        return new JsonObject
        {
            ["schoolId"] = (long)schoolId,
            ["nameOfInstitution"] = nameOfInstitution,
            ["educationOrganizationCategories"] = new JsonArray(
                new JsonObject
                {
                    ["educationOrganizationCategoryDescriptor"] =
                        "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School",
                }
            ),
            ["gradeLevels"] = new JsonArray(
                new JsonObject
                {
                    ["gradeLevelDescriptor"] = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade",
                }
            ),
        };
    }

    public static JsonNode CreateClassPeriodRequestBody(ClassPeriodSeed seed)
    {
        return new JsonObject
        {
            ["classPeriodName"] = seed.ClassPeriodName,
            ["schoolReference"] = new JsonObject { ["schoolId"] = (long)seed.SchoolId },
        };
    }

    public static JsonNode CreateAuthorizationAndRequestBody(AuthorizationAndSeed seed)
    {
        return new JsonObject
        {
            ["authorizationAndId"] = seed.AuthorizationAndId,
            ["name"] = seed.Name,
            ["primarySchoolReference"] = new JsonObject { ["schoolId"] = (long)seed.PrimarySchoolId },
            ["secondarySchoolReference"] = new JsonObject { ["schoolId"] = (long)seed.SecondarySchoolId },
        };
    }

    public static JsonNode CreateAuthorizationRootChildRequestBody(AuthorizationRootChildSeed seed)
    {
        JsonArray classPeriods = [];

        foreach (var classPeriod in seed.ClassPeriods)
        {
            classPeriods.Add(
                new JsonObject
                {
                    ["classPeriodReference"] = new JsonObject
                    {
                        ["classPeriodName"] = classPeriod.ClassPeriodName,
                        ["schoolId"] = (long)classPeriod.SchoolId,
                    },
                }
            );
        }

        return new JsonObject
        {
            ["authorizationRootChildId"] = seed.AuthorizationRootChildId,
            ["name"] = seed.Name,
            ["schoolReference"] = new JsonObject { ["schoolId"] = (long)seed.SchoolId },
            ["classPeriods"] = classPeriods,
        };
    }

    public static JsonNode CreateAuthorizationChildOnlyRequestBody(AuthorizationChildOnlySeed seed)
    {
        JsonArray classPeriods = [];

        foreach (var classPeriod in seed.ClassPeriods)
        {
            classPeriods.Add(
                new JsonObject
                {
                    ["classPeriodReference"] = new JsonObject
                    {
                        ["classPeriodName"] = classPeriod.ClassPeriodName,
                        ["schoolId"] = (long)classPeriod.SchoolId,
                    },
                }
            );
        }

        return new JsonObject
        {
            ["authorizationChildOnlyId"] = seed.AuthorizationChildOnlyId,
            ["name"] = seed.Name,
            ["classPeriods"] = classPeriods,
        };
    }

    public static JsonNode CreateAuthorizationNullableRequestBody(AuthorizationNullableSeed seed)
    {
        JsonObject requestBody = new()
        {
            ["authorizationNullableId"] = seed.AuthorizationNullableId,
            ["name"] = seed.Name,
        };

        if (seed.NullableSchoolId is not null)
        {
            requestBody["nullableSchoolId"] = (long)seed.NullableSchoolId.Value;
        }

        return requestBody;
    }

    public static JsonNode CreateAuthorizationStudentAcademicRecordRequestBody(
        AuthorizationStudentAcademicRecordSeed seed
    )
    {
        return new JsonObject
        {
            ["authorizationStudentAcademicRecordId"] = seed.AuthorizationStudentAcademicRecordId,
            ["name"] = seed.Name,
            ["studentAcademicRecordReference"] = new JsonObject
            {
                ["educationOrganizationId"] = seed.EducationOrganizationId,
                ["schoolYear"] = seed.SchoolYear,
                ["studentUniqueId"] = seed.StudentUniqueId,
                ["termDescriptor"] = seed.TermDescriptor,
            },
        };
    }

    public static JsonNode CreateAuthorizationStudentSchoolRequestBody(AuthorizationStudentSchoolSeed seed)
    {
        JsonObject requestBody = new()
        {
            ["authorizationStudentSchoolId"] = seed.AuthorizationStudentSchoolId,
            ["name"] = seed.Name,
            ["schoolReference"] = new JsonObject { ["schoolId"] = (long)seed.SchoolId },
        };

        if (seed.StudentUniqueId is not null)
        {
            requestBody["studentReference"] = new JsonObject { ["studentUniqueId"] = seed.StudentUniqueId };
        }

        return requestBody;
    }

    public static JsonNode CreateContactRequestBody(ContactSeed seed)
    {
        return new JsonObject
        {
            ["contactUniqueId"] = seed.ContactUniqueId,
            ["firstName"] = seed.FirstName,
            ["lastSurname"] = seed.LastSurname,
        };
    }

    public static JsonNode CreateStaffRequestBody(StaffSeed seed)
    {
        return new JsonObject
        {
            ["staffUniqueId"] = seed.StaffUniqueId,
            ["firstName"] = seed.FirstName,
            ["lastSurname"] = seed.LastSurname,
        };
    }

    public static JsonNode CreateStudentContactAssociationRequestBody(StudentContactAssociationSeed seed)
    {
        return new JsonObject
        {
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = seed.StudentUniqueId },
            ["contactReference"] = new JsonObject { ["contactUniqueId"] = seed.ContactUniqueId },
            ["emergencyContactStatus"] = seed.EmergencyContactStatus,
        };
    }

    public static JsonNode CreateStaffEducationOrganizationAssignmentAssociationRequestBody(
        StaffEducationOrganizationAssignmentAssociationSeed seed
    )
    {
        return new JsonObject
        {
            ["staffReference"] = new JsonObject { ["staffUniqueId"] = seed.StaffUniqueId },
            ["educationOrganizationReference"] = new JsonObject
            {
                ["educationOrganizationId"] = seed.EducationOrganizationId,
            },
            ["staffClassificationDescriptor"] = seed.StaffClassificationDescriptor,
            ["beginDate"] = seed.BeginDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    public static JsonNode CreateStudentEducationOrganizationResponsibilityAssociationRequestBody(
        StudentEducationOrganizationResponsibilityAssociationSeed seed
    )
    {
        return new JsonObject
        {
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = seed.StudentUniqueId },
            ["educationOrganizationReference"] = new JsonObject
            {
                ["educationOrganizationId"] = seed.EducationOrganizationId,
            },
            ["responsibilityDescriptor"] = seed.ResponsibilityDescriptor,
            ["beginDate"] = seed.BeginDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }
}

internal static class RelationalQueryAuthorizationAssertions
{
    public static void AssertInsertSuccess(UpsertResult result)
    {
        if (result is UpsertResult.InsertSuccess)
        {
            return;
        }

        if (result is UpsertResult.UpsertFailureValidation validationFailure)
        {
            Assert.Fail(
                "Expected insert success but received validation failures: "
                    + string.Join(
                        "; ",
                        validationFailure.ValidationFailures.Select(static failure =>
                            $"{failure.Path.Value}: {failure.Message}"
                        )
                    )
            );
        }

        if (result is UpsertResult.UpsertFailureReference referenceFailure)
        {
            var documentFailures = referenceFailure.InvalidDocumentReferences.Select(static reference =>
                $"{reference.Path.Value} -> {reference.TargetResource.ProjectName.Value}.{reference.TargetResource.ResourceName.Value} ({reference.Reason})"
            );
            var descriptorFailures = referenceFailure.InvalidDescriptorReferences.Select(static reference =>
                $"{reference.Path.Value} -> {reference.TargetResource.ProjectName.Value}.{reference.TargetResource.ResourceName.Value} ({reference.Reason})"
            );

            Assert.Fail(
                "Expected insert success but received reference failures: "
                    + string.Join("; ", documentFailures.Concat(descriptorFailures))
            );
        }

        if (result is UpsertResult.UnknownFailure unknownFailure)
        {
            Assert.Fail(
                $"Expected insert success but received unknown failure: {unknownFailure.FailureMessage}"
            );
        }

        Assert.Fail($"Expected insert success but received result type '{result.GetType().Name}'.");
    }
}
