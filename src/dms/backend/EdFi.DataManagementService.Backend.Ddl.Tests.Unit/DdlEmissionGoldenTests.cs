// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

/// <summary>
/// Base class for DDL emission golden file tests.
/// </summary>
public abstract class DdlEmissionGoldenTestBase
{
    /// <summary>
    /// Find project root by looking for the .csproj file.
    /// </summary>
    protected static string FindProjectRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "EdFi.DataManagementService.Backend.Ddl.Tests.Unit.csproj"
            );
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Unable to locate EdFi.DataManagementService.Backend.Ddl.Tests.Unit.csproj in parent directories."
        );
    }

    /// <summary>
    /// Run git diff between expected and actual files.
    /// </summary>
    protected static string RunGitDiff(string expectedPath, string actualPath)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("diff");
        startInfo.ArgumentList.Add("--no-index");
        startInfo.ArgumentList.Add("--ignore-space-at-eol");
        startInfo.ArgumentList.Add("--ignore-cr-at-eol");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(expectedPath);
        startInfo.ArgumentList.Add(actualPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();

        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            throw new TimeoutException("git diff timed out after 30 seconds");
        }

        if (process.ExitCode == 0)
        {
            return string.Empty;
        }

        if (process.ExitCode == 1)
        {
            return output;
        }

        return string.IsNullOrWhiteSpace(error) ? output : $"{error}\n{output}".Trim();
    }

    /// <summary>
    /// Check if UPDATE_GOLDENS environment variable is set.
    /// </summary>
    protected static bool ShouldUpdateGoldens()
    {
        var update = Environment.GetEnvironmentVariable("UPDATE_GOLDENS");

        return string.Equals(update, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(update, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Emits DDL and writes the actual output file. Call this in SetUp (arrange + act).
    /// </summary>
    protected static GoldenTestPaths EmitDdl(
        string fixtureName,
        SqlDialect dialect,
        DerivedRelationalModelSet modelSet
    )
    {
        var projectRoot = FindProjectRoot(TestContext.CurrentContext.TestDirectory);
        var fixtureRoot = Path.Combine(projectRoot, "Fixtures", "ddl-emission");
        var dialectName = dialect switch
        {
            SqlDialect.Pgsql => "pgsql",
            SqlDialect.Mssql => "mssql",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect."),
        };
        var expectedPath = Path.Combine(fixtureRoot, "expected", dialectName, $"{fixtureName}.sql");
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "ddl-emission",
            dialectName,
            $"{fixtureName}.sql"
        );

        var dialectInstance = SqlDialectFactory.Create(dialect);
        var emitter = new RelationalModelDdlEmitter(dialectInstance);
        var ddl = emitter.Emit(modelSet);

        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, ddl);

        if (ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, ddl);
        }

        return new GoldenTestPaths(expectedPath, actualPath);
    }

    /// <summary>
    /// Asserts that the emitted DDL matches the golden file. Call this in the test method.
    /// </summary>
    protected static void AssertGoldenMatch(GoldenTestPaths paths)
    {
        File.Exists(paths.ExpectedPath)
            .Should()
            .BeTrue($"Golden file missing at {paths.ExpectedPath}. Set UPDATE_GOLDENS=1 to generate.");

        var diffOutput = RunGitDiff(paths.ExpectedPath, paths.ActualPath);

        if (!string.IsNullOrWhiteSpace(diffOutput))
        {
            Assert.Fail(
                $"DDL output does not match golden file.\n\n"
                    + $"Expected: {paths.ExpectedPath}\n"
                    + $"Actual: {paths.ActualPath}\n\n"
                    + $"Diff:\n{diffOutput}"
            );
        }
    }

    protected record GoldenTestPaths(string ExpectedPath, string ActualPath);
}

// ═══════════════════════════════════════════════════════════════════
// Golden File Tests - Nested Collections
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_With_NestedCollections_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = NestedCollectionsFixture.Build(SqlDialect.Pgsql);
        _paths = EmitDdl("nested-collections", SqlDialect.Pgsql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_DdlEmitter_With_NestedCollections_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = NestedCollectionsFixture.Build(SqlDialect.Mssql);
        _paths = EmitDdl("nested-collections", SqlDialect.Mssql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Golden File Tests - Polymorphic Abstract Views
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_With_PolymorphicAbstract_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = PolymorphicAbstractFixture.Build(SqlDialect.Pgsql);
        _paths = EmitDdl("polymorphic-abstract", SqlDialect.Pgsql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_DdlEmitter_With_PolymorphicAbstract_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = PolymorphicAbstractFixture.Build(SqlDialect.Mssql);
        _paths = EmitDdl("polymorphic-abstract", SqlDialect.Mssql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Golden File Tests - Identity Propagation
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_With_IdentityPropagation_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = IdentityPropagationFixture.Build(SqlDialect.Pgsql);
        _paths = EmitDdl("identity-propagation", SqlDialect.Pgsql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_DdlEmitter_With_IdentityPropagation_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = IdentityPropagationFixture.Build(SqlDialect.Mssql);
        _paths = EmitDdl("identity-propagation", SqlDialect.Mssql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Golden File Tests - Extension Mapping
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_With_ExtensionMapping_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = ExtensionMappingFixture.Build(SqlDialect.Pgsql);
        _paths = EmitDdl("extension-mapping", SqlDialect.Pgsql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_DdlEmitter_With_ExtensionMapping_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = ExtensionMappingFixture.Build(SqlDialect.Mssql);
        _paths = EmitDdl("extension-mapping", SqlDialect.Mssql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Golden File Tests - Key Unification
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_With_KeyUnification_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;
    private string _ddlContent = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = KeyUnificationFixture.Build(SqlDialect.Pgsql);
        _paths = EmitDdl("key-unification", SqlDialect.Pgsql, modelSet);
        _ddlContent = File.ReadAllText(_paths.ActualPath);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }

    [Test]
    public void It_should_emit_stored_generated_column_for_alias()
    {
        _ddlContent.Should().Contain("GENERATED ALWAYS AS", "alias columns should emit GENERATED ALWAYS AS");
        _ddlContent.Should().Contain("STORED", "alias columns should be STORED in PostgreSQL");
    }
}

[TestFixture]
public class Given_DdlEmitter_With_KeyUnification_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;
    private string _ddlContent = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = KeyUnificationFixture.Build(SqlDialect.Mssql);
        _paths = EmitDdl("key-unification", SqlDialect.Mssql, modelSet);
        _ddlContent = File.ReadAllText(_paths.ActualPath);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }

    [Test]
    public void It_should_emit_persisted_computed_column_for_alias()
    {
        _ddlContent.Should().Contain("PERSISTED", "alias columns should be PERSISTED in MSSQL");
    }

    [Test]
    public void It_should_not_use_update_on_alias_columns()
    {
        // Alias columns are computed — UPDATE() on them would fail with Msg 2114
        _ddlContent
            .Should()
            .NotContain("UPDATE([CourseOffering_SchoolId])", "UPDATE() must not reference alias columns");
        _ddlContent
            .Should()
            .NotContain("UPDATE([School_SchoolId])", "UPDATE() must not reference alias columns");
    }

    [Test]
    public void It_should_not_set_alias_columns_in_propagation()
    {
        // Alias columns are computed — SET on them would fail with Msg 271
        _ddlContent
            .Should()
            .NotContain("SET r.[CourseOffering_SchoolId]", "SET must not target alias columns");
        _ddlContent.Should().NotContain("SET r.[School_SchoolId]", "SET must not target alias columns");
    }

    [Test]
    public void It_should_use_canonical_column_in_update_guard()
    {
        _ddlContent
            .Should()
            .Contain("UPDATE([SchoolId_Unified])", "UPDATE() should reference canonical stored column");
    }

    [Test]
    public void It_should_use_canonical_column_in_propagation_set()
    {
        _ddlContent
            .Should()
            .Contain("[SchoolId_Unified]", "propagation SET should reference canonical stored column");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Golden File Tests - FK Support Indexes
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_With_FkSupportIndex_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;
    private string _ddlContent = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = FkSupportIndexFixture.Build(SqlDialect.Pgsql);
        _paths = EmitDdl("fk-support-index", SqlDialect.Pgsql, modelSet);
        _ddlContent = File.ReadAllText(_paths.ActualPath);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }

    [Test]
    public void It_should_emit_fk_support_index_exactly_once()
    {
        // Use dialect-specific quoted identifier pattern for precise matching
        // Account for IF NOT EXISTS and schema prefix in PostgreSQL DDL
        var indexMatches = System.Text.RegularExpressions.Regex.Matches(
            _ddlContent,
            @"CREATE\s+INDEX\s+IF\s+NOT\s+EXISTS\s+""[^""]+""\.""IX_Enrollment_SchoolId""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        indexMatches.Count.Should().Be(1, "FK-support index should be emitted exactly once");
    }
}

[TestFixture]
public class Given_DdlEmitter_With_FkSupportIndex_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;
    private string _ddlContent = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = FkSupportIndexFixture.Build(SqlDialect.Mssql);
        _paths = EmitDdl("fk-support-index", SqlDialect.Mssql, modelSet);
        _ddlContent = File.ReadAllText(_paths.ActualPath);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }

    [Test]
    public void It_should_emit_fk_support_index_exactly_once()
    {
        // Use dialect-specific quoted identifier pattern for precise matching
        var indexMatches = System.Text.RegularExpressions.Regex.Matches(
            _ddlContent,
            @"CREATE\s+INDEX\s+\[IX_Enrollment_SchoolId\]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        indexMatches.Count.Should().Be(1, "FK-support index should be emitted exactly once");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Fixture Builders
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Fixture for nested collections scenario:
/// School → SchoolAddress → SchoolAddressPhoneNumber
/// </summary>
internal static class NestedCollectionsFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        // Column names
        var documentIdColumn = new DbColumnName("DocumentId");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var addressOrdinalColumn = new DbColumnName("AddressOrdinal");
        var streetColumn = new DbColumnName("Street");
        var phoneOrdinalColumn = new DbColumnName("PhoneNumberOrdinal");
        var phoneNumberColumn = new DbColumnName("PhoneNumber");

        // Root table: School
        var schoolTableName = new DbTableName(schema, "School");
        var schoolTable = new DbTableModel(
            schoolTableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_School", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        // Child collection: SchoolAddress
        var addressTableName = new DbTableName(schema, "SchoolAddress");
        var addressTable = new DbTableModel(
            addressTableName,
            new JsonPathExpression("$.addresses[*]", []),
            new TableKey(
                "PK_SchoolAddress",
                [
                    new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(addressOrdinalColumn, ColumnKind.Scalar),
                ]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    addressOrdinalColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    streetColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 100),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_SchoolAddress_School",
                    [documentIdColumn],
                    schoolTableName,
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        // Nested collection: SchoolAddressPhoneNumber
        var phoneTableName = new DbTableName(schema, "SchoolAddressPhoneNumber");
        var phoneTable = new DbTableModel(
            phoneTableName,
            new JsonPathExpression("$.addresses[*].phoneNumbers[*]", []),
            new TableKey(
                "PK_SchoolAddressPhoneNumber",
                [
                    new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(addressOrdinalColumn, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(phoneOrdinalColumn, ColumnKind.Scalar),
                ]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    addressOrdinalColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    phoneOrdinalColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    phoneNumberColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 20),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_SchoolAddressPhoneNumber_SchoolAddress",
                    [documentIdColumn, addressOrdinalColumn],
                    addressTableName,
                    [documentIdColumn, addressOrdinalColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable, addressTable, phoneTable],
            [],
            []
        );

        // Triggers
        List<DbTriggerInfo> triggers =
        [
            // DocumentStamping on root table (with identity projection column SchoolId)
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // DocumentStamping on child table SchoolAddress (no identity projection)
            new(
                new DbTriggerName("TR_SchoolAddress_Stamp"),
                addressTableName,
                [documentIdColumn],
                [],
                new TriggerKindParameters.DocumentStamping()
            ),
            // DocumentStamping on nested child table (no identity projection)
            new(
                new DbTriggerName("TR_SchoolAddressPhoneNumber_Stamp"),
                phoneTableName,
                [documentIdColumn],
                [],
                new TriggerKindParameters.DocumentStamping()
            ),
            // ReferentialIdentityMaintenance on root table
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    1,
                    "Ed-Fi",
                    "School",
                    [new IdentityElementMapping(schoolIdColumn, "$.schoolId")]
                )
            ),
        ];

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                1,
                [0x01],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [resourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)],
            [],
            [],
            [],
            triggers
        );
    }
}

/// <summary>
/// Fixture for polymorphic abstract views scenario:
/// EducationOrganization (abstract) with School + LEA concrete types
/// </summary>
internal static class PolymorphicAbstractFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var documentIdColumn = new DbColumnName("DocumentId");
        var discriminatorColumn = new DbColumnName("Discriminator");
        var organizationIdColumn = new DbColumnName("EducationOrganizationId");

        // Abstract resource
        var abstractResource = new QualifiedResourceName("Ed-Fi", "EducationOrganization");
        var abstractResourceKey = new ResourceKeyEntry(1, abstractResource, "1.0.0", true);

        // Concrete resources
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var schoolResourceKey = new ResourceKeyEntry(2, schoolResource, "1.0.0", false);

        var leaResource = new QualifiedResourceName("Ed-Fi", "LocalEducationAgency");
        var leaResourceKey = new ResourceKeyEntry(3, leaResource, "1.0.0", false);

        // Identity table for abstract type
        var identityTableName = new DbTableName(schema, "EducationOrganizationIdentity");
        var identityTable = new DbTableModel(
            identityTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_EducationOrganizationIdentity",
                [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    organizationIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    discriminatorColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                // FK from abstract identity table to dms.Document (as created by BuildIdentityTableConstraints)
                new TableConstraint.ForeignKey(
                    "FK_EducationOrganizationIdentity_Document",
                    [documentIdColumn],
                    new DbTableName(new DbSchemaName("dms"), "Document"),
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        // School concrete table
        var schoolTableName = new DbTableName(schema, "School");
        var schoolTable = new DbTableModel(
            schoolTableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_School", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    organizationIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_School_EducationOrganizationIdentity",
                    [documentIdColumn],
                    identityTableName,
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        // LEA concrete table
        var leaTableName = new DbTableName(schema, "LocalEducationAgency");
        var leaTable = new DbTableModel(
            leaTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_LocalEducationAgency",
                [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    organizationIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_LocalEducationAgency_EducationOrganizationIdentity",
                    [documentIdColumn],
                    identityTableName,
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        // Abstract union view
        var viewName = new DbTableName(schema, "EducationOrganization_View");
        List<AbstractUnionViewOutputColumn> outputColumns =
        [
            new(documentIdColumn, new RelationalScalarType(ScalarKind.Int64), null, null),
            new(organizationIdColumn, new RelationalScalarType(ScalarKind.Int32), null, null),
            new(discriminatorColumn, new RelationalScalarType(ScalarKind.String, MaxLength: 50), null, null),
        ];

        var schoolArm = new AbstractUnionViewArm(
            schoolResourceKey,
            schoolTableName,
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(documentIdColumn),
                new AbstractUnionViewProjectionExpression.SourceColumn(organizationIdColumn),
                new AbstractUnionViewProjectionExpression.StringLiteral("Ed-Fi:School"),
            ]
        );

        var leaArm = new AbstractUnionViewArm(
            leaResourceKey,
            leaTableName,
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(documentIdColumn),
                new AbstractUnionViewProjectionExpression.SourceColumn(organizationIdColumn),
                new AbstractUnionViewProjectionExpression.StringLiteral("Ed-Fi:LocalEducationAgency"),
            ]
        );

        var unionView = new AbstractUnionViewInfo(
            abstractResourceKey,
            viewName,
            outputColumns,
            [schoolArm, leaArm]
        );

        var abstractIdentityTable = new AbstractIdentityTableInfo(abstractResourceKey, identityTable);

        var schoolRelationalModel = new RelationalResourceModel(
            schoolResource,
            schema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable],
            [],
            []
        );

        var leaRelationalModel = new RelationalResourceModel(
            leaResource,
            schema,
            ResourceStorageKind.RelationalTables,
            leaTable,
            [leaTable],
            [],
            []
        );

        // Triggers
        var superclassAlias = new SuperclassAliasInfo(
            1,
            "Ed-Fi",
            "EducationOrganization",
            [new IdentityElementMapping(organizationIdColumn, "$.educationOrganizationId")]
        );

        List<DbTriggerInfo> triggers =
        [
            // DocumentStamping on LEA root (with identity projection)
            new(
                new DbTriggerName("TR_LocalEducationAgency_Stamp"),
                leaTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // AbstractIdentityMaintenance on LEA → EducationOrganizationIdentity
            new(
                new DbTriggerName("TR_LocalEducationAgency_AbstractIdentity"),
                leaTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.AbstractIdentityMaintenance(
                    identityTableName,
                    [new TriggerColumnMapping(organizationIdColumn, organizationIdColumn)],
                    "Ed-Fi:LocalEducationAgency"
                )
            ),
            // ReferentialIdentityMaintenance on LEA
            new(
                new DbTriggerName("TR_LocalEducationAgency_ReferentialIdentity"),
                leaTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    3,
                    "Ed-Fi",
                    "LocalEducationAgency",
                    [new IdentityElementMapping(organizationIdColumn, "$.educationOrganizationId")],
                    superclassAlias
                )
            ),
            // DocumentStamping on School root (with identity projection)
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // AbstractIdentityMaintenance on School → EducationOrganizationIdentity
            new(
                new DbTriggerName("TR_School_AbstractIdentity"),
                schoolTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.AbstractIdentityMaintenance(
                    identityTableName,
                    [new TriggerColumnMapping(organizationIdColumn, organizationIdColumn)],
                    "Ed-Fi:School"
                )
            ),
            // ReferentialIdentityMaintenance on School
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                schoolTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    2,
                    "Ed-Fi",
                    "School",
                    [new IdentityElementMapping(organizationIdColumn, "$.educationOrganizationId")],
                    superclassAlias
                )
            ),
        ];

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                3,
                [0x01, 0x02, 0x03],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [abstractResourceKey, schoolResourceKey, leaResourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [
                new ConcreteResourceModel(
                    schoolResourceKey,
                    ResourceStorageKind.RelationalTables,
                    schoolRelationalModel
                ),
                new ConcreteResourceModel(
                    leaResourceKey,
                    ResourceStorageKind.RelationalTables,
                    leaRelationalModel
                ),
            ],
            [abstractIdentityTable],
            [unionView],
            [],
            triggers
        );
    }
}

/// <summary>
/// Fixture for extension mapping scenario:
/// School (core) with Sample extension at root and nested collection levels
/// </summary>
internal static class ExtensionMappingFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var edfiSchema = new DbSchemaName("edfi");
        var sampleSchema = new DbSchemaName("sample");
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        // Column names
        var documentIdColumn = new DbColumnName("DocumentId");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var addressOrdinalColumn = new DbColumnName("AddressOrdinal");
        var streetColumn = new DbColumnName("Street");
        var extensionDataColumn = new DbColumnName("ExtensionData");
        var addressExtDataColumn = new DbColumnName("AddressExtensionData");

        // Core table: School
        var schoolTableName = new DbTableName(edfiSchema, "School");
        var schoolTable = new DbTableModel(
            schoolTableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_School", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        // Core collection: SchoolAddress
        var addressTableName = new DbTableName(edfiSchema, "SchoolAddress");
        var addressTable = new DbTableModel(
            addressTableName,
            new JsonPathExpression("$.addresses[*]", []),
            new TableKey(
                "PK_SchoolAddress",
                [
                    new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(addressOrdinalColumn, ColumnKind.Scalar),
                ]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    addressOrdinalColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    streetColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 100),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_SchoolAddress_School",
                    [documentIdColumn],
                    schoolTableName,
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        // Root extension: SchoolExtension
        var schoolExtTableName = new DbTableName(sampleSchema, "SchoolExtension");
        var schoolExtTable = new DbTableModel(
            schoolExtTableName,
            new JsonPathExpression("$._ext.sample", []),
            new TableKey("PK_SchoolExtension", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    extensionDataColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 200),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_SchoolExtension_School",
                    [documentIdColumn],
                    schoolTableName,
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        // Nested extension: SchoolAddressExtension
        var addressExtTableName = new DbTableName(sampleSchema, "SchoolAddressExtension");
        var addressExtTable = new DbTableModel(
            addressExtTableName,
            new JsonPathExpression("$.addresses[*]._ext.sample", []),
            new TableKey(
                "PK_SchoolAddressExtension",
                [
                    new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(addressOrdinalColumn, ColumnKind.ParentKeyPart),
                ]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    addressOrdinalColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    addressExtDataColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 100),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_SchoolAddressExtension_SchoolAddress",
                    [documentIdColumn, addressOrdinalColumn],
                    addressTableName,
                    [documentIdColumn, addressOrdinalColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            edfiSchema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable, addressTable, schoolExtTable, addressExtTable],
            [],
            []
        );

        // Triggers
        List<DbTriggerInfo> triggers =
        [
            // DocumentStamping on root table (with identity projection column SchoolId)
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // DocumentStamping on child table SchoolAddress (no identity projection)
            new(
                new DbTriggerName("TR_SchoolAddress_Stamp"),
                addressTableName,
                [documentIdColumn],
                [],
                new TriggerKindParameters.DocumentStamping()
            ),
            // DocumentStamping on extension table SchoolAddressExtension (no identity projection)
            new(
                new DbTriggerName("TR_SchoolAddressExtension_Stamp"),
                addressExtTableName,
                [documentIdColumn],
                [],
                new TriggerKindParameters.DocumentStamping()
            ),
            // DocumentStamping on extension table SchoolExtension (no identity projection)
            new(
                new DbTriggerName("TR_SchoolExtension_Stamp"),
                schoolExtTableName,
                [documentIdColumn],
                [],
                new TriggerKindParameters.DocumentStamping()
            ),
            // ReferentialIdentityMaintenance on root table
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    1,
                    "Ed-Fi",
                    "School",
                    [new IdentityElementMapping(schoolIdColumn, "$.schoolId")]
                )
            ),
        ];

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                1,
                [0x01],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                    new SchemaComponentInfo(
                        "sample",
                        "Sample",
                        "1.0.0",
                        false,
                        "aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000"
                    ),
                ],
                [resourceKey]
            ),
            dialect,
            [
                new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, edfiSchema),
                new ProjectSchemaInfo("sample", "Sample", "1.0.0", false, sampleSchema),
            ],
            [new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)],
            [],
            [],
            [],
            triggers
        );
    }
}

/// <summary>
/// Fixture for identity propagation fallback scenario (MSSQL only):
/// StudentSchoolAssociation references School via SchoolId FK.
/// On MSSQL, an IdentityPropagationFallback trigger on StudentSchoolAssociation
/// propagates SchoolId changes to the School root table (replacing ON UPDATE CASCADE).
/// On PostgreSQL, only DocumentStamping and ReferentialIdentityMaintenance are emitted.
/// </summary>
internal static class IdentityPropagationFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var documentIdColumn = new DbColumnName("DocumentId");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var schoolDocumentIdColumn = new DbColumnName("School_DocumentId");
        var studentIdColumn = new DbColumnName("StudentUniqueId");
        var entryDateColumn = new DbColumnName("EntryDate");

        // School resource
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var schoolResourceKey = new ResourceKeyEntry(1, schoolResource, "1.0.0", false);

        var schoolTableName = new DbTableName(schema, "School");
        var schoolTable = new DbTableModel(
            schoolTableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_School", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        // StudentSchoolAssociation resource
        var assocResource = new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation");
        var assocResourceKey = new ResourceKeyEntry(2, assocResource, "1.0.0", false);

        var assocTableName = new DbTableName(schema, "StudentSchoolAssociation");
        var assocTable = new DbTableModel(
            assocTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_StudentSchoolAssociation",
                [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolDocumentIdColumn,
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: schoolResource
                ),
                new DbColumnModel(
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    studentIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    entryDateColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Date),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_StudentSchoolAssociation_School",
                    [schoolDocumentIdColumn],
                    schoolTableName,
                    [documentIdColumn],
                    ReferentialAction.NoAction,
                    ReferentialAction.NoAction
                ),
            ]
        );

        var schoolRelationalModel = new RelationalResourceModel(
            schoolResource,
            schema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable],
            [],
            []
        );

        var assocRelationalModel = new RelationalResourceModel(
            assocResource,
            schema,
            ResourceStorageKind.RelationalTables,
            assocTable,
            [assocTable],
            [],
            []
        );

        // Triggers
        List<DbTriggerInfo> triggers =
        [
            // DocumentStamping on School root
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // ReferentialIdentityMaintenance on School
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    1,
                    "Ed-Fi",
                    "School",
                    [new IdentityElementMapping(schoolIdColumn, "$.schoolId")]
                )
            ),
            // DocumentStamping on StudentSchoolAssociation root
            new(
                new DbTriggerName("TR_StudentSchoolAssociation_Stamp"),
                assocTableName,
                [documentIdColumn],
                [schoolIdColumn, studentIdColumn, entryDateColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // ReferentialIdentityMaintenance on StudentSchoolAssociation
            new(
                new DbTriggerName("TR_StudentSchoolAssociation_ReferentialIdentity"),
                assocTableName,
                [documentIdColumn],
                [schoolIdColumn, studentIdColumn, entryDateColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    2,
                    "Ed-Fi",
                    "StudentSchoolAssociation",
                    [
                        new IdentityElementMapping(schoolIdColumn, "$.schoolReference.schoolId"),
                        new IdentityElementMapping(studentIdColumn, "$.studentReference.studentUniqueId"),
                        new IdentityElementMapping(entryDateColumn, "$.entryDate"),
                    ]
                )
            ),
        ];

        // IdentityPropagationFallback — MSSQL only, trigger on referenced entity (School)
        if (dialect == SqlDialect.Mssql)
        {
            triggers.Add(
                new DbTriggerInfo(
                    new DbTriggerName("TR_School_Propagation"),
                    schoolTableName,
                    [new DbColumnName("DocumentId")],
                    [schoolIdColumn],
                    new TriggerKindParameters.IdentityPropagationFallback([
                        new PropagationReferrerTarget(
                            assocTableName,
                            new DbColumnName("School_DocumentId"),
                            [new TriggerColumnMapping(schoolIdColumn, schoolIdColumn)]
                        ),
                    ])
                )
            );
        }

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                2,
                [0x01, 0x02],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [schoolResourceKey, assocResourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [
                new ConcreteResourceModel(
                    schoolResourceKey,
                    ResourceStorageKind.RelationalTables,
                    schoolRelationalModel
                ),
                new ConcreteResourceModel(
                    assocResourceKey,
                    ResourceStorageKind.RelationalTables,
                    assocRelationalModel
                ),
            ],
            [],
            [],
            [],
            triggers
        );
    }
}

/// <summary>
/// Fixture for FK-support index scenario:
/// Enrollment references School via SchoolId FK, with an explicit FK-support index.
/// Tests that FK-support indexes are emitted exactly once and not duplicated.
/// </summary>
internal static class FkSupportIndexFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var documentIdColumn = new DbColumnName("DocumentId");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var enrollmentIdColumn = new DbColumnName("EnrollmentId");

        // School resource (parent)
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var schoolResourceKey = new ResourceKeyEntry(1, schoolResource, "1.0.0", false);

        var schoolTableName = new DbTableName(schema, "School");
        var schoolTable = new DbTableModel(
            schoolTableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_School", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        // Enrollment resource (child with FK to School)
        var enrollmentResource = new QualifiedResourceName("Ed-Fi", "Enrollment");
        var enrollmentResourceKey = new ResourceKeyEntry(2, enrollmentResource, "1.0.0", false);

        var enrollmentTableName = new DbTableName(schema, "Enrollment");
        var enrollmentTable = new DbTableModel(
            enrollmentTableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_Enrollment", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    enrollmentIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_Enrollment_School",
                    [schoolIdColumn],
                    schoolTableName,
                    [schoolIdColumn],
                    ReferentialAction.NoAction,
                    ReferentialAction.NoAction
                ),
            ]
        );

        var schoolRelationalModel = new RelationalResourceModel(
            schoolResource,
            schema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable],
            [],
            []
        );

        var enrollmentRelationalModel = new RelationalResourceModel(
            enrollmentResource,
            schema,
            ResourceStorageKind.RelationalTables,
            enrollmentTable,
            [enrollmentTable],
            [],
            []
        );

        // FK-support index on Enrollment.SchoolId for join performance
        List<DbIndexInfo> indexes =
        [
            new(
                new DbIndexName("IX_Enrollment_SchoolId"),
                enrollmentTableName,
                [schoolIdColumn],
                IsUnique: false,
                DbIndexKind.ForeignKeySupport
            ),
        ];

        // Minimal triggers (document stamping only)
        List<DbTriggerInfo> triggers =
        [
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            new(
                new DbTriggerName("TR_Enrollment_Stamp"),
                enrollmentTableName,
                [documentIdColumn],
                [enrollmentIdColumn, schoolIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
        ];

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                2,
                [0x01, 0x02],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [schoolResourceKey, enrollmentResourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [
                new ConcreteResourceModel(
                    schoolResourceKey,
                    ResourceStorageKind.RelationalTables,
                    schoolRelationalModel
                ),
                new ConcreteResourceModel(
                    enrollmentResourceKey,
                    ResourceStorageKind.RelationalTables,
                    enrollmentRelationalModel
                ),
            ],
            [],
            [],
            indexes,
            triggers
        );
    }
}

/// <summary>
/// Fixture for key unification with triggers scenario:
/// CourseRegistration has two references (courseOfferingReference and schoolReference) that both
/// carry a SchoolId identity field. Key unification merges them into a canonical SchoolId_Unified
/// column with two persisted computed alias columns (CourseOffering_SchoolId, School_SchoolId).
///
/// Validates:
/// - Computed column definitions (PERSISTED for MSSQL, GENERATED ALWAYS AS ... STORED for PostgreSQL)
/// - UPDATE() guards in MSSQL triggers reference the canonical stored column, not aliases
/// - Propagation trigger SET targets reference canonical stored columns, not aliases
/// - Identity hash expressions in ReferentialIdentity triggers read alias columns from inserted/NEW
/// - FK constraints use the DocumentId FK columns (not identity alias columns)
/// </summary>
internal static class KeyUnificationFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var documentIdColumn = new DbColumnName("DocumentId");

        // ── School resource (referenced entity, source of propagation) ──
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var schoolResourceKey = new ResourceKeyEntry(1, schoolResource, "1.0.0", false);
        var schoolIdColumn = new DbColumnName("SchoolId");

        var schoolTableName = new DbTableName(schema, "School");
        var schoolTable = new DbTableModel(
            schoolTableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_School", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        var schoolRelationalModel = new RelationalResourceModel(
            schoolResource,
            schema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable],
            [],
            []
        );

        // ── CourseRegistration resource (referrer with key-unified alias columns) ──
        var regResource = new QualifiedResourceName("Ed-Fi", "CourseRegistration");
        var regResourceKey = new ResourceKeyEntry(2, regResource, "1.0.0", false);

        // Column names — post-unification layout
        var courseOfferingDocIdColumn = new DbColumnName("CourseOffering_DocumentId");
        var schoolDocIdColumn = new DbColumnName("School_DocumentId");
        var courseOfferingSchoolIdColumn = new DbColumnName("CourseOffering_SchoolId");
        var schoolSchoolIdColumn = new DbColumnName("School_SchoolId");
        var schoolIdUnifiedColumn = new DbColumnName("SchoolId_Unified");
        var localCourseCodeColumn = new DbColumnName("CourseOffering_LocalCourseCode");
        var registrationDateColumn = new DbColumnName("RegistrationDate");

        var regTableName = new DbTableName(schema, "CourseRegistration");
        var regTable = new DbTableModel(
            regTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_CourseRegistration",
                [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            [
                // DocumentId (PK)
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                // CourseOffering_DocumentId (FK to CourseOffering)
                new DbColumnModel(
                    courseOfferingDocIdColumn,
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: new QualifiedResourceName("Ed-Fi", "CourseOffering")
                ),
                // School_DocumentId (FK to School)
                new DbColumnModel(
                    schoolDocIdColumn,
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: schoolResource
                ),
                // CourseOffering_SchoolId — UNIFIED ALIAS → SchoolId_Unified (presence-gated by CourseOffering_DocumentId)
                new(
                    courseOfferingSchoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.courseOfferingReference.schoolId", []),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(schoolIdUnifiedColumn, courseOfferingDocIdColumn)
                ),
                // CourseOffering_LocalCourseCode — stored identity column
                new DbColumnModel(
                    localCourseCodeColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.courseOfferingReference.localCourseCode", []),
                    TargetResource: null
                ),
                // School_SchoolId — UNIFIED ALIAS → SchoolId_Unified (presence-gated by School_DocumentId)
                new(
                    schoolSchoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.schoolReference.schoolId", []),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(schoolIdUnifiedColumn, schoolDocIdColumn)
                ),
                // RegistrationDate — stored own-identity column
                new DbColumnModel(
                    registrationDateColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Date),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.registrationDate", []),
                    TargetResource: null
                ),
                // SchoolId_Unified — canonical stored column (added by key unification)
                new DbColumnModel(
                    schoolIdUnifiedColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_CourseRegistration_CourseOffering",
                    [courseOfferingDocIdColumn],
                    new DbTableName(schema, "CourseOffering"),
                    [documentIdColumn],
                    ReferentialAction.NoAction,
                    ReferentialAction.NoAction
                ),
                new TableConstraint.ForeignKey(
                    "FK_CourseRegistration_School",
                    [schoolDocIdColumn],
                    schoolTableName,
                    [documentIdColumn],
                    ReferentialAction.NoAction,
                    ReferentialAction.NoAction
                ),
            ]
        )
        {
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    schoolIdUnifiedColumn,
                    [courseOfferingSchoolIdColumn, schoolSchoolIdColumn]
                ),
            ],
        };

        var regRelationalModel = new RelationalResourceModel(
            regResource,
            schema,
            ResourceStorageKind.RelationalTables,
            regTable,
            [regTable],
            [],
            []
        );

        // ── Triggers ──

        // Identity projection columns for CourseRegistration use CANONICAL stored columns only.
        // The two alias columns (CourseOffering_SchoolId, School_SchoolId) resolve to the same
        // canonical SchoolId_Unified, so after de-duplication the projection is:
        //   [SchoolId_Unified, CourseOffering_LocalCourseCode, RegistrationDate]
        IReadOnlyList<DbColumnName> regIdentityProjectionColumns =
        [
            schoolIdUnifiedColumn,
            localCourseCodeColumn,
            registrationDateColumn,
        ];

        List<DbTriggerInfo> triggers =
        [
            // ── School triggers ──
            // DocumentStamping on School root
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // ReferentialIdentityMaintenance on School
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    1,
                    "Ed-Fi",
                    "School",
                    [new IdentityElementMapping(schoolIdColumn, "$.schoolId")]
                )
            ),
            // ── CourseRegistration triggers ──
            // DocumentStamping on CourseRegistration root (identity projection = canonical columns)
            new(
                new DbTriggerName("TR_CourseRegistration_Stamp"),
                regTableName,
                [documentIdColumn],
                regIdentityProjectionColumns,
                new TriggerKindParameters.DocumentStamping()
            ),
            // ReferentialIdentityMaintenance on CourseRegistration
            // IdentityElements use alias columns (readable from inserted/NEW for hash computation).
            // IdentityProjectionColumns use canonical stored columns (for UPDATE guards).
            new(
                new DbTriggerName("TR_CourseRegistration_ReferentialIdentity"),
                regTableName,
                [documentIdColumn],
                regIdentityProjectionColumns,
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    2,
                    "Ed-Fi",
                    "CourseRegistration",
                    [
                        new IdentityElementMapping(
                            courseOfferingSchoolIdColumn,
                            "$.courseOfferingReference.schoolId"
                        ),
                        new IdentityElementMapping(
                            localCourseCodeColumn,
                            "$.courseOfferingReference.localCourseCode"
                        ),
                        new IdentityElementMapping(schoolSchoolIdColumn, "$.schoolReference.schoolId"),
                        new IdentityElementMapping(registrationDateColumn, "$.registrationDate"),
                    ]
                )
            ),
        ];

        // IdentityPropagationFallback — MSSQL only.
        // School propagates SchoolId changes to CourseRegistration.
        // The target column is the CANONICAL stored column (SchoolId_Unified), not the alias.
        if (dialect == SqlDialect.Mssql)
        {
            triggers.Add(
                new DbTriggerInfo(
                    new DbTriggerName("TR_School_Propagation"),
                    schoolTableName,
                    [documentIdColumn],
                    [schoolIdColumn],
                    new TriggerKindParameters.IdentityPropagationFallback([
                        new PropagationReferrerTarget(
                            regTableName,
                            schoolDocIdColumn,
                            [new TriggerColumnMapping(schoolIdColumn, schoolIdUnifiedColumn)]
                        ),
                    ])
                )
            );
        }

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                2,
                [0x01, 0x02],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [schoolResourceKey, regResourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [
                new ConcreteResourceModel(
                    schoolResourceKey,
                    ResourceStorageKind.RelationalTables,
                    schoolRelationalModel
                ),
                new ConcreteResourceModel(
                    regResourceKey,
                    ResourceStorageKind.RelationalTables,
                    regRelationalModel
                ),
            ],
            [],
            [],
            [],
            triggers
        );
    }
}
