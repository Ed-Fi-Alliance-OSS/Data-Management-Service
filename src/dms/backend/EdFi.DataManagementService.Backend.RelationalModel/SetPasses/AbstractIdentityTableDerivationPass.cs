// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Derives abstract identity table models for abstract resources in the effective schema set.
/// </summary>
public sealed class AbstractIdentityTableDerivationPass : IRelationalModelSetPass
{
    private const string DiscriminatorColumnLabel = "Discriminator";
    private const int DiscriminatorMaxLength = 256;
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

                ThrowIfDuplicateMemberResourceNames(members, abstractResource);

                var identityDerivations = BuildIdentityColumnDerivations(
                    identityJsonPaths,
                    abstractResource,
                    members,
                    context
                );
                var identityColumns = identityDerivations
                    .Select(derivation => derivation.IdentityColumn)
                    .ToArray();
                var columns = new List<DbColumnModel>(1 + identityColumns.Length + 1)
                {
                    BuildDocumentIdColumn(),
                };
                columns.AddRange(identityColumns);
                columns.Add(BuildDiscriminatorColumn());

                var rootBaseName = ResolveRootBaseName(abstractEntry.ResourceSchema, abstractResource);
                var tableName = new DbTableName(
                    project.ProjectSchema.PhysicalSchema,
                    $"{rootBaseName}Identity"
                );
                var jsonScope = JsonPathExpressionCompiler.FromSegments([]);
                var key = new TableKey(
                    ConstraintNaming.BuildPrimaryKeyName(tableName),
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

                var viewName = new DbTableName(project.ProjectSchema.PhysicalSchema, $"{rootBaseName}_View");
                var outputColumns = BuildViewOutputColumns(identityDerivations);
                var unionArms = BuildUnionArms(identityDerivations, members, context);

                context.AbstractUnionViewsInNameOrder.Add(
                    new AbstractUnionViewInfo(resourceKeyEntry, viewName, outputColumns, unionArms)
                );
            }
        }
    }

    /// <summary>
    /// Builds metadata for all concrete resources required to derive abstract identity tables, including
    /// subclass/superclass linkage and scalar validation inputs.
    /// </summary>
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

            var isSubclass = RequireBoolean(resourceSchema, "isSubclass");
            if (!isSubclass)
            {
                continue;
            }

            var jsonSchemaForInsert = RequireObject(
                resourceSchema["jsonSchemaForInsert"],
                "jsonSchemaForInsert"
            );
            var identityJsonPaths = ExtractIdentityJsonPaths(resourceSchema, resource);
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

            var superclassResource = new QualifiedResourceName(superclassProjectName, superclassResourceName);
            var superclassResourceKey = context.GetResourceKeyEntry(superclassResource);

            if (!superclassResourceKey.IsAbstractResource)
            {
                throw new InvalidOperationException(
                    $"Subclass resource '{FormatResource(resource)}' declares superclass "
                        + $"'{FormatResource(superclassResource)}' that is not abstract. "
                        + "Subclass-of-subclass is not permitted."
                );
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

    /// <summary>
    /// Builds identity column derivations for an abstract resource, including table/view output-column metadata
    /// and per-member source-column projections.
    /// </summary>
    private static IReadOnlyList<IdentityColumnDerivation> BuildIdentityColumnDerivations(
        IReadOnlyList<JsonPathExpression> identityJsonPaths,
        QualifiedResourceName abstractResource,
        IReadOnlyList<ConcreteResourceMetadata> members,
        RelationalModelSetBuilderContext context
    )
    {
        List<IdentityColumnDerivation> derivations = new(identityJsonPaths.Count);

        foreach (var identityPath in identityJsonPaths)
        {
            ColumnSignature? signature = null;
            List<DbColumnName> memberSourceColumns = new(members.Count);

            foreach (var member in members)
            {
                var mappedPath = MapIdentityPathForMember(member, identityPath, abstractResource);
                var resolution = ResolveColumnResolution(member, mappedPath, context);
                var memberSignature = resolution.Signature;

                if (signature is null)
                {
                    signature = memberSignature;
                }
                else if (signature != memberSignature)
                {
                    throw new InvalidOperationException(
                        $"Abstract identity path '{identityPath.Canonical}' for resource "
                            + $"'{FormatResource(abstractResource)}' has inconsistent column types. "
                            + $"Expected {FormatSignature(signature)} but member "
                            + $"'{FormatResource(member.Resource)}' provides {FormatSignature(memberSignature)}."
                    );
                }

                memberSourceColumns.Add(resolution.SourceColumnName);
            }

            if (signature is null)
            {
                throw new InvalidOperationException(
                    $"Abstract identity path '{identityPath.Canonical}' for resource "
                        + $"'{FormatResource(abstractResource)}' did not resolve a column signature."
                );
            }

            var identityColumn = new DbColumnModel(
                BuildColumnName(identityPath),
                signature.Kind,
                signature.ScalarType,
                IsNullable: false,
                identityPath,
                signature.TargetResource
            );

            derivations.Add(new IdentityColumnDerivation(identityColumn, memberSourceColumns.ToArray()));
        }

        return derivations;
    }

    /// <summary>
    /// Builds union-view output columns in the deterministic select-list order:
    /// <c>DocumentId</c>, abstract identity columns, then <c>Discriminator</c>.
    /// </summary>
    private static IReadOnlyList<AbstractUnionViewOutputColumn> BuildViewOutputColumns(
        IReadOnlyList<IdentityColumnDerivation> identityDerivations
    )
    {
        List<AbstractUnionViewOutputColumn> outputColumns = new(1 + identityDerivations.Count + 1)
        {
            new AbstractUnionViewOutputColumn(
                RelationalNameConventions.DocumentIdColumnName,
                new RelationalScalarType(ScalarKind.Int64),
                SourceJsonPath: null,
                TargetResource: null
            ),
        };

        foreach (var derivation in identityDerivations)
        {
            if (derivation.IdentityColumn.ScalarType is not { } scalarType)
            {
                throw new InvalidOperationException(
                    $"Identity column '{derivation.IdentityColumn.ColumnName.Value}' is missing a scalar type."
                );
            }

            outputColumns.Add(
                new AbstractUnionViewOutputColumn(
                    derivation.IdentityColumn.ColumnName,
                    scalarType,
                    derivation.IdentityColumn.SourceJsonPath,
                    derivation.IdentityColumn.TargetResource
                )
            );
        }

        outputColumns.Add(
            new AbstractUnionViewOutputColumn(
                new DbColumnName(DiscriminatorColumnLabel),
                new RelationalScalarType(ScalarKind.String, DiscriminatorMaxLength),
                SourceJsonPath: null,
                TargetResource: null
            )
        );

        return outputColumns;
    }

    /// <summary>
    /// Builds deterministic union-view arms ordered by member <c>ResourceName</c> using ordinal comparison.
    /// </summary>
    private static IReadOnlyList<AbstractUnionViewArm> BuildUnionArms(
        IReadOnlyList<IdentityColumnDerivation> identityDerivations,
        IReadOnlyList<ConcreteResourceMetadata> members,
        RelationalModelSetBuilderContext context
    )
    {
        var projectionCapacity = 1 + identityDerivations.Count + 1;
        List<AbstractUnionViewArm> arms = new(members.Count);

        for (var memberIndex = 0; memberIndex < members.Count; memberIndex++)
        {
            var member = members[memberIndex];
            var discriminatorValue = $"{member.Resource.ProjectName}:{member.Resource.ResourceName}";

            if (discriminatorValue.Length > DiscriminatorMaxLength)
            {
                throw new InvalidOperationException(
                    $"Discriminator value '{discriminatorValue}' exceeds max length "
                        + $"{DiscriminatorMaxLength} for resource '{FormatResource(member.Resource)}'."
                );
            }

            List<AbstractUnionViewProjectionExpression> projections = new(projectionCapacity)
            {
                new AbstractUnionViewProjectionExpression.SourceColumn(
                    RelationalNameConventions.DocumentIdColumnName
                ),
            };

            foreach (var derivation in identityDerivations)
            {
                projections.Add(
                    new AbstractUnionViewProjectionExpression.SourceColumn(
                        derivation.MemberSourceColumnsInMemberOrder[memberIndex]
                    )
                );
            }

            projections.Add(new AbstractUnionViewProjectionExpression.StringLiteral(discriminatorValue));

            arms.Add(
                new AbstractUnionViewArm(
                    context.GetResourceKeyEntry(member.Resource),
                    member.Model.Root.Table,
                    projections.ToArray()
                )
            );
        }

        return arms;
    }

    /// <summary>
    /// Fails fast when multiple concrete members of the same abstract resource share a <c>ResourceName</c>
    /// across different projects.
    /// </summary>
    private static void ThrowIfDuplicateMemberResourceNames(
        IReadOnlyList<ConcreteResourceMetadata> members,
        QualifiedResourceName abstractResource
    )
    {
        var duplicates = members
            .GroupBy(member => member.Resource.ResourceName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();

        if (duplicates.Length == 0)
        {
            return;
        }

        var details = string.Join(
            "; ",
            duplicates.Select(group =>
            {
                var membersWithName = string.Join(
                    ", ",
                    group
                        .Select(member => FormatResource(member.Resource))
                        .OrderBy(label => label, StringComparer.Ordinal)
                );
                return $"'{group.Key}' ({membersWithName})";
            })
        );

        throw new InvalidOperationException(
            $"Abstract resource '{FormatResource(abstractResource)}' has duplicate member "
                + $"ResourceName values across projects: {details}."
        );
    }

    /// <summary>
    /// Maps an abstract identity path to the corresponding identity path on a concrete member, honoring
    /// <c>superclassIdentityJsonPath</c> when present.
    /// </summary>
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

        if (member.SuperclassIdentityJsonPath is not null)
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

    /// <summary>
    /// Resolves the column signature and source column for a member resource at the given identity path,
    /// consulting both the derived table model and descriptor path maps.
    /// </summary>
    private static ColumnResolution ResolveColumnResolution(
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
            if (column.IsNullable)
            {
                throw new InvalidOperationException(
                    $"Identity path '{mappedIdentityPath.Canonical}' resolved to nullable source column "
                        + $"'{column.ColumnName.Value}' on resource '{FormatResource(member.Resource)}'."
                );
            }

            if (column.ScalarType is null)
            {
                throw new InvalidOperationException(
                    $"Identity path '{mappedIdentityPath.Canonical}' resolved to a non-scalar column on "
                        + $"resource '{FormatResource(member.Resource)}'."
                );
            }

            return new ColumnResolution(
                new ColumnSignature(column.Kind, column.ScalarType, column.TargetResource),
                column.ColumnName
            );
        }

        var descriptorPaths = context.GetAllDescriptorPathsForResource(member.Resource);

        if (descriptorPaths.TryGetValue(mappedIdentityPath.Canonical, out var descriptorPath))
        {
            return new ColumnResolution(
                new ColumnSignature(
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    descriptorPath.DescriptorResource
                ),
                BuildColumnName(mappedIdentityPath)
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

        var scalarType = RelationalScalarTypeResolver.ResolveScalarType(
            schemaNode,
            mappedIdentityPath,
            member.DecimalPropertyValidationInfos
        );

        return new ColumnResolution(
            new ColumnSignature(ColumnKind.Scalar, scalarType, null),
            BuildColumnName(mappedIdentityPath)
        );
    }

    /// <summary>
    /// Builds the discriminator column used to record the concrete member type in the abstract identity table.
    /// </summary>
    private static DbColumnModel BuildDiscriminatorColumn()
    {
        return new DbColumnModel(
            new DbColumnName(DiscriminatorColumnLabel),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, DiscriminatorMaxLength),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
    }

    /// <summary>
    /// Builds the <c>DocumentId</c> key column used by abstract identity tables.
    /// </summary>
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

    /// <summary>
    /// Builds a deterministic physical column name from an identity JSONPath by PascalCasing its property
    /// segments.
    /// </summary>
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

    /// <summary>
    /// Builds the unique and FK constraints for an abstract identity table.
    /// </summary>
    private static IReadOnlyList<TableConstraint> BuildIdentityTableConstraints(
        DbTableName tableName,
        IReadOnlyList<DbColumnModel> identityColumns
    )
    {
        List<DbColumnName> uniqueColumns = [RelationalNameConventions.DocumentIdColumnName];
        uniqueColumns.AddRange(identityColumns.Select(column => column.ColumnName));

        var uniqueName = ConstraintNaming.BuildNaturalKeyUniqueName(tableName);
        var fkName = ConstraintNaming.BuildForeignKeyName(tableName, ConstraintNaming.DocumentToken);

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

    /// <summary>
    /// Resolves the root base name for an abstract resource, applying <c>relational.rootTableNameOverride</c>
    /// when present.
    /// </summary>
    private static string ResolveRootBaseName(JsonObject resourceSchema, QualifiedResourceName resource)
    {
        if (!resourceSchema.TryGetPropertyValue("relational", out var relationalNode))
        {
            return RelationalNameConventions.ToPascalCase(resource.ResourceName);
        }

        if (relationalNode is null)
        {
            return RelationalNameConventions.ToPascalCase(resource.ResourceName);
        }

        if (relationalNode is not JsonObject relationalObject)
        {
            throw new InvalidOperationException(
                $"Expected relational to be an object for abstract resource '{FormatResource(resource)}'."
            );
        }

        if (!relationalObject.TryGetPropertyValue("rootTableNameOverride", out var overrideNode))
        {
            return RelationalNameConventions.ToPascalCase(resource.ResourceName);
        }

        if (overrideNode is null)
        {
            throw new InvalidOperationException(
                "relational.rootTableNameOverride must be non-empty on abstract resource "
                    + $"'{FormatResource(resource)}'."
            );
        }

        if (overrideNode is not JsonValue overrideValue)
        {
            throw new InvalidOperationException(
                "relational.rootTableNameOverride must be a string on abstract resource "
                    + $"'{FormatResource(resource)}'."
            );
        }

        var overrideText = overrideValue.GetValue<string>();

        if (string.IsNullOrWhiteSpace(overrideText))
        {
            throw new InvalidOperationException(
                "relational.rootTableNameOverride must be non-empty on abstract resource "
                    + $"'{FormatResource(resource)}'."
            );
        }

        var normalized = RelationalNameConventions.ToPascalCase(overrideText);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException(
                "relational.rootTableNameOverride must normalize to a non-empty name on abstract resource "
                    + $"'{FormatResource(resource)}'."
            );
        }

        return normalized;
    }

    /// <summary>
    /// Extracts and compiles <c>identityJsonPaths</c> from an abstract resource schema, validating duplicates.
    /// </summary>
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

    /// <summary>
    /// Extracts <c>decimalPropertyValidationInfos</c> into a lookup keyed by canonical JSONPath.
    /// </summary>
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

    /// <summary>
    /// Reads a required boolean property from a schema node.
    /// </summary>
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

    /// <summary>
    /// Formats a column signature for diagnostic error messages.
    /// </summary>
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

    /// <summary>
    /// Formats a scalar type for diagnostic error messages.
    /// </summary>
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

    /// <summary>
    /// Represents the resolved kind and type metadata for an identity column across concrete members.
    /// </summary>
    private sealed record ColumnSignature(
        ColumnKind Kind,
        RelationalScalarType ScalarType,
        QualifiedResourceName? TargetResource
    );

    /// <summary>
    /// Captures the resolved signature plus the concrete source column name used by a member projection.
    /// </summary>
    private sealed record ColumnResolution(ColumnSignature Signature, DbColumnName SourceColumnName);

    /// <summary>
    /// Captures an abstract identity output column and the aligned source-column projections for each member.
    /// </summary>
    private sealed record IdentityColumnDerivation(
        DbColumnModel IdentityColumn,
        IReadOnlyList<DbColumnName> MemberSourceColumnsInMemberOrder
    );

    /// <summary>
    /// Captures the derived model and schema metadata for a concrete resource used when deriving abstract
    /// identity tables.
    /// </summary>
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
