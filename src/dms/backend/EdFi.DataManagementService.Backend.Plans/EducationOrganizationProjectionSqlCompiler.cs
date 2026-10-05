// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using Reason = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionMappingIncompatibilityReason;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles the education-organization projection statement from the runtime mapping set.
/// </summary>
/// <remarks>
/// The statement selects exactly the four core types (State Education Agency, Education Service
/// Center, Local Education Agency and School) from the arms of the abstract EducationOrganization
/// union view; every other arm, including extension-defined education organizations, is ignored. Each
/// core reference is read into its own slot, so the handler can validate references that parent
/// precedence will not select. A model that lacks anything the statement needs yields a typed
/// <see cref="EducationOrganizationProjectionSqlCompilation.Incompatible"/> result rather than an
/// exception; only a mapping set compiled for another dialect is a defect and throws.
/// </remarks>
public sealed class EducationOrganizationProjectionSqlCompiler(SqlDialect dialect)
{
    private const string CoreProjectName = "Ed-Fi";
    private const string EducationOrganizationResourceName = "EducationOrganization";
    private const string DocumentIdColumnName = "DocumentId";
    private const string DiscriminatorColumnName = "Discriminator";
    private const string NameOfInstitutionJsonPath = "$.nameOfInstitution";
    private const string ShortNameOfInstitutionJsonPath = "$.shortNameOfInstitution";
    private const string RowLimitParameterName = "RowLimit";
    private const string RootAlias = "r";
    private const string ProjectionAlias = "p";

    private static readonly ReferenceSlotDefinition _localEducationAgencySlot = new(
        "$.localEducationAgencyReference",
        "LocalEducationAgency",
        "$.localEducationAgencyId"
    );
    private static readonly ReferenceSlotDefinition _parentLocalEducationAgencySlot = new(
        "$.parentLocalEducationAgencyReference",
        "LocalEducationAgency",
        "$.localEducationAgencyId"
    );
    private static readonly ReferenceSlotDefinition _educationServiceCenterSlot = new(
        "$.educationServiceCenterReference",
        "EducationServiceCenter",
        "$.educationServiceCenterId"
    );
    private static readonly ReferenceSlotDefinition _stateEducationAgencySlot = new(
        "$.stateEducationAgencyReference",
        "StateEducationAgency",
        "$.stateEducationAgencyId"
    );

    // Statement order; also the order in which a missing type is reported.
    private static readonly CoreArmDefinition[] _coreArms =
    [
        new("StateEducationAgency", null, null, null, null),
        new("EducationServiceCenter", null, null, null, _stateEducationAgencySlot),
        new(
            "LocalEducationAgency",
            null,
            _parentLocalEducationAgencySlot,
            _educationServiceCenterSlot,
            _stateEducationAgencySlot
        ),
        new("School", _localEducationAgencySlot, null, null, null),
    ];

    private readonly SqlDialect _dialect = dialect;
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);
    private readonly EducationOrganizationProjectionResultColumns _resultColumns =
        EducationOrganizationProjectionResultColumns.Default;

    /// <summary>
    /// Compiles the projection statement, or reports why the mapping set cannot be projected.
    /// </summary>
    /// <exception cref="ArgumentException">The mapping set was compiled for another dialect.</exception>
    public EducationOrganizationProjectionSqlCompilation Compile(MappingSet mappingSet)
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        ValidateMappingSetDialect(mappingSet);

        try
        {
            var arms = BuildArms(mappingSet);

            return new EducationOrganizationProjectionSqlCompilation.Compiled(
                new EducationOrganizationProjectionSqlPlan(
                    BuildSql(arms),
                    new QuerySqlParameter(QuerySqlParameterRole.Limit, RowLimitParameterName),
                    arms,
                    _resultColumns,
                    _dialect == SqlDialect.Mssql
                        ? EducationOrganizationProjectionNameEncoding.Utf16LittleEndianBytes
                        : EducationOrganizationProjectionNameEncoding.Text
                )
            );
        }
        catch (IncompatibleMappingException exception)
        {
            return new EducationOrganizationProjectionSqlCompilation.Incompatible(exception.Incompatibility);
        }
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

    private static IReadOnlyList<EducationOrganizationProjectionArm> BuildArms(MappingSet mappingSet)
    {
        var unionViews = mappingSet
            .Model.AbstractUnionViewsInNameOrder.Where(static view =>
                view.AbstractResourceKey.Resource
                == new QualifiedResourceName(CoreProjectName, EducationOrganizationResourceName)
            )
            .ToArray();

        var unionView = unionViews.Length switch
        {
            1 => unionViews[0],
            0 => throw Incompatible(Reason.UnionViewMissing),
            _ => throw Incompatible(Reason.UnionViewAmbiguous),
        };

        var identityOutputIndex = ResolveIdentityOutputIndex(unionView);
        var discriminatorOutputIndex = ResolveSingleOutputIndex(
            unionView,
            static column => column.ColumnName.Value == DiscriminatorColumnName,
            Reason.DiscriminatorOutputUnresolved
        );

        return
        [
            .. _coreArms.Select(definition =>
                BuildArm(mappingSet, unionView, definition, identityOutputIndex, discriminatorOutputIndex)
            ),
        ];
    }

    private static int ResolveIdentityOutputIndex(AbstractUnionViewInfo unionView)
    {
        return ResolveSingleOutputIndex(
            unionView,
            static column =>
                column.ColumnName.Value is not DocumentIdColumnName and not DiscriminatorColumnName,
            Reason.IdentityOutputUnresolved
        );
    }

    private static int ResolveSingleOutputIndex(
        AbstractUnionViewInfo unionView,
        Func<AbstractUnionViewOutputColumn, bool> predicate,
        Reason reasonWhenNotExactlyOne
    )
    {
        var indexes = unionView
            .OutputColumnsInSelectOrder.Select((column, index) => (column, index))
            .Where(entry => predicate(entry.column))
            .Select(static entry => entry.index)
            .ToArray();

        return indexes.Length == 1 ? indexes[0] : throw Incompatible(reasonWhenNotExactlyOne);
    }

    private static EducationOrganizationProjectionArm BuildArm(
        MappingSet mappingSet,
        AbstractUnionViewInfo unionView,
        CoreArmDefinition definition,
        int identityOutputIndex,
        int discriminatorOutputIndex
    )
    {
        var resource = new QualifiedResourceName(CoreProjectName, definition.ResourceName);

        var unionArms = unionView
            .UnionArmsInOrder.Where(arm => arm.ConcreteMemberResourceKey.Resource == resource)
            .ToArray();

        var unionArm = unionArms.Length switch
        {
            1 => unionArms[0],
            0 => throw Incompatible(Reason.ArmMissing, definition.ResourceName),
            _ => throw Incompatible(Reason.ArmAmbiguous, definition.ResourceName),
        };

        var concreteResource =
            mappingSet.Model.ConcreteResourcesInNameOrder.FirstOrDefault(concrete =>
                concrete.ResourceKey.Resource == resource
            ) ?? throw Incompatible(Reason.ConcreteResourceMissing, definition.ResourceName);

        if (concreteResource.StorageKind != ResourceStorageKind.RelationalTables)
        {
            throw Incompatible(Reason.ResourceNotRelational, definition.ResourceName);
        }

        var root = concreteResource.RelationalModel.Root;

        var discriminator = ResolveDiscriminator(unionArm, discriminatorOutputIndex, definition);
        var identityColumn = ResolveIdentityColumn(unionArm, identityOutputIndex, root, definition);

        return new EducationOrganizationProjectionArm(
            resource,
            root.Table,
            discriminator,
            identityColumn,
            ResolveNameColumn(root, NameOfInstitutionJsonPath, isNullable: false, definition),
            ResolveNameColumn(root, ShortNameOfInstitutionJsonPath, isNullable: true, definition),
            ResolveReferenceColumn(
                concreteResource.RelationalModel,
                definition.LocalEducationAgency,
                definition
            ),
            ResolveReferenceColumn(
                concreteResource.RelationalModel,
                definition.ParentLocalEducationAgency,
                definition
            ),
            ResolveReferenceColumn(
                concreteResource.RelationalModel,
                definition.EducationServiceCenter,
                definition
            ),
            ResolveReferenceColumn(
                concreteResource.RelationalModel,
                definition.StateEducationAgency,
                definition
            )
        );
    }

    private static string ResolveDiscriminator(
        AbstractUnionViewArm unionArm,
        int discriminatorOutputIndex,
        CoreArmDefinition definition
    )
    {
        var expected = $"{CoreProjectName}:{definition.ResourceName}";

        return unionArm.ProjectionExpressionsInSelectOrder.ElementAtOrDefault(discriminatorOutputIndex) switch
        {
            AbstractUnionViewProjectionExpression.StringLiteral { Value: var value } when value == expected =>
                value,
            _ => throw Incompatible(Reason.DiscriminatorUnexpected, definition.ResourceName),
        };
    }

    private static DbColumnName ResolveIdentityColumn(
        AbstractUnionViewArm unionArm,
        int identityOutputIndex,
        DbTableModel root,
        CoreArmDefinition definition
    )
    {
        if (
            unionArm.ProjectionExpressionsInSelectOrder.ElementAtOrDefault(identityOutputIndex)
            is not AbstractUnionViewProjectionExpression.SourceColumn { ColumnName: var columnName }
        )
        {
            throw Incompatible(Reason.IdentityColumnUnresolved, definition.ResourceName);
        }

        var column =
            root.Columns.FirstOrDefault(candidate => candidate.ColumnName == columnName)
            ?? throw Incompatible(Reason.ColumnMissing, definition.ResourceName);

        RequireType(column, ScalarKind.Int64, isNullable: false, definition);
        return column.ColumnName;
    }

    private static DbColumnName ResolveNameColumn(
        DbTableModel root,
        string sourceJsonPath,
        bool isNullable,
        CoreArmDefinition definition
    )
    {
        var matches = root
            .Columns.Where(column =>
                column.Kind == ColumnKind.Scalar && column.SourceJsonPath?.Canonical == sourceJsonPath
            )
            .ToArray();

        var column = matches.Length switch
        {
            1 => matches[0],
            0 => throw Incompatible(Reason.ColumnMissing, definition.ResourceName),
            _ => throw Incompatible(Reason.ColumnAmbiguous, definition.ResourceName),
        };

        RequireType(column, ScalarKind.String, isNullable, definition);
        return column.ColumnName;
    }

    private static DbColumnName? ResolveReferenceColumn(
        RelationalResourceModel model,
        ReferenceSlotDefinition? slot,
        CoreArmDefinition definition
    )
    {
        if (slot is null)
        {
            return null;
        }

        // The path is absolute and root-scoped, so it cannot match a binding on a child table.
        var bindings = model
            .DocumentReferenceBindings.Where(binding =>
                binding.ReferenceObjectPath.Canonical == slot.ReferenceObjectPath
            )
            .ToArray();

        var referenceBinding = bindings.Length switch
        {
            1 => bindings[0],
            0 => throw Incompatible(Reason.ReferenceBindingMissing, definition.ResourceName),
            _ => throw Incompatible(Reason.ReferenceBindingAmbiguous, definition.ResourceName),
        };

        if (
            referenceBinding.TargetResource
                != new QualifiedResourceName(CoreProjectName, slot.TargetResourceName)
            || referenceBinding.IdentityBindings.Count != 1
            || referenceBinding.IdentityBindings[0].IdentityJsonPath.Canonical != slot.TargetIdentityJsonPath
        )
        {
            throw Incompatible(Reason.ReferenceBindingIncompatible, definition.ResourceName);
        }

        var columnName = referenceBinding.IdentityBindings[0].Column;
        var column =
            model.Root.Columns.FirstOrDefault(candidate => candidate.ColumnName == columnName)
            ?? throw Incompatible(Reason.ColumnMissing, definition.ResourceName);

        RequireType(column, ScalarKind.Int64, isNullable: true, definition);
        return column.ColumnName;
    }

    /// <summary>
    /// Requires the column's stored type. A nullable expectation accepts either nullability; a
    /// non-nullable one requires a non-nullable column, because the row cannot hold a null there.
    /// </summary>
    private static void RequireType(
        DbColumnModel column,
        ScalarKind kind,
        bool isNullable,
        CoreArmDefinition definition
    )
    {
        if (column.ScalarType?.Kind != kind || (!isNullable && column.IsNullable))
        {
            throw Incompatible(Reason.ColumnTypeIncompatible, definition.ResourceName);
        }
    }

    private string BuildSql(IReadOnlyList<EducationOrganizationProjectionArm> arms)
    {
        var writer = new SqlWriter(_sqlDialect);

        writer.Append("SELECT");
        if (_dialect == SqlDialect.Mssql)
        {
            writer.Append(" TOP (").AppendParameter(RowLimitParameterName).Append(")");
        }
        writer.AppendLine();

        using (writer.Indent())
        {
            var resultColumns = ResultColumnsInSelectOrder();
            for (var index = 0; index < resultColumns.Count; index++)
            {
                AppendQualifiedColumn(writer, ProjectionAlias, resultColumns[index])
                    .AppendLine(index < resultColumns.Count - 1 ? "," : "");
            }
        }

        writer.AppendLine("FROM (");
        using (writer.Indent())
        {
            for (var index = 0; index < arms.Count; index++)
            {
                if (index > 0)
                {
                    writer.AppendLine("UNION ALL");
                }

                AppendArm(writer, arms[index]);
            }
        }
        writer.AppendLine($") {ProjectionAlias}");

        writer.Append("ORDER BY ");
        AppendQualifiedColumn(writer, ProjectionAlias, _resultColumns.EducationOrganizationId).Append(" ASC");

        if (_dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine();
            writer.Append("LIMIT ").AppendParameter(RowLimitParameterName);
        }

        writer.AppendLine(";");
        return writer.ToString();
    }

    private void AppendArm(SqlWriter writer, EducationOrganizationProjectionArm arm)
    {
        writer.AppendLine("SELECT");
        using (writer.Indent())
        {
            AppendQualifiedColumn(writer, RootAlias, arm.EducationOrganizationIdColumn)
                .Append(" AS ")
                .AppendQuoted(_resultColumns.EducationOrganizationId.Value)
                .AppendLine(",");
            writer
                .Append(_sqlDialect.RenderStringLiteral(arm.Discriminator))
                .Append(" AS ")
                .AppendQuoted(_resultColumns.Discriminator.Value)
                .AppendLine(",");
            AppendName(writer, arm.NameOfInstitutionColumn)
                .Append(" AS ")
                .AppendQuoted(_resultColumns.NameOfInstitution.Value)
                .AppendLine(",");
            AppendName(writer, arm.ShortNameOfInstitutionColumn)
                .Append(" AS ")
                .AppendQuoted(_resultColumns.ShortNameOfInstitution.Value)
                .AppendLine(",");
            AppendSlot(
                    writer,
                    arm.LocalEducationAgencyReferenceColumn,
                    _resultColumns.LocalEducationAgencyReference
                )
                .AppendLine(",");
            AppendSlot(
                    writer,
                    arm.ParentLocalEducationAgencyReferenceColumn,
                    _resultColumns.ParentLocalEducationAgencyReference
                )
                .AppendLine(",");
            AppendSlot(
                    writer,
                    arm.EducationServiceCenterReferenceColumn,
                    _resultColumns.EducationServiceCenterReference
                )
                .AppendLine(",");
            AppendSlot(
                    writer,
                    arm.StateEducationAgencyReferenceColumn,
                    _resultColumns.StateEducationAgencyReference
                )
                .AppendLine();
        }
        writer.Append("FROM ").AppendTable(arm.Table).AppendLine($" {RootAlias}");
    }

    /// <summary>
    /// Appends a name column. PostgreSQL returns it as text. SQL Server returns its stored UTF-16 code
    /// units as <c>varbinary(max)</c>, because SqlClient would replace a lone surrogate with U+FFFD
    /// while decoding (<see cref="EducationOrganizationProjectionNameEncoding"/>):
    /// <c>CAST(CONVERT(nvarchar(max), CASE WHEN col COLLATE Latin1_General_100_BIN2 IS NULL THEN NULL
    /// ELSE col END) AS varbinary(max))</c>. The expression keeps what reading the column as text kept:
    /// <list type="bullet">
    /// <item>Nothing is truncated: both conversions are to <c>max</c> types.</item>
    /// <item>The text is the stored text. The value converted to <c>nvarchar</c> is the column itself,
    /// under its own collation, so a column of another character type (another code page, or UTF-8)
    /// converts from its own encoding, and an <c>nvarchar</c> column's code units are copied as they
    /// are. A null stays null and an empty string stays empty.</item>
    /// <item>A column that is not character data still fails the statement, as reading it as text
    /// did, rather than being converted to bytes. <c>COLLATE</c> accepts only character expressions
    /// (error 447 otherwise, a permanent incompatibility), and it appears only in the null test,
    /// whose outcome no collation can change, so it never translates the value.</item>
    /// </list>
    /// </summary>
    private SqlWriter AppendName(SqlWriter writer, DbColumnName column)
    {
        if (_dialect != SqlDialect.Mssql)
        {
            return AppendQualifiedColumn(writer, RootAlias, column);
        }

        writer.Append("CAST(CONVERT(nvarchar(max), CASE WHEN ");
        AppendQualifiedColumn(writer, RootAlias, column);
        writer.Append(" COLLATE Latin1_General_100_BIN2 IS NULL THEN NULL ELSE ");
        AppendQualifiedColumn(writer, RootAlias, column);
        return writer.Append(" END) AS varbinary(max))");
    }

    /// <summary>
    /// Appends a reference slot: the stored identifier, or a typed null for a slot the arm's type
    /// cannot carry. The null is typed because PostgreSQL resolves a chain of <c>UNION ALL</c> arms
    /// pairwise, and two leading untyped nulls would resolve to <c>text</c> before meeting a
    /// <c>bigint</c> arm.
    /// </summary>
    private SqlWriter AppendSlot(SqlWriter writer, DbColumnName? sourceColumn, DbColumnName alias)
    {
        if (sourceColumn is { } column)
        {
            AppendQualifiedColumn(writer, RootAlias, column);
        }
        else
        {
            writer.Append(
                $"CAST(NULL AS {_sqlDialect.RenderColumnType(new RelationalScalarType(ScalarKind.Int64))})"
            );
        }

        return writer.Append(" AS ").AppendQuoted(alias.Value);
    }

    private IReadOnlyList<DbColumnName> ResultColumnsInSelectOrder()
    {
        return
        [
            _resultColumns.EducationOrganizationId,
            _resultColumns.Discriminator,
            _resultColumns.NameOfInstitution,
            _resultColumns.ShortNameOfInstitution,
            _resultColumns.LocalEducationAgencyReference,
            _resultColumns.ParentLocalEducationAgencyReference,
            _resultColumns.EducationServiceCenterReference,
            _resultColumns.StateEducationAgencyReference,
        ];
    }

    private static SqlWriter AppendQualifiedColumn(SqlWriter writer, string tableAlias, DbColumnName column)
    {
        return writer.Append($"{tableAlias}.").AppendQuoted(column.Value);
    }

    private static IncompatibleMappingException Incompatible(Reason reason, string? resourceName = null)
    {
        return new IncompatibleMappingException(
            new EducationOrganizationProjectionMappingIncompatibility(reason, resourceName)
        );
    }

    private sealed record ReferenceSlotDefinition(
        string ReferenceObjectPath,
        string TargetResourceName,
        string TargetIdentityJsonPath
    );

    private sealed record CoreArmDefinition(
        string ResourceName,
        ReferenceSlotDefinition? LocalEducationAgency,
        ReferenceSlotDefinition? ParentLocalEducationAgency,
        ReferenceSlotDefinition? EducationServiceCenter,
        ReferenceSlotDefinition? StateEducationAgency
    );

    /// <summary>
    /// Carries the first incompatibility out of the nested resolution helpers. It never leaves
    /// <see cref="Compile"/>.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Sonar",
        "S3871",
        Justification = "Private control flow caught inside the compiler."
    )]
    private sealed class IncompatibleMappingException(
        EducationOrganizationProjectionMappingIncompatibility incompatibility
    ) : Exception
    {
        public EducationOrganizationProjectionMappingIncompatibility Incompatibility { get; } =
            incompatibility;
    }
}
