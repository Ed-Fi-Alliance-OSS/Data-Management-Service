// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using static EdFi.DataManagementService.Backend.RelationalModel.Manifest.ManifestWriterHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.Manifest;

/// <summary>
/// Emits a deterministic <c>relational-model.{dialect}.manifest.json</c> from a <see cref="DerivedRelationalModelSet"/>.
/// The manifest is a semantic representation of the derived relational model inventory and must be
/// byte-for-byte stable for the same inputs.
/// </summary>
public static class DerivedModelSetManifestEmitter
{
    private static readonly JsonWriterOptions _writerOptions = new() { Indented = true, NewLine = "\n" };

    /// <summary>
    /// Emits the manifest JSON string from the given model set.
    /// </summary>
    /// <param name="modelSet">The derived relational model set to serialize.</param>
    /// <param name="detailedResources">
    /// Optional set of resources for which to emit full table/binding details in the
    /// <c>resource_details</c> section. Pass <c>null</c> to omit the section entirely.
    /// </param>
    /// <param name="extensionSitesProvider">
    /// Optional callback to retrieve extension sites for a resource. Only used when
    /// <paramref name="detailedResources"/> is provided.
    /// </param>
    /// <returns>A UTF-8 JSON string with <c>\n</c> line endings and a trailing newline.</returns>
    public static string Emit(
        DerivedRelationalModelSet modelSet,
        IReadOnlySet<QualifiedResourceName>? detailedResources = null,
        Func<QualifiedResourceName, IReadOnlyList<ExtensionSite>>? extensionSitesProvider = null
    )
    {
        ArgumentNullException.ThrowIfNull(modelSet);

        var buffer = new ArrayBufferWriter<byte>(initialCapacity: 65536);

        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("dialect", modelSet.Dialect.ToString().ToLowerInvariant());
            writer.WriteString("effective_schema_hash", modelSet.EffectiveSchema.EffectiveSchemaHash);
            writer.WriteString(
                "relational_mapping_version",
                modelSet.EffectiveSchema.RelationalMappingVersion
            );

            WriteProjects(writer, modelSet.ProjectSchemasInEndpointOrder);
            WriteResourcesSummary(writer, modelSet.ConcreteResourcesInNameOrder);
            WriteAbstractIdentityTables(writer, modelSet.AbstractIdentityTablesInNameOrder);
            WriteAbstractUnionViews(writer, modelSet.AbstractUnionViewsInNameOrder);
            WriteIndexes(writer, modelSet.IndexesInCreateOrder);
            WriteTriggers(writer, modelSet.TriggersInCreateOrder);
            WriteAuthObjects(writer, modelSet.AuthEdOrgHierarchy, modelSet.ConcreteResourcesInNameOrder);

            if (detailedResources is not null)
            {
                WriteResourceDetails(
                    writer,
                    modelSet.ConcreteResourcesInNameOrder,
                    detailedResources,
                    extensionSitesProvider
                );
            }

            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);

        return json + "\n";
    }

    /// <summary>
    /// Writes the <c>projects</c> array.
    /// </summary>
    private static void WriteProjects(Utf8JsonWriter writer, IReadOnlyList<ProjectSchemaInfo> projects)
    {
        writer.WritePropertyName("projects");
        writer.WriteStartArray();

        foreach (var project in projects)
        {
            writer.WriteStartObject();
            writer.WriteString("project_endpoint_name", project.ProjectEndpointName);
            writer.WriteString("project_name", project.ProjectName);
            writer.WriteString("project_version", project.ProjectVersion);
            writer.WriteBoolean("is_extension", project.IsExtensionProject);
            writer.WriteString("physical_schema", project.PhysicalSchema.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes the <c>resources</c> summary array.
    /// </summary>
    private static void WriteResourcesSummary(
        Utf8JsonWriter writer,
        IReadOnlyList<ConcreteResourceModel> resources
    )
    {
        writer.WritePropertyName("resources");
        writer.WriteStartArray();

        foreach (var resource in resources)
        {
            writer.WriteStartObject();
            writer.WriteString("project_name", resource.ResourceKey.Resource.ProjectName);
            writer.WriteString("resource_name", resource.ResourceKey.Resource.ResourceName);
            writer.WriteString("storage_kind", resource.StorageKind.ToString());
            writer.WriteString("physical_schema", resource.RelationalModel.PhysicalSchema.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes the <c>abstract_identity_tables</c> array.
    /// </summary>
    private static void WriteAbstractIdentityTables(
        Utf8JsonWriter writer,
        IReadOnlyList<AbstractIdentityTableInfo> abstractIdentityTables
    )
    {
        writer.WritePropertyName("abstract_identity_tables");
        writer.WriteStartArray();

        foreach (var tableInfo in abstractIdentityTables)
        {
            writer.WriteStartObject();
            WriteResource(writer, tableInfo.AbstractResourceKey.Resource);
            writer.WritePropertyName("table");
            WriteTable(writer, tableInfo.TableModel);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes the <c>abstract_union_views</c> array.
    /// </summary>
    private static void WriteAbstractUnionViews(
        Utf8JsonWriter writer,
        IReadOnlyList<AbstractUnionViewInfo> abstractUnionViews
    )
    {
        writer.WritePropertyName("abstract_union_views");
        writer.WriteStartArray();

        foreach (var view in abstractUnionViews)
        {
            writer.WriteStartObject();
            WriteResource(writer, view.AbstractResourceKey.Resource);

            writer.WritePropertyName("view_name");
            WriteTableReference(writer, view.ViewName);

            writer.WritePropertyName("output_columns");
            writer.WriteStartArray();
            foreach (var column in view.OutputColumnsInSelectOrder)
            {
                WriteAbstractUnionViewOutputColumn(writer, column);
            }
            writer.WriteEndArray();

            writer.WritePropertyName("union_arms");
            writer.WriteStartArray();
            foreach (var arm in view.UnionArmsInOrder)
            {
                WriteAbstractUnionViewArm(writer, arm);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes a single output column for an abstract union view.
    /// </summary>
    private static void WriteAbstractUnionViewOutputColumn(
        Utf8JsonWriter writer,
        AbstractUnionViewOutputColumn column
    )
    {
        writer.WriteStartObject();
        writer.WriteString("column_name", column.ColumnName.Value);
        writer.WritePropertyName("type");
        WriteScalarType(writer, column.ScalarType);

        writer.WritePropertyName("source_path");
        if (column.SourceJsonPath is { } sourcePath)
        {
            writer.WriteStringValue(sourcePath.Canonical);
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WritePropertyName("target_resource");
        if (column.TargetResource is { } targetResource)
        {
            WriteResourceReference(writer, targetResource);
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a single union arm for an abstract union view.
    /// </summary>
    private static void WriteAbstractUnionViewArm(Utf8JsonWriter writer, AbstractUnionViewArm arm)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("concrete_member");
        WriteResourceReference(writer, arm.ConcreteMemberResourceKey.Resource);

        writer.WritePropertyName("from_table");
        WriteTableReference(writer, arm.FromTable);

        writer.WritePropertyName("projection_expressions");
        writer.WriteStartArray();
        foreach (var expression in arm.ProjectionExpressionsInSelectOrder)
        {
            WriteProjectionExpression(writer, expression);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a single projection expression within a union arm.
    /// </summary>
    private static void WriteProjectionExpression(
        Utf8JsonWriter writer,
        AbstractUnionViewProjectionExpression expression
    )
    {
        writer.WriteStartObject();

        switch (expression)
        {
            case AbstractUnionViewProjectionExpression.SourceColumn sourceColumn:
                writer.WriteString("kind", "SourceColumn");
                writer.WriteString("column_name", sourceColumn.ColumnName.Value);
                break;
            case AbstractUnionViewProjectionExpression.StringLiteral stringLiteral:
                writer.WriteString("kind", "StringLiteral");
                writer.WriteString("value", stringLiteral.Value);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(expression),
                    expression,
                    "Unknown projection expression type."
                );
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes the <c>indexes</c> array.
    /// </summary>
    private static void WriteIndexes(Utf8JsonWriter writer, IReadOnlyList<DbIndexInfo> indexes)
    {
        writer.WritePropertyName("indexes");
        writer.WriteStartArray();

        foreach (var index in indexes)
        {
            writer.WriteStartObject();
            writer.WriteString("name", index.Name.Value);
            writer.WritePropertyName("table");
            WriteTableReference(writer, index.Table);
            writer.WriteString("kind", index.Kind.ToString());
            writer.WriteBoolean("is_unique", index.IsUnique);
            writer.WritePropertyName("key_columns");
            WriteColumnNameList(writer, index.KeyColumns);
            if (index.IncludeColumns is { Count: > 0 })
            {
                writer.WritePropertyName("include_columns");
                WriteColumnNameList(writer, index.IncludeColumns);
            }
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes the <c>triggers</c> array.
    /// </summary>
    private static void WriteTriggers(Utf8JsonWriter writer, IReadOnlyList<DbTriggerInfo> triggers)
    {
        writer.WritePropertyName("triggers");
        writer.WriteStartArray();

        foreach (var trigger in triggers)
        {
            writer.WriteStartObject();
            writer.WriteString("name", trigger.Name.Value);
            writer.WritePropertyName("table");
            WriteTableReference(writer, trigger.Table);
            writer.WriteString(
                "kind",
                trigger.Parameters switch
                {
                    TriggerKindParameters.DocumentStamping => "DocumentStamping",
                    TriggerKindParameters.ReferentialIdentityMaintenance => "ReferentialIdentityMaintenance",
                    TriggerKindParameters.AbstractIdentityMaintenance => "AbstractIdentityMaintenance",
                    TriggerKindParameters.IdentityPropagationFallback => "IdentityPropagationFallback",
                    TriggerKindParameters.AuthHierarchyMaintenance => "AuthHierarchyMaintenance",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(triggers),
                        "Unsupported trigger kind parameters type."
                    ),
                }
            );
            writer.WritePropertyName("key_columns");
            WriteColumnNameList(writer, trigger.KeyColumns);
            writer.WritePropertyName("identity_projection_columns");
            WriteColumnNameList(writer, trigger.IdentityProjectionColumns);

            switch (trigger.Parameters)
            {
                case TriggerKindParameters.AbstractIdentityMaintenance abstractId:
                    writer.WritePropertyName("target_table");
                    WriteTableReference(writer, abstractId.TargetTable);
                    WriteTargetColumnMappings(writer, abstractId.TargetColumnMappings);
                    writer.WriteString("discriminator_value", abstractId.DiscriminatorValue);
                    break;

                case TriggerKindParameters.IdentityPropagationFallback propagation:
                    writer.WritePropertyName("referrer_updates");
                    writer.WriteStartArray();
                    foreach (var referrer in propagation.ReferrerUpdates)
                    {
                        writer.WriteStartObject();
                        writer.WritePropertyName("referrer_table");
                        WriteTableReference(writer, referrer.ReferrerTable);
                        writer.WriteString("referrer_fk_column", referrer.ReferrerFkColumn.Value);
                        WriteTargetColumnMappings(writer, referrer.ColumnMappings);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    break;

                case TriggerKindParameters.ReferentialIdentityMaintenance refId:
                    writer.WriteNumber("resource_key_id", refId.ResourceKeyId);
                    writer.WriteString("project_name", refId.ProjectName);
                    writer.WriteString("resource_name", refId.ResourceName);
                    writer.WritePropertyName("identity_elements");
                    writer.WriteStartArray();
                    foreach (var element in refId.IdentityElements)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("column", element.Column.Value);
                        writer.WriteString("identity_json_path", element.IdentityJsonPath);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();

                    if (refId.SuperclassAlias is { } alias)
                    {
                        writer.WritePropertyName("superclass_alias");
                        writer.WriteStartObject();
                        writer.WriteNumber("resource_key_id", alias.ResourceKeyId);
                        writer.WriteString("project_name", alias.ProjectName);
                        writer.WriteString("resource_name", alias.ResourceName);
                        writer.WritePropertyName("identity_elements");
                        writer.WriteStartArray();
                        foreach (var element in alias.IdentityElements)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("column", element.Column.Value);
                            writer.WriteString("identity_json_path", element.IdentityJsonPath);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    break;

                case TriggerKindParameters.DocumentStamping:
                    // DocumentStamping has no additional properties
                    break;

                case TriggerKindParameters.AuthHierarchyMaintenance auth:
                    writer.WriteString("entity_name", auth.Entity.EntityName);
                    writer.WriteString("trigger_event", auth.TriggerEvent.ToString());
                    writer.WriteString("identity_column", auth.Entity.IdentityColumn.Value);
                    writer.WritePropertyName("parent_fks");
                    writer.WriteStartArray();
                    foreach (var fk in auth.Entity.ParentEdOrgFks)
                    {
                        writer.WriteStartObject();
                        writer.WriteString(
                            "denormalized_parent_id_column",
                            fk.DenormalizedParentIdColumn.Value
                        );
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    break;
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes a <c>target_column_mappings</c> array of source/target column pairs.
    /// </summary>
    private static void WriteTargetColumnMappings(
        Utf8JsonWriter writer,
        IReadOnlyList<TriggerColumnMapping> mappings
    )
    {
        writer.WritePropertyName("target_column_mappings");
        writer.WriteStartArray();
        foreach (var mapping in mappings)
        {
            writer.WriteStartObject();
            writer.WriteString("source_column", mapping.SourceColumn.Value);
            writer.WriteString("target_column", mapping.TargetColumn.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes the <c>auth_objects</c> section: the auth EdOrg-to-EdOrg table and the four people
    /// auth view definitions. Emitted only when the auth EdOrg hierarchy is present, mirroring the
    /// gating in <c>RelationalModelDdlEmitter.EmitAuthTable</c> / <c>EmitPeopleAuthViews</c>.
    /// </summary>
    /// <remarks>
    /// The auth table shape (columns, types, PK) and the four people auth views are hand-emitted
    /// hardcoded objects in the DDL emitter, not derived from the relational model. They must
    /// therefore be hardcoded here too. Any change to the auth table columns/PK or to a view's
    /// joins/output columns in <see cref="EdFi.DataManagementService.Backend.External.AuthNames"/>
    /// or in the DDL emitter MUST be mirrored here so this snapshot and the SQL goldens move
    /// together — per the DMS-1096 acceptance criterion requiring both snapshots to detect auth
    /// object definition changes.
    /// </remarks>
    private static void WriteAuthObjects(
        Utf8JsonWriter writer,
        AuthEdOrgHierarchy? authHierarchy,
        IReadOnlyList<ConcreteResourceModel> concreteResources
    )
    {
        if (authHierarchy is not { EntitiesInNameOrder.Count: > 0 })
        {
            return;
        }

        writer.WritePropertyName("auth_objects");
        writer.WriteStartObject();

        WriteAuthTable(writer);

        if (HasAllPeopleAuthViewAssociations(concreteResources))
        {
            WritePeopleAuthViews(writer);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes the <c>auth_objects.table</c> entry for
    /// <c>auth.EducationOrganizationIdToEducationOrganizationId</c>. Mirrors the columns/PK
    /// emitted by <c>RelationalModelDdlEmitter.EmitAuthTable</c>.
    /// </summary>
    private static void WriteAuthTable(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("table");
        writer.WriteStartObject();

        writer.WritePropertyName("table");
        WriteTableReference(writer, AuthNames.EdOrgIdToEdOrgId);

        writer.WritePropertyName("columns");
        writer.WriteStartArray();
        WriteAuthColumn(writer, AuthNames.SourceEdOrgId.Value);
        WriteAuthColumn(writer, AuthNames.TargetEdOrgId.Value);
        writer.WriteEndArray();

        writer.WritePropertyName("primary_key");
        writer.WriteStartObject();
        writer.WriteString("name", "PK_EducationOrganizationIdToEducationOrganizationId");
        writer.WritePropertyName("columns");
        writer.WriteStartArray();
        writer.WriteStringValue(AuthNames.SourceEdOrgId.Value);
        writer.WriteStringValue(AuthNames.TargetEdOrgId.Value);
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes one auth-table column entry. Auth table columns are uniformly <c>bigint NOT NULL</c>
    /// per the DDL emitter; if that ever changes here, the SQL goldens must move too.
    /// </summary>
    private static void WriteAuthColumn(Utf8JsonWriter writer, string columnName)
    {
        writer.WriteStartObject();
        writer.WriteString("name", columnName);
        writer.WriteString("type", "bigint");
        writer.WriteBoolean("is_nullable", false);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Returns whether the model contains all five association resources joined by the people auth
    /// views. Matches the guard in <c>RelationalModelDdlEmitter.EmitPeopleAuthViews</c>; when any
    /// is missing, the DDL emitter skips view emission and so does this manifest section.
    /// </summary>
    private static bool HasAllPeopleAuthViewAssociations(
        IReadOnlyList<ConcreteResourceModel> concreteResources
    )
    {
        string[] requiredResourceNames =
        [
            "StudentSchoolAssociation",
            "StudentContactAssociation",
            "StaffEducationOrganizationAssignmentAssociation",
            "StaffEducationOrganizationEmploymentAssociation",
            "StudentEducationOrganizationResponsibilityAssociation",
        ];

        return Array.TrueForAll(
            requiredResourceNames,
            requiredResourceName =>
                concreteResources.Any(r =>
                    DataModelConstants.IsCoreProjectName(r.ResourceKey.Resource.ProjectName)
                    && r.ResourceKey.Resource.ResourceName == requiredResourceName
                )
        );
    }

    /// <summary>
    /// Writes the <c>auth_objects.views</c> array describing the four people auth views in the
    /// same alphabetical order they are emitted by the DDL emitter.
    /// </summary>
    private static void WritePeopleAuthViews(Utf8JsonWriter writer)
    {
        var edfi = new DbSchemaName("edfi");
        var authEdOrgTable = AuthNames.EdOrgIdToEdOrgId;
        var sourceCol = AuthNames.SourceEdOrgId.Value;
        var targetCol = AuthNames.TargetEdOrgId.Value;
        var schoolIdUnified = AuthNames.SchoolIdUnified.Value;
        var studentDocId = AuthNames.StudentDocumentId.Value;
        var contactDocId = AuthNames.ContactDocumentId.Value;
        var staffDocId = AuthNames.StaffDocumentId.Value;
        var edOrgEdOrgId = AuthNames.EdOrgEdOrgId.Value;
        var edOrgAlias = "edOrg";

        writer.WritePropertyName("views");
        writer.WriteStartArray();

        // 1. EducationOrganizationIdToContactDocumentId — DISTINCT, single arm
        WriteAuthView(
            writer,
            viewName: "EducationOrganizationIdToContactDocumentId",
            armsSetOperator: "NONE",
            arms:
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
        // DISTINCT unnecessary. Pinning `UNION` here flips the manifest if a future change swaps it.
        WriteAuthView(
            writer,
            viewName: "EducationOrganizationIdToStaffDocumentId",
            armsSetOperator: "UNION",
            arms:
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
        WriteAuthView(
            writer,
            viewName: "EducationOrganizationIdToStudentDocumentId",
            armsSetOperator: "NONE",
            arms:
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
        WriteAuthView(
            writer,
            viewName: "EducationOrganizationIdToStudentDocumentIdThroughResponsibility",
            armsSetOperator: "NONE",
            arms:
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

        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes a single <c>auth_objects.views[]</c> entry: the view's qualified name, the set-operator
    /// joining its arms, and the ordered list of arms.
    /// </summary>
    private static void WriteAuthView(
        Utf8JsonWriter writer,
        string viewName,
        string armsSetOperator,
        IReadOnlyList<AuthViewArm> arms
    )
    {
        writer.WriteStartObject();

        writer.WritePropertyName("view");
        WriteTableReference(writer, new DbTableName(AuthNames.AuthSchema, viewName));

        // `arms_set_operator` captures the deduplication semantic between arms ("UNION", "UNION_ALL",
        // or "NONE" for single-arm views). Without it, swapping `UNION` for `UNION ALL` in the DDL
        // emitter would be invisible to the manifest goldens.
        writer.WriteString("arms_set_operator", armsSetOperator);

        writer.WritePropertyName("arms");
        writer.WriteStartArray();
        foreach (var arm in arms)
        {
            WriteAuthViewArm(writer, arm);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a single auth view arm including its source table/alias, output columns, and joins.
    /// </summary>
    private static void WriteAuthViewArm(Utf8JsonWriter writer, AuthViewArm arm)
    {
        writer.WriteStartObject();

        writer.WriteBoolean("select_distinct", arm.SelectDistinct);

        writer.WritePropertyName("source");
        writer.WriteStartObject();
        writer.WriteString("alias", arm.SourceAlias);
        writer.WritePropertyName("table");
        WriteTableReference(writer, arm.SourceTable);
        writer.WriteEndObject();

        writer.WritePropertyName("output_columns");
        writer.WriteStartArray();
        foreach (var column in arm.OutputColumns)
        {
            writer.WriteStartObject();
            writer.WriteString("alias", column.Alias);
            writer.WriteString("column", column.ColumnName);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("joins");
        writer.WriteStartArray();
        foreach (var join in arm.Joins)
        {
            writer.WriteStartObject();
            writer.WriteString("alias", join.Alias);
            writer.WritePropertyName("table");
            WriteTableReference(writer, join.Table);
            writer.WritePropertyName("on");
            writer.WriteStartArray();
            foreach (var predicate in join.On)
            {
                writer.WriteStartObject();
                writer.WriteString("left_alias", predicate.LeftAlias);
                writer.WriteString("left_column", predicate.LeftColumn);
                writer.WriteString("right_alias", predicate.RightAlias);
                writer.WriteString("right_column", predicate.RightColumn);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private sealed record AuthViewArm(
        bool SelectDistinct,
        string SourceAlias,
        DbTableName SourceTable,
        IReadOnlyList<AuthViewOutputColumn> OutputColumns,
        IReadOnlyList<AuthViewJoin> Joins
    );

    private sealed record AuthViewOutputColumn(string Alias, string ColumnName);

    private sealed record AuthViewJoin(
        string Alias,
        DbTableName Table,
        IReadOnlyList<AuthViewJoinPredicate> On
    );

    private sealed record AuthViewJoinPredicate(
        string LeftAlias,
        string LeftColumn,
        string RightAlias,
        string RightColumn
    );

    /// <summary>
    /// Writes the optional <c>resource_details</c> array for the requested resources.
    /// </summary>
    private static void WriteResourceDetails(
        Utf8JsonWriter writer,
        IReadOnlyList<ConcreteResourceModel> resources,
        IReadOnlySet<QualifiedResourceName> detailedResources,
        Func<QualifiedResourceName, IReadOnlyList<ExtensionSite>>? extensionSitesProvider
    )
    {
        writer.WritePropertyName("resource_details");
        writer.WriteStartArray();

        foreach (var resource in resources)
        {
            if (!detailedResources.Contains(resource.ResourceKey.Resource))
            {
                continue;
            }

            var extensionSites = extensionSitesProvider?.Invoke(resource.ResourceKey.Resource) ?? [];
            WriteResourceDetail(writer, resource, extensionSites);
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes a single resource detail object including tables, bindings, and extension sites.
    /// </summary>
    private static void WriteResourceDetail(
        Utf8JsonWriter writer,
        ConcreteResourceModel resource,
        IReadOnlyList<ExtensionSite> extensionSites
    )
    {
        var model = resource.RelationalModel;

        writer.WriteStartObject();
        WriteResource(writer, model.Resource);
        writer.WriteString("physical_schema", model.PhysicalSchema.Value);
        writer.WriteString("storage_kind", model.StorageKind.ToString());

        writer.WritePropertyName("tables");
        writer.WriteStartArray();
        if (model.StorageKind != ResourceStorageKind.SharedDescriptorTable)
        {
            var descriptorFkDeduplicationsByTable = BuildDescriptorForeignKeyDeduplicationLookup(
                model.DescriptorForeignKeyDeduplications
            );

            foreach (var table in model.TablesInDependencyOrder)
            {
                WriteTable(writer, table, descriptorFkDeduplicationsByTable);
            }
        }
        writer.WriteEndArray();

        writer.WritePropertyName("key_unification_equality_constraints");
        WriteKeyUnificationEqualityConstraintDiagnostics(writer, model.KeyUnificationEqualityConstraints);

        writer.WritePropertyName("document_reference_bindings");
        writer.WriteStartArray();
        foreach (var binding in model.DocumentReferenceBindings)
        {
            WriteDocumentReferenceBinding(writer, binding);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("descriptor_edge_sources");
        writer.WriteStartArray();
        foreach (var edge in model.DescriptorEdgeSources)
        {
            WriteDescriptorEdge(writer, edge);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("extension_sites");
        writer.WriteStartArray();
        foreach (var site in extensionSites)
        {
            WriteExtensionSite(writer, site);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a single document reference binding with its identity bindings.
    /// </summary>
    private static void WriteDocumentReferenceBinding(Utf8JsonWriter writer, DocumentReferenceBinding binding)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("is_identity_component", binding.IsIdentityComponent);
        writer.WriteString("reference_object_path", binding.ReferenceObjectPath.Canonical);
        writer.WritePropertyName("table");
        WriteTableReference(writer, binding.Table);
        writer.WriteString("fk_column", binding.FkColumn.Value);
        writer.WritePropertyName("target_resource");
        WriteResourceReference(writer, binding.TargetResource);
        writer.WritePropertyName("identity_bindings");
        writer.WriteStartArray();
        foreach (var identityBinding in binding.IdentityBindings)
        {
            WriteReferenceIdentityBinding(writer, identityBinding);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a single reference identity binding.
    /// </summary>
    private static void WriteReferenceIdentityBinding(
        Utf8JsonWriter writer,
        ReferenceIdentityBinding identityBinding
    )
    {
        writer.WriteStartObject();
        writer.WriteString("identity_json_path", identityBinding.IdentityJsonPath.Canonical);
        writer.WriteString("reference_json_path", identityBinding.ReferenceJsonPath.Canonical);
        writer.WriteString("column", identityBinding.Column.Value);
        writer.WriteEndObject();
    }
}
