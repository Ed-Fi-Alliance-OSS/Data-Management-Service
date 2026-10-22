// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// The cross-engine descriptor identity matrix. For each Unicode pair it measures whether the engine treats
/// two descriptor URIs as one identity (the second insert collides on
/// <c>UX_Descriptor_UriLowered_ResourceKeyId</c>) and requires every descriptor surface to agree with that
/// verdict: the write probe, reference resolution, a stored-wins descriptor POST and <c>/deletes</c>
/// recreation suppression. <see cref="StringComparison.OrdinalIgnoreCase"/> is recorded beside it, because it
/// is looser than the engines for some pairs.
/// </summary>
/// <remarks>
/// Every (pair, measurement) starts from a baseline where only <c>a</c> exists. SQL-only measurements seed
/// <c>a</c> in their own transaction and roll it back. The stored-wins POST runs through the real
/// <see cref="IDescriptorWriteHandler"/>, which commits on its own connection, so its seed is committed and
/// removed afterwards; every pair has its own namespace, so committed rows never meet another pair's probe.
/// </remarks>
public abstract class DescriptorUnicodeVerdictMatrixTests
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const string LoweredUriIndex = "UX_Descriptor_UriLowered_ResourceKeyId";
    private const string UnloweredUriIndex = "UX_Descriptor_ResourceKeyId_Uri";

    private static readonly QualifiedResourceName _descriptorResource = new("Ed-Fi", "ProgramTypeDescriptor");
    private static readonly QualifiedResourceName _requestResource = new(
        "Ed-Fi",
        "StudentProgramAssociation"
    );

    /// <summary>
    /// The pinned verdicts: true when the pair is one identity. Code values are written as escapes so no
    /// lookalike glyph hides in the source.
    /// </summary>
    private static readonly VerdictPair[] _pairs =
    [
        new("a/A (ASCII control)", "a", "A", Postgresql: true, SqlServer: true, OrdinalIgnoreCase: true),
        new("sharp s/ss", "\u00DF", "ss", Postgresql: false, SqlServer: true, OrdinalIgnoreCase: false),
        new("fullwidth A/A", "\uFF21", "A", Postgresql: false, SqlServer: true, OrdinalIgnoreCase: false),
        new("dotted I/i", "\u0130", "i", Postgresql: true, SqlServer: true, OrdinalIgnoreCase: false),
        new(
            "precomposed e acute/e + combining acute",
            "\u00E9",
            "e\u0301",
            Postgresql: false,
            SqlServer: true,
            OrdinalIgnoreCase: false
        ),
        new(
            "N grave/n grave",
            "\u01F8",
            "\u01F9",
            Postgresql: true,
            SqlServer: true,
            OrdinalIgnoreCase: true
        ),
        new("A/A + emoji", "A", "A\U0001F600", Postgresql: false, SqlServer: true, OrdinalIgnoreCase: false),
        new(
            "A + emoji/A + other emoji",
            "A\U0001F600",
            "A\U0001F601",
            Postgresql: false,
            SqlServer: true,
            OrdinalIgnoreCase: false
        ),
        new("long s/s", "\u017F", "s", Postgresql: false, SqlServer: false, OrdinalIgnoreCase: false),
        new("dotless i/i", "\u0131", "i", Postgresql: false, SqlServer: false, OrdinalIgnoreCase: false),
        new("Kelvin sign/k", "\u212A", "k", Postgresql: true, SqlServer: false, OrdinalIgnoreCase: false),
        new(
            "micro sign/Greek mu",
            "\u00B5",
            "\u03BC",
            Postgresql: false,
            SqlServer: false,
            OrdinalIgnoreCase: true
        ),
    ];

    private IAsyncDisposable _database = null!;
    private MappingSet _mappingSet = null!;
    private ServiceProvider _serviceProvider = null!;

    protected abstract SqlDialect Dialect { get; }

    protected abstract string ConnectionString { get; }

    protected abstract DbConnection CreateConnection();

    /// <summary>
    /// Provisions a dedicated database from <paramref name="fixtureRelativePath"/> and returns it with the
    /// mapping set compiled from the same DDL.
    /// </summary>
    protected abstract Task<(IAsyncDisposable Database, MappingSet MappingSet)> ProvisionAsync(
        string fixtureRelativePath
    );

    /// <summary>
    /// A provider that resolves <see cref="IDescriptorWriteHandler"/> and <see cref="IDataStoreSelection"/>
    /// for this engine.
    /// </summary>
    protected abstract ServiceProvider CreateServiceProvider();

    private protected abstract INaturalKeyLookupCommandBuilder CreateLookupBuilder();

    private protected abstract IRelationalParameterConfigurator CreateParameterConfigurator();

    private bool IsPgsql => Dialect == SqlDialect.Pgsql;

    private string Q(string name) => IsPgsql ? $"\"{name}\"" : $"[{name}]";

    private string Table(string schema, string name) => $"{Q(schema)}.{Q(name)}";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        (_database, _mappingSet) = await ProvisionAsync(FixtureRelativePath);
        _serviceProvider = CreateServiceProvider();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_gives_every_descriptor_surface_the_pinned_collation_verdict_for_every_pair()
    {
        List<string> observed = [];
        for (int index = 0; index < _pairs.Length; index++)
        {
            observed.Add(Format(_pairs[index], await MeasureAsync(_pairs[index], index)));
        }

        await TestContext.Out.WriteLineAsync(string.Join(Environment.NewLine, observed));

        // Every companion surface must reach the collation's verdict; only OrdinalIgnoreCase may differ.
        observed
            .Should()
            .Equal(
                _pairs.Select(pair =>
                {
                    bool same = IsPgsql ? pair.Postgresql : pair.SqlServer;
                    return Format(pair, new Verdicts(same, pair.OrdinalIgnoreCase, same, same, same, same));
                })
            );
    }

    /// <summary>A pair of code values under one namespace, with each engine's pinned identity verdict.</summary>
    private sealed record VerdictPair(
        string Name,
        string A,
        string B,
        bool Postgresql,
        bool SqlServer,
        bool OrdinalIgnoreCase
    );

    private sealed record Verdicts(
        bool Collation,
        bool OrdinalIgnoreCase,
        bool Probe,
        bool Reference,
        bool StoredWins,
        bool Deletes
    );

    private sealed record Uris(string Namespace, string A, string B);

    private static string Format(VerdictPair pair, Verdicts verdicts) =>
        $"{pair.Name}: collation={Same(verdicts.Collation)} ordinalIgnoreCase={Same(verdicts.OrdinalIgnoreCase)} "
        + $"probe={Same(verdicts.Probe)} reference={Same(verdicts.Reference)} "
        + $"storedWins={Same(verdicts.StoredWins)} deletes={Same(verdicts.Deletes)}";

    private static string Same(bool same) => same ? "same" : "different";

    private async Task<Verdicts> MeasureAsync(VerdictPair pair, int index)
    {
        var uris = new Uris($"uri://matrix.test/{index}/ProgramTypeDescriptor", pair.A, pair.B);

        return new Verdicts(
            Collation: await InRolledBackTransactionAsync(
                (connection, transaction) => MeasureCollationAsync(connection, transaction, uris)
            ),
            OrdinalIgnoreCase: string.Equals(
                $"{uris.Namespace}#{uris.A}",
                $"{uris.Namespace}#{uris.B}",
                StringComparison.OrdinalIgnoreCase
            ),
            Probe: await InRolledBackTransactionAsync(
                (connection, transaction) => MeasureProbeAsync(connection, transaction, uris)
            ),
            Reference: await InRolledBackTransactionAsync(
                (connection, transaction) => MeasureReferenceAsync(connection, transaction, uris)
            ),
            StoredWins: await MeasureStoredWinsAsync(uris),
            Deletes: await InRolledBackTransactionAsync(
                (connection, transaction) => MeasureDeletesAsync(connection, transaction, uris)
            )
        );
    }

    /// <summary>
    /// Inserts <c>b</c> beside <c>a</c>. Only the lowered index is left to decide: the unlowered URI index is
    /// dropped inside the transaction, so a collision there cannot stand in for the lowered verdict.
    /// </summary>
    private async Task<bool> MeasureCollationAsync(
        DbConnection connection,
        DbTransaction transaction,
        Uris uris
    )
    {
        await ExecuteAsync(
            connection,
            transaction,
            IsPgsql
                ? $"DROP INDEX {Table("dms", UnloweredUriIndex)};"
                : $"DROP INDEX {Q(UnloweredUriIndex)} ON {Table("dms", "Descriptor")};"
        );
        await InsertDescriptorAsync(connection, transaction, uris.Namespace, uris.A, Guid.NewGuid());

        try
        {
            await InsertDescriptorAsync(connection, transaction, uris.Namespace, uris.B, Guid.NewGuid());
            return false;
        }
        catch (DbException exception) when (exception.Message.Contains(LoweredUriIndex))
        {
            return true;
        }
    }

    private async Task<bool> MeasureProbeAsync(DbConnection connection, DbTransaction transaction, Uris uris)
    {
        long documentId = await InsertDescriptorAsync(
            connection,
            transaction,
            uris.Namespace,
            uris.A,
            Guid.NewGuid()
        );

        RelationalWriteTargetLookupResult target =
            await RelationalWriteTargetLookupSupport.ResolveDescriptorForPostAsync(
                CreateSessionExecutor(connection, transaction),
                _mappingSet,
                _descriptorResource,
                $"{uris.Namespace}#{uris.B}",
                new DocumentUuid(Guid.NewGuid())
            );

        return target switch
        {
            RelationalWriteTargetLookupResult.ExistingDocument existing => existing.DocumentId == documentId
                ? true
                : throw new InvalidOperationException($"The probe found DocumentId {existing.DocumentId}."),
            RelationalWriteTargetLookupResult.CreateNew => false,
            _ => throw new InvalidOperationException($"Unexpected probe result {target}."),
        };
    }

    private async Task<bool> MeasureReferenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        Uris uris
    )
    {
        long documentId = await InsertDescriptorAsync(
            connection,
            transaction,
            uris.Namespace,
            uris.A,
            Guid.NewGuid()
        );
        var path = new JsonPath("$.programTypeDescriptor");

        ResolvedReferenceSet result = await new NaturalKeyReferenceResolver(
            CreateLookupBuilder(),
            new ResolvedDocumentReferenceMapFactory()
        ).ResolveAsync(
            new ReferenceResolverRequest(
                _mappingSet,
                _requestResource,
                [],
                [
                    new DescriptorReference(
                        new BaseResourceInfo(
                            new ProjectName(_descriptorResource.ProjectName),
                            new ResourceName(_descriptorResource.ResourceName),
                            true
                        ),
                        new DocumentIdentity([
                            new DocumentIdentityElement(
                                DocumentIdentity.DescriptorIdentityJsonPath,
                                $"{uris.Namespace}#{uris.B}"
                            ),
                        ]),
                        path
                    ),
                ]
            ),
            CreateSessionExecutor(connection, transaction)
        );

        return result.SuccessfulDescriptorReferencesByPath.TryGetValue(path, out var resolved)
            && (
                resolved.DocumentId == documentId
                    ? true
                    : throw new InvalidOperationException($"The reference resolved to {resolved.DocumentId}.")
            );
    }

    /// <summary>
    /// Posts <c>b</c> through the real descriptor write handler against a committed <c>a</c>. "Same" means the
    /// POST updated <c>a</c>'s document and kept its stored components; "different" means it created a row.
    /// </summary>
    private async Task<bool> MeasureStoredWinsAsync(Uris uris)
    {
        var storedUuid = new DocumentUuid(Guid.NewGuid());
        try
        {
            await using (DbConnection connection = CreateConnection())
            {
                await connection.OpenAsync();
                await InsertDescriptorAsync(connection, null, uris.Namespace, uris.A, storedUuid.Value);
            }

            await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
            scope
                .ServiceProvider.GetRequiredService<IDataStoreSelection>()
                .SetSelectedDataStore(
                    new DataStore(
                        Id: 1,
                        DataStoreType: "test",
                        Name: "DescriptorUnicodeVerdictMatrix",
                        ConnectionString: ConnectionString,
                        RouteContext: []
                    )
                );
            UpsertResult result = await scope
                .ServiceProvider.GetRequiredService<IDescriptorWriteHandler>()
                .HandlePostWithSamePolicyForCreateAndUpdateAsync(
                    new DescriptorWriteRequest(
                        _mappingSet,
                        _descriptorResource,
                        new JsonObject
                        {
                            ["namespace"] = uris.Namespace,
                            ["codeValue"] = uris.B,
                            ["shortDescription"] = "Posted through the matrix",
                        },
                        new DocumentUuid(Guid.NewGuid()),
                        new TraceId("descriptor-unicode-verdict-matrix")
                    )
                );

            List<string> storedCodeValues = await ReadCodeValuesAsync(uris.Namespace);
            return result switch
            {
                UpsertResult.UpdateSuccess update
                    when update.ExistingDocumentUuid == storedUuid
                        && storedCodeValues.SequenceEqual([uris.A]) => true,
                UpsertResult.InsertSuccess when storedCodeValues.Count == 2 => false,
                _ => throw new InvalidOperationException(
                    $"Unexpected descriptor POST outcome {result} with stored code values [{string.Join(", ", storedCodeValues)}]."
                ),
            };
        }
        finally
        {
            await DeleteNamespaceAsync(uris.Namespace);
        }
    }

    /// <summary>Deletes <c>a</c>, recreates the descriptor as <c>b</c>, and reads descriptor <c>/deletes</c>.</summary>
    private async Task<bool> MeasureDeletesAsync(
        DbConnection connection,
        DbTransaction transaction,
        Uris uris
    )
    {
        var repository = new RelationalChangeQueryRepository(
            CreateSessionExecutor(connection, transaction),
            CreateParameterConfigurator()
        );
        long windowStart = await repository.GetNewestChangeVersion() + 1;

        long documentId = await InsertDescriptorAsync(
            connection,
            transaction,
            uris.Namespace,
            uris.A,
            Guid.NewGuid()
        );
        await ExecuteAsync(
            connection,
            transaction,
            $"DELETE FROM {Table("dms", "Descriptor")} WHERE {Q("DocumentId")} = @documentId;",
            ("documentId", documentId)
        );
        await InsertDescriptorAsync(connection, transaction, uris.Namespace, uris.B, Guid.NewGuid());

        ConcreteResourceModel resourceModel = _mappingSet.Model.ConcreteResourcesInNameOrder.Single(model =>
            model.RelationalModel.Resource == _descriptorResource
        );
        TrackedChangeQueryResult result = await repository.QueryTrackedChanges(
            new MatrixTrackedChangeQueryRequest(
                ResourceInfo: new ResourceInfo(
                    new ProjectName(_descriptorResource.ProjectName),
                    new ResourceName(_descriptorResource.ResourceName),
                    IsDescriptor: true,
                    new SemVer("5.2.0"),
                    AllowIdentityUpdates: false
                ),
                Operation: ChangeQueryEndpointOperation.Deletes,
                PaginationParameters: new PaginationParameters(
                    Limit: 25,
                    Offset: 0,
                    TotalCount: true,
                    MaximumPageSize: 500
                ),
                ChangeVersionRange: new ChangeVersionRange(windowStart, long.MaxValue),
                TraceId: new TraceId("descriptor-unicode-verdict-matrix-deletes"),
                AuthorizationContext: new RelationalAuthorizationContext([]),
                AuthorizationStrategyEvaluators: [],
                MappingSet: _mappingSet,
                ResourceModel: resourceModel,
                TrackedChangeTable: _mappingSet.Model.TrackedChangeTablesInNameOrder.Single(table =>
                    table.Kind is TrackedChangeTableKind.SharedDescriptor
                )
            )
        );

        return result.TotalCount switch
        {
            0 => true,
            1 when result.Items[0]!["keyValues"]!["codeValue"]!.GetValue<string>() == uris.A => false,
            _ => throw new InvalidOperationException(
                $"Unexpected /deletes page: {result.Items.ToJsonString()}"
            ),
        };
    }

    private static IRelationalCommandExecutor CreateSessionExecutor(
        DbConnection connection,
        DbTransaction transaction
    ) =>
        (
            (IRelationalWriteSession)new RelationalWriteSession(connection, transaction)
        ).CreateCommandExecutor();

    private async Task<bool> InRolledBackTransactionAsync(
        Func<DbConnection, DbTransaction, Task<bool>> measure
    )
    {
        await using DbConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        try
        {
            return await measure(connection, transaction);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private async Task<long> InsertDescriptorAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string @namespace,
        string codeValue,
        Guid documentUuid
    )
    {
        string insertDocument =
            $"INSERT INTO {Table("dms", "Document")} ({Q("DocumentUuid")}, {Q("ResourceKeyId")})";
        long documentId = Convert.ToInt64(
            (
                await RowsAsync(
                    connection,
                    transaction,
                    IsPgsql
                        ? $"{insertDocument} VALUES (@uuid, @key) RETURNING {Q("DocumentId")};"
                        : $"DECLARE @ids table (Id bigint); {insertDocument} OUTPUT inserted.DocumentId INTO @ids VALUES (@uuid, @key); SELECT Id FROM @ids;",
                    ("uuid", documentUuid),
                    ("key", _mappingSet.ResourceKeyIdByResource[_descriptorResource])
                )
            ).Single()[0]
        );

        await ExecuteAsync(
            connection,
            transaction,
            $"INSERT INTO {Table("dms", "Descriptor")} ({Q("DocumentId")}, {Q("ResourceKeyId")}, {Q("Namespace")}, {Q("CodeValue")}, {Q("ShortDescription")}) VALUES (@documentId, @key, @ns, @codeValue, 'Matrix');",
            ("documentId", documentId),
            ("key", _mappingSet.ResourceKeyIdByResource[_descriptorResource]),
            ("ns", @namespace),
            ("codeValue", codeValue)
        );

        return documentId;
    }

    private async Task<List<string>> ReadCodeValuesAsync(string @namespace)
    {
        await using DbConnection connection = CreateConnection();
        await connection.OpenAsync();

        return
        [
            .. (
                await RowsAsync(
                    connection,
                    null,
                    $"SELECT {Q("CodeValue")} FROM {Table("dms", "Descriptor")} WHERE {Q("Namespace")} = @ns ORDER BY {Q("DescriptorId")};",
                    ("ns", @namespace)
                )
            ).Select(row => (string)row[0]),
        ];
    }

    /// <summary>
    /// Removes every committed row a stored-wins measurement left under its namespace: the descriptors, the
    /// tombstones their deletion writes, and their documents.
    /// </summary>
    private async Task DeleteNamespaceAsync(string @namespace)
    {
        await using DbConnection connection = CreateConnection();
        await connection.OpenAsync();
        string descriptor = Table("dms", "Descriptor");
        List<object[]> documentIds = await RowsAsync(
            connection,
            null,
            $"SELECT {Q("DocumentId")} FROM {descriptor} WHERE {Q("Namespace")} = @ns;",
            ("ns", @namespace)
        );

        await ExecuteAsync(
            connection,
            null,
            $"DELETE FROM {descriptor} WHERE {Q("Namespace")} = @ns;",
            ("ns", @namespace)
        );
        await ExecuteAsync(
            connection,
            null,
            $"DELETE FROM {Table("tracked_changes_edfi", "Descriptor")} WHERE {Q("OldNamespace")} = @ns;",
            ("ns", @namespace)
        );
        foreach (object[] row in documentIds)
        {
            await ExecuteAsync(
                connection,
                null,
                $"DELETE FROM {Table("dms", "Document")} WHERE {Q("DocumentId")} = @documentId;",
                ("documentId", row[0])
            );
        }
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        params (string Name, object Value)[] values
    )
    {
        await using DbCommand command = CreateCommand(connection, transaction, sql, values);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<object[]>> RowsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        params (string Name, object Value)[] values
    )
    {
        await using DbCommand command = CreateCommand(connection, transaction, sql, values);
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        List<object[]> rows = [];

        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }

        return rows;
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        (string Name, object Value)[] values
    )
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 60;

        foreach (var (name, value) in values)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}

file sealed record MatrixTrackedChangeQueryRequest(
    ResourceInfo ResourceInfo,
    ChangeQueryEndpointOperation Operation,
    PaginationParameters PaginationParameters,
    ChangeVersionRange ChangeVersionRange,
    TraceId TraceId,
    RelationalAuthorizationContext AuthorizationContext,
    IReadOnlyList<AuthorizationStrategyEvaluator> AuthorizationStrategyEvaluators,
    MappingSet MappingSet,
    ConcreteResourceModel ResourceModel,
    TrackedChangeTableInfo TrackedChangeTable
) : IRelationalTrackedChangeQueryRequest;
