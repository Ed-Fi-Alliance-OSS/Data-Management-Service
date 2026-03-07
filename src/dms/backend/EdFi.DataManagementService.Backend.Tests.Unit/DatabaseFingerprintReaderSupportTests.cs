// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class DatabaseFingerprintReaderSupportTests
{
    private const string TableDisplayName = "dms.EffectiveSchema";

    private static readonly DatabaseFingerprintColumnNames _columnNames = new(
        EffectiveSchemaSingletonId: "EffectiveSchemaSingletonId",
        ApiSchemaFormatVersion: "ApiSchemaFormatVersion",
        EffectiveSchemaHash: "EffectiveSchemaHash",
        ResourceKeyCount: "ResourceKeyCount",
        ResourceKeySeedHash: "ResourceKeySeedHash"
    );

    private static FingerprintRow CreateValidRow() =>
        new(
            EffectiveSchemaSingletonId: 1,
            ApiSchemaFormatVersion: "1.0",
            EffectiveSchemaHash: new string('a', 64),
            ResourceKeyCount: 42,
            ResourceKeySeedHash: Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()
        );

    private static DbDataReader CreateReader(params FingerprintRow[] rows)
    {
        var table = new DataTable();
        table.Columns.Add("EffectiveSchemaSingletonId", typeof(short));
        table.Columns.Add("ApiSchemaFormatVersion", typeof(string));
        table.Columns.Add("EffectiveSchemaHash", typeof(string));
        table.Columns.Add("ResourceKeyCount", typeof(short));
        table.Columns.Add("ResourceKeySeedHash", typeof(byte[]));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.EffectiveSchemaSingletonId,
                row.ApiSchemaFormatVersion,
                row.EffectiveSchemaHash,
                row.ResourceKeyCount,
                row.ResourceKeySeedHash
            );
        }

        return table.CreateDataReader();
    }

    private sealed record FingerprintRow(
        short EffectiveSchemaSingletonId,
        string ApiSchemaFormatVersion,
        string EffectiveSchemaHash,
        short ResourceKeyCount,
        byte[] ResourceKeySeedHash
    );

    [TestFixture]
    [Parallelizable]
    public class Given_The_Shared_EffectiveSchema_Query_Metadata : DatabaseFingerprintReaderSupportTests
    {
        [TestCase(SqlDialect.Pgsql)]
        [TestCase(SqlDialect.Mssql)]
        public void It_matches_the_provisioned_effective_schema_definition(SqlDialect dialect)
        {
            var query = DatabaseFingerprintReaderSupport.GetEffectiveSchemaQuery(dialect);

            query.TableDisplayName.Should().Be(EffectiveSchemaTableDefinition.TableDisplayName);
            query
                .ExistsCommandText.Should()
                .Be(EffectiveSchemaTableDefinition.RenderExistsCommandText(dialect));
            query
                .ReadCommandText.Should()
                .Be(EffectiveSchemaTableDefinition.RenderReadFingerprintCommandText(dialect));
            query
                .ColumnNames.Should()
                .BeEquivalentTo(
                    new DatabaseFingerprintColumnNames(
                        EffectiveSchemaSingletonId: EffectiveSchemaTableDefinition
                            .EffectiveSchemaSingletonId
                            .Value,
                        ApiSchemaFormatVersion: EffectiveSchemaTableDefinition.ApiSchemaFormatVersion.Value,
                        EffectiveSchemaHash: EffectiveSchemaTableDefinition.EffectiveSchemaHash.Value,
                        ResourceKeyCount: EffectiveSchemaTableDefinition.ResourceKeyCount.Value,
                        ResourceKeySeedHash: EffectiveSchemaTableDefinition.ResourceKeySeedHash.Value
                    )
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Well_Formed_Fingerprint_Row : DatabaseFingerprintReaderSupportTests
    {
        private DatabaseFingerprint? _result;

        [SetUp]
        public async Task Setup()
        {
            await using var reader = CreateReader(CreateValidRow());
            _result = await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                reader,
                _columnNames,
                TableDisplayName
            );
        }

        [Test]
        public void It_returns_the_fingerprint()
        {
            _result.Should().NotBeNull();
            _result!.ApiSchemaFormatVersion.Should().Be("1.0");
            _result.EffectiveSchemaHash.Should().Be(new string('a', 64));
            _result.ResourceKeyCount.Should().Be(42);
            _result.ResourceKeySeedHash.Should().HaveCount(32);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Table_Has_No_Rows : DatabaseFingerprintReaderSupportTests
    {
        private DatabaseFingerprint? _result;

        [SetUp]
        public async Task Setup()
        {
            await using var reader = CreateReader();
            _result = await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                reader,
                _columnNames,
                TableDisplayName
            );
        }

        [Test]
        public void It_returns_null()
        {
            _result.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Multiple_Fingerprint_Rows : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(
                    CreateValidRow(),
                    CreateValidRow() with
                    {
                        EffectiveSchemaSingletonId = 2,
                    }
                );

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_fails_the_singleton_contract()
        {
            await _act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage(
                    "dms.EffectiveSchema must contain exactly one singleton row, but multiple rows were found."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Singleton_Id_Is_Not_One : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(
                    CreateValidRow() with
                    {
                        EffectiveSchemaSingletonId = 2,
                    }
                );

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_reports_the_invalid_singleton_id()
        {
            await _act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage(
                    "dms.EffectiveSchema must contain a singleton row with EffectiveSchemaSingletonId = 1, but found 2."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_ApiSchemaFormatVersion_Is_Empty : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(CreateValidRow() with { ApiSchemaFormatVersion = " " });

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_reports_the_empty_version()
        {
            await _act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("dms.EffectiveSchema.ApiSchemaFormatVersion must not be empty.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_EffectiveSchemaHash_Is_Not_Lowercase_Hex : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(
                    CreateValidRow() with
                    {
                        EffectiveSchemaHash = $"{new string('a', 63)}G",
                    }
                );

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_reports_the_invalid_hash_format()
        {
            await _act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("dms.EffectiveSchema.EffectiveSchemaHash must be 64 lowercase hex characters.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_EffectiveSchemaHash_Has_The_Wrong_Length : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(
                    CreateValidRow() with
                    {
                        EffectiveSchemaHash = new string('a', 63),
                    }
                );

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_reports_the_invalid_hash_length()
        {
            await _act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("dms.EffectiveSchema.EffectiveSchemaHash must be 64 lowercase hex characters.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_ResourceKeyCount_Is_Negative : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(CreateValidRow() with { ResourceKeyCount = -1 });

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_reports_the_invalid_count()
        {
            await _act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("dms.EffectiveSchema.ResourceKeyCount must be non-negative, but found -1.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_ResourceKeySeedHash_Has_The_Wrong_Length : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(
                    CreateValidRow() with
                    {
                        ResourceKeySeedHash = new byte[31],
                    }
                );

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_reports_the_invalid_seed_hash_length()
        {
            await _act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage(
                    "dms.EffectiveSchema.ResourceKeySeedHash must be exactly 32 bytes, but found 31."
                );
        }
    }
}
