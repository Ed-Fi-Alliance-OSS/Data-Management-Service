// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for dialect identifier shortening using Pgsql rules.
/// </summary>
[TestFixture]
public class Given_Pgsql_Identifier_Shortening
{
    private ShorteningScenario _scenario = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _scenario = ShorteningScenario.Build(new PgsqlDialectRules(), "Pgsql");
    }

    /// <summary>
    /// It should shorten identifiers across the derived model.
    /// </summary>
    [Test]
    public void It_should_shorten_identifiers_across_the_model()
    {
        IdentifierShorteningAssertions.AssertShortened(_scenario);
    }
}

/// <summary>
/// Test fixture for dialect identifier shortening using Mssql rules.
/// </summary>
[TestFixture]
public class Given_Mssql_Identifier_Shortening
{
    private ShorteningScenario _scenario = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _scenario = ShorteningScenario.Build(new MssqlDialectRules(), "Mssql");
    }

    /// <summary>
    /// It should shorten identifiers across the derived model.
    /// </summary>
    [Test]
    public void It_should_shorten_identifiers_across_the_model()
    {
        IdentifierShorteningAssertions.AssertShortened(_scenario);
    }
}

/// <summary>
/// Test fixture for cross-dialect identifier shortening behavior.
/// </summary>
[TestFixture]
public class Given_Cross_Dialect_Identifier_Shortening
{
    private static readonly string LongIdentifier = $"CrossDialectIdentifier{new string('A', 80)}";

    /// <summary>
    /// It should produce different shortened values for Pgsql and Mssql limits.
    /// </summary>
    [Test]
    public void It_should_produce_different_shortened_values_for_pgsql_and_mssql_limits()
    {
        var pgsqlRules = new PgsqlDialectRules();
        var mssqlRules = new MssqlDialectRules();

        LongIdentifier.Length.Should().BeGreaterThan(pgsqlRules.MaxIdentifierLength);
        LongIdentifier.Length.Should().BeLessThanOrEqualTo(mssqlRules.MaxIdentifierLength);

        var pgsqlShortened = pgsqlRules.ShortenIdentifier(LongIdentifier);
        var mssqlShortened = mssqlRules.ShortenIdentifier(LongIdentifier);

        pgsqlShortened.Should().NotBe(LongIdentifier);
        mssqlShortened.Should().Be(LongIdentifier);
        pgsqlShortened.Should().NotBe(mssqlShortened);
        pgsqlShortened.Length.Should().BeLessThanOrEqualTo(pgsqlRules.MaxIdentifierLength);
        mssqlShortened.Length.Should().BeLessThanOrEqualTo(mssqlRules.MaxIdentifierLength);
        pgsqlRules.ShortenIdentifier(LongIdentifier).Should().Be(pgsqlShortened);
        mssqlRules.ShortenIdentifier(LongIdentifier).Should().Be(mssqlShortened);
    }
}

/// <summary>
/// Test fixture for schema shortening with a small identifier limit.
/// </summary>
[TestFixture]
public class Given_Short_Identifier_Limit
{
    private ShorteningScenario _scenario = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _scenario = ShorteningScenario.Build(new TinyDialectRules(20), "Tiny");
    }

    /// <summary>
    /// It should shorten project schema identifiers.
    /// </summary>
    [Test]
    public void It_should_shorten_project_schema_identifiers()
    {
        var expectedSchema = _scenario.DialectRules.ShortenIdentifier(_scenario.Identifiers.SchemaName);
        var projectSchema = _scenario.Result.ProjectSchemasInEndpointOrder.Single(schema =>
            schema.ProjectEndpointName == ShorteningScenario.CoreProjectEndpointName
        );

        projectSchema.PhysicalSchema.Value.Should().Be(expectedSchema);
    }

    /// <summary>
    /// Test dialect rules that enforce a small identifier length limit by truncation, used to drive shortening
    /// behavior in fixtures.
    /// </summary>
    private sealed class TinyDialectRules : ISqlDialectRules
    {
        private static readonly SqlScalarTypeDefaults Defaults = new PgsqlDialectRules().ScalarTypeDefaults;

        /// <summary>
        /// Initializes a new instance with the specified maximum identifier length.
        /// </summary>
        public TinyDialectRules(int maxIdentifierLength)
        {
            if (maxIdentifierLength <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxIdentifierLength),
                    maxIdentifierLength,
                    "Identifier length limit must be positive."
                );
            }

            MaxIdentifierLength = maxIdentifierLength;
        }

        /// <summary>
        /// Gets the dialect identifier reported by this fixture rules implementation.
        /// </summary>
        public SqlDialect Dialect => SqlDialect.Pgsql;

        /// <summary>
        /// Gets the configured maximum identifier length used for truncation.
        /// </summary>
        public int MaxIdentifierLength { get; }

        /// <summary>
        /// Gets the scalar type defaults reused by this fixture rules implementation.
        /// </summary>
        public SqlScalarTypeDefaults ScalarTypeDefaults => Defaults;

        /// <summary>
        /// Shortens an identifier by truncating it to <see cref="MaxIdentifierLength"/>.
        /// </summary>
        public string ShortenIdentifier(string identifier)
        {
            if (identifier is null)
            {
                throw new ArgumentNullException(nameof(identifier));
            }

            return identifier.Length <= MaxIdentifierLength ? identifier : identifier[..MaxIdentifierLength];
        }
    }
}

/// <summary>
/// Captures a single identifier-shortening test run, including the derived model and the long identifiers
/// used as inputs.
/// </summary>
internal sealed record ShorteningScenario(
    DerivedRelationalModelSet Result,
    ShorteningIdentifiers Identifiers,
    ISqlDialectRules DialectRules
)
{
    internal const string CoreProjectEndpointName = "ed-fi";
    internal static readonly QualifiedResourceName Resource = new("Ed-Fi", "School");
    internal static readonly QualifiedResourceName AbstractResource = new("Ed-Fi", "SchoolTypeDescriptor");

    /// <summary>
    /// Builds a test scenario by injecting long identifiers into a derived model set, then running the dialect
    /// shortening pass.
    /// </summary>
    public static ShorteningScenario Build(ISqlDialectRules dialectRules, string prefix)
    {
        ArgumentNullException.ThrowIfNull(dialectRules);

        var identifiers = ShorteningIdentifiers.Create(prefix, dialectRules.MaxIdentifierLength + 12);
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new IdentifierShorteningFixturePass(effectiveSchemaSet, identifiers),
                new ApplyDialectIdentifierShorteningPass(),
            }
        );

        var result = builder.Build(effectiveSchemaSet, dialectRules.Dialect, dialectRules);

        return new ShorteningScenario(result, identifiers, dialectRules);
    }
}

/// <summary>
/// Holds long identifier values used to verify dialect shortening across schemas, tables, columns, constraints,
/// indexes, triggers, and abstract resource artifacts.
/// </summary>
internal sealed record ShorteningIdentifiers(
    string SchemaName,
    string TableName,
    string KeyColumnName,
    string FkColumnName,
    string IdentityColumnName,
    string DescriptorColumnName,
    string UniqueConstraintName,
    string ForeignKeyConstraintName,
    string AllOrNoneConstraintName,
    string IndexName,
    string TriggerName,
    string AbstractTableName,
    string AbstractColumnName,
    string ViewName,
    string ViewColumnName
)
{
    /// <summary>
    /// Creates long identifier values using the supplied prefix and target length.
    /// </summary>
    public static ShorteningIdentifiers Create(string prefix, int length)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException("Prefix must be non-empty.", nameof(prefix));
        }

        return new ShorteningIdentifiers(
            SchemaName: BuildLongIdentifier(prefix, "Schema", length),
            TableName: BuildLongIdentifier(prefix, "Table", length),
            KeyColumnName: BuildLongIdentifier(prefix, "KeyColumn", length),
            FkColumnName: BuildLongIdentifier(prefix, "FkColumn", length),
            IdentityColumnName: BuildLongIdentifier(prefix, "IdentityColumn", length),
            DescriptorColumnName: BuildLongIdentifier(prefix, "DescriptorColumn", length),
            UniqueConstraintName: BuildLongIdentifier(prefix, "UniqueConstraint", length),
            ForeignKeyConstraintName: BuildLongIdentifier(prefix, "ForeignKeyConstraint", length),
            AllOrNoneConstraintName: BuildLongIdentifier(prefix, "AllOrNoneConstraint", length),
            IndexName: BuildLongIdentifier(prefix, "Index", length),
            TriggerName: BuildLongIdentifier(prefix, "Trigger", length),
            AbstractTableName: BuildLongIdentifier(prefix, "AbstractTable", length),
            AbstractColumnName: BuildLongIdentifier(prefix, "AbstractColumn", length),
            ViewName: BuildLongIdentifier(prefix, "View", length),
            ViewColumnName: BuildLongIdentifier(prefix, "ViewColumn", length)
        );
    }

    /// <summary>
    /// Builds an identifier string by padding a prefix/label base with <c>A</c> characters to reach the target length.
    /// </summary>
    private static string BuildLongIdentifier(string prefix, string label, int length)
    {
        var baseValue = $"{prefix}{label}";

        if (baseValue.Length >= length)
        {
            return baseValue;
        }

        return baseValue + new string('A', length - baseValue.Length);
    }
}

/// <summary>
/// Assertion helpers that validate expected identifier shortening across the derived relational model set.
/// </summary>
internal static class IdentifierShorteningAssertions
{
    /// <summary>
    /// Asserts that all relevant identifiers in the derived model set are shortened as expected for the dialect.
    /// </summary>
    public static void AssertShortened(ShorteningScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var dialectRules = scenario.DialectRules;
        var identifiers = scenario.Identifiers;
        var result = scenario.Result;

        var expectedSchema = dialectRules.ShortenIdentifier(identifiers.SchemaName);
        var expectedTable = dialectRules.ShortenIdentifier(identifiers.TableName);
        var expectedPrimaryKey = dialectRules.ShortenIdentifier($"PK_{identifiers.TableName}");
        var expectedKey = dialectRules.ShortenIdentifier(identifiers.KeyColumnName);
        var expectedFk = dialectRules.ShortenIdentifier(identifiers.FkColumnName);
        var expectedIdentity = dialectRules.ShortenIdentifier(identifiers.IdentityColumnName);
        var expectedDescriptor = dialectRules.ShortenIdentifier(identifiers.DescriptorColumnName);
        var expectedUnique = dialectRules.ShortenIdentifier(identifiers.UniqueConstraintName);
        var expectedForeign = dialectRules.ShortenIdentifier(identifiers.ForeignKeyConstraintName);
        var expectedAllNone = dialectRules.ShortenIdentifier(identifiers.AllOrNoneConstraintName);
        var expectedIndex = dialectRules.ShortenIdentifier(identifiers.IndexName);
        var expectedTrigger = dialectRules.ShortenIdentifier(identifiers.TriggerName);
        var expectedAbstractTable = dialectRules.ShortenIdentifier(identifiers.AbstractTableName);
        var expectedAbstractPrimaryKey = dialectRules.ShortenIdentifier(
            $"PK_{identifiers.AbstractTableName}"
        );
        var expectedAbstractColumn = dialectRules.ShortenIdentifier(identifiers.AbstractColumnName);
        var expectedView = dialectRules.ShortenIdentifier(identifiers.ViewName);
        var expectedViewColumn = dialectRules.ShortenIdentifier(identifiers.ViewColumnName);

        var projectSchema = result.ProjectSchemasInEndpointOrder.Single(schema =>
            schema.ProjectEndpointName == ShorteningScenario.CoreProjectEndpointName
        );

        projectSchema.PhysicalSchema.Value.Should().Be(expectedSchema);

        var resourceModel = result.ConcreteResourcesInNameOrder.Single().RelationalModel;

        resourceModel.PhysicalSchema.Value.Should().Be(expectedSchema);
        resourceModel.Root.Table.Schema.Value.Should().Be(expectedSchema);
        resourceModel.Root.Table.Name.Should().Be(expectedTable);
        resourceModel.Root.Key.ConstraintName.Should().Be(expectedPrimaryKey);
        resourceModel.Root.Key.Columns.Single().ColumnName.Value.Should().Be(expectedKey);
        resourceModel
            .Root.Columns.Single(column => column.ColumnName.Value == expectedKey)
            .ColumnName.Value.Should()
            .Be(expectedKey);

        var uniqueConstraint = resourceModel.Root.Constraints.OfType<TableConstraint.Unique>().Single();
        uniqueConstraint.Name.Should().Be(expectedUnique);
        uniqueConstraint.Columns.Single().Value.Should().Be(expectedKey);

        var foreignKey = resourceModel.Root.Constraints.OfType<TableConstraint.ForeignKey>().Single();
        foreignKey.Name.Should().Be(expectedForeign);
        foreignKey.Columns.Single().Value.Should().Be(expectedFk);
        foreignKey.TargetTable.Schema.Value.Should().Be(expectedSchema);
        foreignKey.TargetTable.Name.Should().Be(expectedTable);
        foreignKey.TargetColumns.Single().Value.Should().Be(expectedKey);

        var allOrNone = resourceModel
            .Root.Constraints.OfType<TableConstraint.AllOrNoneNullability>()
            .Single();
        allOrNone.Name.Should().Be(expectedAllNone);
        allOrNone.FkColumn.Value.Should().Be(expectedFk);
        allOrNone.DependentColumns.Single().Value.Should().Be(expectedIdentity);

        var binding = resourceModel.DocumentReferenceBindings.Single();
        binding.Table.Schema.Value.Should().Be(expectedSchema);
        binding.Table.Name.Should().Be(expectedTable);
        binding.FkColumn.Value.Should().Be(expectedFk);
        binding.IdentityBindings.Single().Column.Value.Should().Be(expectedIdentity);

        var edge = resourceModel.DescriptorEdgeSources.Single();
        edge.Table.Schema.Value.Should().Be(expectedSchema);
        edge.Table.Name.Should().Be(expectedTable);
        edge.FkColumn.Value.Should().Be(expectedDescriptor);

        var index = result.IndexesInCreateOrder.Single();
        index.Name.Value.Should().Be(expectedIndex);
        index.Table.Schema.Value.Should().Be(expectedSchema);
        index.Table.Name.Should().Be(expectedTable);
        index.KeyColumns.Single().Value.Should().Be(expectedKey);

        var trigger = result.TriggersInCreateOrder.Single();
        trigger.Name.Value.Should().Be(expectedTrigger);
        trigger.Table.Schema.Value.Should().Be(expectedSchema);
        trigger.Table.Name.Should().Be(expectedTable);
        trigger.KeyColumns.Single().Value.Should().Be(expectedKey);

        var abstractTable = result.AbstractIdentityTablesInNameOrder.Single();
        abstractTable.TableModel.Table.Schema.Value.Should().Be(expectedSchema);
        abstractTable.TableModel.Table.Name.Should().Be(expectedAbstractTable);
        abstractTable.TableModel.Key.ConstraintName.Should().Be(expectedAbstractPrimaryKey);
        abstractTable.TableModel.Key.Columns.Single().ColumnName.Value.Should().Be(expectedAbstractColumn);

        var abstractView = result.AbstractUnionViewsInNameOrder.Single();
        abstractView.ViewName.Schema.Value.Should().Be(expectedSchema);
        abstractView.ViewName.Name.Should().Be(expectedView);
        abstractView.OutputColumnsInSelectOrder.Single().ColumnName.Value.Should().Be(expectedViewColumn);

        var arm = abstractView.UnionArmsInOrder.Single();
        arm.FromTable.Schema.Value.Should().Be(expectedSchema);
        arm.FromTable.Name.Should().Be(expectedTable);
        var sourceColumnProjection = arm
            .ProjectionExpressionsInSelectOrder.Single()
            .Should()
            .BeOfType<AbstractUnionViewProjectionExpression.SourceColumn>()
            .Subject;
        sourceColumnProjection.ColumnName.Value.Should().Be(expectedKey);
    }
}

/// <summary>
/// Fixture pass that injects long identifiers directly into the builder context so shortening behavior can be
/// tested without depending on schema derivation specifics.
/// </summary>
internal sealed class IdentifierShorteningFixturePass : IRelationalModelSetPass
{
    private readonly ResourceKeyEntry _resourceKey;
    private readonly ResourceKeyEntry _abstractKey;
    private readonly ShorteningIdentifiers _identifiers;

    /// <summary>
    /// Initializes a new fixture pass, resolving resource keys required for seeded model inventory.
    /// </summary>
    public IdentifierShorteningFixturePass(
        EffectiveSchemaSet effectiveSchemaSet,
        ShorteningIdentifiers identifiers
    )
    {
        ArgumentNullException.ThrowIfNull(effectiveSchemaSet);
        ArgumentNullException.ThrowIfNull(identifiers);

        _resourceKey = DerivedRelationalModelSetInvariantTestHelpers.FindResourceKey(
            effectiveSchemaSet,
            ShorteningScenario.Resource.ProjectName,
            ShorteningScenario.Resource.ResourceName
        );
        _abstractKey = DerivedRelationalModelSetInvariantTestHelpers.FindResourceKey(
            effectiveSchemaSet,
            ShorteningScenario.AbstractResource.ProjectName,
            ShorteningScenario.AbstractResource.ResourceName
        );
        _identifiers = identifiers;
    }

    /// <summary>
    /// Seeds the builder context with models and inventories that contain long identifiers.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        UpdateProjectSchema(context);

        var schema = new DbSchemaName(_identifiers.SchemaName);
        var tableName = new DbTableName(schema, _identifiers.TableName);

        var keyColumn = new DbKeyColumn(
            new DbColumnName(_identifiers.KeyColumnName),
            ColumnKind.ParentKeyPart
        );
        var columns = new[]
        {
            new DbColumnModel(
                new DbColumnName(_identifiers.KeyColumnName),
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new DbColumnModel(
                new DbColumnName(_identifiers.FkColumnName),
                ColumnKind.DocumentFk,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.ref"),
                TargetResource: ShorteningScenario.AbstractResource
            ),
            new DbColumnModel(
                new DbColumnName(_identifiers.IdentityColumnName),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String, MaxLength: 20),
                IsNullable: false,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.ref.identity"),
                TargetResource: null
            ),
            new DbColumnModel(
                new DbColumnName(_identifiers.DescriptorColumnName),
                ColumnKind.DescriptorFk,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.descriptor"),
                TargetResource: ShorteningScenario.AbstractResource
            ),
        };

        var constraints = new TableConstraint[]
        {
            new TableConstraint.Unique(
                _identifiers.UniqueConstraintName,
                new[] { new DbColumnName(_identifiers.KeyColumnName) }
            ),
            new TableConstraint.ForeignKey(
                _identifiers.ForeignKeyConstraintName,
                new[] { new DbColumnName(_identifiers.FkColumnName) },
                tableName,
                new[] { new DbColumnName(_identifiers.KeyColumnName) },
                ReferentialAction.NoAction,
                ReferentialAction.NoAction
            ),
            new TableConstraint.AllOrNoneNullability(
                _identifiers.AllOrNoneConstraintName,
                new DbColumnName(_identifiers.FkColumnName),
                new[] { new DbColumnName(_identifiers.IdentityColumnName) }
            ),
        };

        var table = new DbTableModel(
            tableName,
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey(ConstraintNaming.BuildPrimaryKeyName(tableName), [keyColumn]),
            columns,
            constraints
        );

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.ref"),
            Table: tableName,
            FkColumn: new DbColumnName(_identifiers.FkColumnName),
            TargetResource: ShorteningScenario.AbstractResource,
            IdentityBindings: new[]
            {
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.ref.identity"),
                    new DbColumnName(_identifiers.IdentityColumnName)
                ),
            }
        );

        var descriptorEdge = new DescriptorEdgeSource(
            IsIdentityComponent: false,
            DescriptorValuePath: JsonPathExpressionCompiler.Compile("$.descriptor"),
            Table: tableName,
            FkColumn: new DbColumnName(_identifiers.DescriptorColumnName),
            DescriptorResource: ShorteningScenario.AbstractResource
        );

        var resourceModel = new RelationalResourceModel(
            ShorteningScenario.Resource,
            schema,
            ResourceStorageKind.RelationalTables,
            table,
            new[] { table },
            new[] { binding },
            new[] { descriptorEdge }
        );

        context.ConcreteResourcesInNameOrder.Add(
            new ConcreteResourceModel(_resourceKey, ResourceStorageKind.RelationalTables, resourceModel)
        );

        var abstractTableName = new DbTableName(schema, _identifiers.AbstractTableName);
        var abstractTable = new DbTableModel(
            abstractTableName,
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey(
                ConstraintNaming.BuildPrimaryKeyName(abstractTableName),
                new[]
                {
                    new DbKeyColumn(
                        new DbColumnName(_identifiers.AbstractColumnName),
                        ColumnKind.ParentKeyPart
                    ),
                }
            ),
            new[]
            {
                new DbColumnModel(
                    new DbColumnName(_identifiers.AbstractColumnName),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            },
            Array.Empty<TableConstraint>()
        );

        context.AbstractIdentityTablesInNameOrder.Add(
            new AbstractIdentityTableInfo(_abstractKey, abstractTable)
        );

        var abstractView = new AbstractUnionViewInfo(
            _abstractKey,
            new DbTableName(schema, _identifiers.ViewName),
            new[]
            {
                new AbstractUnionViewOutputColumn(
                    new DbColumnName(_identifiers.ViewColumnName),
                    new RelationalScalarType(ScalarKind.Int64),
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            },
            new[]
            {
                new AbstractUnionViewArm(
                    _resourceKey,
                    tableName,
                    new AbstractUnionViewProjectionExpression[]
                    {
                        new AbstractUnionViewProjectionExpression.SourceColumn(
                            new DbColumnName(_identifiers.KeyColumnName)
                        ),
                    }
                ),
            }
        );

        context.AbstractUnionViewsInNameOrder.Add(abstractView);

        context.IndexInventory.Add(
            new DbIndexInfo(
                new DbIndexName(_identifiers.IndexName),
                tableName,
                new[] { new DbColumnName(_identifiers.KeyColumnName) },
                IsUnique: true,
                DbIndexKind.UniqueConstraint
            )
        );

        context.TriggerInventory.Add(
            new DbTriggerInfo(
                new DbTriggerName(_identifiers.TriggerName),
                tableName,
                DbTriggerKind.DocumentStamping,
                new[] { new DbColumnName(_identifiers.KeyColumnName) }
            )
        );
    }

    /// <summary>
    /// Updates the core project schema to use the seeded long schema identifier.
    /// </summary>
    private void UpdateProjectSchema(RelationalModelSetBuilderContext context)
    {
        context.UpdateProjectSchema(
            ShorteningScenario.CoreProjectEndpointName,
            schema => schema with { PhysicalSchema = new DbSchemaName(_identifiers.SchemaName) }
        );
    }
}
