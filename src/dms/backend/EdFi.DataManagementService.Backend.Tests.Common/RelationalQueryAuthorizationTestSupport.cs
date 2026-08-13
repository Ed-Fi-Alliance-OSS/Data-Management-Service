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

/// <summary>
/// Seed for the synthetic <c>AuthorizationNamespaceResource</c>, whose root table carries both a nullable
/// <c>Namespace</c> securable column and an EdOrg securable column. A <see langword="null"/>
/// <paramref name="Namespace"/> omits the property from the request body, which is how a write exercises
/// the missing-proposed-namespace failure and how a stored row is seeded with an uninitialized namespace.
/// </summary>
internal sealed record AuthorizationNamespaceSeed(
    DocumentUuid DocumentUuid,
    int AuthorizationNamespaceId,
    string Name,
    string? Namespace,
    int SchoolId,
    IReadOnlyList<ClassPeriodReferenceSeed> ClassPeriods
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

/// <summary>
/// The employment half of the two staff pathways the <c>auth.EducationOrganizationIdToStaffDocumentId</c>
/// view combines. Seeding a staff member with both an assignment and an employment association at the same
/// EducationOrganization is what produces a cross-arm duplicate authorization pair.
/// </summary>
internal sealed record StaffEducationOrganizationEmploymentAssociationSeed(
    DocumentUuid DocumentUuid,
    string StaffUniqueId,
    int EducationOrganizationId,
    string EmploymentStatusDescriptor,
    DateOnly HireDate
);

internal sealed record StudentEducationOrganizationResponsibilityAssociationSeed(
    DocumentUuid DocumentUuid,
    string StudentUniqueId,
    int EducationOrganizationId,
    string ResponsibilityDescriptor,
    DateOnly BeginDate
);

internal sealed record ClassPeriodReferenceSeed(string ClassPeriodName, int SchoolId);

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

    public static JsonNode CreateAuthorizationNamespaceRequestBody(AuthorizationNamespaceSeed seed)
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

        JsonObject requestBody = new()
        {
            ["authorizationNamespaceId"] = seed.AuthorizationNamespaceId,
            ["name"] = seed.Name,
            ["schoolReference"] = new JsonObject { ["schoolId"] = (long)seed.SchoolId },
            ["classPeriods"] = classPeriods,
        };

        // Namespace is optional on this resource, so a null seed value omits the property entirely
        // (the missing-proposed-namespace case) while an empty string is written through as an
        // uninitialized stored value.
        if (seed.Namespace is not null)
        {
            requestBody["namespace"] = seed.Namespace;
        }

        return requestBody;
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

    public static JsonNode CreateStaffEducationOrganizationEmploymentAssociationRequestBody(
        StaffEducationOrganizationEmploymentAssociationSeed seed
    )
    {
        return new JsonObject
        {
            ["staffReference"] = new JsonObject { ["staffUniqueId"] = seed.StaffUniqueId },
            ["educationOrganizationReference"] = new JsonObject
            {
                ["educationOrganizationId"] = seed.EducationOrganizationId,
            },
            ["employmentStatusDescriptor"] = seed.EmploymentStatusDescriptor,
            ["hireDate"] = seed.HireDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
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

/// <summary>
/// Row-count targets for the bulk relationship-authorization volume generators (DMS-1331). Both engines share
/// these so the PostgreSQL and SQL Server differentials measure the same populations.
/// </summary>
/// <remarks>
/// The authorized count is an explicit target, not a side effect of "some reachable and some unreachable
/// rows". Measurements bind an OFFSET, and an offset past the last authorized row would EXPLAIN an empty page
/// — green while proving nothing — so every measuring fixture asserts its authorized count against the
/// deepest offset it uses plus the page limit.
/// </remarks>
internal sealed record RelationshipAuthorizationVolumeCounts
{
    private RelationshipAuthorizationVolumeCounts(int authorizedRowsPerRoot, int unauthorizedRowsPerRoot)
    {
        AuthorizedRowsPerRoot = authorizedRowsPerRoot;
        UnauthorizedRowsPerRoot = unauthorizedRowsPerRoot;
    }

    /// <summary>
    /// Runs on every pull request. Feeds the differential row-set equivalence proof and the volume-independent
    /// plan-shape assertions.
    /// </summary>
    public static RelationshipAuthorizationVolumeCounts Ci { get; } = new(8000, 2000);

    /// <summary>
    /// The deep-offset and timing lane. The authorized count exceeds the ticket's literal OFFSET 100000 plus
    /// any page limit those fixtures use, which is what keeps that measurement on a real page.
    /// </summary>
    public static RelationshipAuthorizationVolumeCounts DeepOffset { get; } = new(120000, 30000);

    public int AuthorizedRowsPerRoot { get; }

    public int UnauthorizedRowsPerRoot { get; }

    public int TotalRowsPerRoot => AuthorizedRowsPerRoot + UnauthorizedRowsPerRoot;

    /// <summary>
    /// Every <c>Stride</c>-th generated student is enrolled at the unreachable school. Interleaving the two
    /// populations across the DocumentId ordering is what makes authorized and unauthorized rows alternate
    /// across page boundaries instead of partitioning into a reachable prefix and an unreachable suffix.
    /// </summary>
    public int Stride =>
        TotalRowsPerRoot % UnauthorizedRowsPerRoot == 0
            ? TotalRowsPerRoot / UnauthorizedRowsPerRoot
            : throw new InvalidOperationException(
                $"Volume counts must interleave evenly: total {TotalRowsPerRoot} is not a multiple of the unauthorized count {UnauthorizedRowsPerRoot}."
            );
}

/// <summary>
/// The fixed identities the volume generators write. Shared across engines so a spec built for one dialect
/// describes the other engine's rows too.
/// </summary>
internal static class RelationshipAuthorizationVolumeIdentifiers
{
    /// <summary>The claim EdOrg the measured queries authorize against. Reaches the reachable school only.</summary>
    public const long ClaimEducationOrganizationId = 990_000L;

    /// <summary>
    /// Carries the whole Section/Course/GradingPeriod chain plus the authorized students' enrollments. One
    /// designated school keeps every <c>GENERATED ALWAYS</c> unified column consistent with its parent's
    /// reference key by construction.
    /// </summary>
    public const int ReachableSchoolId = 990_001;

    /// <summary>
    /// Carries only the unauthorized students' enrollments. Authorization is a property of the enrollment's
    /// school, so a single school would authorize every generated student or none.
    /// </summary>
    public const int UnreachableSchoolId = 990_002;

    public const int SchoolYear = 2024;

    public const string StudentUniqueIdPrefix = "VOL-";

    public const string TermDescriptorUri = "uri://ed-fi.org/TermDescriptor#Fall Semester";
    public const string GradeLevelDescriptorUri = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade";
    public const string GradingPeriodDescriptorUri =
        "uri://ed-fi.org/GradingPeriodDescriptor#First Six Weeks";
    public const string GradeTypeDescriptorUri = "uri://ed-fi.org/GradeTypeDescriptor#Semester";
    public const string CourseAttemptResultDescriptorUri =
        "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass";
    public const string AttendanceEventCategoryDescriptorUri =
        "uri://ed-fi.org/AttendanceEventCategoryDescriptor#In Attendance";

    public const string CourseCode = "VOL-COURSE";
    public const string CourseTitle = "Volume Course";
    public const string LocalCourseCode = "VOL-LOCAL";
    public const string SessionName = "VOL-SESSION";
    public const string SectionIdentifier = "VOL-SECTION";
    public const string GradingPeriodName = "VOL-GRADINGPERIOD";
    public const string GradebookEntryIdentifier = "VOL-GBE";
    public const string GradebookEntryNamespace = "uri://ed-fi.org/volume";
}

/// <summary>
/// What a volume generator produced, so fixtures can assert their preconditions instead of trusting the
/// generator.
/// </summary>
internal sealed record RelationshipAuthorizationVolumeGenerationResult(
    RelationshipAuthorizationVolumeCounts Counts,
    long AuthorizedStudentCount,
    long UnauthorizedStudentCount
);

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
