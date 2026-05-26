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
/// </summary>
public sealed record AuthViewOutputColumn(string Alias, DbColumnName Column);

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
    /// The four people auth views in alphabetical name order: Contact, Staff, Student,
    /// StudentThroughResponsibility.
    /// </summary>
    public static readonly IReadOnlyList<AuthViewDefinition> PeopleAuthViews = BuildPeopleAuthViews();

    /// <summary>
    /// Returns whether the supplied concrete resources contain all five core associations required
    /// by the people auth views. Mirrors the guard in
    /// <c>RelationalModelDdlEmitter.EmitPeopleAuthViews</c> and the manifest emitter so the
    /// emission decision is single-sourced.
    /// </summary>
    public static bool HasAllPeopleAuthViewAssociations(
        IReadOnlyList<ConcreteResourceModel> concreteResources
    )
    {
        return GetMissingPeopleAuthAssociationResourceNames(concreteResources).Count == 0;
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

    private static IReadOnlyList<AuthViewDefinition> BuildPeopleAuthViews()
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
        var contactView = new AuthViewDefinition(
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
        );

        // 2. EducationOrganizationIdToStaffDocumentId — UNION of two arms (assignment + employment).
        // UNION (not UNION ALL) is deliberate: the deduplicating set-operator is what makes per-arm
        // DISTINCT unnecessary.
        var staffView = new AuthViewDefinition(
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
        );

        // 3. EducationOrganizationIdToStudentDocumentId — DISTINCT, single arm
        var studentView = new AuthViewDefinition(
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
        );

        // 4. EducationOrganizationIdToStudentDocumentIdThroughResponsibility — DISTINCT, single arm
        var studentThroughResponsibilityView = new AuthViewDefinition(
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
                            new DbTableName(edfi, "StudentEducationOrganizationResponsibilityAssociation"),
                            [new AuthViewJoinPredicate(edOrgAlias, targetCol, "seora", edOrgEdOrgId)]
                        ),
                    ]
                ),
            ]
        );

        return [contactView, staffView, studentView, studentThroughResponsibilityView];
    }
}
