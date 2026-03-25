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

[TestFixture]
public class Given_RelationalReferenceResolverAdapter
{
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");

    [Test]
    public async Task It_can_drive_reference_resolution_without_repository_wiring()
    {
        var documentReferentialId = new ReferentialId(Guid.NewGuid());
        var descriptorReferentialId = new ReferentialId(Guid.NewGuid());
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("ReferentialId", documentReferentialId.Value),
                        ("DocumentId", 101L),
                        ("ResourceKeyId", (short)11),
                        ("IsDescriptor", false)
                    ),
                    RelationalAccessTestData.CreateRow(
                        ("ReferentialId", descriptorReferentialId.Value),
                        ("DocumentId", 202L),
                        ("ResourceKeyId", (short)12),
                        ("IsDescriptor", true)
                    )
                ),
            ]),
        ]);
        var sut = new ReferenceResolver(new TestRelationalReferenceResolverAdapter(executor));

        var result = await sut.ResolveAsync(
            new ReferenceResolverRequest(
                MappingSet: RelationalAccessTestData.CreateMappingSet(_requestResource),
                RequestResource: _requestResource,
                DocumentReferences:
                [
                    RelationalAccessTestData.CreateDocumentReference(
                        documentReferentialId,
                        "$.schoolReference"
                    ),
                    RelationalAccessTestData.CreateDocumentReference(
                        documentReferentialId,
                        "$.educationOrganizationReference"
                    ),
                ],
                DescriptorReferences:
                [
                    RelationalAccessTestData.CreateDescriptorReference(
                        descriptorReferentialId,
                        "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
                        "$.schoolTypeDescriptor"
                    ),
                ]
            )
        );

        executor.Commands.Should().ContainSingle();
        executor.Commands[0].CommandText.Should().Be("test lookup");
        executor
            .Commands[0]
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(documentReferentialId.Value, descriptorReferentialId.Value);

        result
            .SuccessfulDocumentReferencesByPath.Keys.Should()
            .Equal(new JsonPath("$.schoolReference"), new JsonPath("$.educationOrganizationReference"));
        result
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.schoolReference")]
            .DocumentId.Should()
            .Be(101L);
        result.DocumentReferenceOccurrences.Should().HaveCount(2);
        result
            .SuccessfulDescriptorReferencesByPath[new JsonPath("$.schoolTypeDescriptor")]
            .DocumentId.Should()
            .Be(202L);
    }
}

internal sealed record BatchReadResult(
    IReadOnlyList<int> Values,
    bool MovedToSecondResultSet,
    IReadOnlyList<string> Labels
);

internal sealed class InMemoryRelationalCommandExecutor(
    IReadOnlyList<InMemoryRelationalCommandExecution> executions
) : IRelationalCommandExecutor
{
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

internal sealed class TestRelationalReferenceResolverAdapter(IRelationalCommandExecutor commandExecutor)
    : IReferenceResolverAdapter
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
        ReferenceLookupRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = request
            .ReferentialIds.Select(
                (referentialId, ordinal) => new RelationalParameter($"@p{ordinal}", referentialId.Value)
            )
            .ToArray();

        return _commandExecutor.ExecuteReaderAsync(
            new RelationalCommand("test lookup", parameters),
            ReferenceLookupResultReader.ReadAsync,
            cancellationToken
        );
    }
}

internal static class RelationalAccessTestData
{
    public static MappingSet CreateMappingSet(QualifiedResourceName requestResource)
    {
        const string EffectiveSchemaHash = "test-hash";

        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: EffectiveSchemaHash,
            ResourceKeyCount: 1,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder:
            [
                new ResourceKeyEntry(
                    ResourceKeyId: 1,
                    Resource: requestResource,
                    ResourceVersion: "1.0",
                    IsAbstractResource: false
                ),
            ]
        );

        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: effectiveSchema,
            Dialect: SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder: [],
            ConcreteResourcesInNameOrder: [],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey(EffectiveSchemaHash, SqlDialect.Pgsql, "v1"),
            Model: modelSet,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short> { [requestResource] = 1 },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [1] = new ResourceKeyEntry(1, requestResource, "1.0", false),
            }
        );
    }

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
}
