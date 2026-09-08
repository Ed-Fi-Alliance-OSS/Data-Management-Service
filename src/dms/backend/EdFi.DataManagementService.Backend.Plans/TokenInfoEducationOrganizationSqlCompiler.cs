// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles the relational token_info education organization lookup SQL from the runtime mapping set.
/// </summary>
public sealed class TokenInfoEducationOrganizationSqlCompiler(SqlDialect dialect)
{
    private const string EducationOrganizationResourceName = "EducationOrganization";
    private const string DiscriminatorColumnName = "Discriminator";
    private const string NameOfInstitutionJsonPath = "$.nameOfInstitution";
    private const string ConcreteEdOrgCte = "concrete_edorg";
    private const string ClaimedEdOrgCte = "claimed_edorg";
    private const string AccessibleTargetsCte = "accessible_targets";
    private const string AncestorLinksCte = "ancestor_links";
    private const string RootAlias = "r";
    private const string ConcreteAlias = "c";
    private const string HierarchyAlias = "h";
    private const string AccessibleAlias = "a";
    private const string LinkAlias = "link";
    private const string TargetAlias = "target";
    private const string AncestorAlias = "ancestor";

    private readonly SqlDialect _dialect = dialect;
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);
    private readonly TokenInfoEducationOrganizationResultColumns _resultColumns =
        TokenInfoEducationOrganizationResultColumns.Default;

    /// <summary>
    /// Compiles a relational token_info education organization SQL plan.
    /// </summary>
    public TokenInfoEducationOrganizationSqlPlan Compile(TokenInfoEducationOrganizationSqlSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.MappingSet);
        ArgumentNullException.ThrowIfNull(spec.ClaimEducationOrganizationIdParameterization);

        ValidateMappingSetDialect(spec.MappingSet);
        AuthorizationClaimEducationOrganizationIdParameterizationValidator.ValidateOrThrow(
            spec.ClaimEducationOrganizationIdParameterization,
            _dialect,
            $"{nameof(spec)}.{nameof(spec.ClaimEducationOrganizationIdParameterization)}",
            nameof(TokenInfoEducationOrganizationSqlCompiler)
        );

        var projectionArms = BuildProjectionArms(spec.MappingSet);
        var parametersInOrder =
            AuthorizationClaimEducationOrganizationIdSqlHelper.BuildFilterParametersInOrder(
                spec.ClaimEducationOrganizationIdParameterization
            );

        return new TokenInfoEducationOrganizationSqlPlan(
            BuildSql(projectionArms, spec.ClaimEducationOrganizationIdParameterization),
            parametersInOrder,
            projectionArms,
            _resultColumns
        );
    }

    private void ValidateMappingSetDialect(MappingSet mappingSet)
    {
        if (mappingSet.Model.Dialect != _dialect)
        {
            throw new ArgumentException(
                $"Mapping set model dialect '{mappingSet.Model.Dialect}' does not match compiler dialect '{_dialect}'.",
                nameof(mappingSet)
            );
        }

        if (mappingSet.Key.Dialect != _dialect)
        {
            throw new ArgumentException(
                $"Mapping set key dialect '{mappingSet.Key.Dialect}' does not match compiler dialect '{_dialect}'.",
                nameof(mappingSet)
            );
        }
    }

    private static IReadOnlyList<TokenInfoEducationOrganizationProjectionArm> BuildProjectionArms(
        MappingSet mappingSet
    )
    {
        var edOrgUnionViews = mappingSet
            .Model.AbstractUnionViewsInNameOrder.Where(static view =>
                string.Equals(
                    view.AbstractResourceKey.Resource.ResourceName,
                    EducationOrganizationResourceName,
                    StringComparison.Ordinal
                )
            )
            .ToArray();

        if (edOrgUnionViews.Length == 0)
        {
            throw new InvalidOperationException(
                $"Mapping set does not contain an abstract '{EducationOrganizationResourceName}' union view."
            );
        }

        if (edOrgUnionViews.Length > 1)
        {
            throw new InvalidOperationException(
                $"Mapping set contains {edOrgUnionViews.Length} abstract '{EducationOrganizationResourceName}' union views; expected one."
            );
        }

        var edOrgUnionView = edOrgUnionViews[0];
        if (edOrgUnionView.UnionArmsInOrder.Count == 0)
        {
            throw new InvalidOperationException(
                $"EducationOrganization union view '{edOrgUnionView.ViewName}' does not contain any concrete projection arms."
            );
        }

        var educationOrganizationIdOutputIndex = ResolveEducationOrganizationIdOutputIndex(edOrgUnionView);
        var discriminatorOutputIndex = ResolveOutputColumnIndex(edOrgUnionView, DiscriminatorColumnName);
        var concreteResourcesByResource = mappingSet.Model.ConcreteResourcesInNameOrder.ToDictionary(
            static concrete => concrete.ResourceKey.Resource,
            static concrete => concrete
        );

        List<TokenInfoEducationOrganizationProjectionArm> projectionArms = new(
            edOrgUnionView.UnionArmsInOrder.Count
        );

        foreach (var arm in edOrgUnionView.UnionArmsInOrder)
        {
            var resource = arm.ConcreteMemberResourceKey.Resource;

            if (!concreteResourcesByResource.TryGetValue(resource, out var concreteResource))
            {
                throw new InvalidOperationException(
                    $"EducationOrganization union view references concrete member '{FormatResource(resource)}', but the mapping set does not contain that concrete resource."
                );
            }

            if (concreteResource.StorageKind != ResourceStorageKind.RelationalTables)
            {
                throw new InvalidOperationException(
                    $"EducationOrganization union view member '{FormatResource(resource)}' uses storage kind '{concreteResource.StorageKind}', but token_info requires relational-table storage."
                );
            }

            projectionArms.Add(
                new TokenInfoEducationOrganizationProjectionArm(
                    resource,
                    arm.FromTable,
                    ResolveSourceColumn(
                        arm,
                        educationOrganizationIdOutputIndex,
                        nameof(TokenInfoEducationOrganizationProjectionArm.EducationOrganizationIdColumn)
                    ),
                    ResolveNameOfInstitutionColumn(concreteResource),
                    ResolveDiscriminator(arm, discriminatorOutputIndex)
                )
            );
        }

        return projectionArms;
    }

    private static int ResolveOutputColumnIndex(AbstractUnionViewInfo view, string columnName)
    {
        var matches = view
            .OutputColumnsInSelectOrder.Select((column, index) => (column, index))
            .Where(entry =>
                string.Equals(entry.column.ColumnName.Value, columnName, StringComparison.Ordinal)
            )
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0].index,
            0 => throw new InvalidOperationException(
                $"EducationOrganization union view '{view.ViewName}' does not expose output column '{columnName}'."
            ),
            _ => throw new InvalidOperationException(
                $"EducationOrganization union view '{view.ViewName}' exposes duplicate output column '{columnName}'."
            ),
        };
    }

    private static int ResolveEducationOrganizationIdOutputIndex(AbstractUnionViewInfo view)
    {
        var matches = view
            .OutputColumnsInSelectOrder.Select((column, index) => (column, index))
            .Where(static entry =>
                !string.Equals(entry.column.ColumnName.Value, "DocumentId", StringComparison.Ordinal)
                && !string.Equals(
                    entry.column.ColumnName.Value,
                    DiscriminatorColumnName,
                    StringComparison.Ordinal
                )
            )
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0].index,
            0 => throw new InvalidOperationException(
                $"EducationOrganization union view '{view.ViewName}' does not expose an identity output column."
            ),
            _ => throw new InvalidOperationException(
                $"EducationOrganization union view '{view.ViewName}' exposes {matches.Length} identity output columns; token_info supports exactly one EducationOrganizationId column."
            ),
        };
    }

    private static DbColumnName ResolveSourceColumn(
        AbstractUnionViewArm arm,
        int outputIndex,
        string outputLabel
    )
    {
        if (outputIndex >= arm.ProjectionExpressionsInSelectOrder.Count)
        {
            throw new InvalidOperationException(
                $"EducationOrganization union arm '{FormatResource(arm.ConcreteMemberResourceKey.Resource)}' has too few projection expressions for output '{outputLabel}'."
            );
        }

        return arm.ProjectionExpressionsInSelectOrder[outputIndex] switch
        {
            AbstractUnionViewProjectionExpression.SourceColumn sourceColumn => sourceColumn.ColumnName,
            _ => throw new InvalidOperationException(
                $"EducationOrganization union arm '{FormatResource(arm.ConcreteMemberResourceKey.Resource)}' must project output '{outputLabel}' from a source column."
            ),
        };
    }

    private static string ResolveDiscriminator(AbstractUnionViewArm arm, int discriminatorOutputIndex)
    {
        if (discriminatorOutputIndex >= arm.ProjectionExpressionsInSelectOrder.Count)
        {
            throw new InvalidOperationException(
                $"EducationOrganization union arm '{FormatResource(arm.ConcreteMemberResourceKey.Resource)}' has too few projection expressions for discriminator output."
            );
        }

        return arm.ProjectionExpressionsInSelectOrder[discriminatorOutputIndex] switch
        {
            AbstractUnionViewProjectionExpression.StringLiteral literal => literal.Value,
            _ => throw new InvalidOperationException(
                $"EducationOrganization union arm '{FormatResource(arm.ConcreteMemberResourceKey.Resource)}' must project discriminator from a string literal."
            ),
        };
    }

    private static DbColumnName ResolveNameOfInstitutionColumn(ConcreteResourceModel concreteResource)
    {
        var matches = concreteResource
            .RelationalModel.Root.Columns.Where(static column =>
                column.Kind == ColumnKind.Scalar
                && column.SourceJsonPath is { Canonical: NameOfInstitutionJsonPath }
            )
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0].ColumnName,
            0 => throw new InvalidOperationException(
                $"Concrete EducationOrganization resource '{FormatResource(concreteResource.ResourceKey.Resource)}' does not expose a root scalar column for '{NameOfInstitutionJsonPath}'."
            ),
            _ => throw new InvalidOperationException(
                $"Concrete EducationOrganization resource '{FormatResource(concreteResource.ResourceKey.Resource)}' exposes duplicate root scalar columns for '{NameOfInstitutionJsonPath}'."
            ),
        };
    }

    private string BuildSql(
        IReadOnlyList<TokenInfoEducationOrganizationProjectionArm> projectionArms,
        AuthorizationClaimEducationOrganizationIdParameterization claimParameterization
    )
    {
        var writer = new SqlWriter(_sqlDialect);

        writer.AppendLine($"WITH {ConcreteEdOrgCte} AS (");
        using (writer.Indent())
        {
            AppendConcreteEdOrgProjection(writer, projectionArms);
        }
        writer.AppendLine("),");

        AppendClaimedEdOrgCte(writer, claimParameterization);
        writer.AppendLine(",");
        AppendAccessibleTargetsCte(writer);
        writer.AppendLine(",");
        AppendAncestorLinksCte(writer);
        AppendFinalSelect(writer);

        return writer.ToString();
    }

    private void AppendConcreteEdOrgProjection(
        SqlWriter writer,
        IReadOnlyList<TokenInfoEducationOrganizationProjectionArm> projectionArms
    )
    {
        for (var index = 0; index < projectionArms.Count; index++)
        {
            if (index > 0)
            {
                writer.AppendLine("UNION ALL");
            }

            var arm = projectionArms[index];

            writer.AppendLine("SELECT");
            using (writer.Indent())
            {
                AppendQualifiedColumn(writer, RootAlias, arm.EducationOrganizationIdColumn)
                    .Append(" AS ")
                    .AppendQuoted(_resultColumns.EducationOrganizationId.Value)
                    .AppendLine(",");
                AppendQualifiedColumn(writer, RootAlias, arm.NameOfInstitutionColumn)
                    .Append(" AS ")
                    .AppendQuoted(_resultColumns.NameOfInstitution.Value)
                    .AppendLine(",");
                writer
                    .Append(_sqlDialect.RenderStringLiteral(arm.Discriminator))
                    .Append(" AS ")
                    .AppendQuoted(_resultColumns.Discriminator.Value)
                    .AppendLine();
            }
            writer.Append("FROM ").AppendTable(arm.Table).AppendLine($" {RootAlias}");
        }
    }

    private void AppendClaimedEdOrgCte(
        SqlWriter writer,
        AuthorizationClaimEducationOrganizationIdParameterization claimParameterization
    )
    {
        writer.AppendLine($"{ClaimedEdOrgCte} AS (");
        using (writer.Indent())
        {
            writer.AppendLine("SELECT");
            using (writer.Indent())
            {
                AppendQualifiedColumn(writer, ConcreteAlias, _resultColumns.EducationOrganizationId)
                    .AppendLine(",");
                AppendQualifiedColumn(writer, ConcreteAlias, _resultColumns.NameOfInstitution)
                    .AppendLine(",");
                AppendQualifiedColumn(writer, ConcreteAlias, _resultColumns.Discriminator).AppendLine();
            }

            writer.AppendLine($"FROM {ConcreteEdOrgCte} {ConcreteAlias}");
            writer
                .Append($"WHERE {ConcreteAlias}.")
                .AppendQuoted(_resultColumns.EducationOrganizationId.Value);
            AuthorizationClaimEducationOrganizationIdSqlHelper.AppendClaimFilterSql(
                writer,
                claimParameterization
            );
            writer.AppendLine();
        }
        writer.Append(")");
    }

    private void AppendAccessibleTargetsCte(SqlWriter writer)
    {
        writer.AppendLine($"{AccessibleTargetsCte} AS (");
        using (writer.Indent())
        {
            writer.AppendLine("SELECT DISTINCT");
            using (writer.Indent())
            {
                AppendQualifiedColumn(writer, HierarchyAlias, AuthNames.TargetEdOrgId)
                    .Append(" AS ")
                    .AppendQuoted(_resultColumns.EducationOrganizationId.Value)
                    .AppendLine();
            }

            writer.Append("FROM ").AppendTable(AuthNames.EdOrgIdToEdOrgId).AppendLine($" {HierarchyAlias}");
            writer.AppendLine($"INNER JOIN {ClaimedEdOrgCte} {ConcreteAlias}");
            using (writer.Indent())
            {
                writer.Append("ON ");
                AppendQualifiedColumn(writer, HierarchyAlias, AuthNames.SourceEdOrgId).Append(" = ");
                AppendQualifiedColumn(writer, ConcreteAlias, _resultColumns.EducationOrganizationId)
                    .AppendLine();
            }

            writer.AppendLine("UNION");
            writer.AppendLine("SELECT");
            using (writer.Indent())
            {
                AppendQualifiedColumn(writer, ConcreteAlias, _resultColumns.EducationOrganizationId)
                    .AppendLine();
            }
            writer.AppendLine($"FROM {ClaimedEdOrgCte} {ConcreteAlias}");
        }
        writer.Append(")");
    }

    private void AppendAncestorLinksCte(SqlWriter writer)
    {
        writer.AppendLine($"{AncestorLinksCte} AS (");
        using (writer.Indent())
        {
            writer.AppendLine("SELECT");
            using (writer.Indent())
            {
                AppendQualifiedColumn(writer, HierarchyAlias, AuthNames.TargetEdOrgId).AppendLine(",");
                AppendQualifiedColumn(writer, HierarchyAlias, AuthNames.SourceEdOrgId).AppendLine();
            }

            writer.Append("FROM ").AppendTable(AuthNames.EdOrgIdToEdOrgId).AppendLine($" {HierarchyAlias}");
            writer.AppendLine($"INNER JOIN {AccessibleTargetsCte} {AccessibleAlias}");
            using (writer.Indent())
            {
                writer.Append("ON ");
                AppendQualifiedColumn(writer, HierarchyAlias, AuthNames.TargetEdOrgId).Append(" = ");
                AppendQualifiedColumn(writer, AccessibleAlias, _resultColumns.EducationOrganizationId)
                    .AppendLine();
            }

            writer.AppendLine("UNION");
            writer.AppendLine("SELECT");
            using (writer.Indent())
            {
                AppendQualifiedColumn(writer, AccessibleAlias, _resultColumns.EducationOrganizationId)
                    .Append(" AS ")
                    .AppendQuoted(AuthNames.TargetEdOrgId.Value)
                    .AppendLine(",");
                AppendQualifiedColumn(writer, AccessibleAlias, _resultColumns.EducationOrganizationId)
                    .Append(" AS ")
                    .AppendQuoted(AuthNames.SourceEdOrgId.Value)
                    .AppendLine();
            }
            writer.AppendLine($"FROM {AccessibleTargetsCte} {AccessibleAlias}");
        }
        writer.AppendLine(")");
    }

    private void AppendFinalSelect(SqlWriter writer)
    {
        writer.AppendLine("SELECT");
        using (writer.Indent())
        {
            AppendQualifiedColumn(writer, TargetAlias, _resultColumns.EducationOrganizationId)
                .Append(" AS ")
                .AppendQuoted(_resultColumns.EducationOrganizationId.Value)
                .AppendLine(",");
            AppendQualifiedColumn(writer, TargetAlias, _resultColumns.NameOfInstitution)
                .Append(" AS ")
                .AppendQuoted(_resultColumns.NameOfInstitution.Value)
                .AppendLine(",");
            AppendQualifiedColumn(writer, TargetAlias, _resultColumns.Discriminator)
                .Append(" AS ")
                .AppendQuoted(_resultColumns.Discriminator.Value)
                .AppendLine(",");
            AppendQualifiedColumn(writer, AncestorAlias, _resultColumns.Discriminator)
                .Append(" AS ")
                .AppendQuoted(_resultColumns.AncestorDiscriminator.Value)
                .AppendLine(",");
            AppendQualifiedColumn(writer, AncestorAlias, _resultColumns.EducationOrganizationId)
                .Append(" AS ")
                .AppendQuoted(_resultColumns.AncestorEducationOrganizationId.Value)
                .AppendLine();
        }

        writer.AppendLine($"FROM {AccessibleTargetsCte} {AccessibleAlias}");
        writer.AppendLine($"INNER JOIN {ConcreteEdOrgCte} {TargetAlias}");
        using (writer.Indent())
        {
            writer.Append("ON ");
            AppendQualifiedColumn(writer, TargetAlias, _resultColumns.EducationOrganizationId).Append(" = ");
            AppendQualifiedColumn(writer, AccessibleAlias, _resultColumns.EducationOrganizationId)
                .AppendLine();
        }
        writer.AppendLine($"INNER JOIN {AncestorLinksCte} {LinkAlias}");
        using (writer.Indent())
        {
            writer.Append("ON ");
            AppendQualifiedColumn(writer, LinkAlias, AuthNames.TargetEdOrgId).Append(" = ");
            AppendQualifiedColumn(writer, AccessibleAlias, _resultColumns.EducationOrganizationId)
                .AppendLine();
        }
        writer.AppendLine($"INNER JOIN {ConcreteEdOrgCte} {AncestorAlias}");
        using (writer.Indent())
        {
            writer.Append("ON ");
            AppendQualifiedColumn(writer, AncestorAlias, _resultColumns.EducationOrganizationId)
                .Append(" = ");
            AppendQualifiedColumn(writer, LinkAlias, AuthNames.SourceEdOrgId).AppendLine();
        }

        writer.AppendLine("ORDER BY");
        using (writer.Indent())
        {
            AppendQualifiedColumn(writer, TargetAlias, _resultColumns.EducationOrganizationId)
                .AppendLine(" ASC,");
            AppendQualifiedColumn(writer, AncestorAlias, _resultColumns.EducationOrganizationId)
                .AppendLine(" ASC,");
            AppendQualifiedColumn(writer, TargetAlias, _resultColumns.Discriminator).AppendLine(" ASC,");
            AppendQualifiedColumn(writer, AncestorAlias, _resultColumns.Discriminator).AppendLine(" ASC;");
        }
    }

    private static SqlWriter AppendQualifiedColumn(SqlWriter writer, string tableAlias, DbColumnName column)
    {
        return writer.Append($"{tableAlias}.").AppendQuoted(column.Value);
    }

    private static string FormatResource(QualifiedResourceName resource)
    {
        return $"{resource.ProjectName}.{resource.ResourceName}";
    }
}
