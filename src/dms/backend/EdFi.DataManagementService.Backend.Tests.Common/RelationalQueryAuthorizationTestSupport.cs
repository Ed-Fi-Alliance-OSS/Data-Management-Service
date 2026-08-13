// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
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

internal sealed record CourseSeed(
    DocumentUuid DocumentUuid,
    string CourseCode,
    int EducationOrganizationId,
    string CourseTitle
);

/// <summary>
/// CourseTranscript's two reference groups: a Course and a StudentAcademicRecord, each named by its full
/// natural key. It is the transitive person pathway's target — its Student is reached through the
/// StudentAcademicRecord it points at, never through a column of its own.
/// </summary>
internal sealed record CourseTranscriptSeed(
    DocumentUuid DocumentUuid,
    string CourseCode,
    int CourseEducationOrganizationId,
    int StudentAcademicRecordEducationOrganizationId,
    int SchoolYear,
    string StudentUniqueId,
    string TermDescriptor,
    string CourseAttemptResultDescriptor
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

/// <summary>
/// One differential subject: a target resource paired with the query spec both the real compiler and the
/// test-owned emitter consume.
/// </summary>
internal sealed record RelationshipAuthorizationDifferentialSpec(
    string ResourceName,
    DbTableName RootTable,
    PageDocumentIdQuerySpec QuerySpec
);

/// <summary>
/// The five resources DMS-1331 names, with their real DS 5.2 tables and columns and their real person paths —
/// three direct and two transitive. Shared by both engines so the PostgreSQL and SQL Server differentials
/// measure the same shapes.
/// </summary>
internal static class RelationshipAuthorizationDifferentialSpecs
{
    private static readonly DbSchemaName _edfi = new("edfi");
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");
    private static readonly DbColumnName _studentDocumentIdColumn = new("Student_DocumentId");

    public static IReadOnlyList<RelationshipAuthorizationDifferentialSpec> Create(
        SqlDialect dialect,
        IReadOnlyList<long> claimEducationOrganizationIds
    )
    {
        var studentTable = new DbTableName(_edfi, "Student");
        var studentSectionAssociationTable = new DbTableName(_edfi, "StudentSectionAssociation");
        var studentAcademicRecordTable = new DbTableName(_edfi, "StudentAcademicRecord");

        return
        [
            CreateDirectSpec(
                dialect,
                claimEducationOrganizationIds,
                studentSectionAssociationTable,
                studentTable
            ),
            CreateDirectSpec(
                dialect,
                claimEducationOrganizationIds,
                new DbTableName(_edfi, "StudentSectionAttendanceEvent"),
                studentTable
            ),
            CreateDirectSpec(
                dialect,
                claimEducationOrganizationIds,
                new DbTableName(_edfi, "StudentGradebookEntry"),
                studentTable
            ),
            CreateTransitiveSpec(
                dialect,
                claimEducationOrganizationIds,
                new DbTableName(_edfi, "Grade"),
                new DbColumnName("StudentSectionAssociation_DocumentId"),
                studentSectionAssociationTable,
                studentTable
            ),
            CreateTransitiveSpec(
                dialect,
                claimEducationOrganizationIds,
                new DbTableName(_edfi, "CourseTranscript"),
                new DbColumnName("StudentAcademicRecord_DocumentId"),
                studentAcademicRecordTable,
                studentTable
            ),
        ];
    }

    private static RelationshipAuthorizationDifferentialSpec CreateDirectSpec(
        SqlDialect dialect,
        IReadOnlyList<long> claimEducationOrganizationIds,
        DbTableName rootTable,
        DbTableName studentTable
    )
    {
        var subject = new PageDocumentIdAuthorizationPersonSubject(
            rootTable,
            _studentDocumentIdColumn,
            RelationshipAuthorizationAuthObject.CreatePerson(
                RelationshipAuthorizationPersonAuthViewKind.Student
            ),
            [StudentContributor()],
            new RelationshipAuthorizationPersonSubjectMetadata(
                RelationshipAuthorizationPersonKind.Student,
                new RelationshipAuthorizationPersonSubjectPath(
                    RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn,
                    [new ColumnPathStep(rootTable, _studentDocumentIdColumn, studentTable, _documentIdColumn)]
                ),
                new RelationshipAuthorizationPersonStoredAnchor(rootTable, _documentIdColumn),
                ProposedAnchor: null
            )
        );

        return new RelationshipAuthorizationDifferentialSpec(
            rootTable.Name,
            rootTable,
            CreateQuerySpec(dialect, claimEducationOrganizationIds, rootTable, subject)
        );
    }

    private static RelationshipAuthorizationDifferentialSpec CreateTransitiveSpec(
        SqlDialect dialect,
        IReadOnlyList<long> claimEducationOrganizationIds,
        DbTableName rootTable,
        DbColumnName rootReferenceColumn,
        DbTableName intermediateTable,
        DbTableName studentTable
    )
    {
        var subject = new PageDocumentIdAuthorizationPersonSubject(
            intermediateTable,
            _studentDocumentIdColumn,
            RelationshipAuthorizationAuthObject.CreatePerson(
                RelationshipAuthorizationPersonAuthViewKind.Student
            ),
            [StudentContributor()],
            new RelationshipAuthorizationPersonSubjectMetadata(
                RelationshipAuthorizationPersonKind.Student,
                new RelationshipAuthorizationPersonSubjectPath(
                    RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath,
                    [
                        new ColumnPathStep(
                            rootTable,
                            rootReferenceColumn,
                            intermediateTable,
                            _documentIdColumn
                        ),
                        new ColumnPathStep(
                            intermediateTable,
                            _studentDocumentIdColumn,
                            studentTable,
                            _documentIdColumn
                        ),
                    ]
                ),
                new RelationshipAuthorizationPersonStoredAnchor(rootTable, _documentIdColumn),
                ProposedAnchor: null
            )
        );

        return new RelationshipAuthorizationDifferentialSpec(
            rootTable.Name,
            rootTable,
            CreateQuerySpec(dialect, claimEducationOrganizationIds, rootTable, subject)
        );
    }

    private static PageDocumentIdQuerySpec CreateQuerySpec(
        SqlDialect dialect,
        IReadOnlyList<long> claimEducationOrganizationIds,
        DbTableName rootTable,
        PageDocumentIdAuthorizationPersonSubject subject
    ) =>
        new(
            RootTable: rootTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Mode: new PageCandidateMode.Traditional(IncludeTotalCountSql: true),
            Authorization: new PageDocumentIdAuthorizationSpec(
                [
                    new PageDocumentIdAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                        [subject]
                    ),
                ],
                AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                    dialect,
                    claimEducationOrganizationIds,
                    RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
                )
            )
        );

    private static RelationshipAuthorizationSubjectContributor StudentContributor() =>
        new(SecurableElementKind.Student, "$.studentReference.studentUniqueId", "StudentUniqueId");
}

/// <summary>
/// Which authorization predicate shape the differential emitter writes.
/// </summary>
internal enum RelationshipAuthorizationPredicateShape
{
    /// <summary>The shape DMS-1331 introduces: anchored on a column of the root row.</summary>
    Anchored,

    /// <summary>
    /// The pre-DMS-1331 shape: a primary-key self-join of the root table. Deliberately frozen here, in test
    /// code, because this is the only test that proves the new shape returns the same rows as the old one
    /// rather than the same rows as a hand-listed expectation.
    /// </summary>
    Legacy,
}

internal sealed record RelationshipAuthorizationDifferentialSql(string PageSql, string TotalCountSql);

/// <summary>
/// A test-owned, self-contained emitter of complete, executable page and totalCount SQL, carrying both
/// authorization predicate shapes so a differential can execute them side by side (DMS-1331 AC2/AC4).
/// </summary>
/// <remarks>
/// <para>
/// Why fresh rather than a copy of the deleted production methods: those call helpers that remain
/// <c>private</c> on <c>PageDocumentIdSqlCompiler</c>, the page and count wrappers are private too, the only
/// public entry point is <c>Compile</c>, and this assembly is absent from the Plans <c>InternalsVisibleTo</c>
/// list — which would not help anyway against private members. Widening production visibility for a test is
/// the wrong trade, so this is written against the public models instead.
/// </para>
/// <para>
/// The gap that opens is that a test-owned wrapper could drift from the product. The differential closes it by
/// asserting that <see cref="RelationshipAuthorizationPredicateShape.Anchored"/> returns the identical ordered
/// DocumentId sequence and totalCount as the real compiler's output. Combined with anchored ≡ legacy, that
/// gives production ≡ legacy transitively, without reaching into any private member.
/// </para>
/// <para>
/// That equivalence is over <b>result rows only</b>. Two different SQL texts can return identical result sets
/// under entirely different plans, so this emitter is never a plan or timing subject: measurements take
/// production's <c>Compile()</c> output as the "after" arm and use this emitter only for the legacy "before".
/// </para>
/// <para>
/// Both shapes drive the full ordered <c>ColumnPathStep</c> list. A four-input
/// (root, anchor, person column, auth view) helper could not express Grade or CourseTranscript, whose paths
/// traverse an intermediate table.
/// </para>
/// </remarks>
internal static class RelationshipAuthorizationDifferentialSqlEmitter
{
    private const string RootAlias = "r";

    public static RelationshipAuthorizationDifferentialSql Emit(
        PageDocumentIdQuerySpec spec,
        SqlDialect dialect,
        RelationshipAuthorizationPredicateShape shape,
        int claimEducationOrganizationIdCount
    )
    {
        ArgumentNullException.ThrowIfNull(spec);

        var subject = SinglePersonSubject(spec);

        return new RelationshipAuthorizationDifferentialSql(
            EmitPageSql(spec, subject, dialect, shape, claimEducationOrganizationIdCount),
            EmitTotalCountSql(spec, subject, dialect, shape, claimEducationOrganizationIdCount)
        );
    }

    /// <summary>
    /// The differential specs each carry exactly one relationship strategy with exactly one person subject.
    /// Anything else is a malformed spec rather than a shape this emitter should guess at.
    /// </summary>
    private static PageDocumentIdAuthorizationPersonSubject SinglePersonSubject(PageDocumentIdQuerySpec spec)
    {
        var strategies = spec.Authorization?.Strategies;

        if (
            strategies is not { Count: 1 }
            || strategies[0].Subjects is not { Count: 1 }
            || strategies[0].Subjects[0] is not PageDocumentIdAuthorizationPersonSubject personSubject
        )
        {
            throw new InvalidOperationException(
                "The differential emitter requires a spec with exactly one strategy carrying exactly one person subject."
            );
        }

        return personSubject;
    }

    private static string EmitPageSql(
        PageDocumentIdQuerySpec spec,
        PageDocumentIdAuthorizationPersonSubject subject,
        SqlDialect dialect,
        RelationshipAuthorizationPredicateShape shape,
        int claimEducationOrganizationIdCount
    )
    {
        var writer = new SqlWriter(SqlDialectFactory.Create(dialect));

        writer.Append($"SELECT {RootAlias}.").AppendQuoted("DocumentId").AppendLine();
        AppendFromAndWhere(writer, spec, subject, dialect, shape, claimEducationOrganizationIdCount);
        writer.Append($"ORDER BY {RootAlias}.").AppendQuoted("DocumentId").AppendLine(" ASC");
        writer.AppendLine(
            dialect == SqlDialect.Mssql
                ? "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;"
                : "LIMIT @limit OFFSET @offset;"
        );

        return writer.ToString();
    }

    private static string EmitTotalCountSql(
        PageDocumentIdQuerySpec spec,
        PageDocumentIdAuthorizationPersonSubject subject,
        SqlDialect dialect,
        RelationshipAuthorizationPredicateShape shape,
        int claimEducationOrganizationIdCount
    )
    {
        var writer = new SqlWriter(SqlDialectFactory.Create(dialect));

        writer.AppendLine("SELECT COUNT(1)");
        AppendFromAndWhere(writer, spec, subject, dialect, shape, claimEducationOrganizationIdCount);
        writer.AppendLine(";");

        return writer.ToString();
    }

    private static void AppendFromAndWhere(
        SqlWriter writer,
        PageDocumentIdQuerySpec spec,
        PageDocumentIdAuthorizationPersonSubject subject,
        SqlDialect dialect,
        RelationshipAuthorizationPredicateShape shape,
        int claimEducationOrganizationIdCount
    )
    {
        writer.Append("FROM ").AppendTable(spec.RootTable).AppendLine($" {RootAlias}");
        writer.Append("WHERE (");
        AppendAuthorizationPredicate(
            writer,
            spec.RootTable,
            subject,
            dialect,
            shape,
            claimEducationOrganizationIdCount
        );
        writer.AppendLine(")");
    }

    /// <summary>
    /// The one place the two shapes differ. Everything around it — root relation, keyset ordering, paging and
    /// claim parameterization — is shared, so a differential over these two texts isolates the predicate.
    /// </summary>
    private static void AppendAuthorizationPredicate(
        SqlWriter writer,
        DbTableName rootTable,
        PageDocumentIdAuthorizationPersonSubject subject,
        SqlDialect dialect,
        RelationshipAuthorizationPredicateShape shape,
        int claimEducationOrganizationIdCount
    )
    {
        var personMetadata = subject.PersonMetadata;
        var pathSteps = personMetadata.Path.Steps;
        var aliasOrdinal = 0;

        if (shape == RelationshipAuthorizationPredicateShape.Legacy)
        {
            // r."DocumentId" IN (SELECT t0."DocumentId" FROM <root> t0 [JOIN each hop] WHERE
            //     t<n>.<personColumn> IN (<membership>))
            var rootSubqueryAlias = $"t{aliasOrdinal++}";

            writer
                .Append($"{RootAlias}.")
                .AppendQuoted(personMetadata.StoredAnchor.RootDocumentIdColumn.Value);
            writer.Append(" IN (SELECT ").Append($"{rootSubqueryAlias}.");
            writer.AppendQuoted(personMetadata.StoredAnchor.RootDocumentIdColumn.Value);
            writer.Append(" FROM ").AppendTable(rootTable).Append($" {rootSubqueryAlias}");

            var legacySourceAlias = AppendPathJoins(
                writer,
                pathSteps,
                rootSubqueryAlias,
                firstJoinedStepIndex: 0,
                ref aliasOrdinal
            );

            writer.Append($" WHERE {legacySourceAlias}.");
            writer.AppendQuoted(TerminalPersonColumn(subject, pathSteps).Value);
            AppendMembershipSubquery(
                writer,
                subject,
                dialect,
                $"t{aliasOrdinal}",
                claimEducationOrganizationIdCount
            );
            writer.Append(")");
            return;
        }

        switch (personMetadata.Path.Kind)
        {
            case RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId:
            case RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn:
                // r.<anchor> IN (<membership>)
                writer.Append($"{RootAlias}.").AppendQuoted(AnchorColumn(subject, pathSteps).Value);
                AppendMembershipSubquery(
                    writer,
                    subject,
                    dialect,
                    $"t{aliasOrdinal}",
                    claimEducationOrganizationIdCount
                );
                return;
            case RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath:
                AppendAnchoredTransitivePredicate(
                    writer,
                    subject,
                    pathSteps,
                    dialect,
                    claimEducationOrganizationIdCount,
                    ref aliasOrdinal
                );
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(subject),
                    personMetadata.Path.Kind,
                    "Unsupported person path kind."
                );
        }
    }

    /// <summary>
    /// r.&lt;firstStep.SourceColumn&gt; IN (SELECT t0.&lt;firstStep.TargetColumn&gt; FROM
    /// &lt;firstStep.TargetTable&gt; t0 [JOIN remaining hops] WHERE t&lt;n&gt;.&lt;personColumn&gt; IN (…)).
    /// </summary>
    private static void AppendAnchoredTransitivePredicate(
        SqlWriter writer,
        PageDocumentIdAuthorizationPersonSubject subject,
        IReadOnlyList<ColumnPathStep> pathSteps,
        SqlDialect dialect,
        int claimEducationOrganizationIdCount,
        ref int aliasOrdinal
    )
    {
        var firstStep = pathSteps[0];
        var firstHopAlias = $"t{aliasOrdinal++}";

        writer.Append($"{RootAlias}.").AppendQuoted(firstStep.SourceColumnName.Value);
        writer.Append(" IN (SELECT ").Append($"{firstHopAlias}.");
        writer.AppendQuoted(RequiredTargetColumn(firstStep).Value);
        writer.Append(" FROM ").AppendTable(RequiredTargetTable(firstStep));
        writer.Append($" {firstHopAlias}");

        var terminalSourceAlias = AppendPathJoins(
            writer,
            pathSteps,
            firstHopAlias,
            firstJoinedStepIndex: 1,
            ref aliasOrdinal
        );

        writer.Append($" WHERE {terminalSourceAlias}.");
        writer.AppendQuoted(TerminalPersonColumn(subject, pathSteps).Value);
        AppendMembershipSubquery(
            writer,
            subject,
            dialect,
            $"t{aliasOrdinal}",
            claimEducationOrganizationIdCount
        );
        writer.Append(")");
    }

    /// <summary>
    /// Joins the intermediate hops of a transitive path, starting at <paramref name="firstJoinedStepIndex"/>
    /// and stopping before the terminal step, which carries the person column rather than a join.
    /// </summary>
    private static string AppendPathJoins(
        SqlWriter writer,
        IReadOnlyList<ColumnPathStep> pathSteps,
        string sourceAlias,
        int firstJoinedStepIndex,
        ref int aliasOrdinal
    )
    {
        var currentSourceAlias = sourceAlias;

        for (var stepIndex = firstJoinedStepIndex; stepIndex < pathSteps.Count - 1; stepIndex++)
        {
            var step = pathSteps[stepIndex];
            var joinAlias = $"t{aliasOrdinal++}";

            writer.Append(" JOIN ").AppendTable(RequiredTargetTable(step));
            writer.Append($" {joinAlias} ON {joinAlias}.");
            writer.AppendQuoted(RequiredTargetColumn(step).Value);
            writer.Append($" = {currentSourceAlias}.");
            writer.AppendQuoted(step.SourceColumnName.Value);

            currentSourceAlias = joinAlias;
        }

        return currentSourceAlias;
    }

    private static void AppendMembershipSubquery(
        SqlWriter writer,
        PageDocumentIdAuthorizationPersonSubject subject,
        SqlDialect dialect,
        string authAlias,
        int claimEducationOrganizationIdCount
    )
    {
        writer.Append(" IN (SELECT ").Append($"{authAlias}.");
        writer.AppendQuoted(subject.AuthObject.SubjectValueColumn.Value);
        writer.Append(" FROM ").AppendTable(subject.AuthObject.Name);
        writer.Append($" {authAlias} WHERE {authAlias}.");
        writer.AppendQuoted(subject.AuthObject.ClaimEducationOrganizationIdColumn.Value);

        if (dialect == SqlDialect.Mssql)
        {
            var placeholders = Enumerable
                .Range(0, claimEducationOrganizationIdCount)
                .Select(static index => $"@ClaimEducationOrganizationIds_{index}");

            writer.Append($" IN ({string.Join(", ", placeholders)})");
        }
        else
        {
            writer.Append(" = ANY(@ClaimEducationOrganizationIds)");
        }

        writer.Append(")");
    }

    private static DbColumnName AnchorColumn(
        PageDocumentIdAuthorizationPersonSubject subject,
        IReadOnlyList<ColumnPathStep> pathSteps
    ) =>
        subject.PersonMetadata.Path.Kind == RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId
            ? subject.PersonMetadata.StoredAnchor.RootDocumentIdColumn
            : pathSteps[0].SourceColumnName;

    private static DbColumnName TerminalPersonColumn(
        PageDocumentIdAuthorizationPersonSubject subject,
        IReadOnlyList<ColumnPathStep> pathSteps
    ) => pathSteps.Count == 0 ? subject.Column : pathSteps[^1].SourceColumnName;

    private static DbTableName RequiredTargetTable(ColumnPathStep step) =>
        step.TargetTable
        ?? throw new InvalidOperationException("Transitive person path steps must include a target table.");

    private static DbColumnName RequiredTargetColumn(ColumnPathStep step) =>
        step.TargetColumnName
        ?? throw new InvalidOperationException("Transitive person path steps must include a target column.");
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
