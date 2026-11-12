// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_Natural_Key_Lookup_Result_Reader
{
    private const short SchoolResourceKeyId = 11;
    private const short LocalEducationAgencyResourceKeyId = 12;
    private const short SchoolTypeDescriptorResourceKeyId = 13;
    private const short EducationOrganizationResourceKeyId = 30;

    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _educationOrganizationResource = new(
        "Ed-Fi",
        "EducationOrganization"
    );
    private static readonly QualifiedResourceName _schoolTypeDescriptorResource = new(
        "Ed-Fi",
        "SchoolTypeDescriptor"
    );

    private static readonly NaturalKeyLookupTarget _schoolTarget = NaturalKeyLookupTarget.ForProbe(
        new NaturalKeyProbeTarget(
            _schoolResource,
            NaturalKeyProbeTargetKind.Concrete,
            new DbTableName(new DbSchemaName("edfi"), "School"),
            [Key("schoolId", "SchoolId")],
            new DbColumnName("DocumentId")
        ),
        SchoolResourceKeyId
    );

    private static readonly NaturalKeyLookupTarget _educationOrganizationTarget =
        NaturalKeyLookupTarget.ForProbe(
            new NaturalKeyProbeTarget(
                _educationOrganizationResource,
                NaturalKeyProbeTargetKind.Abstract,
                new DbTableName(new DbSchemaName("edfi"), "EducationOrganizationIdentity"),
                [Key("educationOrganizationId", "EducationOrganizationId")],
                new DbColumnName("DocumentId"),
                new DbColumnName("ResourceKeyId")
            ),
            EducationOrganizationResourceKeyId
        );

    private static readonly NaturalKeyLookupTarget _schoolTypeDescriptorTarget =
        NaturalKeyLookupTarget.ForDescriptor(
            _schoolTypeDescriptorResource,
            SchoolTypeDescriptorResourceKeyId
        );

    [Test]
    public async Task It_returns_a_miss_for_every_entry_when_no_rows_match()
    {
        var request = CreateRequest((_schoolTarget, 2), (_schoolTypeDescriptorTarget, 1));

        var matches = await ReadAllAsync(request, InMemoryRelationalResultSet.Create());

        matches[0].Should().HaveCount(2).And.OnlyContain(match => match == null);
        matches[1].Should().ContainSingle().Which.Should().BeNull();
    }

    [Test]
    public async Task It_maps_one_row_to_its_group_and_ordinal()
    {
        var request = CreateRequest((_schoolTarget, 2));

        var matches = await ReadAllAsync(
            request,
            InMemoryRelationalResultSet.Create(Row(1, 2, 101, null, SchoolResourceKeyId))
        );

        matches[0][0].Should().BeNull();
        matches[0][1].Should().Be(new NaturalKeyLookupMatch(101, null, SchoolResourceKeyId));
    }

    [Test]
    public async Task It_maps_rows_by_group_and_ordinal_not_by_row_position()
    {
        var request = CreateRequest((_schoolTarget, 2), (_schoolTypeDescriptorTarget, 1));

        var matches = await ReadAllAsync(
            request,
            InMemoryRelationalResultSet.Create(
                Row(2, 1, 303, 9, SchoolTypeDescriptorResourceKeyId),
                Row(1, 2, 202, null, SchoolResourceKeyId),
                Row(1, 1, 101, null, SchoolResourceKeyId)
            )
        );

        matches[0][0]!.DocumentId.Should().Be(101);
        matches[0][1]!.DocumentId.Should().Be(202);
        matches[1][0]!.DocumentId.Should().Be(303);
    }

    [Test]
    public async Task It_throws_corruption_when_two_rows_match_one_ordinal()
    {
        var request = CreateRequest((_schoolTarget, 1));

        var act = () =>
            ReadAllAsync(
                request,
                InMemoryRelationalResultSet.Create(
                    Row(1, 1, 101, null, SchoolResourceKeyId),
                    Row(1, 1, 102, null, SchoolResourceKeyId)
                )
            );

        await act.Should()
            .ThrowAsync<ReferenceLookupCorruptionException>()
            .WithMessage("*more than one row for ordinal 1 of group 1 ('Ed-Fi.School')*");
    }

    [TestCase(0, 1, "*group ordinal 0*")]
    [TestCase(2, 1, "*group ordinal 2*")]
    [TestCase(1, 0, "*ordinal 0 for group 1*")]
    [TestCase(1, 2, "*ordinal 2 for group 1*")]
    public async Task It_rejects_a_row_outside_the_request(int groupOrdinal, int ordinal, string message)
    {
        var request = CreateRequest((_schoolTarget, 1));

        var act = () =>
            ReadAllAsync(
                request,
                InMemoryRelationalResultSet.Create(Row(groupOrdinal, ordinal, 101, null, SchoolResourceKeyId))
            );

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
    }

    [Test]
    public async Task It_keeps_unequal_descriptor_and_document_ids_in_their_own_slots()
    {
        var request = CreateRequest((_schoolTypeDescriptorTarget, 1));

        var matches = await ReadAllAsync(
            request,
            InMemoryRelationalResultSet.Create(Row(1, 1, 900001, 7, SchoolTypeDescriptorResourceKeyId))
        );

        matches[0][0]!.DescriptorId.Should().Be(7);
        matches[0][0]!.DocumentId.Should().Be(900001);
    }

    [Test]
    public async Task It_rejects_a_descriptor_row_without_a_descriptor_id()
    {
        var request = CreateRequest((_schoolTypeDescriptorTarget, 1));

        var act = () =>
            ReadAllAsync(
                request,
                InMemoryRelationalResultSet.Create(Row(1, 1, 900001, null, SchoolTypeDescriptorResourceKeyId))
            );

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*has no DescriptorId*");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_a_descriptor_id_on_a_non_descriptor_group(bool abstractTarget)
    {
        var target = abstractTarget ? _educationOrganizationTarget : _schoolTarget;
        var request = CreateRequest((target, 1));

        var act = () =>
            ReadAllAsync(request, InMemoryRelationalResultSet.Create(Row(1, 1, 101, 7, SchoolResourceKeyId)));

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned a DescriptorId, but the group is not a descriptor target*");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_a_key_other_than_the_projected_literal(bool descriptorTarget)
    {
        var target = descriptorTarget ? _schoolTypeDescriptorTarget : _schoolTarget;
        int? descriptorId = descriptorTarget ? 7 : null;
        var request = CreateRequest((target, 1));

        var act = () =>
            ReadAllAsync(
                request,
                InMemoryRelationalResultSet.Create(
                    Row(1, 1, 101, descriptorId, LocalEducationAgencyResourceKeyId)
                )
            );

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"*returned ResourceKeyId {LocalEducationAgencyResourceKeyId}*");
    }

    [Test]
    public async Task It_keeps_the_concrete_member_key_an_abstract_group_returns()
    {
        var request = CreateRequest((_educationOrganizationTarget, 1));

        var matches = await ReadAllAsync(
            request,
            InMemoryRelationalResultSet.Create(Row(1, 1, 101, null, LocalEducationAgencyResourceKeyId))
        );

        matches[0][0].Should().Be(new NaturalKeyLookupMatch(101, null, LocalEducationAgencyResourceKeyId));
    }

    [Test]
    public async Task It_rejects_a_null_required_column()
    {
        var request = CreateRequest((_schoolTarget, 1));

        var act = () =>
            ReadAllAsync(
                request,
                InMemoryRelationalResultSet.Create(
                    new Dictionary<string, object?>
                    {
                        [NaturalKeyLookupColumns.GroupOrdinal] = 1,
                        [NaturalKeyLookupColumns.Ordinal] = 1,
                        [NaturalKeyLookupColumns.DocumentId] = null,
                        [NaturalKeyLookupColumns.DescriptorId] = null,
                        [NaturalKeyLookupColumns.ResourceKeyId] = SchoolResourceKeyId,
                    }
                )
            );

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*NULL for required column 'DocumentId'*");
    }

    [Test]
    public async Task It_reads_only_the_current_result_set()
    {
        var request = CreateRequest((_schoolTarget, 1));
        var reader = new NextResultCountingReader(
            new InMemoryRelationalCommandReader([
                InMemoryRelationalResultSet.Create(Row(1, 1, 101, null, SchoolResourceKeyId)),
                InMemoryRelationalResultSet.Create(new Dictionary<string, object?> { ["Marker"] = 7 }),
            ])
        );
        var matches = NaturalKeyLookupResultReader.CreateMatchTable(request);

        await NaturalKeyLookupResultReader.ReadCurrentResultSetAsync(reader, request, matches);

        reader.NextResultCalls.Should().Be(0);
        matches[0][0]!.DocumentId.Should().Be(101);
        (await reader.NextResultAsync()).Should().BeTrue();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetFieldValue<int>(reader.GetOrdinal("Marker")).Should().Be(7);
    }

    [Test]
    public async Task It_reads_every_result_set_of_a_standalone_command()
    {
        var request = CreateRequest((_schoolTarget, 1), (_schoolTypeDescriptorTarget, 1));

        var matches = await ReadAllAsync(
            request,
            InMemoryRelationalResultSet.Create(Row(1, 1, 101, null, SchoolResourceKeyId)),
            InMemoryRelationalResultSet.Create(Row(2, 1, 900001, 7, SchoolTypeDescriptorResourceKeyId))
        );

        matches[0][0]!.DocumentId.Should().Be(101);
        matches[1][0].Should().Be(new NaturalKeyLookupMatch(900001, 7, SchoolTypeDescriptorResourceKeyId));
    }

    [Test]
    public async Task It_decodes_a_composite_result_set_without_advancing_or_closing_the_reader()
    {
        var request = CreateRequest((_schoolTypeDescriptorTarget, 1));
        var lookupTable = CreateLookupTable();
        lookupTable.Rows.Add(1, 1, 900001L, 7, SchoolTypeDescriptorResourceKeyId);
        var markerTable = new DataTable();
        markerTable.Columns.Add("Marker", typeof(int));
        markerTable.Rows.Add(7);
        using var reader = new DataTableReader([lookupTable, markerTable]);

        var decoded = await NaturalKeyLookupResultReader.ReadCompositeResultSetAsync(reader, request);

        var matches = decoded.Should().BeOfType<NaturalKeyLookupMatch?[][]>().Subject;
        matches[0][0].Should().Be(new NaturalKeyLookupMatch(900001, 7, SchoolTypeDescriptorResourceKeyId));
        reader.IsClosed.Should().BeFalse();
        (await reader.NextResultAsync()).Should().BeTrue();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(7);
    }

    [Test]
    public void It_rejects_group_ordinals_that_are_not_sequential()
    {
        var request = new NaturalKeyLookupRequest(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            [new NaturalKeyLookupGroup(2, _schoolTarget, [new NaturalKeyLookupEntry([255901L])])]
        );

        var act = () => NaturalKeyLookupResultReader.CreateMatchTable(request);

        act.Should().Throw<InvalidOperationException>().WithMessage("*declares group ordinal 2*");
    }

    [Test]
    public void It_rejects_a_group_without_entries()
    {
        var request = new NaturalKeyLookupRequest(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            [new NaturalKeyLookupGroup(1, _schoolTarget, [])]
        );

        var act = () => NaturalKeyLookupResultReader.CreateMatchTable(request);

        act.Should().Throw<InvalidOperationException>().WithMessage("*has no entries*");
    }

    private static Task<NaturalKeyLookupMatch?[][]> ReadAllAsync(
        NaturalKeyLookupRequest request,
        params InMemoryRelationalResultSet[] resultSets
    ) =>
        NaturalKeyLookupResultReader.ReadAllResultSetsAsync(
            new InMemoryRelationalCommandReader(resultSets),
            request
        );

    private static NaturalKeyLookupRequest CreateRequest(
        params (NaturalKeyLookupTarget Target, int EntryCount)[] groups
    ) =>
        new(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            [
                .. groups.Select(
                    (group, index) =>
                        new NaturalKeyLookupGroup(
                            index + 1,
                            group.Target,
                            [
                                .. Enumerable
                                    .Range(0, group.EntryCount)
                                    .Select(entry => new NaturalKeyLookupEntry([(long)entry])),
                            ]
                        )
                ),
            ]
        );

    private static Dictionary<string, object?> Row(
        int groupOrdinal,
        int ordinal,
        long documentId,
        int? descriptorId,
        short resourceKeyId
    ) =>
        new()
        {
            [NaturalKeyLookupColumns.GroupOrdinal] = groupOrdinal,
            [NaturalKeyLookupColumns.Ordinal] = ordinal,
            [NaturalKeyLookupColumns.DocumentId] = documentId,
            [NaturalKeyLookupColumns.DescriptorId] = descriptorId,
            [NaturalKeyLookupColumns.ResourceKeyId] = resourceKeyId,
        };

    private static DataTable CreateLookupTable()
    {
        var table = new DataTable();
        table.Columns.Add(NaturalKeyLookupColumns.GroupOrdinal, typeof(int));
        table.Columns.Add(NaturalKeyLookupColumns.Ordinal, typeof(int));
        table.Columns.Add(NaturalKeyLookupColumns.DocumentId, typeof(long));
        table.Columns.Add(NaturalKeyLookupColumns.DescriptorId, typeof(int)).AllowDBNull = true;
        table.Columns.Add(NaturalKeyLookupColumns.ResourceKeyId, typeof(short));
        return table;
    }

    private static NaturalKeyProbeKeyColumn Key(string propertyName, string columnName) =>
        new(
            new JsonPathExpression($"$.{propertyName}", [new JsonPathSegment.Property(propertyName)]),
            new DbColumnName(columnName),
            new NaturalKeyProbeKeyBinding.Scalar(new RelationalScalarType(ScalarKind.Int64))
        );

    private sealed class NextResultCountingReader(IRelationalCommandReader inner) : IRelationalCommandReader
    {
        public int NextResultCalls { get; private set; }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public Task<bool> ReadAsync(CancellationToken cancellationToken = default) =>
            inner.ReadAsync(cancellationToken);

        public Task<bool> NextResultAsync(CancellationToken cancellationToken = default)
        {
            NextResultCalls++;
            return inner.NextResultAsync(cancellationToken);
        }

        public int GetOrdinal(string name) => inner.GetOrdinal(name);

        public T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);

        public bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
    }
}
