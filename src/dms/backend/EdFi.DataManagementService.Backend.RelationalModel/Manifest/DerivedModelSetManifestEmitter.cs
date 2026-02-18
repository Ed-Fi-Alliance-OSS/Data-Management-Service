// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.RelationalModel.Manifest;

/// <summary>
/// Emits a deterministic <c>relational-model.manifest.json</c> from a <see cref="DerivedRelationalModelSet"/>.
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
            writer.WriteString("dialect", modelSet.Dialect.ToString());

            WriteProjects(writer, modelSet.ProjectSchemasInEndpointOrder);
            WriteResourcesSummary(writer, modelSet.ConcreteResourcesInNameOrder);
            WriteAbstractIdentityTables(writer, modelSet.AbstractIdentityTablesInNameOrder);
            WriteAbstractUnionViews(writer, modelSet.AbstractUnionViewsInNameOrder);
            WriteIndexes(writer, modelSet.IndexesInCreateOrder);
            WriteTriggers(writer, modelSet.TriggersInCreateOrder);

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
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(trigger),
                        "Unsupported trigger kind parameters type."
                    ),
                }
            );
            writer.WritePropertyName("key_columns");
            WriteColumnNameList(writer, trigger.KeyColumns);
            writer.WritePropertyName("identity_projection_columns");
            WriteColumnNameList(writer, trigger.IdentityProjectionColumns);

            var targetTable = trigger.Parameters switch
            {
                TriggerKindParameters.AbstractIdentityMaintenance a => (DbTableName?)a.TargetTable,
                TriggerKindParameters.IdentityPropagationFallback p => p.TargetTable,
                _ => null,
            };

            if (targetTable is { } tt)
            {
                writer.WritePropertyName("target_table");
                WriteTableReference(writer, tt);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

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
            foreach (var table in model.TablesInDependencyOrder)
            {
                WriteTable(writer, table);
            }
        }
        writer.WriteEndArray();

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
    /// Writes a <c>resource</c> property containing project and resource name.
    /// </summary>
    private static void WriteResource(Utf8JsonWriter writer, QualifiedResourceName resource)
    {
        writer.WritePropertyName("resource");
        writer.WriteStartObject();
        writer.WriteString("project_name", resource.ProjectName);
        writer.WriteString("resource_name", resource.ResourceName);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a table object with its key columns, columns, and constraints.
    /// </summary>
    private static void WriteTable(Utf8JsonWriter writer, DbTableModel table)
    {
        writer.WriteStartObject();
        writer.WriteString("schema", table.Table.Schema.Value);
        writer.WriteString("name", table.Table.Name);
        writer.WriteString("scope", table.JsonScope.Canonical);

        writer.WritePropertyName("key_columns");
        writer.WriteStartArray();
        foreach (var keyColumn in table.Key.Columns)
        {
            WriteKeyColumn(writer, keyColumn);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("columns");
        writer.WriteStartArray();
        foreach (var column in table.Columns)
        {
            WriteColumn(writer, column);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("constraints");
        writer.WriteStartArray();
        foreach (var constraint in table.Constraints)
        {
            WriteConstraint(writer, constraint);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a single key column object.
    /// </summary>
    private static void WriteKeyColumn(Utf8JsonWriter writer, DbKeyColumn keyColumn)
    {
        writer.WriteStartObject();
        writer.WriteString("name", keyColumn.ColumnName.Value);
        writer.WriteString("kind", keyColumn.Kind.ToString());
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a single column object with its type and source path.
    /// </summary>
    private static void WriteColumn(Utf8JsonWriter writer, DbColumnModel column)
    {
        writer.WriteStartObject();
        writer.WriteString("name", column.ColumnName.Value);
        writer.WriteString("kind", column.Kind.ToString());
        writer.WritePropertyName("type");
        WriteScalarType(writer, column.ScalarType);
        writer.WriteBoolean("is_nullable", column.IsNullable);
        writer.WritePropertyName("source_path");
        if (column.SourceJsonPath is { } sourcePath)
        {
            writer.WriteStringValue(sourcePath.Canonical);
        }
        else
        {
            writer.WriteNullValue();
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a scalar type object or null value.
    /// </summary>
    private static void WriteScalarType(Utf8JsonWriter writer, RelationalScalarType? scalarType)
    {
        if (scalarType is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("kind", scalarType.Kind.ToString());

        if (scalarType.MaxLength is not null)
        {
            writer.WriteNumber("max_length", scalarType.MaxLength.Value);
        }

        if (scalarType.Decimal is not null)
        {
            writer.WriteNumber("precision", scalarType.Decimal.Value.Precision);
            writer.WriteNumber("scale", scalarType.Decimal.Value.Scale);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a single table constraint (Unique, ForeignKey, or AllOrNoneNullability).
    /// </summary>
    private static void WriteConstraint(Utf8JsonWriter writer, TableConstraint constraint)
    {
        writer.WriteStartObject();

        switch (constraint)
        {
            case TableConstraint.Unique unique:
                writer.WriteString("kind", "Unique");
                writer.WriteString("name", unique.Name);
                writer.WritePropertyName("columns");
                WriteColumnNameList(writer, unique.Columns);
                break;
            case TableConstraint.ForeignKey foreignKey:
                writer.WriteString("kind", "ForeignKey");
                writer.WriteString("name", foreignKey.Name);
                writer.WritePropertyName("columns");
                WriteColumnNameList(writer, foreignKey.Columns);
                writer.WritePropertyName("target_table");
                WriteTableReference(writer, foreignKey.TargetTable);
                writer.WritePropertyName("target_columns");
                WriteColumnNameList(writer, foreignKey.TargetColumns);
                writer.WriteString("on_delete", foreignKey.OnDelete.ToString());
                writer.WriteString("on_update", foreignKey.OnUpdate.ToString());
                break;
            case TableConstraint.AllOrNoneNullability allOrNone:
                writer.WriteString("kind", "AllOrNoneNullability");
                writer.WriteString("name", allOrNone.Name);
                writer.WriteString("fk_column", allOrNone.FkColumn.Value);
                writer.WritePropertyName("dependent_columns");
                WriteColumnNameList(writer, allOrNone.DependentColumns);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(constraint),
                    constraint,
                    "Unknown table constraint type."
                );
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes an array of column name strings.
    /// </summary>
    private static void WriteColumnNameList(Utf8JsonWriter writer, IReadOnlyList<DbColumnName> columns)
    {
        writer.WriteStartArray();
        foreach (var column in columns)
        {
            writer.WriteStringValue(column.Value);
        }
        writer.WriteEndArray();
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
        writer.WriteString("reference_json_path", identityBinding.ReferenceJsonPath.Canonical);
        writer.WriteString("column", identityBinding.Column.Value);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a single descriptor edge source.
    /// </summary>
    private static void WriteDescriptorEdge(Utf8JsonWriter writer, DescriptorEdgeSource edge)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("is_identity_component", edge.IsIdentityComponent);
        writer.WriteString("descriptor_value_path", edge.DescriptorValuePath.Canonical);
        writer.WritePropertyName("table");
        WriteTableReference(writer, edge.Table);
        writer.WriteString("fk_column", edge.FkColumn.Value);
        writer.WritePropertyName("descriptor_resource");
        WriteResourceReference(writer, edge.DescriptorResource);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a single extension site with its project keys.
    /// </summary>
    private static void WriteExtensionSite(Utf8JsonWriter writer, ExtensionSite site)
    {
        writer.WriteStartObject();
        writer.WriteString("owning_scope", site.OwningScope.Canonical);
        writer.WriteString("extension_path", site.ExtensionPath.Canonical);
        writer.WritePropertyName("project_keys");
        writer.WriteStartArray();
        foreach (var projectKey in site.ProjectKeys)
        {
            writer.WriteStringValue(projectKey);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a table reference object with schema and name.
    /// </summary>
    private static void WriteTableReference(Utf8JsonWriter writer, DbTableName tableName)
    {
        writer.WriteStartObject();
        writer.WriteString("schema", tableName.Schema.Value);
        writer.WriteString("name", tableName.Name);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a resource reference object with project and resource name.
    /// </summary>
    private static void WriteResourceReference(Utf8JsonWriter writer, QualifiedResourceName resource)
    {
        writer.WriteStartObject();
        writer.WriteString("project_name", resource.ProjectName);
        writer.WriteString("resource_name", resource.ResourceName);
        writer.WriteEndObject();
    }
}
