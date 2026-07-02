// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Set-operator joining the arms of an auth view definition. Captures the deduplication semantic
/// between arms: <see cref="Union"/> deduplicates, <see cref="UnionAll"/> does not, and
/// <see cref="None"/> is used for single-arm views where no separator is emitted.
/// </summary>
public enum AuthViewSetOperator
{
    /// <summary>Single-arm view; no set-operator is emitted between arms.</summary>
    None,

    /// <summary><c>UNION</c> — deduplicates across arms.</summary>
    Union,

    /// <summary><c>UNION ALL</c> — preserves duplicates across arms.</summary>
    UnionAll,
}

/// <summary>
/// A single column definition in an auth-schema table.
/// </summary>
public sealed record AuthTableColumn(DbColumnName Name, string SqlType, bool IsNullable);

/// <summary>
/// A table in the <c>auth</c> schema (columns, primary key, table name). Consumed by both the DDL
/// emitter (renders SQL) and the manifest emitter (serializes JSON) so a single edit to the
/// definition moves both outputs together.
/// </summary>
public sealed record AuthEdOrgTableDefinition(
    DbTableName Table,
    IReadOnlyList<AuthTableColumn> Columns,
    string PrimaryKeyName,
    IReadOnlyList<DbColumnName> PrimaryKeyColumns
);

/// <summary>
/// One output column of an auth view arm: the alias of the source table/join and the underlying
/// column name. Both fields are independently meaningful — the alias drives the SQL projection
/// (<c>edOrg.SourceEducationOrganizationId</c>) and is recorded in the manifest verbatim.
/// When <paramref name="OutputName"/> is set, the projection is renamed
/// (<c>sca_tc.OldContact_DocumentId AS Contact_DocumentId</c>); when <c>null</c>, the column is
/// projected under its own name.
/// </summary>
public sealed record AuthViewOutputColumn(string Alias, DbColumnName Column, DbColumnName? OutputName = null);

/// <summary>
/// One ON-clause predicate of an auth view join, expressed as a column-equality between two
/// aliased sides.
/// </summary>
public sealed record AuthViewJoinPredicate(
    string LeftAlias,
    DbColumnName LeftColumn,
    string RightAlias,
    DbColumnName RightColumn
);

/// <summary>
/// A single <c>INNER JOIN</c> in an auth view arm.
/// </summary>
public sealed record AuthViewJoin(string Alias, DbTableName Table, IReadOnlyList<AuthViewJoinPredicate> On);

/// <summary>
/// One arm of an auth view (a single <c>SELECT [DISTINCT] ... FROM ... JOIN ...</c> statement).
/// Multi-arm views combine arms with the view's <see cref="AuthViewDefinition.ArmsSetOperator"/>.
/// </summary>
public sealed record AuthViewArm(
    bool SelectDistinct,
    string SourceAlias,
    DbTableName SourceTable,
    IReadOnlyList<AuthViewOutputColumn> OutputColumns,
    IReadOnlyList<AuthViewJoin> Joins
);

/// <summary>
/// A people auth view definition consumed by both the DDL emitter and the manifest emitter.
/// </summary>
public sealed record AuthViewDefinition(
    DbTableName View,
    AuthViewSetOperator ArmsSetOperator,
    IReadOnlyList<AuthViewArm> Arms
);

public enum AuthPeopleViewKind
{
    Student,
    Contact,
    Staff,
    StudentThroughResponsibility,
}

/// <summary>
/// Metadata for a people auth view that is needed outside DDL emission.
/// </summary>
public sealed record AuthPeopleAuthViewDefinition(
    AuthPeopleViewKind Kind,
    AuthViewDefinition ViewDefinition,
    DbColumnName PersonDocumentIdOutputColumn,
    DbColumnName ClaimEducationOrganizationIdColumn,
    string FailureHint
)
{
    public DbTableName View => ViewDefinition.View;
}

public enum ReadChangesAuthViewKind
{
    Student,
    Contact,
    Staff,
    StudentDeletedResponsibility,
}

/// <summary>
/// Inventory entry for a <c>ReadChanges</c> authorization view used by Change Query
/// <c>/deletes</c> and <c>/keyChanges</c> endpoints. The view definition unions
/// current-association arms with tracked-change (<c>tracked_changes_*</c>) arms, always joined
/// against the current <c>auth.EducationOrganizationIdToEducationOrganizationId</c> hierarchy.
/// Consumed by the DDL emitter (renders SQL) and the manifest emitter (serializes JSON).
/// </summary>
public sealed record ReadChangesAuthorizationViewInfo(
    ReadChangesAuthViewKind Kind,
    AuthViewDefinition ViewDefinition,
    DbColumnName PersonDocumentIdOutputColumn,
    DbColumnName ClaimEducationOrganizationIdColumn
)
{
    public DbTableName View => ViewDefinition.View;
}

/// <summary>
/// The prerequisites that determine whether people auth views are emitted for a model set.
/// </summary>
public sealed record PeopleAuthViewAvailability(
    bool HasAuthEdOrgHierarchy,
    IReadOnlyList<string> MissingAssociationResourceNames
)
{
    public bool IsAvailable => HasAuthEdOrgHierarchy && MissingAssociationResourceNames.Count == 0;
}

/// <summary>
/// Single source of truth for the structural shape of the emitted <c>auth.*</c> database objects:
/// the <c>auth.EducationOrganizationIdToEducationOrganizationId</c> table and the four people
/// auth views (Contact / Staff / Student / StudentThroughResponsibility).
/// </summary>
/// <remarks>
/// Per the DMS-1096 acceptance criterion ("when an auth object definition changes, the change is
/// detected by the following snapshots: relational-model.manifest.json AND pgsql.sql / mssql.sql"),
/// both the DDL emitter (<c>RelationalModelDdlEmitter</c>) and the manifest emitter
/// (<c>DerivedModelSetManifestEmitter</c>) must read their auth object structure from this one
/// source. Any structural change — column name, type, view name, join column, set-operator,
/// added/removed view — is made here and naturally flows into both outputs so both snapshots move
/// together.
/// </remarks>
public static class AuthObjectDefinitions
{
    /// <summary>
    /// Concrete resource names that must be present for the people auth views to be emitted. The
    /// guard exists because synthetic / partial test models can omit association resources, and
    /// emitting views referencing nonexistent tables would fail SQL deployment.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredPeopleAuthAssociationResourceNames =
    [
        "StudentSchoolAssociation",
        "StudentContactAssociation",
        "StaffEducationOrganizationAssignmentAssociation",
        "StaffEducationOrganizationEmploymentAssociation",
        "StudentEducationOrganizationResponsibilityAssociation",
    ];

    /// <summary>
    /// The <c>auth.EducationOrganizationIdToEducationOrganizationId</c> table: two
    /// <c>bigint NOT NULL</c> columns with a composite primary key.
    /// </summary>
    public static readonly AuthEdOrgTableDefinition AuthEdOrgTable = new(
        Table: AuthNames.EdOrgIdToEdOrgId,
        Columns:
        [
            new AuthTableColumn(AuthNames.SourceEdOrgId, "bigint", IsNullable: false),
            new AuthTableColumn(AuthNames.TargetEdOrgId, "bigint", IsNullable: false),
        ],
        PrimaryKeyName: "PK_EducationOrganizationIdToEducationOrganizationId",
        PrimaryKeyColumns: [AuthNames.SourceEdOrgId, AuthNames.TargetEdOrgId]
    );

    /// <summary>
    /// The schema holding the core tracked-change tables the <c>ReadChanges</c> auth views join.
    /// A property (not a field) so it can be read from static field initializers without a
    /// declaration-order dependency.
    /// </summary>
    private static DbSchemaName TrackedChangesEdfiSchema => new("tracked_changes_edfi");

    /// <summary>
    /// The four people auth views in alphabetical name order: Contact, Staff, Student,
    /// StudentThroughResponsibility.
    /// </summary>
    public static readonly IReadOnlyList<AuthPeopleAuthViewDefinition> PeopleAuthViewDefinitions =
        BuildPeopleAuthViewDefinitions();

    /// <summary>
    /// The emitted structural definitions for the four people auth views.
    /// </summary>
    public static readonly IReadOnlyList<AuthViewDefinition> PeopleAuthViews =
    [
        .. PeopleAuthViewDefinitions.Select(static definition => definition.ViewDefinition),
    ];

    /// <summary>
    /// The four <c>ReadChanges</c> authorization views in alphabetical name order: Contact, Staff,
    /// StudentDeletedResponsibility, StudentIncludingDeletes. The builder resolves the people auth
    /// view names from a locally built list, so this initializer does not depend on
    /// <see cref="PeopleAuthViewDefinitions"/> having been initialized first.
    /// </summary>
    /// <remarks>
    /// The fourth view is named <c>EducationOrganizationIdToStudentDocumentIdDeletedResponsibility</c>
    /// (exactly 63 characters) rather than the design's
    /// <c>...ThroughDeletedResponsibility</c> (70 characters), which exceeds PostgreSQL's 63-character
    /// identifier limit and would be silently truncated on CREATE.
    /// </remarks>
    public static readonly IReadOnlyList<ReadChangesAuthorizationViewInfo> ReadChangesAuthorizationViewDefinitions =
        BuildReadChangesAuthorizationViewDefinitions();

    /// <summary>
    /// The emitted structural definitions for the four <c>ReadChanges</c> authorization views.
    /// </summary>
    public static readonly IReadOnlyList<AuthViewDefinition> ReadChangesAuthViews =
    [
        .. ReadChangesAuthorizationViewDefinitions.Select(static definition => definition.ViewDefinition),
    ];

    public static AuthPeopleAuthViewDefinition GetPeopleAuthViewDefinition(AuthPeopleViewKind kind)
    {
        foreach (var definition in PeopleAuthViewDefinitions)
        {
            if (definition.Kind == kind)
            {
                return definition;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported people auth view kind.");
    }

    /// <summary>
    /// Returns whether the supplied model set inputs satisfy the same prerequisites used by DDL,
    /// manifest, and relationship authorization planning for the people auth views.
    /// </summary>
    public static PeopleAuthViewAvailability GetPeopleAuthViewAvailability(
        AuthEdOrgHierarchy? authHierarchy,
        IReadOnlyList<ConcreteResourceModel> concreteResources
    ) =>
        new(
            authHierarchy is { EntitiesInNameOrder.Count: > 0 },
            GetMissingPeopleAuthAssociationResourceNames(concreteResources)
        );

    /// <summary>
    /// Returns whether the supplied tracked-change inventory contains all five
    /// <c>tracked_changes_edfi</c> association tables the <c>ReadChanges</c> auth views join. The
    /// derivation pass creates them unconditionally for every concrete resource, so production model
    /// sets always satisfy this; the check guards synthetic / partial model sets, where emitting
    /// views referencing nonexistent tracked-change tables would fail SQL deployment.
    /// </summary>
    public static bool HasReadChangesTrackedChangeTables(
        IReadOnlyList<TrackedChangeTableInfo> trackedChangeTables
    )
    {
        ArgumentNullException.ThrowIfNull(trackedChangeTables);

        return RequiredPeopleAuthAssociationResourceNames.All(requiredResourceName =>
            trackedChangeTables.Any(t =>
                t.Table.Schema == TrackedChangesEdfiSchema && t.Table.Name == requiredResourceName
            )
        );
    }

    /// <summary>
    /// Returns the concrete core association resource names missing from the supplied model set that
    /// caused the people auth views to be suppressed.
    /// </summary>
    public static IReadOnlyList<string> GetMissingPeopleAuthAssociationResourceNames(
        IReadOnlyList<ConcreteResourceModel> concreteResources
    )
    {
        ArgumentNullException.ThrowIfNull(concreteResources);

        return
        [
            .. RequiredPeopleAuthAssociationResourceNames.Where(requiredResourceName =>
                !concreteResources.Any(r =>
                    DataModelConstants.IsCoreProjectName(r.ResourceKey.Resource.ProjectName)
                    && r.ResourceKey.Resource.ResourceName == requiredResourceName
                )
            ),
        ];
    }

    private static IReadOnlyList<AuthPeopleAuthViewDefinition> BuildPeopleAuthViewDefinitions()
    {
        var edfi = new DbSchemaName("edfi");
        var authEdOrgTable = AuthNames.EdOrgIdToEdOrgId;
        var sourceCol = AuthNames.SourceEdOrgId;
        var targetCol = AuthNames.TargetEdOrgId;
        var schoolIdUnified = AuthNames.SchoolIdUnified;
        var studentDocId = AuthNames.StudentDocumentId;
        var contactDocId = AuthNames.ContactDocumentId;
        var staffDocId = AuthNames.StaffDocumentId;
        var edOrgEdOrgId = AuthNames.EdOrgEdOrgId;
        const string edOrgAlias = "edOrg";

        // 1. EducationOrganizationIdToContactDocumentId — DISTINCT, single arm
        var contactView = new AuthPeopleAuthViewDefinition(
            Kind: AuthPeopleViewKind.Contact,
            ViewDefinition: new AuthViewDefinition(
                View: new DbTableName(AuthNames.AuthSchema, "EducationOrganizationIdToContactDocumentId"),
                ArmsSetOperator: AuthViewSetOperator.None,
                Arms:
                [
                    new AuthViewArm(
                        SelectDistinct: true,
                        SourceAlias: edOrgAlias,
                        SourceTable: authEdOrgTable,
                        OutputColumns:
                        [
                            new AuthViewOutputColumn(edOrgAlias, sourceCol),
                            new AuthViewOutputColumn("sca", contactDocId),
                        ],
                        Joins:
                        [
                            new AuthViewJoin(
                                "ssa",
                                new DbTableName(edfi, "StudentSchoolAssociation"),
                                [new AuthViewJoinPredicate(edOrgAlias, targetCol, "ssa", schoolIdUnified)]
                            ),
                            new AuthViewJoin(
                                "sca",
                                new DbTableName(edfi, "StudentContactAssociation"),
                                [new AuthViewJoinPredicate("ssa", studentDocId, "sca", studentDocId)]
                            ),
                        ]
                    ),
                ]
            ),
            PersonDocumentIdOutputColumn: contactDocId,
            ClaimEducationOrganizationIdColumn: sourceCol,
            FailureHint: "You may need to create corresponding 'StudentSchoolAssociation' and 'StudentContactAssociation' items."
        );

        // 2. EducationOrganizationIdToStaffDocumentId — UNION of two arms (assignment + employment).
        // UNION (not UNION ALL) is deliberate: the deduplicating set-operator is what makes per-arm
        // DISTINCT unnecessary.
        var staffView = new AuthPeopleAuthViewDefinition(
            Kind: AuthPeopleViewKind.Staff,
            ViewDefinition: new AuthViewDefinition(
                View: new DbTableName(AuthNames.AuthSchema, "EducationOrganizationIdToStaffDocumentId"),
                ArmsSetOperator: AuthViewSetOperator.Union,
                Arms:
                [
                    new AuthViewArm(
                        SelectDistinct: false,
                        SourceAlias: edOrgAlias,
                        SourceTable: authEdOrgTable,
                        OutputColumns:
                        [
                            new AuthViewOutputColumn(edOrgAlias, sourceCol),
                            new AuthViewOutputColumn("seoaa", staffDocId),
                        ],
                        Joins:
                        [
                            new AuthViewJoin(
                                "seoaa",
                                new DbTableName(edfi, "StaffEducationOrganizationAssignmentAssociation"),
                                [new AuthViewJoinPredicate(edOrgAlias, targetCol, "seoaa", edOrgEdOrgId)]
                            ),
                        ]
                    ),
                    new AuthViewArm(
                        SelectDistinct: false,
                        SourceAlias: edOrgAlias,
                        SourceTable: authEdOrgTable,
                        OutputColumns:
                        [
                            new AuthViewOutputColumn(edOrgAlias, sourceCol),
                            new AuthViewOutputColumn("seoea", staffDocId),
                        ],
                        Joins:
                        [
                            new AuthViewJoin(
                                "seoea",
                                new DbTableName(edfi, "StaffEducationOrganizationEmploymentAssociation"),
                                [new AuthViewJoinPredicate(edOrgAlias, targetCol, "seoea", edOrgEdOrgId)]
                            ),
                        ]
                    ),
                ]
            ),
            PersonDocumentIdOutputColumn: staffDocId,
            ClaimEducationOrganizationIdColumn: sourceCol,
            FailureHint: "You may need to create corresponding 'StaffEducationOrganizationEmploymentAssociation' or 'StaffEducationOrganizationAssignmentAssociation' items."
        );

        // 3. EducationOrganizationIdToStudentDocumentId — DISTINCT, single arm
        var studentView = new AuthPeopleAuthViewDefinition(
            Kind: AuthPeopleViewKind.Student,
            ViewDefinition: new AuthViewDefinition(
                View: new DbTableName(AuthNames.AuthSchema, "EducationOrganizationIdToStudentDocumentId"),
                ArmsSetOperator: AuthViewSetOperator.None,
                Arms:
                [
                    new AuthViewArm(
                        SelectDistinct: true,
                        SourceAlias: edOrgAlias,
                        SourceTable: authEdOrgTable,
                        OutputColumns:
                        [
                            new AuthViewOutputColumn(edOrgAlias, sourceCol),
                            new AuthViewOutputColumn("ssa", studentDocId),
                        ],
                        Joins:
                        [
                            new AuthViewJoin(
                                "ssa",
                                new DbTableName(edfi, "StudentSchoolAssociation"),
                                [new AuthViewJoinPredicate(edOrgAlias, targetCol, "ssa", schoolIdUnified)]
                            ),
                        ]
                    ),
                ]
            ),
            PersonDocumentIdOutputColumn: studentDocId,
            ClaimEducationOrganizationIdColumn: sourceCol,
            FailureHint: "You may need to create a corresponding 'StudentSchoolAssociation' item."
        );

        // 4. EducationOrganizationIdToStudentDocumentIdThroughResponsibility — DISTINCT, single arm
        var studentThroughResponsibilityView = new AuthPeopleAuthViewDefinition(
            Kind: AuthPeopleViewKind.StudentThroughResponsibility,
            ViewDefinition: new AuthViewDefinition(
                View: new DbTableName(
                    AuthNames.AuthSchema,
                    "EducationOrganizationIdToStudentDocumentIdThroughResponsibility"
                ),
                ArmsSetOperator: AuthViewSetOperator.None,
                Arms:
                [
                    new AuthViewArm(
                        SelectDistinct: true,
                        SourceAlias: edOrgAlias,
                        SourceTable: authEdOrgTable,
                        OutputColumns:
                        [
                            new AuthViewOutputColumn(edOrgAlias, sourceCol),
                            new AuthViewOutputColumn("seora", studentDocId),
                        ],
                        Joins:
                        [
                            new AuthViewJoin(
                                "seora",
                                new DbTableName(
                                    edfi,
                                    "StudentEducationOrganizationResponsibilityAssociation"
                                ),
                                [new AuthViewJoinPredicate(edOrgAlias, targetCol, "seora", edOrgEdOrgId)]
                            ),
                        ]
                    ),
                ]
            ),
            PersonDocumentIdOutputColumn: studentDocId,
            ClaimEducationOrganizationIdColumn: sourceCol,
            FailureHint: "You may need to create a corresponding 'StudentEducationOrganizationResponsibilityAssociation' item."
        );

        return [contactView, staffView, studentView, studentThroughResponsibilityView];
    }

    /// <summary>
    /// Builds the four <c>ReadChanges</c> authorization view definitions. Each view unions a
    /// current/current arm (selecting from the corresponding people auth view) with tracked-change
    /// arms joining <c>tracked_changes_edfi</c> association tables via their old-value
    /// identity/securable columns against the current EdOrg hierarchy. Arms use plain
    /// <c>SELECT</c> with <c>UNION</c> (not <c>UNION ALL</c>) so duplicate authorization pairs
    /// across arms are eliminated. See <c>change-queries.md</c> ("Authorization views") for design.
    /// </summary>
    private static IReadOnlyList<ReadChangesAuthorizationViewInfo> BuildReadChangesAuthorizationViewDefinitions()
    {
        // Built locally (not read from the PeopleAuthViewDefinitions field) so this builder is safe
        // to run from a static field initializer regardless of field declaration order.
        var peopleViewDefinitions = BuildPeopleAuthViewDefinitions();
        var edfi = new DbSchemaName("edfi");
        var trackedChanges = TrackedChangesEdfiSchema;
        var authEdOrgTable = AuthNames.EdOrgIdToEdOrgId;
        var sourceCol = AuthNames.SourceEdOrgId;
        var targetCol = AuthNames.TargetEdOrgId;
        var studentDocId = AuthNames.StudentDocumentId;
        var contactDocId = AuthNames.ContactDocumentId;
        var staffDocId = AuthNames.StaffDocumentId;
        const string edOrgAlias = "edOrg";

        // Matches DeriveTrackedChangeInventoryPass: only the Old prefix is added; internal
        // underscores from the source column are preserved.
        static DbColumnName OldValueColumn(DbColumnName sourceColumn) => new("Old" + sourceColumn.Value);

        var oldSchoolIdUnified = OldValueColumn(AuthNames.SchoolIdUnified);
        var oldStudentDocId = OldValueColumn(studentDocId);
        var oldContactDocId = OldValueColumn(contactDocId);
        var oldStaffDocId = OldValueColumn(staffDocId);
        var oldEdOrgEdOrgId = OldValueColumn(AuthNames.EdOrgEdOrgId);

        var trackedSsa = new DbTableName(trackedChanges, "StudentSchoolAssociation");
        var trackedSca = new DbTableName(trackedChanges, "StudentContactAssociation");
        var trackedSeoaa = new DbTableName(trackedChanges, "StaffEducationOrganizationAssignmentAssociation");
        var trackedSeoea = new DbTableName(trackedChanges, "StaffEducationOrganizationEmploymentAssociation");
        var trackedSeora = new DbTableName(
            trackedChanges,
            "StudentEducationOrganizationResponsibilityAssociation"
        );

        // An arm selecting the current people auth view verbatim (current/current combination).
        AuthViewArm CurrentViewArm(AuthPeopleViewKind kind, string alias, DbColumnName personDocId)
        {
            var currentView = peopleViewDefinitions.Single(definition => definition.Kind == kind).View;
            return new AuthViewArm(
                SelectDistinct: false,
                SourceAlias: alias,
                SourceTable: currentView,
                OutputColumns:
                [
                    new AuthViewOutputColumn(alias, AuthNames.SourceEdOrgId),
                    new AuthViewOutputColumn(alias, personDocId),
                ],
                Joins: []
            );
        }

        // 1. EducationOrganizationIdToContactDocumentIdIncludingDeletes — UNION of four arms:
        // current/current, current SSA / tracked SCA, tracked SSA / current SCA, tracked/tracked.
        var contactView = new ReadChangesAuthorizationViewInfo(
            Kind: ReadChangesAuthViewKind.Contact,
            ViewDefinition: new AuthViewDefinition(
                View: new DbTableName(
                    AuthNames.AuthSchema,
                    "EducationOrganizationIdToContactDocumentIdIncludingDeletes"
                ),
                ArmsSetOperator: AuthViewSetOperator.Union,
                Arms:
                [
                    CurrentViewArm(AuthPeopleViewKind.Contact, "edOrgToContact", contactDocId),
                    new AuthViewArm(
                        SelectDistinct: false,
                        SourceAlias: edOrgAlias,
                        SourceTable: authEdOrgTable,
                        OutputColumns:
                        [
                            new AuthViewOutputColumn(edOrgAlias, sourceCol),
                            new AuthViewOutputColumn("sca_tc", oldContactDocId, contactDocId),
                        ],
                        Joins:
                        [
                            new AuthViewJoin(
                                "ssa",
                                new DbTableName(edfi, "StudentSchoolAssociation"),
                                [
                                    new AuthViewJoinPredicate(
                                        edOrgAlias,
                                        targetCol,
                                        "ssa",
                                        AuthNames.SchoolIdUnified
                                    ),
                                ]
                            ),
                            new AuthViewJoin(
                                "sca_tc",
                                trackedSca,
                                [new AuthViewJoinPredicate("ssa", studentDocId, "sca_tc", oldStudentDocId)]
                            ),
                        ]
                    ),
                    new AuthViewArm(
                        SelectDistinct: false,
                        SourceAlias: edOrgAlias,
                        SourceTable: authEdOrgTable,
                        OutputColumns:
                        [
                            new AuthViewOutputColumn(edOrgAlias, sourceCol),
                            new AuthViewOutputColumn("sca", contactDocId),
                        ],
                        Joins:
                        [
                            new AuthViewJoin(
                                "ssa_tc",
                                trackedSsa,
                                [
                                    new AuthViewJoinPredicate(
                                        edOrgAlias,
                                        targetCol,
                                        "ssa_tc",
                                        oldSchoolIdUnified
                                    ),
                                ]
                            ),
                            new AuthViewJoin(
                                "sca",
                                new DbTableName(edfi, "StudentContactAssociation"),
                                [new AuthViewJoinPredicate("ssa_tc", oldStudentDocId, "sca", studentDocId)]
                            ),
                        ]
                    ),
                    new AuthViewArm(
                        SelectDistinct: false,
                        SourceAlias: edOrgAlias,
                        SourceTable: authEdOrgTable,
                        OutputColumns:
                        [
                            new AuthViewOutputColumn(edOrgAlias, sourceCol),
                            new AuthViewOutputColumn("sca_tc", oldContactDocId, contactDocId),
                        ],
                        Joins:
                        [
                            new AuthViewJoin(
                                "ssa_tc",
                                trackedSsa,
                                [
                                    new AuthViewJoinPredicate(
                                        edOrgAlias,
                                        targetCol,
                                        "ssa_tc",
                                        oldSchoolIdUnified
                                    ),
                                ]
                            ),
                            new AuthViewJoin(
                                "sca_tc",
                                trackedSca,
                                [
                                    new AuthViewJoinPredicate(
                                        "ssa_tc",
                                        oldStudentDocId,
                                        "sca_tc",
                                        oldStudentDocId
                                    ),
                                ]
                            ),
                        ]
                    ),
                ]
            ),
            PersonDocumentIdOutputColumn: contactDocId,
            ClaimEducationOrganizationIdColumn: sourceCol
        );

        // 2. EducationOrganizationIdToStaffDocumentIdIncludingDeletes — UNION of three arms:
        // current/current, tracked assignment, tracked employment (assignment first, matching the
        // current staff view's arm order).
        var staffView = new ReadChangesAuthorizationViewInfo(
            Kind: ReadChangesAuthViewKind.Staff,
            ViewDefinition: new AuthViewDefinition(
                View: new DbTableName(
                    AuthNames.AuthSchema,
                    "EducationOrganizationIdToStaffDocumentIdIncludingDeletes"
                ),
                ArmsSetOperator: AuthViewSetOperator.Union,
                Arms:
                [
                    CurrentViewArm(AuthPeopleViewKind.Staff, "edOrgToStaff", staffDocId),
                    new AuthViewArm(
                        SelectDistinct: false,
                        SourceAlias: edOrgAlias,
                        SourceTable: authEdOrgTable,
                        OutputColumns:
                        [
                            new AuthViewOutputColumn(edOrgAlias, sourceCol),
                            new AuthViewOutputColumn("seoaa_tc", oldStaffDocId, staffDocId),
                        ],
                        Joins:
                        [
                            new AuthViewJoin(
                                "seoaa_tc",
                                trackedSeoaa,
                                [
                                    new AuthViewJoinPredicate(
                                        edOrgAlias,
                                        targetCol,
                                        "seoaa_tc",
                                        oldEdOrgEdOrgId
                                    ),
                                ]
                            ),
                        ]
                    ),
                    new AuthViewArm(
                        SelectDistinct: false,
                        SourceAlias: edOrgAlias,
                        SourceTable: authEdOrgTable,
                        OutputColumns:
                        [
                            new AuthViewOutputColumn(edOrgAlias, sourceCol),
                            new AuthViewOutputColumn("seoea_tc", oldStaffDocId, staffDocId),
                        ],
                        Joins:
                        [
                            new AuthViewJoin(
                                "seoea_tc",
                                trackedSeoea,
                                [
                                    new AuthViewJoinPredicate(
                                        edOrgAlias,
                                        targetCol,
                                        "seoea_tc",
                                        oldEdOrgEdOrgId
                                    ),
                                ]
                            ),
                        ]
                    ),
                ]
            ),
            PersonDocumentIdOutputColumn: staffDocId,
            ClaimEducationOrganizationIdColumn: sourceCol
        );

        // 3. EducationOrganizationIdToStudentDocumentIdDeletedResponsibility — UNION of two arms:
        // current/current (through-responsibility view) and tracked responsibility association.
        // Named without "Through" to fit PostgreSQL's 63-character identifier limit (see field remarks).
        var studentDeletedResponsibilityView = new ReadChangesAuthorizationViewInfo(
            Kind: ReadChangesAuthViewKind.StudentDeletedResponsibility,
            ViewDefinition: new AuthViewDefinition(
                View: new DbTableName(
                    AuthNames.AuthSchema,
                    "EducationOrganizationIdToStudentDocumentIdDeletedResponsibility"
                ),
                ArmsSetOperator: AuthViewSetOperator.Union,
                Arms:
                [
                    CurrentViewArm(
                        AuthPeopleViewKind.StudentThroughResponsibility,
                        "edOrgToStudentResp",
                        studentDocId
                    ),
                    new AuthViewArm(
                        SelectDistinct: false,
                        SourceAlias: edOrgAlias,
                        SourceTable: authEdOrgTable,
                        OutputColumns:
                        [
                            new AuthViewOutputColumn(edOrgAlias, sourceCol),
                            new AuthViewOutputColumn("seora_tc", oldStudentDocId, studentDocId),
                        ],
                        Joins:
                        [
                            new AuthViewJoin(
                                "seora_tc",
                                trackedSeora,
                                [
                                    new AuthViewJoinPredicate(
                                        edOrgAlias,
                                        targetCol,
                                        "seora_tc",
                                        oldEdOrgEdOrgId
                                    ),
                                ]
                            ),
                        ]
                    ),
                ]
            ),
            PersonDocumentIdOutputColumn: studentDocId,
            ClaimEducationOrganizationIdColumn: sourceCol
        );

        // 4. EducationOrganizationIdToStudentDocumentIdIncludingDeletes — UNION of two arms:
        // current/current and tracked StudentSchoolAssociation.
        var studentView = new ReadChangesAuthorizationViewInfo(
            Kind: ReadChangesAuthViewKind.Student,
            ViewDefinition: new AuthViewDefinition(
                View: new DbTableName(
                    AuthNames.AuthSchema,
                    "EducationOrganizationIdToStudentDocumentIdIncludingDeletes"
                ),
                ArmsSetOperator: AuthViewSetOperator.Union,
                Arms:
                [
                    CurrentViewArm(AuthPeopleViewKind.Student, "edOrgToStudent", studentDocId),
                    new AuthViewArm(
                        SelectDistinct: false,
                        SourceAlias: edOrgAlias,
                        SourceTable: authEdOrgTable,
                        OutputColumns:
                        [
                            new AuthViewOutputColumn(edOrgAlias, sourceCol),
                            new AuthViewOutputColumn("ssa_tc", oldStudentDocId, studentDocId),
                        ],
                        Joins:
                        [
                            new AuthViewJoin(
                                "ssa_tc",
                                trackedSsa,
                                [
                                    new AuthViewJoinPredicate(
                                        edOrgAlias,
                                        targetCol,
                                        "ssa_tc",
                                        oldSchoolIdUnified
                                    ),
                                ]
                            ),
                        ]
                    ),
                ]
            ),
            PersonDocumentIdOutputColumn: studentDocId,
            ClaimEducationOrganizationIdColumn: sourceCol
        );

        // Alphabetical by view name: Contact, Staff, StudentDeletedResponsibility, StudentIncludingDeletes.
        return [contactView, staffView, studentDeletedResponsibilityView, studentView];
    }
}
