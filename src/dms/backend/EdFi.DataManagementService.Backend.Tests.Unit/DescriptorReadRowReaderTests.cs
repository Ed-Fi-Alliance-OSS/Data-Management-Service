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
                ("ContentVersion", 777L),
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
                    ContentVersion: 777L,
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
                ("ContentVersion", 202L),
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

    [TestCase("CodeValue")]
    [TestCase("ShortDescription")]
    public async Task It_classifies_required_descriptor_nulls_as_invariant_failures(string columnName)
    {
        var row = RelationalAccessTestData
            .CreateRow(
                ("DocumentId", 303L),
                ("DocumentUuid", Guid.Parse("aaaaaaaa-1111-2222-3333-dddddddddddd")),
                ("ContentVersion", 303L),
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
    public async Task It_returns_a_null_namespace_when_the_descriptor_row_has_a_null_namespace()
    {
        // Namespace is read nullably so the namespace-authorization path (DescriptorReadHandler)
        // can surface the stored-namespace-uninitialized 403. Without this, an invariant
        // exception would mask the namespace 403 as an UnknownFailure 500.
        var row = RelationalAccessTestData
            .CreateRow(
                ("DocumentId", 304L),
                ("DocumentUuid", Guid.Parse("bbbbbbbb-1111-2222-3333-dddddddddddd")),
                ("ContentVersion", 304L),
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

        row["Namespace"] = null;

        await using var reader = CreateReader(row);

        var result = await DescriptorReadRowReader.ReadSingleOrDefaultAsync(reader);

        result.Should().NotBeNull();
        result!.Namespace.Should().BeNull();
        result.CodeValue.Should().Be("Magnet");
    }

    [Test]
    public async Task It_returns_all_rows_in_result_set_order()
    {
        await using var reader = CreateReader(
            RelationalAccessTestData.CreateRow(
                ("DocumentId", 401L),
                ("DocumentUuid", Guid.Parse("aaaaaaaa-1111-2222-3333-eeeeeeeeeeee")),
                ("ContentVersion", 401L),
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
                ("ContentVersion", 402L),
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

        var result = await DescriptorReadRowReader.ReadAllAsync(reader, carriesSelectedAnchor: false);

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
                ("ContentVersion", 501L),
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
                ("ContentVersion", 502L),
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

    /// <summary>
    /// A page told it carries the anchor reads it from the aliased column the page-rows statement
    /// projects it under, rather than from the row's own <c>dms.Document</c> ContentVersion.
    /// </summary>
    [Test]
    public async Task It_reads_the_selected_anchor_when_the_statement_carries_it()
    {
        await using var reader = CreateReader(
            AnchoredRow(documentId: 601L, contentVersion: 900L, selectedAnchor: 610L),
            AnchoredRow(documentId: 602L, contentVersion: 901L, selectedAnchor: 620L)
        );

        var result = await DescriptorReadRowReader.ReadAllAsync(reader, carriesSelectedAnchor: true);

        result.Select(row => row.SelectedAnchor).Should().Equal(610L, 620L);
        result
            .Select(row => row.ContentVersion)
            .Should()
            .Equal([900L, 901L], "the document's own ContentVersion is a different column");
    }

    /// <summary>
    /// The unwindowed page is the common case, and it must not go looking for a column its statement
    /// never projected — on a real provider that lookup is reported by throwing, once per row.
    /// </summary>
    [Test]
    public async Task It_does_not_look_for_the_selected_anchor_when_the_statement_carries_none()
    {
        await using var inner = CreateReader(
            AnchoredRow(documentId: 701L, contentVersion: 900L, selectedAnchor: null),
            AnchoredRow(documentId: 702L, contentVersion: 901L, selectedAnchor: null)
        );
        var reader = new AnchorLookupCountingReader(inner);

        var result = await DescriptorReadRowReader.ReadAllAsync(reader, carriesSelectedAnchor: false);

        result.Select(row => row.SelectedAnchor).Should().Equal(default(long?), default(long?));
        reader
            .AnchorLookups.Should()
            .Be(0, "a page carrying no anchor must not pay a lookup per row to discover that");
    }

    /// <summary>
    /// Counts how often the reader is asked for the anchor column. A real provider reports an absent
    /// name by throwing, so a per-row probe on a page that carries no anchor is a thrown and caught
    /// exception per row — invisible in behavior and visible only in this count.
    /// </summary>
    private sealed class AnchorLookupCountingReader(IRelationalCommandReader inner) : IRelationalCommandReader
    {
        public int AnchorLookups { get; private set; }

        public int GetOrdinal(string name)
        {
            if (string.Equals(name, "SelectedAnchor", StringComparison.Ordinal))
            {
                AnchorLookups++;
            }

            return inner.GetOrdinal(name);
        }

        public Task<bool> ReadAsync(CancellationToken cancellationToken = default) =>
            inner.ReadAsync(cancellationToken);

        public Task<bool> NextResultAsync(CancellationToken cancellationToken = default) =>
            inner.NextResultAsync(cancellationToken);

        public T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);

        public bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// A descriptor row whose <c>SelectedAnchor</c> is deliberately different from its
    /// <c>ContentVersion</c>, so a reader that confused the two would be caught. Omits the column
    /// entirely when no anchor is supplied, matching the statement an unwindowed page emits.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> AnchoredRow(
        long documentId,
        long contentVersion,
        long? selectedAnchor
    )
    {
        (string, object?)[] columns =
        [
            ("DocumentId", documentId),
            ("DocumentUuid", Guid.NewGuid()),
            ("ContentVersion", contentVersion),
            ("ContentLastModifiedAt", new DateTimeOffset(2026, 5, 5, 17, 0, 0, TimeSpan.Zero)),
            ("ResourceKeyId", (short)13),
            ("Namespace", "uri://ed-fi.org/SchoolTypeDescriptor"),
            ("CodeValue", $"Code{documentId}"),
            ("ShortDescription", $"Short{documentId}"),
            ("Description", null),
            ("EffectiveBeginDate", null),
            ("EffectiveEndDate", null),
            ("Discriminator", "SchoolTypeDescriptor"),
        ];

        return RelationalAccessTestData.CreateRow(
            selectedAnchor is null ? columns : [.. columns, ("SelectedAnchor", (object?)selectedAnchor)]
        );
    }

    private static InMemoryRelationalCommandReader CreateReader(
        params IReadOnlyDictionary<string, object?>[] rows
    ) => new([InMemoryRelationalResultSet.Create(rows)]);
}
