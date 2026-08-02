// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_InMemoryRelationalCommandExecutor
{
    [Test]
    public async Task It_supports_multiple_result_sets_for_future_batched_prerequisites()
    {
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(RelationalAccessTestData.CreateRow(("Value", 101))),
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(("Label", "stored")),
                    RelationalAccessTestData.CreateRow(("Label", "request"))
                ),
            ]),
        ]);

        var result = await executor.ExecuteReaderAsync(
            new RelationalCommand(
                "select 101 as Value; select 'stored' as Label union all select 'request';",
                [new RelationalParameter("@p0", 101)]
            ),
            async (reader, cancellationToken) =>
            {
                List<int> values = [];

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    values.Add(reader.GetRequiredFieldValue<int>("Value"));
                }

                var movedToSecondResultSet = await reader
                    .NextResultAsync(cancellationToken)
                    .ConfigureAwait(false);

                List<string> labels = [];

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    labels.Add(reader.GetRequiredFieldValue<string>("Label"));
                }

                return new BatchReadResult(values, movedToSecondResultSet, labels);
            }
        );

        executor.Commands.Should().ContainSingle();
        executor.Commands[0].CommandText.Should().Contain("select 101 as Value");
        executor.Commands[0].Parameters.Should().ContainSingle();
        executor.Commands[0].Parameters[0].Name.Should().Be("@p0");
        executor.Commands[0].Parameters[0].Value.Should().Be(101);

        result.Values.Should().Equal(101);
        result.MovedToSecondResultSet.Should().BeTrue();
        result.Labels.Should().Equal("stored", "request");
    }
}

internal sealed record BatchReadResult(
    IReadOnlyList<int> Values,
    bool MovedToSecondResultSet,
    IReadOnlyList<string> Labels
);

internal sealed class InMemoryRelationalCommandExecutor(
    IReadOnlyList<InMemoryRelationalCommandExecution> executions,
    SqlDialect dialect = SqlDialect.Pgsql
) : IRelationalCommandExecutor
{
    public SqlDialect Dialect => dialect;

    private readonly Queue<InMemoryRelationalCommandExecution> _executions = new(executions);

    public List<RelationalCommand> Commands { get; } = [];

    public async Task<TResult> ExecuteReaderAsync<TResult>(
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(readAsync);

        Commands.Add(command);

        if (!_executions.TryDequeue(out var execution))
        {
            throw new AssertionException(
                "No in-memory relational command execution was configured for this call."
            );
        }

        await using var reader = new InMemoryRelationalCommandReader(execution.ResultSets);
        return await readAsync(reader, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record InMemoryRelationalCommandExecution(
    IReadOnlyList<InMemoryRelationalResultSet> ResultSets
);

internal sealed class InMemoryRelationalResultSet
{
    private readonly Dictionary<string, int> _ordinalByName;
    private readonly IReadOnlyList<string> _columns;
    private readonly IReadOnlyList<IReadOnlyList<object?>> _rows;

    private InMemoryRelationalResultSet(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows
    )
    {
        _columns = columns;
        _rows = rows;
        _ordinalByName = columns
            .Select((column, ordinal) => (column, ordinal))
            .ToDictionary(entry => entry.column, entry => entry.ordinal, StringComparer.Ordinal);
    }

    public int RowCount => _rows.Count;

    public static InMemoryRelationalResultSet Create(params IReadOnlyDictionary<string, object?>[] rows)
    {
        List<string> columns = [];
        Dictionary<string, int> ordinalByName = new(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            foreach (var columnName in row.Keys)
            {
                if (ordinalByName.TryAdd(columnName, columns.Count))
                {
                    columns.Add(columnName);
                }
            }
        }

        List<IReadOnlyList<object?>> valuesByRow = [];

        foreach (var row in rows)
        {
            object?[] values = new object?[columns.Count];

            foreach (var (columnName, value) in row)
            {
                values[ordinalByName[columnName]] = value;
            }

            valuesByRow.Add(values);
        }

        return new InMemoryRelationalResultSet(columns, valuesByRow);
    }

    public int GetOrdinal(string name) =>
        _ordinalByName.TryGetValue(name, out var ordinal)
            ? ordinal
            : throw new IndexOutOfRangeException($"Column '{name}' was not found.");

    public object? GetValue(int rowIndex, int ordinal)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count)
        {
            throw new InvalidOperationException("A row must be selected before reading column values.");
        }

        if (ordinal < 0 || ordinal >= _columns.Count)
        {
            throw new IndexOutOfRangeException(
                $"Column ordinal '{ordinal}' was not found for the current result set."
            );
        }

        return _rows[rowIndex][ordinal];
    }
}

internal sealed class InMemoryRelationalCommandReader(IReadOnlyList<InMemoryRelationalResultSet> resultSets)
    : IRelationalCommandReader
{
    private readonly IReadOnlyList<InMemoryRelationalResultSet> _resultSets = resultSets;
    private int _resultSetIndex;
    private int _rowIndex = -1;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task<bool> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_resultSets.Count is 0)
        {
            return Task.FromResult(false);
        }

        var nextRowIndex = _rowIndex + 1;

        if (nextRowIndex >= CurrentResultSet.RowCount)
        {
            return Task.FromResult(false);
        }

        _rowIndex = nextRowIndex;
        return Task.FromResult(true);
    }

    public Task<bool> NextResultAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var nextResultSetIndex = _resultSetIndex + 1;

        if (nextResultSetIndex >= _resultSets.Count)
        {
            return Task.FromResult(false);
        }

        _resultSetIndex = nextResultSetIndex;
        _rowIndex = -1;

        return Task.FromResult(true);
    }

    public int GetOrdinal(string name) => CurrentResultSet.GetOrdinal(name);

    public T GetFieldValue<T>(int ordinal)
    {
        var value = CurrentResultSet.GetValue(_rowIndex, ordinal);

        if (value is null or DBNull)
        {
            throw new InvalidOperationException(
                $"Column ordinal '{ordinal}' does not contain a value in the current row."
            );
        }

        if (value is T typedValue)
        {
            return typedValue;
        }

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    public bool IsDBNull(int ordinal) => CurrentResultSet.GetValue(_rowIndex, ordinal) is null or DBNull;

    private InMemoryRelationalResultSet CurrentResultSet =>
        _resultSets.Count is 0
            ? throw new InvalidOperationException("No result sets were configured for this reader.")
            : _resultSets[_resultSetIndex];
}

internal static class RelationalAccessTestData
{
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _localEducationAgencyResource = new(
        "Ed-Fi",
        "LocalEducationAgency"
    );
    private static readonly QualifiedResourceName _educationOrganizationResource = new(
        "Ed-Fi",
        "EducationOrganization"
    );
    private static readonly QualifiedResourceName _meetingResource = new("Ed-Fi", "Meeting");
    private static readonly QualifiedResourceName _decimalKeyResource = new("Ed-Fi", "DecimalKeyResource");
    private static readonly QualifiedResourceName _studentAcademicRecordResource = new(
        "Ed-Fi",
        "StudentAcademicRecord"
    );
    private static readonly QualifiedResourceName _schoolClassificationResource = new(
        "Ed-Fi",
        "SchoolClassification"
    );
    private static readonly QualifiedResourceName _schoolClassificationMemberResource = new(
        "Ed-Fi",
        "SchoolClassificationMember"
    );
    private static readonly QualifiedResourceName _schoolTypeDescriptorResource = new(
        "Ed-Fi",
        "SchoolTypeDescriptor"
    );

    public static MappingSet CreateMappingSet(QualifiedResourceName requestResource)
    {
        const string EffectiveSchemaHash = "test-hash";
        var studentKey = new ResourceKeyEntry(1, requestResource, "1.0", false);
        var schoolKey = new ResourceKeyEntry(11, _schoolResource, "1.0", false);
        var localEducationAgencyKey = new ResourceKeyEntry(12, _localEducationAgencyResource, "1.0", false);
        var schoolTypeDescriptorKey = new ResourceKeyEntry(13, _schoolTypeDescriptorResource, "1.0", false);
        var meetingKey = new ResourceKeyEntry(14, _meetingResource, "1.0", false);
        var decimalKeyKey = new ResourceKeyEntry(15, _decimalKeyResource, "1.0", false);
        var studentAcademicRecordKey = new ResourceKeyEntry(16, _studentAcademicRecordResource, "1.0", false);
        var schoolClassificationKey = new ResourceKeyEntry(17, _schoolClassificationResource, "1.0", true);
        var schoolClassificationMemberKey = new ResourceKeyEntry(
            18,
            _schoolClassificationMemberResource,
            "1.0",
            false
        );
        var educationOrganizationKey = new ResourceKeyEntry(30, _educationOrganizationResource, "1.0", true);

        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: EffectiveSchemaHash,
            ResourceKeyCount: 10,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder:
            [
                studentKey,
                schoolKey,
                localEducationAgencyKey,
                schoolTypeDescriptorKey,
                meetingKey,
                decimalKeyKey,
                studentAcademicRecordKey,
                schoolClassificationKey,
                schoolClassificationMemberKey,
                educationOrganizationKey,
            ]
        );

        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: effectiveSchema,
            Dialect: SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder: [],
            ConcreteResourcesInNameOrder:
            [
                new ConcreteResourceModel(
                    studentKey,
                    ResourceStorageKind.RelationalTables,
                    CreateRelationalResourceModel(requestResource, "Student")
                ),
                new ConcreteResourceModel(
                    schoolKey,
                    ResourceStorageKind.RelationalTables,
                    CreateRelationalResourceModel(_schoolResource, "School")
                ),
                new ConcreteResourceModel(
                    localEducationAgencyKey,
                    ResourceStorageKind.RelationalTables,
                    CreateRelationalResourceModel(_localEducationAgencyResource, "LocalEducationAgency")
                ),
                new ConcreteResourceModel(
                    meetingKey,
                    ResourceStorageKind.RelationalTables,
                    CreateRelationalResourceModel(_meetingResource, "Meeting")
                ),
                new ConcreteResourceModel(
                    decimalKeyKey,
                    ResourceStorageKind.RelationalTables,
                    CreateRelationalResourceModel(_decimalKeyResource, "DecimalKeyResource")
                ),
                new ConcreteResourceModel(
                    studentAcademicRecordKey,
                    ResourceStorageKind.RelationalTables,
                    CreateRelationalResourceModel(_studentAcademicRecordResource, "StudentAcademicRecord")
                ),
                new ConcreteResourceModel(
                    schoolTypeDescriptorKey,
                    ResourceStorageKind.SharedDescriptorTable,
                    CreateRelationalResourceModel(
                        _schoolTypeDescriptorResource,
                        "Descriptor",
                        ResourceStorageKind.SharedDescriptorTable
                    )
                ),
                new ConcreteResourceModel(
                    schoolClassificationMemberKey,
                    ResourceStorageKind.RelationalTables,
                    CreateRelationalResourceModel(
                        _schoolClassificationMemberResource,
                        "SchoolClassificationMember"
                    )
                ),
            ],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder:
            [
                new AbstractUnionViewInfo(
                    educationOrganizationKey,
                    new DbTableName(new DbSchemaName("edfi"), "EducationOrganization_View"),
                    [
                        new AbstractUnionViewOutputColumn(
                            new DbColumnName("DocumentId"),
                            new RelationalScalarType(ScalarKind.Int64),
                            null,
                            null
                        ),
                        new AbstractUnionViewOutputColumn(
                            new DbColumnName("EducationOrganizationId"),
                            new RelationalScalarType(ScalarKind.Int32),
                            new JsonPathExpression("$.educationOrganizationId", []),
                            null
                        ),
                    ],
                    [
                        CreateAbstractUnionArm(schoolKey, "School", "SchoolId"),
                        CreateAbstractUnionArm(
                            localEducationAgencyKey,
                            "LocalEducationAgency",
                            "LocalEducationAgencyId"
                        ),
                    ]
                ),
                new AbstractUnionViewInfo(
                    schoolClassificationKey,
                    new DbTableName(new DbSchemaName("edfi"), "SchoolClassification_View"),
                    [
                        new AbstractUnionViewOutputColumn(
                            new DbColumnName("DocumentId"),
                            new RelationalScalarType(ScalarKind.Int64),
                            null,
                            null
                        ),
                        new AbstractUnionViewOutputColumn(
                            new DbColumnName("SchoolTypeDescriptor_DescriptorId"),
                            new RelationalScalarType(ScalarKind.Int64),
                            new JsonPathExpression("$.schoolTypeDescriptor", []),
                            _schoolTypeDescriptorResource,
                            IsDescriptorReference: true
                        ),
                    ],
                    [
                        CreateAbstractUnionArm(
                            schoolClassificationMemberKey,
                            "SchoolClassificationMember",
                            "SchoolTypeDescriptor_DescriptorId"
                        ),
                    ]
                ),
            ],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey(EffectiveSchemaHash, SqlDialect.Pgsql, "v1"),
            Model: modelSet,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: effectiveSchema.ResourceKeysInIdOrder.ToDictionary(
                entry => entry.Resource,
                entry => entry.ResourceKeyId
            ),
            ResourceKeyById: effectiveSchema.ResourceKeysInIdOrder.ToDictionary(
                entry => entry.ResourceKeyId,
                entry => entry
            ),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    // ---------------------------------------------------------------------------------------------
    // Natural-key probe fixtures. These describe the compiled probe metadata the natural-key lookup
    // command builders consume; they are deliberately independent of the mapping-set and reference
    // fixtures above, which serve the resolver's request shape rather than its probe metadata.
    // ---------------------------------------------------------------------------------------------

    public static readonly QualifiedResourceName StudentSectionAssociationResource = new(
        "Ed-Fi",
        "StudentSectionAssociation"
    );

    public static readonly QualifiedResourceName ProgramResource = new("Ed-Fi", "Program");

    public static readonly QualifiedResourceName ProgramTypeDescriptorResource = new(
        "Ed-Fi",
        "ProgramTypeDescriptor"
    );

    public static readonly QualifiedResourceName SchoolResource = _schoolResource;

    public static readonly QualifiedResourceName EducationOrganizationResource =
        _educationOrganizationResource;

    public static readonly QualifiedResourceName SchoolTypeDescriptorResource = _schoolTypeDescriptorResource;

    public static readonly QualifiedResourceName AllScalarKindsResource = new(
        "Ed-Fi",
        "AllScalarKindsResource"
    );

    /// <summary>
    /// A mapping set carrying compiled natural-key probe metadata, including a populated
    /// <see cref="DescriptorProbeTarget.DiscriminatorLiteralByResource"/>. The default
    /// <see cref="MappingSet.DescriptorProbeTarget"/> is non-null but has an EMPTY literal map, so a
    /// fixture that forgets to populate it emits descriptor predicates that silently match nothing.
    /// </summary>
    public static MappingSet CreateNaturalKeyProbeMappingSet() =>
        CreateMappingSet(new QualifiedResourceName("Ed-Fi", "Student")) with
        {
            NaturalKeyProbeTargets = new Dictionary<QualifiedResourceName, NaturalKeyProbeTarget>
            {
                [SchoolResource] = CreateSchoolProbeTarget(),
                [StudentSectionAssociationResource] = CreateStudentSectionAssociationProbeTarget(),
                [ProgramResource] = CreateProgramProbeTarget(),
                [EducationOrganizationResource] = CreateEducationOrganizationProbeTarget(),
                [AllScalarKindsResource] = CreateAllScalarKindsProbeTarget(),
            },
            DescriptorProbeTarget = new DescriptorProbeTarget(
                new DbTableName(new DbSchemaName("dms"), "Descriptor"),
                DescriptorProbeColumns.UriLowered,
                new DbColumnName("Discriminator"),
                new Dictionary<QualifiedResourceName, string>
                {
                    [ProgramTypeDescriptorResource] = ProgramTypeDescriptorResource.ResourceName,
                    [SchoolTypeDescriptorResource] = SchoolTypeDescriptorResource.ResourceName,
                }
            ),
        };

    /// <summary>Single Int32 probe column on the concrete root table.</summary>
    public static NaturalKeyProbeTarget CreateSchoolProbeTarget() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new DbColumnName("DocumentId"),
            IsAbstract: false,
            [CreateProbeColumn("SchoolId", "$.schoolId", ScalarKind.Int32)]
        );

    /// <summary>The seven-column RefKey shape — the widest realistic probe in DS 5.2.</summary>
    public static NaturalKeyProbeTarget CreateStudentSectionAssociationProbeTarget() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "StudentSectionAssociation"),
            new DbColumnName("DocumentId"),
            IsAbstract: false,
            [
                CreateProbeColumn("BeginDate", "$.beginDate", ScalarKind.Date),
                CreateProbeColumn(
                    "Section_LocalCourseCode",
                    "$.sectionReference.localCourseCode",
                    ScalarKind.String,
                    maxLength: 60
                ),
                CreateProbeColumn("Section_SchoolId", "$.sectionReference.schoolId", ScalarKind.Int64),
                CreateProbeColumn("Section_SchoolYear", "$.sectionReference.schoolYear", ScalarKind.Int32),
                CreateProbeColumn(
                    "Section_SectionIdentifier",
                    "$.sectionReference.sectionIdentifier",
                    ScalarKind.String,
                    maxLength: 255
                ),
                CreateProbeColumn(
                    "Section_SessionName",
                    "$.sectionReference.sessionName",
                    ScalarKind.String,
                    maxLength: 60
                ),
                CreateProbeColumn(
                    "Student_StudentUniqueId",
                    "$.studentReference.studentUniqueId",
                    ScalarKind.String,
                    maxLength: 32
                ),
            ]
        );

    /// <summary>A probe with a descriptor-valued identity part, resolved inline through dms.Descriptor.</summary>
    public static NaturalKeyProbeTarget CreateProgramProbeTarget() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "Program"),
            new DbColumnName("DocumentId"),
            IsAbstract: false,
            [
                CreateProbeColumn(
                    "EducationOrganization_EducationOrganizationId",
                    "$.educationOrganizationReference.educationOrganizationId",
                    ScalarKind.Int64
                ),
                CreateProbeColumn("ProgramName", "$.programName", ScalarKind.String, maxLength: 60),
                CreateProbeColumn(
                    "ProgramTypeDescriptor_DescriptorId",
                    "$.programTypeDescriptor",
                    ScalarKind.Int64,
                    descriptorResource: ProgramTypeDescriptorResource
                ),
            ]
        );

    /// <summary>An abstract target: the {Abstract}Identity table, never the union view.</summary>
    public static NaturalKeyProbeTarget CreateEducationOrganizationProbeTarget() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "EducationOrganizationIdentity"),
            new DbColumnName("DocumentId"),
            IsAbstract: true,
            [CreateProbeColumn("EducationOrganizationId", "$.educationOrganizationId", ScalarKind.Int64)]
        );

    /// <summary>One probe column per <see cref="ScalarKind"/>, for provider type-mapping coverage.</summary>
    public static NaturalKeyProbeTarget CreateAllScalarKindsProbeTarget() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "AllScalarKindsResource"),
            new DbColumnName("DocumentId"),
            IsAbstract: false,
            [
                CreateProbeColumn("StringKey", "$.stringKey", ScalarKind.String, maxLength: 50),
                CreateProbeColumn("Int32Key", "$.int32Key", ScalarKind.Int32),
                CreateProbeColumn("Int64Key", "$.int64Key", ScalarKind.Int64),
                CreateProbeColumn(
                    "DecimalKey",
                    "$.decimalKey",
                    ScalarKind.Decimal,
                    decimalPrecisionScale: (9, 2)
                ),
                CreateProbeColumn("BooleanKey", "$.booleanKey", ScalarKind.Boolean),
                CreateProbeColumn("DateKey", "$.dateKey", ScalarKind.Date),
                CreateProbeColumn("DateTimeKey", "$.dateTimeKey", ScalarKind.DateTime),
                CreateProbeColumn("TimeKey", "$.timeKey", ScalarKind.Time),
            ]
        );

    /// <summary>The eight values matching <see cref="CreateAllScalarKindsProbeTarget"/>, in column order.</summary>
    public static IReadOnlyList<object> CreateAllScalarKindsValues() =>
        [
            "alpha",
            2026,
            9_000_000_000L,
            1.5m,
            true,
            new DateOnly(2026, 3, 5),
            new DateTime(2026, 3, 5, 13, 30, 45, DateTimeKind.Utc),
            new TimeOnly(13, 30, 45),
        ];

    /// <summary>Builds a group's entries, stamping the required one-based ordinals.</summary>
    public static IReadOnlyList<NaturalKeyLookupEntry> CreateNaturalKeyEntries(
        IEnumerable<IReadOnlyList<object>> valueRows
    ) => [.. valueRows.Select((values, index) => new NaturalKeyLookupEntry(index + 1, values))];

    /// <summary>Builds <paramref name="entryCount"/> distinct entries of <paramref name="columnCount"/> Int64 values.</summary>
    public static IReadOnlyList<NaturalKeyLookupEntry> CreateSyntheticNaturalKeyEntries(
        int entryCount,
        int columnCount
    ) =>
        CreateNaturalKeyEntries(
            Enumerable
                .Range(0, entryCount)
                .Select(entryIndex =>
                    (IReadOnlyList<object>)
                        [
                            .. Enumerable
                                .Range(0, columnCount)
                                .Select(columnIndex => (object)(long)((entryIndex * 100) + columnIndex)),
                        ]
                )
        );

    /// <summary>A synthetic all-Int64 probe target of the requested width, for chunking-guard coverage.</summary>
    public static NaturalKeyProbeTarget CreateSyntheticProbeTarget(int columnCount) =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "SyntheticTarget"),
            new DbColumnName("DocumentId"),
            IsAbstract: false,
            [
                .. Enumerable
                    .Range(0, columnCount)
                    .Select(columnIndex =>
                        CreateProbeColumn(
                            string.Create(CultureInfo.InvariantCulture, $"Key{columnIndex}"),
                            string.Create(CultureInfo.InvariantCulture, $"$.key{columnIndex}"),
                            ScalarKind.Int64
                        )
                    ),
            ]
        );

    private static NaturalKeyProbeColumn CreateProbeColumn(
        string columnName,
        string identityJsonPath,
        ScalarKind scalarKind,
        int? maxLength = null,
        (int Precision, int Scale)? decimalPrecisionScale = null,
        QualifiedResourceName? descriptorResource = null
    ) =>
        new(
            new DbColumnName(columnName),
            new JsonPathExpression(identityJsonPath, []),
            new RelationalScalarType(scalarKind, maxLength, decimalPrecisionScale),
            descriptorResource
        );

    public static DocumentReference CreateDocumentReference(ReferentialId referentialId, string path) =>
        new(
            ResourceInfo: new BaseResourceInfo(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("School"),
                IsDescriptor: false
            ),
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
            ]),
            ReferentialId: referentialId,
            Path: new JsonPath(path)
        );

    public static DescriptorReference CreateDescriptorReference(
        ReferentialId referentialId,
        string uri,
        string path
    ) =>
        new(
            ResourceInfo: new BaseResourceInfo(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("SchoolTypeDescriptor"),
                IsDescriptor: true
            ),
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(DocumentIdentity.DescriptorIdentityJsonPath, uri),
            ]),
            ReferentialId: referentialId,
            Path: new JsonPath(path)
        );

    public static IReadOnlyDictionary<string, object?> CreateRow(
        params (string ColumnName, object? Value)[] values
    )
    {
        Dictionary<string, object?> row = new(StringComparer.Ordinal);

        foreach (var (columnName, value) in values)
        {
            row[columnName] = value;
        }

        return row;
    }

    private static RelationalResourceModel CreateRelationalResourceModel(
        QualifiedResourceName resource,
        string tableName,
        ResourceStorageKind storageKind = ResourceStorageKind.RelationalTables
    )
    {
        List<DbColumnModel> columns =
        [
            new DbColumnModel(
                new DbColumnName("DocumentId"),
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
        ];

        if (storageKind is ResourceStorageKind.RelationalTables)
        {
            columns.AddRange(CreateIdentityColumns(resource));
        }

        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), tableName),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                $"PK_{tableName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: columns,
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: storageKind,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static IReadOnlyList<DbColumnModel> CreateIdentityColumns(QualifiedResourceName resource)
    {
        return resource.ResourceName switch
        {
            "School" => [CreateIdentityColumn("SchoolId", "$.schoolId", ScalarKind.Int32)],
            "LocalEducationAgency" =>
            [
                CreateIdentityColumn("LocalEducationAgencyId", "$.localEducationAgencyId", ScalarKind.Int32),
            ],
            "Meeting" => [CreateIdentityColumn("MeetingDateTime", "$.meetingDateTime", ScalarKind.DateTime)],
            "DecimalKeyResource" =>
            [
                CreateIdentityColumn(
                    "DecimalKey",
                    "$.decimalKey",
                    ScalarKind.Decimal,
                    decimalPrecisionScale: (9, 2)
                ),
            ],
            "StudentAcademicRecord" =>
            [
                CreateIdentityColumn(
                    "EducationOrganization_EducationOrganizationId",
                    "$.educationOrganizationReference.educationOrganizationId",
                    ScalarKind.Int64
                ),
                CreateIdentityColumn(
                    "SchoolYear_SchoolYear",
                    "$.schoolYearTypeReference.schoolYear",
                    ScalarKind.Int32
                ),
                CreateIdentityColumn(
                    "Student_StudentUniqueId",
                    "$.studentReference.studentUniqueId",
                    ScalarKind.String
                ),
                CreateDescriptorIdentityColumn("TermDescriptor_DescriptorId", "$.termDescriptor"),
            ],
            "SchoolClassificationMember" =>
            [
                CreateDescriptorIdentityColumn("SchoolTypeDescriptor_DescriptorId", "$.schoolTypeDescriptor"),
            ],
            _ => [],
        };
    }

    private static DbColumnModel CreateIdentityColumn(
        string columnName,
        string jsonPath,
        ScalarKind scalarKind,
        (int Precision, int Scale)? decimalPrecisionScale = null
    ) =>
        new(
            new DbColumnName(columnName),
            ColumnKind.Scalar,
            new RelationalScalarType(scalarKind, Decimal: decimalPrecisionScale),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression(jsonPath, []),
            TargetResource: null
        );

    private static DbColumnModel CreateDescriptorIdentityColumn(string columnName, string jsonPath) =>
        new(
            new DbColumnName(columnName),
            ColumnKind.DescriptorFk,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression(jsonPath, []),
            TargetResource: _schoolTypeDescriptorResource
        );

    private static AbstractUnionViewArm CreateAbstractUnionArm(
        ResourceKeyEntry concreteMemberResourceKey,
        string tableName,
        string identityColumnName
    ) =>
        new(
            concreteMemberResourceKey,
            new DbTableName(new DbSchemaName("edfi"), tableName),
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(new DbColumnName("DocumentId")),
                new AbstractUnionViewProjectionExpression.SourceColumn(new DbColumnName(identityColumnName)),
            ]
        );
}
