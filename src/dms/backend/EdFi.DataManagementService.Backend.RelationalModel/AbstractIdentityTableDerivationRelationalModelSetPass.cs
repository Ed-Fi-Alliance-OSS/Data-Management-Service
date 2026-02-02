// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Derives abstract identity table models for abstract resources in the effective schema set.
/// </summary>
public sealed class AbstractIdentityTableDerivationRelationalModelSetPass : IRelationalModelSetPass
{
    private const string DiscriminatorColumnLabel = "Discriminator";
    private static readonly DbSchemaName _dmsSchemaName = new("dms");
    private static readonly DbTableName _documentTableName = new(_dmsSchemaName, "Document");

    /// <summary>
    /// Executes abstract identity table derivation across all abstract resources.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var concreteModelsByResource = context.ConcreteResourcesInNameOrder.ToDictionary(model =>
            model.ResourceKey.Resource
        );
        var concreteMetadataByResource = BuildConcreteMetadata(context, concreteModelsByResource);

        foreach (var project in context.EnumerateProjectsInEndpointOrder())
        {
            var projectSchema = project.EffectiveProject.ProjectSchema;
            var projectName = project.ProjectSchema.ProjectName;

            if (projectSchema["abstractResources"] is not JsonObject abstractResources)
            {
                continue;
            }

            foreach (
                var abstractEntry in OrderResourceSchemas(
                    abstractResources,
                    "projectSchema.abstractResources",
                    requireNonEmptyKey: true
                )
            )
            {
                var abstractResource = new QualifiedResourceName(projectName, abstractEntry.ResourceName);
                var identityJsonPaths = ExtractIdentityJsonPaths(
                    abstractEntry.ResourceSchema,
                    abstractResource
                );
                var members = concreteMetadataByResource
                    .Values.Where(metadata =>
                        metadata.IsSubclass
                        && string.Equals(
                            metadata.SuperclassProjectName,
                            abstractResource.ProjectName,
                            StringComparison.Ordinal
                        )
                        && string.Equals(
                            metadata.SuperclassResourceName,
                            abstractResource.ResourceName,
                            StringComparison.Ordinal
                        )
                    )
                    .OrderBy(metadata => metadata.Resource.ResourceName, StringComparer.Ordinal)
                    .ToArray();

                if (members.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Abstract resource '{FormatResource(abstractResource)}' has no concrete members."
                    );
                }

                var identityColumns = BuildIdentityColumns(
                    identityJsonPaths,
                    abstractResource,
                    members,
                    context
                );
                var columns = new List<DbColumnModel>(1 + identityColumns.Count + 1)
                {
                    BuildDocumentIdColumn(),
                };
                columns.AddRange(identityColumns);
                columns.Add(BuildDiscriminatorColumn());

                var tableName = new DbTableName(
                    project.ProjectSchema.PhysicalSchema,
                    $"{RelationalNameConventions.ToPascalCase(abstractEntry.ResourceName)}Identity"
                );
                var jsonScope = JsonPathExpressionCompiler.FromSegments([]);
                var key = new TableKey(
                    new[]
                    {
                        new DbKeyColumn(
                            RelationalNameConventions.DocumentIdColumnName,
                            ColumnKind.ParentKeyPart
                        ),
                    }
                );
                var constraints = BuildIdentityTableConstraints(tableName, identityColumns);
                var resourceKeyEntry = context.GetResourceKeyEntry(abstractResource);
                var tableModel = new DbTableModel(tableName, jsonScope, key, columns.ToArray(), constraints);

                context.AbstractIdentityTablesInNameOrder.Add(
                    new AbstractIdentityTableInfo(resourceKeyEntry, tableModel)
                );
            }
        }
    }

    private static Dictionary<QualifiedResourceName, ConcreteResourceMetadata> BuildConcreteMetadata(
        RelationalModelSetBuilderContext context,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> concreteModelsByResource
    )
    {
        Dictionary<QualifiedResourceName, ConcreteResourceMetadata> metadataByResource = new();

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var resourceSchema = resourceContext.ResourceSchema;
            var resourceName = resourceContext.ResourceName;
            var projectName = resourceContext.Project.ProjectSchema.ProjectName;
            var resource = new QualifiedResourceName(projectName, resourceName);

            if (IsResourceExtension(resourceSchema, resource))
            {
                continue;
            }

            if (!concreteModelsByResource.TryGetValue(resource, out var model))
            {
                throw new InvalidOperationException(
                    $"Concrete resource model not found for resource '{FormatResource(resource)}'."
                );
            }

            var jsonSchemaForInsert = RequireObject(
                resourceSchema["jsonSchemaForInsert"],
                "jsonSchemaForInsert"
            );
            var identityJsonPaths = ExtractIdentityJsonPaths(resourceSchema, resource);
            var isSubclass = RequireBoolean(resourceSchema, "isSubclass");
            var subclassType = TryGetOptionalString(resourceSchema, "subclassType");
            var superclassProjectName = TryGetOptionalString(resourceSchema, "superclassProjectName");
            var superclassResourceName = TryGetOptionalString(resourceSchema, "superclassResourceName");
            var superclassIdentityJsonPath = TryGetOptionalString(
                resourceSchema,
                "superclassIdentityJsonPath"
            );
            JsonPathExpression? superclassIdentityPath = superclassIdentityJsonPath is null
                ? null
                : JsonPathExpressionCompiler.Compile(superclassIdentityJsonPath);
            var decimalInfos = ExtractDecimalPropertyValidationInfos(resourceSchema);

            if (isSubclass)
            {
                if (string.IsNullOrWhiteSpace(superclassProjectName))
                {
                    throw new InvalidOperationException(
                        $"Expected superclassProjectName to be present for subclass resource "
                            + $"'{FormatResource(resource)}'."
                    );
                }

                if (string.IsNullOrWhiteSpace(superclassResourceName))
                {
                    throw new InvalidOperationException(
                        $"Expected superclassResourceName to be present for subclass resource "
                            + $"'{FormatResource(resource)}'."
                    );
                }
            }

            metadataByResource[resource] = new ConcreteResourceMetadata(
                resource,
                model.RelationalModel,
                jsonSchemaForInsert,
                identityJsonPaths,
                isSubclass,
                subclassType,
                superclassProjectName,
                superclassResourceName,
                superclassIdentityPath,
                decimalInfos
            );
        }

        return metadataByResource;
    }

    private static IReadOnlyList<DbColumnModel> BuildIdentityColumns(
        IReadOnlyList<JsonPathExpression> identityJsonPaths,
        QualifiedResourceName abstractResource,
        IReadOnlyList<ConcreteResourceMetadata> members,
        RelationalModelSetBuilderContext context
    )
    {
        List<DbColumnModel> columns = new(identityJsonPaths.Count);

        foreach (var identityPath in identityJsonPaths)
        {
            ColumnSignature? signature = null;

            foreach (var member in members)
            {
                var mappedPath = MapIdentityPathForMember(member, identityPath, abstractResource);
                var memberSignature = ResolveColumnSignature(member, mappedPath, context);

                if (signature is null)
                {
                    signature = memberSignature;
                    continue;
                }

                if (signature != memberSignature)
                {
                    throw new InvalidOperationException(
                        $"Abstract identity path '{identityPath.Canonical}' for resource "
                            + $"'{FormatResource(abstractResource)}' has inconsistent column types. "
                            + $"Expected {FormatSignature(signature)} but member "
                            + $"'{FormatResource(member.Resource)}' provides {FormatSignature(memberSignature)}."
                    );
                }
            }

            if (signature is null)
            {
                throw new InvalidOperationException(
                    $"Abstract identity path '{identityPath.Canonical}' for resource "
                        + $"'{FormatResource(abstractResource)}' did not resolve a column signature."
                );
            }

            columns.Add(
                new DbColumnModel(
                    BuildColumnName(identityPath),
                    signature.Kind,
                    signature.ScalarType,
                    IsNullable: false,
                    identityPath,
                    signature.TargetResource
                )
            );
        }

        return columns;
    }

    private static JsonPathExpression MapIdentityPathForMember(
        ConcreteResourceMetadata member,
        JsonPathExpression abstractIdentityPath,
        QualifiedResourceName abstractResource
    )
    {
        if (!member.IsSubclass)
        {
            throw new InvalidOperationException(
                $"Concrete member '{FormatResource(member.Resource)}' is not marked as a subclass."
            );
        }

        var isAssociation = string.Equals(member.SubclassType, "association", StringComparison.Ordinal);

        if (!isAssociation && member.SuperclassIdentityJsonPath is not null)
        {
            if (member.IdentityJsonPaths.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Subclass resource '{FormatResource(member.Resource)}' must declare exactly one "
                        + "identityJsonPath when using superclassIdentityJsonPath."
                );
            }

            var superclassIdentityPath = member.SuperclassIdentityJsonPath.Value;

            if (
                !string.Equals(
                    superclassIdentityPath.Canonical,
                    abstractIdentityPath.Canonical,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidOperationException(
                    $"Subclass resource '{FormatResource(member.Resource)}' declares "
                        + $"superclassIdentityJsonPath '{superclassIdentityPath.Canonical}', "
                        + $"but abstract resource '{FormatResource(abstractResource)}' requires identity "
                        + $"path '{abstractIdentityPath.Canonical}'."
                );
            }

            return member.IdentityJsonPaths[0];
        }

        foreach (var identityPath in member.IdentityJsonPaths)
        {
            if (
                string.Equals(
                    identityPath.Canonical,
                    abstractIdentityPath.Canonical,
                    StringComparison.Ordinal
                )
            )
            {
                return identityPath;
            }
        }

        throw new InvalidOperationException(
            $"Abstract identity path '{abstractIdentityPath.Canonical}' for resource "
                + $"'{FormatResource(abstractResource)}' was not found in identityJsonPaths for member "
                + $"'{FormatResource(member.Resource)}'."
        );
    }

    private static ColumnSignature ResolveColumnSignature(
        ConcreteResourceMetadata member,
        JsonPathExpression mappedIdentityPath,
        RelationalModelSetBuilderContext context
    )
    {
        var column = member.Model.Root.Columns.FirstOrDefault(col =>
            col.SourceJsonPath is { } sourcePath
            && string.Equals(sourcePath.Canonical, mappedIdentityPath.Canonical, StringComparison.Ordinal)
        );

        if (column is not null)
        {
            if (column.ScalarType is null)
            {
                throw new InvalidOperationException(
                    $"Identity path '{mappedIdentityPath.Canonical}' resolved to a non-scalar column on "
                        + $"resource '{FormatResource(member.Resource)}'."
                );
            }

            return new ColumnSignature(column.Kind, column.ScalarType, column.TargetResource);
        }

        var descriptorPaths = context.GetAllDescriptorPathsForResource(member.Resource);

        if (descriptorPaths.TryGetValue(mappedIdentityPath.Canonical, out var descriptorPath))
        {
            return new ColumnSignature(
                ColumnKind.DescriptorFk,
                new RelationalScalarType(ScalarKind.Int64),
                descriptorPath.DescriptorResource
            );
        }

        var schemaNode = ResolveSchemaForPath(
            member.JsonSchemaForInsert,
            mappedIdentityPath,
            member.Resource,
            "Identity"
        );
        var schemaKind = JsonSchemaTraversalConventions.DetermineSchemaKind(
            schemaNode,
            mappedIdentityPath.Canonical,
            includeTypePathInErrors: true
        );

        if (schemaKind != SchemaKind.Scalar)
        {
            throw new InvalidOperationException(
                $"Identity path '{mappedIdentityPath.Canonical}' on resource "
                    + $"'{FormatResource(member.Resource)}' must resolve to a scalar schema."
            );
        }

        var scalarType = ResolveScalarType(schemaNode, mappedIdentityPath, member);

        return new ColumnSignature(ColumnKind.Scalar, scalarType, null);
    }

    private static DbColumnModel BuildDiscriminatorColumn()
    {
        return new DbColumnModel(
            new DbColumnName(DiscriminatorColumnLabel),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, 256),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
    }

    private static DbColumnModel BuildDocumentIdColumn()
    {
        return new DbColumnModel(
            RelationalNameConventions.DocumentIdColumnName,
            ColumnKind.ParentKeyPart,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
    }

    private static DbColumnName BuildColumnName(JsonPathExpression identityPath)
    {
        List<string> segments = [];

        foreach (var segment in identityPath.Segments)
        {
            switch (segment)
            {
                case JsonPathSegment.Property property:
                    segments.Add(property.Name);
                    break;
                case JsonPathSegment.AnyArrayElement:
                    throw new InvalidOperationException(
                        $"Identity path '{identityPath.Canonical}' must not include array segments."
                    );
            }
        }

        if (segments.Count == 0)
        {
            throw new InvalidOperationException(
                $"Identity path '{identityPath.Canonical}' must include at least one property segment."
            );
        }

        StringBuilder builder = new();

        foreach (var segment in segments)
        {
            builder.Append(RelationalNameConventions.ToPascalCase(segment));
        }

        return new DbColumnName(builder.ToString());
    }

    private static IReadOnlyList<TableConstraint> BuildIdentityTableConstraints(
        DbTableName tableName,
        IReadOnlyList<DbColumnModel> identityColumns
    )
    {
        List<DbColumnName> uniqueColumns = [RelationalNameConventions.DocumentIdColumnName];
        uniqueColumns.AddRange(identityColumns.Select(column => column.ColumnName));

        var uniqueName =
            $"UX_{tableName.Name}_{string.Join("_", uniqueColumns.Select(column => column.Value))}";
        var fkName = RelationalNameConventions.ForeignKeyName(
            tableName.Name,
            new[] { RelationalNameConventions.DocumentIdColumnName }
        );

        return new TableConstraint[]
        {
            new TableConstraint.Unique(uniqueName, uniqueColumns.ToArray()),
            new TableConstraint.ForeignKey(
                fkName,
                new[] { RelationalNameConventions.DocumentIdColumnName },
                _documentTableName,
                new[] { RelationalNameConventions.DocumentIdColumnName },
                OnDelete: ReferentialAction.Cascade
            ),
        };
    }

    private static IReadOnlyList<JsonPathExpression> ExtractIdentityJsonPaths(
        JsonObject resourceSchema,
        QualifiedResourceName resource
    )
    {
        if (resourceSchema["identityJsonPaths"] is not JsonArray identityJsonPaths)
        {
            throw new InvalidOperationException(
                $"Expected identityJsonPaths to be present on resource '{FormatResource(resource)}'."
            );
        }

        List<JsonPathExpression> compiledPaths = new(identityJsonPaths.Count);
        HashSet<string> seenPaths = new(StringComparer.Ordinal);
        HashSet<string> duplicatePaths = new(StringComparer.Ordinal);

        foreach (var identityJsonPath in identityJsonPaths)
        {
            if (identityJsonPath is null)
            {
                throw new InvalidOperationException(
                    "Expected identityJsonPaths to not contain null entries, invalid ApiSchema."
                );
            }

            var identityPath = identityJsonPath.GetValue<string>();
            var compiledPath = JsonPathExpressionCompiler.Compile(identityPath);
            compiledPaths.Add(compiledPath);

            if (!seenPaths.Add(compiledPath.Canonical))
            {
                duplicatePaths.Add(compiledPath.Canonical);
            }
        }

        if (duplicatePaths.Count > 0)
        {
            var duplicates = string.Join(", ", duplicatePaths.OrderBy(path => path, StringComparer.Ordinal));

            throw new InvalidOperationException(
                $"identityJsonPaths on abstract resource '{FormatResource(resource)}' contains duplicate JSONPaths: {duplicates}."
            );
        }

        return compiledPaths.ToArray();
    }

    private static Dictionary<string, DecimalPropertyValidationInfo> ExtractDecimalPropertyValidationInfos(
        JsonObject resourceSchema
    )
    {
        Dictionary<string, DecimalPropertyValidationInfo> decimalInfosByPath = new(StringComparer.Ordinal);

        if (resourceSchema["decimalPropertyValidationInfos"] is not JsonArray decimalInfos)
        {
            return decimalInfosByPath;
        }

        foreach (var decimalInfo in decimalInfos)
        {
            if (decimalInfo is null)
            {
                throw new InvalidOperationException(
                    "Expected decimalPropertyValidationInfos to not contain null entries, invalid ApiSchema."
                );
            }

            if (decimalInfo is not JsonObject decimalInfoObject)
            {
                throw new InvalidOperationException(
                    "Expected decimalPropertyValidationInfos entries to be objects, invalid ApiSchema."
                );
            }

            var decimalPath = RequireString(decimalInfoObject, "path");
            var totalDigits = decimalInfoObject["totalDigits"]?.GetValue<short?>();
            var decimalPlaces = decimalInfoObject["decimalPlaces"]?.GetValue<short?>();
            var decimalJsonPath = JsonPathExpressionCompiler.Compile(decimalPath);

            if (
                !decimalInfosByPath.TryAdd(
                    decimalJsonPath.Canonical,
                    new DecimalPropertyValidationInfo(decimalJsonPath, totalDigits, decimalPlaces)
                )
            )
            {
                throw new InvalidOperationException(
                    $"Decimal validation info for '{decimalJsonPath.Canonical}' is already defined."
                );
            }
        }

        return decimalInfosByPath;
    }

    private static bool RequireBoolean(JsonObject node, string propertyName)
    {
        return node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<bool>(),
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a boolean, invalid ApiSchema."
            ),
        };
    }

    private static bool IsResourceExtension(JsonObject resourceSchema, QualifiedResourceName resource)
    {
        if (
            !resourceSchema.TryGetPropertyValue("isResourceExtension", out var resourceExtensionNode)
            || resourceExtensionNode is null
        )
        {
            throw new InvalidOperationException(
                $"Expected isResourceExtension to be on ResourceSchema for resource "
                    + $"'{FormatResource(resource)}', invalid ApiSchema."
            );
        }

        return resourceExtensionNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<bool>(),
            _ => throw new InvalidOperationException(
                $"Expected isResourceExtension to be a boolean for resource "
                    + $"'{FormatResource(resource)}', invalid ApiSchema."
            ),
        };
    }

    private static RelationalScalarType ResolveScalarType(
        JsonObject schema,
        JsonPathExpression sourcePath,
        ConcreteResourceMetadata member
    )
    {
        var schemaType = GetSchemaType(schema, sourcePath.Canonical);

        return schemaType switch
        {
            "string" => ResolveStringType(schema, sourcePath),
            "integer" => ResolveIntegerType(schema, sourcePath),
            "number" => ResolveDecimalType(sourcePath, member),
            "boolean" => new RelationalScalarType(ScalarKind.Boolean),
            _ => throw new InvalidOperationException(
                $"Unsupported scalar type '{schemaType}' at {sourcePath.Canonical}."
            ),
        };
    }

    private static RelationalScalarType ResolveStringType(JsonObject schema, JsonPathExpression sourcePath)
    {
        var format = GetOptionalString(schema, "format", sourcePath.Canonical);

        if (!string.IsNullOrWhiteSpace(format))
        {
            return format switch
            {
                "date" => new RelationalScalarType(ScalarKind.Date),
                "date-time" => new RelationalScalarType(ScalarKind.DateTime),
                "time" => new RelationalScalarType(ScalarKind.Time),
                _ => BuildStringType(schema, sourcePath),
            };
        }

        return BuildStringType(schema, sourcePath);
    }

    private static RelationalScalarType BuildStringType(JsonObject schema, JsonPathExpression sourcePath)
    {
        if (!schema.TryGetPropertyValue("maxLength", out var maxLengthNode) || maxLengthNode is null)
        {
            throw new InvalidOperationException(
                $"String schema maxLength is required at {sourcePath.Canonical}."
            );
        }

        if (maxLengthNode is not JsonValue maxLengthValue)
        {
            throw new InvalidOperationException(
                $"Expected maxLength to be a number at {sourcePath.Canonical}."
            );
        }

        var maxLength = maxLengthValue.GetValue<int>();
        if (maxLength <= 0)
        {
            throw new InvalidOperationException(
                $"String schema maxLength must be positive at {sourcePath.Canonical}."
            );
        }

        return new RelationalScalarType(ScalarKind.String, maxLength);
    }

    private static RelationalScalarType ResolveIntegerType(JsonObject schema, JsonPathExpression sourcePath)
    {
        var format = GetOptionalString(schema, "format", sourcePath.Canonical);

        return format switch
        {
            "int64" => new RelationalScalarType(ScalarKind.Int64),
            _ => new RelationalScalarType(ScalarKind.Int32),
        };
    }

    private static RelationalScalarType ResolveDecimalType(
        JsonPathExpression sourcePath,
        ConcreteResourceMetadata member
    )
    {
        if (!member.DecimalPropertyValidationInfos.TryGetValue(sourcePath.Canonical, out var validationInfo))
        {
            throw new InvalidOperationException(
                $"Decimal property validation info is required for number properties at "
                    + $"{sourcePath.Canonical}."
            );
        }

        if (validationInfo.TotalDigits is null || validationInfo.DecimalPlaces is null)
        {
            throw new InvalidOperationException(
                $"Decimal property validation info must include totalDigits and decimalPlaces at "
                    + $"{sourcePath.Canonical}."
            );
        }

        if (validationInfo.TotalDigits <= 0 || validationInfo.DecimalPlaces < 0)
        {
            throw new InvalidOperationException(
                $"Decimal property validation info must be positive for {sourcePath.Canonical}."
            );
        }

        if (validationInfo.DecimalPlaces > validationInfo.TotalDigits)
        {
            throw new InvalidOperationException(
                $"Decimal places cannot exceed total digits for {sourcePath.Canonical}."
            );
        }

        return new RelationalScalarType(
            ScalarKind.Decimal,
            Decimal: (validationInfo.TotalDigits.Value, validationInfo.DecimalPlaces.Value)
        );
    }

    private static string GetSchemaType(JsonObject schema, string path)
    {
        if (!schema.TryGetPropertyValue("type", out var typeNode) || typeNode is null)
        {
            throw new InvalidOperationException($"Schema type must be specified at {path}.");
        }

        return typeNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            _ => throw new InvalidOperationException($"Expected type to be a string at {path}.type."),
        };
    }

    private static string? GetOptionalString(JsonObject schema, string propertyName, string path)
    {
        if (!schema.TryGetPropertyValue(propertyName, out var valueNode) || valueNode is null)
        {
            return null;
        }

        return valueNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a string at {path}.{propertyName}."
            ),
        };
    }

    private static string FormatSignature(ColumnSignature signature)
    {
        var typeLabel = signature.Kind switch
        {
            ColumnKind.DescriptorFk when signature.TargetResource is { } descriptor =>
                $"Descriptor({FormatResource(descriptor)})",
            ColumnKind.DescriptorFk => "Descriptor",
            ColumnKind.Scalar => FormatScalarType(signature.ScalarType),
            _ => signature.Kind.ToString(),
        };

        return $"{signature.Kind}:{typeLabel}";
    }

    private static string FormatScalarType(RelationalScalarType scalarType)
    {
        return scalarType.Kind switch
        {
            ScalarKind.String when scalarType.MaxLength is not null =>
                $"String({scalarType.MaxLength.Value})",
            ScalarKind.Decimal when scalarType.Decimal is not null =>
                $"Decimal({scalarType.Decimal.Value.Precision},{scalarType.Decimal.Value.Scale})",
            _ => scalarType.Kind.ToString(),
        };
    }

    private sealed record ColumnSignature(
        ColumnKind Kind,
        RelationalScalarType ScalarType,
        QualifiedResourceName? TargetResource
    );

    private sealed record ConcreteResourceMetadata(
        QualifiedResourceName Resource,
        RelationalResourceModel Model,
        JsonObject JsonSchemaForInsert,
        IReadOnlyList<JsonPathExpression> IdentityJsonPaths,
        bool IsSubclass,
        string? SubclassType,
        string? SuperclassProjectName,
        string? SuperclassResourceName,
        JsonPathExpression? SuperclassIdentityJsonPath,
        IReadOnlyDictionary<string, DecimalPropertyValidationInfo> DecimalPropertyValidationInfos
    );
}
