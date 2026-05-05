// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_DescriptorReadRowReader
{
    [Test]
    public async Task It_reads_descriptor_rows_with_provider_neutral_date_and_timestamp_values()
    {
        var documentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");

        await using var reader = CreateReader(
            RelationalAccessTestData.CreateRow(
                ("DocumentId", 101L),
                ("DocumentUuid", documentUuid),
                ("ContentLastModifiedAt", new DateTime(2026, 5, 5, 14, 30, 45, DateTimeKind.Unspecified)),
                ("ResourceKeyId", (short)13),
                ("Namespace", "uri://ed-fi.org/SchoolTypeDescriptor"),
                ("CodeValue", "Alternative"),
                ("ShortDescription", "Alternative"),
                ("Description", "Alternative school type"),
                ("EffectiveBeginDate", new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Unspecified)),
                ("EffectiveEndDate", new DateOnly(2025, 12, 31)),
                ("Discriminator", "SchoolTypeDescriptor")
            )
        );

        var result = await DescriptorReadRowReader.ReadSingleOrDefaultAsync(reader);

        result
            .Should()
            .Be(
                new DescriptorReadRow(
                    DocumentId: 101L,
                    DocumentUuid: documentUuid,
                    ContentLastModifiedAt: new DateTimeOffset(2026, 5, 5, 14, 30, 45, TimeSpan.Zero),
                    ResourceKeyId: 13,
                    Namespace: "uri://ed-fi.org/SchoolTypeDescriptor",
                    CodeValue: "Alternative",
                    ShortDescription: "Alternative",
                    Description: "Alternative school type",
                    EffectiveBeginDate: new DateOnly(2025, 1, 15),
                    EffectiveEndDate: new DateOnly(2025, 12, 31),
                    Discriminator: "SchoolTypeDescriptor"
                )
            );
    }

    [Test]
    public async Task It_preserves_null_optional_descriptor_fields_and_absent_discriminator()
    {
        var documentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-cccccccccccc");

        await using var reader = CreateReader(
            RelationalAccessTestData.CreateRow(
                ("DocumentId", 202L),
                ("DocumentUuid", documentUuid),
                ("ContentLastModifiedAt", new DateTimeOffset(2026, 5, 5, 15, 0, 0, TimeSpan.Zero)),
                ("ResourceKeyId", (short)13),
                ("Namespace", "uri://ed-fi.org/SchoolTypeDescriptor"),
                ("CodeValue", "Charter"),
                ("ShortDescription", "Charter"),
                ("Description", null),
                ("EffectiveBeginDate", null),
                ("EffectiveEndDate", null)
            )
        );

        var result = await DescriptorReadRowReader.ReadSingleOrDefaultAsync(reader);

        result.Should().NotBeNull();
        result!.Description.Should().BeNull();
        result.EffectiveBeginDate.Should().BeNull();
        result.EffectiveEndDate.Should().BeNull();
        result.Discriminator.Should().BeNull();
    }

    [TestCase("Namespace")]
    [TestCase("CodeValue")]
    [TestCase("ShortDescription")]
    public async Task It_classifies_required_descriptor_nulls_as_invariant_failures(string columnName)
    {
        var row = RelationalAccessTestData
            .CreateRow(
                ("DocumentId", 303L),
                ("DocumentUuid", Guid.Parse("aaaaaaaa-1111-2222-3333-dddddddddddd")),
                ("ContentLastModifiedAt", new DateTimeOffset(2026, 5, 5, 16, 0, 0, TimeSpan.Zero)),
                ("ResourceKeyId", (short)13),
                ("Namespace", "uri://ed-fi.org/SchoolTypeDescriptor"),
                ("CodeValue", "Magnet"),
                ("ShortDescription", "Magnet"),
                ("Description", "Magnet school type"),
                ("EffectiveBeginDate", new DateOnly(2025, 1, 1)),
                ("EffectiveEndDate", null)
            )
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        row[columnName] = null;

        await using var reader = CreateReader(row);

        var act = async () => await DescriptorReadRowReader.ReadSingleOrDefaultAsync(reader);

        var exception = await act.Should().ThrowAsync<DescriptorReadInvariantException>();
        exception
            .Which.Message.Should()
            .Contain($"dms.Descriptor.{columnName} must not be null.")
            .And.Contain("DocumentId 303")
            .And.Contain("ResourceKeyId=13");
    }

    [Test]
    public async Task It_returns_all_rows_in_result_set_order()
    {
        await using var reader = CreateReader(
            RelationalAccessTestData.CreateRow(
                ("DocumentId", 401L),
                ("DocumentUuid", Guid.Parse("aaaaaaaa-1111-2222-3333-eeeeeeeeeeee")),
                ("ContentLastModifiedAt", new DateTimeOffset(2026, 5, 5, 17, 0, 0, TimeSpan.Zero)),
                ("ResourceKeyId", (short)13),
                ("Namespace", "uri://ed-fi.org/SchoolTypeDescriptor"),
                ("CodeValue", "First"),
                ("ShortDescription", "First"),
                ("Description", null),
                ("EffectiveBeginDate", null),
                ("EffectiveEndDate", null)
            ),
            RelationalAccessTestData.CreateRow(
                ("DocumentId", 402L),
                ("DocumentUuid", Guid.Parse("aaaaaaaa-1111-2222-3333-ffffffffffff")),
                ("ContentLastModifiedAt", new DateTimeOffset(2026, 5, 5, 18, 0, 0, TimeSpan.Zero)),
                ("ResourceKeyId", (short)13),
                ("Namespace", "uri://ed-fi.org/SchoolTypeDescriptor"),
                ("CodeValue", "Second"),
                ("ShortDescription", "Second"),
                ("Description", "Second item"),
                ("EffectiveBeginDate", new DateOnly(2025, 2, 1)),
                ("EffectiveEndDate", null),
                ("Discriminator", "SchoolTypeDescriptor")
            )
        );

        var result = await DescriptorReadRowReader.ReadAllAsync(reader);

        result.Select(row => row.DocumentId).Should().Equal(401L, 402L);
        result.Select(row => row.CodeValue).Should().Equal("First", "Second");
    }

    [Test]
    public async Task It_rejects_multiple_rows_when_a_single_row_is_expected()
    {
        await using var reader = CreateReader(
            RelationalAccessTestData.CreateRow(
                ("DocumentId", 501L),
                ("DocumentUuid", Guid.Parse("aaaaaaaa-1111-2222-3333-111111111111")),
                ("ContentLastModifiedAt", new DateTimeOffset(2026, 5, 5, 19, 0, 0, TimeSpan.Zero)),
                ("ResourceKeyId", (short)13),
                ("Namespace", "uri://ed-fi.org/SchoolTypeDescriptor"),
                ("CodeValue", "One"),
                ("ShortDescription", "One"),
                ("Description", null),
                ("EffectiveBeginDate", null),
                ("EffectiveEndDate", null)
            ),
            RelationalAccessTestData.CreateRow(
                ("DocumentId", 502L),
                ("DocumentUuid", Guid.Parse("aaaaaaaa-1111-2222-3333-222222222222")),
                ("ContentLastModifiedAt", new DateTimeOffset(2026, 5, 5, 20, 0, 0, TimeSpan.Zero)),
                ("ResourceKeyId", (short)13),
                ("Namespace", "uri://ed-fi.org/SchoolTypeDescriptor"),
                ("CodeValue", "Two"),
                ("ShortDescription", "Two"),
                ("Description", null),
                ("EffectiveBeginDate", null),
                ("EffectiveEndDate", null)
            )
        );

        var act = async () => await DescriptorReadRowReader.ReadSingleOrDefaultAsync(reader);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Descriptor single-row read returned multiple rows.");
    }

    private static InMemoryRelationalCommandReader CreateReader(
        params IReadOnlyDictionary<string, object?>[] rows
    ) => new([InMemoryRelationalResultSet.Create(rows)]);
}
