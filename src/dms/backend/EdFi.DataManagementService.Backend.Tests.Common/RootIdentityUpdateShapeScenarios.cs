// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// The dialect-specific operations the root identity update-shape scenarios need: production writes through
/// the relational repository, the recorded write-session command stream, and dialect-quoted readbacks.
/// </summary>
internal interface IRootIdentityUpdateShapeHarness
{
    MappingSet MappingSet { get; }

    Task SeedDescriptorAsync(
        Guid documentUuid,
        string resourceName,
        string @namespace,
        string codeValue,
        string shortDescription
    );

    Task InsertAuthEdgeAsync(long sourceEducationOrganizationId, long targetEducationOrganizationId);

    Task<UpsertResult> UpsertAsync(string resourceName, JsonNode requestBody, DocumentUuid documentUuid);

    Task<UpdateResult> UpdateAsync(
        string resourceName,
        JsonNode requestBody,
        DocumentUuid documentUuid,
        IReadOnlyList<long>? claimEducationOrganizationIds = null,
        IReadOnlyList<string>? strategyNames = null
    );

    void ResetRecorder();

    IReadOnlyList<string> RecordedWriteCommandTexts { get; }

    Task<AuthorizationWriteSideEffectState> ReadSideEffectStateAsync(
        string resourceName,
        DocumentUuid documentUuid
    );

    /// <summary>
    /// The <c>tracked_changes_edfi</c> rows of one document of an Ed-Fi resource, oldest first.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadTrackedChangesAsync(
        string resourceName,
        DocumentUuid documentUuid
    );
}

/// <summary>
/// DMS-1565 root update shapes against the authoritative DS 5.2 model, run through the production
/// repository so the identity guard selects the shape. An unchanged identity must leave the
/// identity-projection columns out of the root SET list, which keeps the database from evaluating the
/// incoming <c>ON UPDATE CASCADE</c> branches; an allowed identity change keeps the full list so those
/// cascades propagate it. Stamp triggers compare values, so persisted data is the same for either shape:
/// only the recorded statement shows which shape ran.
/// </summary>
internal static class RootIdentityUpdateShapeScenarios
{
    public const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    public const string Session = "Session";
    public const string CourseOffering = "CourseOffering";
    public const string Section = "Section";

    public const long ClaimEducationOrganizationId = 900;
    public const int AuthorizedSchoolId = 100;
    public const int UnauthorizedSchoolId = 300;
    public const int SchoolYear = 2025;
    public const string SessionName = "Fall 2025";
    public const string RenamedSessionName = "Autumn 2025";
    public const string MovedSessionName = "Winter 2025";
    public const string CourseCode = "ALG-1";
    public const string LocalCourseCode = "ALG-1-01";
    public const string SectionIdentifier = "SEC-01";
    public const string RelationshipsWithEdOrgsOnly = "RelationshipsWithEdOrgsOnly";

    public static readonly DocumentUuid AuthorizedSchoolUuid = Uuid("0a000000-0000-0000-0000-000000000100");
    public static readonly DocumentUuid UnauthorizedSchoolUuid = Uuid("0a000000-0000-0000-0000-000000000300");
    public static readonly DocumentUuid SchoolYearTypeUuid = Uuid("0b000000-0000-0000-0000-000000002025");
    public static readonly DocumentUuid CourseUuid = Uuid("0c000000-0000-0000-0000-000000000001");
    public static readonly DocumentUuid SessionUuid = Uuid("0d000000-0000-0000-0000-000000000100");
    public static readonly DocumentUuid UnauthorizedSessionUuid = Uuid(
        "0d000000-0000-0000-0000-000000000300"
    );
    public static readonly DocumentUuid CourseOfferingUuid = Uuid("0e000000-0000-0000-0000-000000000001");
    public static readonly DocumentUuid SectionUuid = Uuid("0f000000-0000-0000-0000-000000000001");

    private static readonly string[] _sessionIdentityColumns =
    [
        "SessionName",
        "School_SchoolId",
        "SchoolYear_SchoolYear",
    ];

    private static readonly string[] _courseOfferingIdentityColumns =
    [
        "LocalCourseCode",
        "SchoolId_Unified",
        "Session_SchoolYear",
        "Session_SessionName",
    ];

    /// <summary>
    /// Creates School 100 and 300, SchoolYearType 2025, a Course, a Session at each school, and the
    /// CourseOffering and Section that reference the School 100 Session, all through the production upsert,
    /// then clears the recorder so assertions see only the write under test.
    /// </summary>
    public static async Task SeedAsync(IRootIdentityUpdateShapeHarness harness)
    {
        await harness.SeedDescriptorAsync(
            Guid.Parse("40444444-4444-4444-4444-444444444444"),
            "EducationOrganizationCategoryDescriptor",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
            "School",
            "School"
        );
        await harness.SeedDescriptorAsync(
            Guid.Parse("60666666-6666-6666-6666-666666666666"),
            "GradeLevelDescriptor",
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Tenth grade",
            "Tenth grade"
        );
        await harness.SeedDescriptorAsync(
            Guid.Parse("70777777-7777-7777-7777-777777777777"),
            "TermDescriptor",
            "uri://ed-fi.org/TermDescriptor",
            "Fall Semester",
            "Fall Semester"
        );

        await AssertCreatedAsync(
            harness.UpsertAsync(
                "School",
                RelationalQueryAuthorizationRequestBodies.CreateSchoolRequestBody(
                    AuthorizedSchoolId,
                    "North School"
                ),
                AuthorizedSchoolUuid
            )
        );
        await AssertCreatedAsync(
            harness.UpsertAsync(
                "School",
                RelationalQueryAuthorizationRequestBodies.CreateSchoolRequestBody(
                    UnauthorizedSchoolId,
                    "West School"
                ),
                UnauthorizedSchoolUuid
            )
        );
        await AssertCreatedAsync(
            harness.UpsertAsync(
                "SchoolYearType",
                new JsonObject
                {
                    ["schoolYear"] = SchoolYear,
                    ["currentSchoolYear"] = true,
                    ["schoolYearDescription"] = "2024-2025",
                },
                SchoolYearTypeUuid
            )
        );
        await AssertCreatedAsync(
            harness.UpsertAsync(
                "Course",
                new JsonObject
                {
                    ["courseCode"] = CourseCode,
                    ["courseTitle"] = "Algebra I",
                    ["numberOfParts"] = 1,
                    ["educationOrganizationReference"] = new JsonObject
                    {
                        ["educationOrganizationId"] = (long)AuthorizedSchoolId,
                    },
                },
                CourseUuid
            )
        );
        await AssertCreatedAsync(
            harness.UpsertAsync(Session, SessionBody(AuthorizedSchoolId, SessionName), SessionUuid)
        );
        await AssertCreatedAsync(
            harness.UpsertAsync(
                Session,
                SessionBody(UnauthorizedSchoolId, SessionName),
                UnauthorizedSessionUuid
            )
        );
        await AssertCreatedAsync(
            harness.UpsertAsync(
                CourseOffering,
                CourseOfferingBody(LocalCourseCode, SessionName, "Algebra I - 01"),
                CourseOfferingUuid
            )
        );
        await AssertCreatedAsync(
            harness.UpsertAsync(
                Section,
                new JsonObject
                {
                    ["sectionIdentifier"] = SectionIdentifier,
                    ["sectionName"] = "Algebra I Section 1",
                    ["courseOfferingReference"] = new JsonObject
                    {
                        ["localCourseCode"] = LocalCourseCode,
                        ["schoolId"] = (long)AuthorizedSchoolId,
                        ["schoolYear"] = SchoolYear,
                        ["sessionName"] = SessionName,
                    },
                },
                SectionUuid
            )
        );

        await harness.InsertAuthEdgeAsync(ClaimEducationOrganizationId, AuthorizedSchoolId);
        harness.ResetRecorder();
    }

    public static JsonNode SessionBody(
        int schoolId,
        string sessionName,
        string endDate = "2025-12-19",
        int totalInstructionalDays = 88
    ) =>
        new JsonObject
        {
            ["sessionName"] = sessionName,
            ["schoolReference"] = new JsonObject { ["schoolId"] = (long)schoolId },
            ["schoolYearTypeReference"] = new JsonObject { ["schoolYear"] = SchoolYear },
            ["beginDate"] = "2025-08-18",
            ["endDate"] = endDate,
            ["termDescriptor"] = "uri://ed-fi.org/TermDescriptor#Fall Semester",
            ["totalInstructionalDays"] = totalInstructionalDays,
        };

    public static JsonNode CourseOfferingBody(
        string localCourseCode,
        string sessionName,
        string localCourseTitle
    ) =>
        new JsonObject
        {
            ["localCourseCode"] = localCourseCode,
            ["localCourseTitle"] = localCourseTitle,
            ["courseReference"] = new JsonObject
            {
                ["courseCode"] = CourseCode,
                ["educationOrganizationId"] = (long)AuthorizedSchoolId,
            },
            ["schoolReference"] = new JsonObject { ["schoolId"] = (long)AuthorizedSchoolId },
            ["sessionReference"] = new JsonObject
            {
                ["schoolId"] = (long)AuthorizedSchoolId,
                ["schoolYear"] = SchoolYear,
                ["sessionName"] = sessionName,
            },
        };

    /// <summary>
    /// The real Session model's identity projection is exactly its three natural-key columns, and an
    /// identity-unchanged PUT assigns every other writable non-key column (including unchanged ones) and
    /// none of those three. Dependent CourseOffering and Section rows are untouched and no key change is
    /// tracked.
    /// </summary>
    public static async Task AssertUnchangedIdentitySessionPutOmitsIdentityAssignmentsAsync(
        IRootIdentityUpdateShapeHarness harness
    )
    {
        var sessionColumns = ResolveRootUpdateColumns(harness.MappingSet, Session);
        sessionColumns.IdentityProjection.Should().BeEquivalentTo(_sessionIdentityColumns);

        var sessionBefore = await harness.ReadSideEffectStateAsync(Session, SessionUuid);
        var courseOfferingBefore = await harness.ReadSideEffectStateAsync(CourseOffering, CourseOfferingUuid);
        var sectionBefore = await harness.ReadSideEffectStateAsync(Section, SectionUuid);

        var result = await harness.UpdateAsync(
            Session,
            SessionBody(AuthorizedSchoolId, SessionName, endDate: "2025-12-20", totalInstructionalDays: 89),
            SessionUuid
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var assignedColumns = SingleRootUpdate(harness, Session);
        assignedColumns.Should().Equal(sessionColumns.Reduced);
        assignedColumns.Should().NotContain(_sessionIdentityColumns);
        assignedColumns
            .Should()
            .Contain([
                "BeginDate",
                "EndDate",
                "TotalInstructionalDays",
                "TermDescriptor_DescriptorId",
                "School_DocumentId",
                "SchoolYear_DocumentId",
            ]);

        var sessionAfter = await harness.ReadSideEffectStateAsync(Session, SessionUuid);
        var sessionRow = RootRow(sessionAfter);
        PersistedDate(sessionRow["EndDate"]).Should().Be(new DateOnly(2025, 12, 20));
        sessionRow["TotalInstructionalDays"].Should().Be("89");
        sessionRow["SessionName"].Should().Be(SessionName);
        sessionAfter.Document.ContentVersion.Should().BeGreaterThan(sessionBefore.Document.ContentVersion);

        (await harness.ReadSideEffectStateAsync(CourseOffering, CourseOfferingUuid))
            .Should()
            .BeEquivalentTo(courseOfferingBefore);
        (await harness.ReadSideEffectStateAsync(Section, SectionUuid)).Should().BeEquivalentTo(sectionBefore);
        (await harness.ReadTrackedChangesAsync(Session, SessionUuid)).Should().BeEmpty();
        (await harness.ReadTrackedChangesAsync(CourseOffering, CourseOfferingUuid)).Should().BeEmpty();
        (await harness.ReadTrackedChangesAsync(Section, SectionUuid)).Should().BeEmpty();
    }

    /// <summary>
    /// Session allows identity updates. Renaming it under a claim related to its school is authorized and
    /// uses the full SET list, the cascade carries the new name into the CourseOffering and on into the
    /// Section, each document's ContentVersion advances, each key change is tracked, and reference resolution
    /// follows the new identity.
    /// </summary>
    public static async Task AssertAllowedSessionIdentityChangeUsesFullShapeAndCascadesAsync(
        IRootIdentityUpdateShapeHarness harness
    )
    {
        var sessionColumns = ResolveRootUpdateColumns(harness.MappingSet, Session);
        var courseOfferingBefore = await harness.ReadSideEffectStateAsync(CourseOffering, CourseOfferingUuid);
        var sectionBefore = await harness.ReadSideEffectStateAsync(Section, SectionUuid);

        var result = await harness.UpdateAsync(
            Session,
            SessionBody(AuthorizedSchoolId, RenamedSessionName),
            SessionUuid,
            [ClaimEducationOrganizationId],
            [RelationshipsWithEdOrgsOnly]
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var assignedColumns = SingleRootUpdate(harness, Session);
        assignedColumns.Should().Equal(sessionColumns.Full);
        assignedColumns.Should().Contain(_sessionIdentityColumns);

        RootRow(await harness.ReadSideEffectStateAsync(Session, SessionUuid))["SessionName"]
            .Should()
            .Be(RenamedSessionName);

        var courseOfferingAfter = await harness.ReadSideEffectStateAsync(CourseOffering, CourseOfferingUuid);
        RootRow(courseOfferingAfter)["Session_SessionName"].Should().Be(RenamedSessionName);
        courseOfferingAfter
            .Document.ContentVersion.Should()
            .BeGreaterThan(courseOfferingBefore.Document.ContentVersion);

        var sectionAfter = await harness.ReadSideEffectStateAsync(Section, SectionUuid);
        RootRow(sectionAfter)["CourseOffering_SessionName"].Should().Be(RenamedSessionName);
        sectionAfter.Document.ContentVersion.Should().BeGreaterThan(sectionBefore.Document.ContentVersion);

        AssertSingleKeyChange(
            await harness.ReadTrackedChangesAsync(Session, SessionUuid),
            "OldSessionName",
            "NewSessionName"
        );
        AssertSingleKeyChange(
            await harness.ReadTrackedChangesAsync(CourseOffering, CourseOfferingUuid),
            "OldSession_SessionName",
            "NewSession_SessionName"
        );
        AssertSingleKeyChange(
            await harness.ReadTrackedChangesAsync(Section, SectionUuid),
            "OldCourseOffering_SessionName",
            "NewCourseOffering_SessionName"
        );

        // The schema keeps no separate referential-identity table: references resolve against the
        // referenced root's natural key, so a reference to the new identity resolves and one to the old
        // identity does not.
        (
            await harness.UpsertAsync(
                CourseOffering,
                CourseOfferingBody("ALG-1-02", RenamedSessionName, "Algebra I - 02"),
                Uuid("0e000000-0000-0000-0000-000000000002")
            )
        )
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>();
        (
            await harness.UpsertAsync(
                CourseOffering,
                CourseOfferingBody("ALG-1-03", SessionName, "Algebra I - 03"),
                Uuid("0e000000-0000-0000-0000-000000000003")
            )
        )
            .Should()
            .BeOfType<UpsertResult.UpsertFailureReference>();
    }

    /// <summary>
    /// CourseOffering does not allow identity updates, so changing its local course code fails with the
    /// immutable-identity result before any DML, leaving its rows, its dependents and change tracking alone.
    /// </summary>
    public static async Task AssertDisallowedIdentityChangeFailsWithoutMutationAsync(
        IRootIdentityUpdateShapeHarness harness
    )
    {
        var courseOfferingBefore = await harness.ReadSideEffectStateAsync(CourseOffering, CourseOfferingUuid);
        var sectionBefore = await harness.ReadSideEffectStateAsync(Section, SectionUuid);

        var result = await harness.UpdateAsync(
            CourseOffering,
            CourseOfferingBody("ALG-1-99", SessionName, "Algebra I - 01"),
            CourseOfferingUuid
        );

        result.Should().BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
        RootUpdates(harness, CourseOffering).Should().BeEmpty();
        (await harness.ReadSideEffectStateAsync(CourseOffering, CourseOfferingUuid))
            .Should()
            .BeEquivalentTo(courseOfferingBefore);
        (await harness.ReadSideEffectStateAsync(Section, SectionUuid)).Should().BeEquivalentTo(sectionBefore);
        (await harness.ReadTrackedChangesAsync(CourseOffering, CourseOfferingUuid)).Should().BeEmpty();
    }

    /// <summary>
    /// CourseOffering's School and Session references share one key-unified SchoolId. Its identity
    /// projection names the canonical stored column, not the computed aliases, and the reduced update omits
    /// that stored column while never assigning an alias.
    /// </summary>
    public static async Task AssertUnchangedUnifiedKeyIdentityOmitsCanonicalStoredColumnAsync(
        IRootIdentityUpdateShapeHarness harness
    )
    {
        var rootTable = RootTablePlan(harness.MappingSet, CourseOffering).TableModel;
        var aliasCanonicalColumns = rootTable
            .Columns.Where(static column =>
                column.ColumnName.Value is "School_SchoolId" or "Session_SchoolId"
            )
            .Select(static column => column.Storage)
            .ToArray();
        aliasCanonicalColumns
            .Should()
            .HaveCount(2)
            .And.AllSatisfy(storage =>
                storage
                    .Should()
                    .BeOfType<ColumnStorage.UnifiedAlias>()
                    .Which.CanonicalColumn.Value.Should()
                    .Be("SchoolId_Unified")
            );

        var courseOfferingColumns = ResolveRootUpdateColumns(harness.MappingSet, CourseOffering);
        courseOfferingColumns.IdentityProjection.Should().BeEquivalentTo(_courseOfferingIdentityColumns);

        var result = await harness.UpdateAsync(
            CourseOffering,
            CourseOfferingBody(LocalCourseCode, SessionName, "Algebra I - Honors"),
            CourseOfferingUuid
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var assignedColumns = SingleRootUpdate(harness, CourseOffering);
        assignedColumns.Should().Equal(courseOfferingColumns.Reduced);
        assignedColumns
            .Should()
            .NotContain(_courseOfferingIdentityColumns)
            .And.NotContain(["School_SchoolId", "Session_SchoolId"])
            .And.Contain([
                "LocalCourseTitle",
                "Course_DocumentId",
                "School_DocumentId",
                "Session_DocumentId",
            ]);
        RootRow(await harness.ReadSideEffectStateAsync(CourseOffering, CourseOfferingUuid))[
            "LocalCourseTitle"
        ]
            .Should()
            .Be("Algebra I - Honors");
    }

    /// <summary>
    /// An identical PUT stays a guarded no-op: no root UPDATE and no ContentVersion change.
    /// </summary>
    public static async Task AssertIdenticalSessionPutIssuesNoRootUpdateAsync(
        IRootIdentityUpdateShapeHarness harness
    )
    {
        var sessionBefore = await harness.ReadSideEffectStateAsync(Session, SessionUuid);

        var result = await harness.UpdateAsync(
            Session,
            SessionBody(AuthorizedSchoolId, SessionName),
            SessionUuid
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        RootUpdates(harness, Session).Should().BeEmpty();
        (await harness.ReadSideEffectStateAsync(Session, SessionUuid)).Should().BeEquivalentTo(sessionBefore);
    }

    /// <summary>
    /// Relationship authorization still applies to the reduced update: a claim related to the Session's
    /// school is authorized and takes the reduced shape.
    /// </summary>
    public static async Task AssertAuthorizedUnchangedIdentitySessionPutUsesReducedShapeAsync(
        IRootIdentityUpdateShapeHarness harness
    )
    {
        var result = await harness.UpdateAsync(
            Session,
            SessionBody(AuthorizedSchoolId, SessionName, endDate: "2025-12-21"),
            SessionUuid,
            [ClaimEducationOrganizationId],
            [RelationshipsWithEdOrgsOnly]
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        SingleRootUpdate(harness, Session)
            .Should()
            .Equal(ResolveRootUpdateColumns(harness.MappingSet, Session).Reduced);
        PersistedDate(RootRow(await harness.ReadSideEffectStateAsync(Session, SessionUuid))["EndDate"])
            .Should()
            .Be(new DateOnly(2025, 12, 21));
    }

    /// <summary>
    /// A claim unrelated to the stored Session's school is denied before any DML, leaving the Session as it
    /// was.
    /// </summary>
    public static async Task AssertUnauthorizedUnchangedIdentitySessionPutIsDeniedWithoutMutationAsync(
        IRootIdentityUpdateShapeHarness harness
    )
    {
        var sessionBefore = await harness.ReadSideEffectStateAsync(Session, UnauthorizedSessionUuid);

        var result = await harness.UpdateAsync(
            Session,
            SessionBody(UnauthorizedSchoolId, SessionName, endDate: "2025-12-21"),
            UnauthorizedSessionUuid,
            [ClaimEducationOrganizationId],
            [RelationshipsWithEdOrgsOnly]
        );

        result.Should().BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>();
        RootUpdates(harness, Session).Should().BeEmpty();
        (await harness.ReadSideEffectStateAsync(Session, UnauthorizedSessionUuid))
            .Should()
            .BeEquivalentTo(sessionBefore);
    }

    /// <summary>
    /// An identity-changing PUT is authorized against the proposed identity as well as the stored one. Moving
    /// the Session from the claim's school to an unrelated school, under a new name so the move cannot collide
    /// with that school's own Session, is denied and changes nothing: the Session, its CourseOffering and
    /// Section, and change tracking are all left as they were. The proposed-side check travels in the same
    /// command as the DML, ahead of it, so the recorded command text carries the root update either way and
    /// only the persisted state shows that nothing was applied.
    /// </summary>
    public static async Task AssertUnauthorizedSessionIdentityMoveIsDeniedWithoutMutationAsync(
        IRootIdentityUpdateShapeHarness harness
    )
    {
        var sessionBefore = await harness.ReadSideEffectStateAsync(Session, SessionUuid);
        var courseOfferingBefore = await harness.ReadSideEffectStateAsync(CourseOffering, CourseOfferingUuid);
        var sectionBefore = await harness.ReadSideEffectStateAsync(Section, SectionUuid);

        var result = await harness.UpdateAsync(
            Session,
            SessionBody(UnauthorizedSchoolId, MovedSessionName),
            SessionUuid,
            [ClaimEducationOrganizationId],
            [RelationshipsWithEdOrgsOnly]
        );

        result.Should().BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>();
        (await harness.ReadSideEffectStateAsync(Session, SessionUuid)).Should().BeEquivalentTo(sessionBefore);
        (await harness.ReadSideEffectStateAsync(CourseOffering, CourseOfferingUuid))
            .Should()
            .BeEquivalentTo(courseOfferingBefore);
        (await harness.ReadSideEffectStateAsync(Section, SectionUuid)).Should().BeEquivalentTo(sectionBefore);
        (await harness.ReadTrackedChangesAsync(Session, SessionUuid)).Should().BeEmpty();
        (await harness.ReadTrackedChangesAsync(CourseOffering, CourseOfferingUuid)).Should().BeEmpty();
        (await harness.ReadTrackedChangesAsync(Section, SectionUuid)).Should().BeEmpty();
    }

    /// <summary>
    /// The root table's writable non-key columns in binding order (the compiled full SET list), its
    /// document-stamping identity projection, and the reduced list that leaves the projection out.
    /// </summary>
    public static (
        IReadOnlyList<string> Full,
        IReadOnlyList<string> IdentityProjection,
        IReadOnlyList<string> Reduced
    ) ResolveRootUpdateColumns(MappingSet mappingSet, string resourceName)
    {
        var rootPlan = RootTablePlan(mappingSet, resourceName);
        var keyColumns = rootPlan
            .TableModel.Key.Columns.Select(static keyColumn => keyColumn.ColumnName.Value)
            .ToHashSet(StringComparer.Ordinal);
        var full = rootPlan
            .ColumnBindings.Select(static binding => binding.Column.ColumnName.Value)
            .Where(column => !keyColumns.Contains(column))
            .ToArray();
        var identityProjection = mappingSet
            .Model.TriggersInCreateOrder.Single(trigger =>
                trigger.Table.Equals(rootPlan.TableModel.Table)
                && trigger.Parameters is TriggerKindParameters.DocumentStamping
            )
            .IdentityProjectionColumns.Select(static column => column.Value)
            .ToArray();

        return (full, identityProjection, [.. full.Where(column => !identityProjection.Contains(column))]);
    }

    /// <summary>
    /// The SET-list columns of every <c>UPDATE</c> the recorded write commands issued against the
    /// resource's root table, one list per statement in issue order. The match is anchored on the quoted
    /// table name directly after <c>UPDATE</c>, so lock hints and other tables' statements never match.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> RootUpdates(
        IRootIdentityUpdateShapeHarness harness,
        string resourceName
    )
    {
        var table = RootTablePlan(harness.MappingSet, resourceName).TableModel.Table;
        var quotedTable = harness.MappingSet.Key.Dialect switch
        {
            SqlDialect.Pgsql => $"\"{table.Schema.Value}\".\"{table.Name}\"",
            SqlDialect.Mssql => $"[{table.Schema.Value}].[{table.Name}]",
            _ => throw new ArgumentOutOfRangeException(nameof(harness)),
        };
        var statementPattern = new Regex(
            $@"\bUPDATE\s+{Regex.Escape(quotedTable)}\s+SET\s+(?<assignments>.*?)\s+WHERE\b",
            RegexOptions.Singleline
        );
        var assignmentPattern = new Regex(@"[""\[](?<column>[^""\]]+)[""\]]\s*=\s*");

        return harness
            .RecordedWriteCommandTexts.SelectMany(commandText => statementPattern.Matches(commandText))
            .Select(statement =>
                (IReadOnlyList<string>)
                    assignmentPattern
                        .Matches(statement.Groups["assignments"].Value)
                        .Select(static assignment => assignment.Groups["column"].Value)
                        .ToArray()
            )
            .ToArray();
    }

    private static IReadOnlyList<string> SingleRootUpdate(
        IRootIdentityUpdateShapeHarness harness,
        string resourceName
    ) => RootUpdates(harness, resourceName).Should().ContainSingle().Subject;

    private static TableWritePlan RootTablePlan(MappingSet mappingSet, string resourceName)
    {
        var writePlan = mappingSet.WritePlansByResource[new QualifiedResourceName("Ed-Fi", resourceName)];

        return writePlan.TablePlansInDependencyOrder.Single(tablePlan =>
            tablePlan.TableModel.Table.Equals(writePlan.Model.Root.Table)
        );
    }

    private static IReadOnlyDictionary<string, string?> RootRow(AuthorizationWriteSideEffectState state) =>
        state.ResourceTables[0].Rows.Should().ContainSingle().Subject;

    /// <summary>
    /// A persisted date as normalized by either dialect's snapshot: SQL Server reads it as a round-trip
    /// <see cref="DateTime"/>, PostgreSQL as an invariant <see cref="DateOnly"/>.
    /// </summary>
    private static DateOnly PersistedDate(string? value) =>
        DateOnly.FromDateTime(
            DateTime.Parse(
                value ?? throw new InvalidOperationException("Expected a persisted date."),
                System.Globalization.CultureInfo.InvariantCulture
            )
        );

    private static void AssertSingleKeyChange(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> trackedChanges,
        string oldColumn,
        string newColumn
    )
    {
        var trackedChange = trackedChanges.Should().ContainSingle().Subject;
        trackedChange[oldColumn].Should().Be(SessionName);
        trackedChange[newColumn].Should().Be(RenamedSessionName);
    }

    private static async Task AssertCreatedAsync(Task<UpsertResult> upsert) =>
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(await upsert);

    private static DocumentUuid Uuid(string value) => new(Guid.Parse(value));
}
