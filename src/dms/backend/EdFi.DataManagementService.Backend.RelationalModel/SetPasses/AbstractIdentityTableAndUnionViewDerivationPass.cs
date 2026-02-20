// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Derives abstract identity table and union view models for abstract resources in the effective schema set.
/// </summary>
public sealed class AbstractIdentityTableAndUnionViewDerivationPass : IRelationalModelSetPass
{
    private const string DiscriminatorColumnLabel = "Discriminator";
    private const int DiscriminatorMaxLength = 256;
    private static readonly DbSchemaName _dmsSchemaName = new("dms");
    private static readonly DbTableName _documentTableName = new(_dmsSchemaName, "Document");

    /// <summary>
    /// Executes abstract identity table and union view derivation across all abstract resources.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var concreteModelsByResource = context.ConcreteResourcesInNameOrder.ToDictionary(model =>
            model.ResourceKey.Resource
        );
        var membersByAbstractResource = BuildMembersByAbstractResource(context, concreteModelsByResource);

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
                var identityJsonPaths = IdentityJsonPathsExtractor.ExtractIdentityJsonPaths(
                    abstractEntry.ResourceSchema,
                    abstractResource.ProjectName,
                    abstractResource.ResourceName
                );

                if (!membersByAbstractResource.TryGetValue(abstractResource, out var members))
                {
                    members = [];
                }

                if (members.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Abstract resource '{FormatResource(abstractResource)}' has no concrete members."
                    );
                }

                var identityDerivations = BuildIdentityColumnDerivations(
                    identityJsonPaths,
                    abstractResource,
                    members
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
                    [
                        new DbKeyColumn(
                            RelationalNameConventions.DocumentIdColumnName,
                            ColumnKind.ParentKeyPart
                        ),
                    ]
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
    /// Builds deterministic concrete-member lookup keyed by abstract superclass resource.
    /// </summary>
    private static IReadOnlyDictionary<
        QualifiedResourceName,
        IReadOnlyList<ConcreteResourceMetadata>
    > BuildMembersByAbstractResource(
        RelationalModelSetBuilderContext context,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> concreteModelsByResource
    )
    {
        Dictionary<QualifiedResourceName, List<ConcreteResourceMetadata>> membersByAbstractResource = [];

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

            var isSubclass = ApiSchemaNodeRequirements.TryGetOptionalBoolean(
                resourceSchema,
                "isSubclass",
                defaultValue: false
            );
            if (!isSubclass)
            {
                continue;
            }

            var identityJsonPaths = IdentityJsonPathsExtractor.ExtractIdentityJsonPaths(
                resourceSchema,
                resource.ProjectName,
                resource.ResourceName
            );
            var superclassProjectName = RequireSubclassString(
                resourceSchema,
                "superclassProjectName",
                resource
            );
            var superclassResourceName = RequireSubclassString(
                resourceSchema,
                "superclassResourceName",
                resource
            );
            var superclassIdentityJsonPath = TryGetOptionalString(
                resourceSchema,
                "superclassIdentityJsonPath"
            );
            JsonPathExpression? superclassIdentityPath = superclassIdentityJsonPath is null
                ? null
                : JsonPathExpressionCompiler.Compile(superclassIdentityJsonPath);
            var rootColumnsBySourcePath = BuildRootColumnsBySourcePath(model.RelationalModel.Root, resource);

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

            if (!membersByAbstractResource.TryGetValue(superclassResource, out var members))
            {
                members = [];
                membersByAbstractResource.Add(superclassResource, members);
            }

            members.Add(
                new ConcreteResourceMetadata(
                    resource,
                    model.RelationalModel,
                    identityJsonPaths,
                    superclassIdentityPath,
                    rootColumnsBySourcePath
                )
            );
        }

        return membersByAbstractResource.ToDictionary(
            pair => pair.Key,
            static pair =>
                (IReadOnlyList<ConcreteResourceMetadata>)
                    pair
                        .Value.OrderBy(metadata => metadata.Resource.ResourceName, StringComparer.Ordinal)
                        .ThenBy(metadata => metadata.Resource.ProjectName, StringComparer.Ordinal)
                        .ToArray()
        );
    }

    /// <summary>
    /// Builds an O(1) lookup of root columns keyed by canonical <c>SourceJsonPath</c>, failing fast when
    /// duplicate mappings are detected.
    /// </summary>
    private static IReadOnlyDictionary<string, DbColumnModel> BuildRootColumnsBySourcePath(
        DbTableModel rootTable,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, DbColumnModel> columnsByPath = new(StringComparer.Ordinal);

        foreach (var column in rootTable.Columns)
        {
            if (column.SourceJsonPath is not { } sourceJsonPath)
            {
                continue;
            }

            if (!columnsByPath.TryAdd(sourceJsonPath.Canonical, column))
            {
                var existingColumn = columnsByPath[sourceJsonPath.Canonical];

                throw new InvalidOperationException(
                    $"Concrete resource '{FormatResource(resource)}' has duplicate root-column "
                        + $"SourceJsonPath mapping for '{sourceJsonPath.Canonical}' on table "
                        + $"'{rootTable.Table}': '{existingColumn.ColumnName.Value}' and "
                        + $"'{column.ColumnName.Value}'."
                );
            }
        }

        return columnsByPath;
    }

    /// <summary>
    /// Builds identity column derivations for an abstract resource, including table/view output-column metadata
    /// and per-member source-column projections.
    /// </summary>
    private static IReadOnlyList<IdentityColumnDerivation> BuildIdentityColumnDerivations(
        IReadOnlyList<JsonPathExpression> identityJsonPaths,
        QualifiedResourceName abstractResource,
        IReadOnlyList<ConcreteResourceMetadata> members
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
                var resolution = ResolveColumnResolution(member, mappedPath);
                var memberSignature = resolution.Signature;

                if (signature is null)
                {
                    signature = memberSignature;
                }
                else
                {
                    signature = ResolveCanonicalColumnSignature(
                        signature,
                        memberSignature,
                        identityPath,
                        abstractResource,
                        member.Resource
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
                new DbColumnName(BuildIdentityPartBaseName(identityPath)),
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
    /// Resolves a canonical column signature that can safely represent all participating member signatures.
    /// </summary>
    private static ColumnSignature ResolveCanonicalColumnSignature(
        ColumnSignature currentSignature,
        ColumnSignature memberSignature,
        JsonPathExpression identityPath,
        QualifiedResourceName abstractResource,
        QualifiedResourceName memberResource
    )
    {
        if (currentSignature.Kind != memberSignature.Kind)
        {
            throw CreateInconsistentColumnTypesException(
                currentSignature,
                memberSignature,
                identityPath,
                abstractResource,
                memberResource
            );
        }

        if (currentSignature.TargetResource != memberSignature.TargetResource)
        {
            throw CreateInconsistentColumnTypesException(
                currentSignature,
                memberSignature,
                identityPath,
                abstractResource,
                memberResource
            );
        }

        var canonicalScalarType = ResolveCanonicalScalarType(
            currentSignature,
            memberSignature,
            identityPath,
            abstractResource,
            memberResource
        );

        return currentSignature with
        {
            ScalarType = canonicalScalarType,
        };
    }

    /// <summary>
    /// Resolves a canonical scalar type across two compatible signatures.
    /// </summary>
    private static RelationalScalarType ResolveCanonicalScalarType(
        ColumnSignature currentSignature,
        ColumnSignature memberSignature,
        JsonPathExpression identityPath,
        QualifiedResourceName abstractResource,
        QualifiedResourceName memberResource
    )
    {
        var currentScalarType = currentSignature.ScalarType;
        var memberScalarType = memberSignature.ScalarType;

        if (currentScalarType.Kind == memberScalarType.Kind)
        {
            return currentScalarType.Kind switch
            {
                ScalarKind.String => ResolveCanonicalStringType(currentScalarType, memberScalarType),
                ScalarKind.Decimal => ResolveCanonicalDecimalType(
                    currentScalarType,
                    memberScalarType,
                    identityPath,
                    abstractResource
                ),
                _ => currentScalarType,
            };
        }

        if (
            (currentScalarType.Kind == ScalarKind.Int32 && memberScalarType.Kind == ScalarKind.Int64)
            || (currentScalarType.Kind == ScalarKind.Int64 && memberScalarType.Kind == ScalarKind.Int32)
        )
        {
            return new RelationalScalarType(ScalarKind.Int64);
        }

        throw CreateInconsistentColumnTypesException(
            currentSignature,
            memberSignature,
            identityPath,
            abstractResource,
            memberResource
        );
    }

    /// <summary>
    /// Resolves the canonical string type by selecting the largest max length (or unbounded when any member is
    /// unbounded).
    /// </summary>
    private static RelationalScalarType ResolveCanonicalStringType(
        RelationalScalarType currentScalarType,
        RelationalScalarType memberScalarType
    )
    {
        int? canonicalMaxLength =
            currentScalarType.MaxLength is null || memberScalarType.MaxLength is null
                ? null
                : Math.Max(currentScalarType.MaxLength.Value, memberScalarType.MaxLength.Value);

        return new RelationalScalarType(ScalarKind.String, canonicalMaxLength);
    }

    /// <summary>
    /// Resolves the canonical decimal type by preserving the largest integer digits and fractional scale
    /// across members.
    /// </summary>
    private static RelationalScalarType ResolveCanonicalDecimalType(
        RelationalScalarType currentScalarType,
        RelationalScalarType memberScalarType,
        JsonPathExpression identityPath,
        QualifiedResourceName abstractResource
    )
    {
        if (currentScalarType.Decimal is not { } currentDecimal)
        {
            throw new InvalidOperationException(
                $"Expected decimal type metadata for abstract identity path '{identityPath.Canonical}' on "
                    + $"resource '{FormatResource(abstractResource)}'."
            );
        }

        if (memberScalarType.Decimal is not { } memberDecimal)
        {
            throw new InvalidOperationException(
                $"Expected decimal type metadata for abstract identity path '{identityPath.Canonical}' on "
                    + $"resource '{FormatResource(abstractResource)}'."
            );
        }

        if (currentDecimal.Scale > currentDecimal.Precision)
        {
            throw new InvalidOperationException(
                $"Decimal type metadata is invalid for abstract identity path '{identityPath.Canonical}' on "
                    + $"resource '{FormatResource(abstractResource)}'. Precision {currentDecimal.Precision} must "
                    + $"be greater than or equal to scale {currentDecimal.Scale}."
            );
        }

        if (memberDecimal.Scale > memberDecimal.Precision)
        {
            throw new InvalidOperationException(
                $"Decimal type metadata is invalid for abstract identity path '{identityPath.Canonical}' on "
                    + $"resource '{FormatResource(abstractResource)}'. Precision {memberDecimal.Precision} must "
                    + $"be greater than or equal to scale {memberDecimal.Scale}."
            );
        }

        var canonicalIntegerDigits = Math.Max(
            currentDecimal.Precision - currentDecimal.Scale,
            memberDecimal.Precision - memberDecimal.Scale
        );
        var canonicalScale = Math.Max(currentDecimal.Scale, memberDecimal.Scale);
        var canonicalPrecision = canonicalIntegerDigits + canonicalScale;

        if (canonicalScale > canonicalPrecision)
        {
            throw new InvalidOperationException(
                $"Canonical decimal type is invalid for abstract identity path '{identityPath.Canonical}' on "
                    + $"resource '{FormatResource(abstractResource)}'. Precision {canonicalPrecision} must be "
                    + $"greater than or equal to scale {canonicalScale}."
            );
        }

        return new RelationalScalarType(ScalarKind.Decimal, Decimal: (canonicalPrecision, canonicalScale));
    }

    /// <summary>
    /// Creates a standardized inconsistent-signature exception for abstract identity type derivation.
    /// </summary>
    private static InvalidOperationException CreateInconsistentColumnTypesException(
        ColumnSignature expectedSignature,
        ColumnSignature memberSignature,
        JsonPathExpression identityPath,
        QualifiedResourceName abstractResource,
        QualifiedResourceName memberResource
    )
    {
        return new InvalidOperationException(
            $"Abstract identity path '{identityPath.Canonical}' for resource "
                + $"'{FormatResource(abstractResource)}' has inconsistent column types. "
                + $"Expected {FormatSignature(expectedSignature)} but member "
                + $"'{FormatResource(memberResource)}' provides {FormatSignature(memberSignature)}."
        );
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
    /// Builds deterministic union-view arms ordered by member <c>(ResourceName, ProjectName)</c> using ordinal comparison.
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
                var sourceColumnName = derivation.MemberSourceColumnsInMemberOrder[memberIndex];

                // Look up the source column's scalar type from the concrete member table
                // so the emitter can emit an explicit CAST when it differs from the canonical type.
                var sourceColumnModel =
                    member.Model.Root.Columns.FirstOrDefault(c => c.ColumnName == sourceColumnName)
                    ?? throw new InvalidOperationException(
                        $"Column '{sourceColumnName}' not found in root table of member "
                            + $"'{FormatResource(member.Resource)}' during abstract union view derivation."
                    );

                projections.Add(
                    new AbstractUnionViewProjectionExpression.SourceColumn(
                        sourceColumnName,
                        sourceColumnModel.ScalarType
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
    /// Maps an abstract identity path to the corresponding identity path on a concrete member, honoring
    /// <c>superclassIdentityJsonPath</c> when present.
    /// </summary>
    private static JsonPathExpression MapIdentityPathForMember(
        ConcreteResourceMetadata member,
        JsonPathExpression abstractIdentityPath,
        QualifiedResourceName abstractResource
    )
    {
        if (member.SuperclassIdentityJsonPath is not null)
        {
            if (member.IdentityJsonPaths.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Member '{FormatResource(member.Resource)}' has SuperclassIdentityJsonPath set "
                        + $"but {member.IdentityJsonPaths.Count} identity paths (expected exactly 1)."
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
    /// Resolves the column signature and source column for a member resource at the given identity path using
    /// the precomputed root-column SourceJsonPath lookup.
    /// </summary>
    private static ColumnResolution ResolveColumnResolution(
        ConcreteResourceMetadata member,
        JsonPathExpression mappedIdentityPath
    )
    {
        if (!member.RootColumnsBySourcePath.TryGetValue(mappedIdentityPath.Canonical, out var column))
        {
            throw new InvalidOperationException(
                $"Identity path '{mappedIdentityPath.Canonical}' for member "
                    + $"'{FormatResource(member.Resource)}' did not resolve to a root-column "
                    + $"SourceJsonPath mapping on table '{member.Model.Root.Table}'."
            );
        }

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
    /// Builds the unique and FK constraints for an abstract identity table.
    /// </summary>
    private static IReadOnlyList<TableConstraint> BuildIdentityTableConstraints(
        DbTableName tableName,
        IReadOnlyList<DbColumnModel> identityColumns
    )
    {
        List<TableConstraint> constraints = [];
        var naturalKeyColumns = identityColumns.Select(column => column.ColumnName).ToArray();

        if (naturalKeyColumns.Length > 0)
        {
            var naturalKeyName = ConstraintNaming.BuildNaturalKeyUniqueName(tableName);
            constraints.Add(new TableConstraint.Unique(naturalKeyName, naturalKeyColumns));

            List<DbColumnName> referenceKeyColumns = [RelationalNameConventions.DocumentIdColumnName];
            referenceKeyColumns.AddRange(naturalKeyColumns);

            var referenceKeyName = ConstraintNaming.BuildReferenceKeyUniqueName(tableName);
            constraints.Add(new TableConstraint.Unique(referenceKeyName, referenceKeyColumns.ToArray()));
        }

        var fkName = ConstraintNaming.BuildForeignKeyName(tableName, ConstraintNaming.DocumentToken);

        constraints.Add(
            new TableConstraint.ForeignKey(
                fkName,
                [RelationalNameConventions.DocumentIdColumnName],
                _documentTableName,
                [RelationalNameConventions.DocumentIdColumnName],
                OnDelete: ReferentialAction.Cascade
            )
        );

        return constraints.ToArray();
    }

    /// <summary>
    /// Resolves the root base name for an abstract resource, applying <c>relational.rootTableNameOverride</c>
    /// when present.
    /// </summary>
    private static string ResolveRootBaseName(JsonObject resourceSchema, QualifiedResourceName resource)
    {
        var relationalObject = RelationalOverridesExtractor.GetRelationalObject(
            resourceSchema,
            resource.ProjectName,
            resource.ResourceName,
            isDescriptor: false
        );
        var rootTableNameOverride = RelationalOverridesExtractor.ExtractRootTableNameOverride(
            relationalObject,
            isResourceExtension: false,
            resource.ProjectName,
            resource.ResourceName
        );

        return rootTableNameOverride ?? RelationalNameConventions.ToPascalCase(resource.ResourceName);
    }

    /// <summary>
    /// Reads a required subclass string property while preserving resource-qualified diagnostics.
    /// </summary>
    private static string RequireSubclassString(
        JsonObject resourceSchema,
        string propertyName,
        QualifiedResourceName resource
    )
    {
        try
        {
            return RequireString(resourceSchema, propertyName);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Subclass resource '{FormatResource(resource)}' has invalid {propertyName}: {exception.Message}",
                exception
            );
        }
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
        IReadOnlyList<JsonPathExpression> IdentityJsonPaths,
        JsonPathExpression? SuperclassIdentityJsonPath,
        IReadOnlyDictionary<string, DbColumnModel> RootColumnsBySourcePath
    );
}
