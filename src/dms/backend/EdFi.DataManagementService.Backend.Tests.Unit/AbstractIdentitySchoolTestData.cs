// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Shared School / EducationOrganization abstract-identity model used by both the constraint-resolver and
/// the failure-mapper abstract-identity tests, so the two suites exercise identical table shapes and cannot
/// silently drift apart.
/// </summary>
internal static class AbstractIdentitySchoolTestData
{
    internal const string NaturalKeyConstraintName = "UX_EducationOrganizationIdentity_NK";
    internal const string ReferenceKeyConstraintName = "UX_EducationOrganizationIdentity_RefKey";

    internal static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    internal static readonly QualifiedResourceName EducationOrganizationResource = new(
        "Ed-Fi",
        "EducationOrganization"
    );

    /// <summary>
    /// Builds the concrete School root table with its <c>SchoolId</c> identity column.
    /// </summary>
    internal static DbTableModel SchoolRootTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression("$.schoolId", [new JsonPathSegment.Property("schoolId")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        );

    /// <summary>
    /// Models edfi."EducationOrganizationIdentity" the way AbstractIdentityTableAndUnionViewDerivationPass
    /// builds it: DocumentId primary key, the _NK natural-key unique over the projected identity column, the
    /// _RefKey helper that also includes DocumentId, and the document FK.
    /// </summary>
    internal static DbTableModel EducationOrganizationIdentityTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "EducationOrganizationIdentity"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_EducationOrganizationIdentity",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("EducationOrganizationId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression(
                        "$.educationOrganizationId",
                        [new JsonPathSegment.Property("educationOrganizationId")]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Discriminator"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 256),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            [
                new TableConstraint.Unique(
                    "UX_EducationOrganizationIdentity_NK",
                    [new DbColumnName("EducationOrganizationId")]
                ),
                new TableConstraint.Unique(
                    "UX_EducationOrganizationIdentity_RefKey",
                    [new DbColumnName("EducationOrganizationId"), new DbColumnName("DocumentId")]
                ),
                new TableConstraint.ForeignKey(
                    "FK_EducationOrganizationIdentity_Document",
                    [new DbColumnName("DocumentId")],
                    new DbTableName(new DbSchemaName("dms"), "Document"),
                    [new DbColumnName("DocumentId")],
                    OnDelete: ReferentialAction.Cascade
                ),
            ]
        );

    /// <summary>
    /// Builds the write plan and mapping set shared by the abstract-identity tests, using the single-column
    /// EducationOrganization identity table.
    /// </summary>
    internal static (ResourceWritePlan WritePlan, MappingSet MappingSet) BuildSchoolWriteModel() =>
        BuildSchoolWriteModel(EducationOrganizationIdentityTable());

    /// <summary>
    /// Builds the write plan and mapping set shared by the abstract-identity tests. The School
    /// referential-identity trigger is always present so the failure mapper can project the concrete identity
    /// values; the constraint resolver ignores triggers.
    /// </summary>
    /// <param name="abstractIdentityTable">
    /// The abstract identity table placed in the mapping set's abstract-identity inventory. Tests pass a
    /// composite-key variant to cover multi-column natural keys.
    /// </param>
    internal static (ResourceWritePlan WritePlan, MappingSet MappingSet) BuildSchoolWriteModel(
        DbTableModel abstractIdentityTable
    )
    {
        var schoolRootTable = SchoolRootTable();

        var resourceModel = new RelationalResourceModel(
            SchoolResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            schoolRootTable,
            [schoolRootTable],
            [],
            []
        );

        var writePlan = new ResourceWritePlan(resourceModel, []);

        var schoolKey = new ResourceKeyEntry(1, SchoolResource, "1.0.0", false);
        var educationOrganizationKey = new ResourceKeyEntry(2, EducationOrganizationResource, "1.0.0", true);

        var schoolReferentialIdentityTrigger = new DbTriggerInfo(
            new DbTriggerName("TR_School_ReferentialIdentity"),
            schoolRootTable.Table,
            [new DbColumnName("DocumentId")],
            [new DbColumnName("SchoolId")],
            new TriggerKindParameters.ReferentialIdentityMaintenance(
                schoolKey.ResourceKeyId,
                SchoolResource.ProjectName,
                SchoolResource.ResourceName,
                [
                    new IdentityElementMapping(
                        new DbColumnName("SchoolId"),
                        "$.schoolId",
                        new RelationalScalarType(ScalarKind.Int32)
                    ),
                ]
            )
        );

        var mappingSet = new MappingSet(
            new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            new DerivedRelationalModelSet(
                new EffectiveSchemaInfo(
                    "1.0",
                    "v1",
                    "schema-hash",
                    2,
                    [1, 2],
                    [new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash")],
                    [schoolKey, educationOrganizationKey]
                ),
                SqlDialect.Pgsql,
                [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi"))],
                [new ConcreteResourceModel(schoolKey, ResourceStorageKind.RelationalTables, resourceModel)],
                [new AbstractIdentityTableInfo(educationOrganizationKey, abstractIdentityTable)],
                [],
                [],
                [schoolReferentialIdentityTrigger]
            ),
            new Dictionary<QualifiedResourceName, ResourceWritePlan> { [SchoolResource] = writePlan },
            new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            new Dictionary<QualifiedResourceName, short>
            {
                [SchoolResource] = schoolKey.ResourceKeyId,
                [EducationOrganizationResource] = educationOrganizationKey.ResourceKeyId,
            },
            new Dictionary<short, ResourceKeyEntry>
            {
                [schoolKey.ResourceKeyId] = schoolKey,
                [educationOrganizationKey.ResourceKeyId] = educationOrganizationKey,
            },
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>()
        );

        return (writePlan, mappingSet);
    }
}
