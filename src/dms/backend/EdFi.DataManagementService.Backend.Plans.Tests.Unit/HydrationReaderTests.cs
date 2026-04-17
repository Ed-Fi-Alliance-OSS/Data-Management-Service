// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_HydrationReader_With_Document_Metadata_Result_Sets
{
    [Test]
    public async Task It_reads_document_metadata_rows_using_the_fixed_ordinal_contract()
    {
        DateTimeOffset firstContentLastModifiedAt = new(2026, 4, 3, 14, 10, 11, TimeSpan.Zero);
        DateTimeOffset firstIdentityLastModifiedAt = new(2026, 4, 3, 14, 12, 13, TimeSpan.Zero);
        DateTimeOffset secondContentLastModifiedAt = new(2026, 4, 4, 8, 9, 10, TimeSpan.Zero);
        DateTimeOffset secondIdentityLastModifiedAt = new(2026, 4, 4, 8, 11, 12, TimeSpan.Zero);

        using var reader = HydrationDescriptorResultTestHelper.CreateReader(
            CreateDocumentMetadataTable(
                (
                    42L,
                    Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                    101L,
                    201L,
                    firstContentLastModifiedAt,
                    firstIdentityLastModifiedAt
                ),
                (
                    84L,
                    Guid.Parse("cccccccc-4444-5555-6666-dddddddddddd"),
                    102L,
                    202L,
                    secondContentLastModifiedAt,
                    secondIdentityLastModifiedAt
                )
            )
        );

        var result = await HydrationReader.ReadDocumentMetadataAsync(reader, CancellationToken.None);

        result
            .Should()
            .Equal(
                new DocumentMetadataRow(
                    DocumentId: 42L,
                    DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                    ContentVersion: 101L,
                    IdentityVersion: 201L,
                    ContentLastModifiedAt: firstContentLastModifiedAt,
                    IdentityLastModifiedAt: firstIdentityLastModifiedAt
                ),
                new DocumentMetadataRow(
                    DocumentId: 84L,
                    DocumentUuid: Guid.Parse("cccccccc-4444-5555-6666-dddddddddddd"),
                    ContentVersion: 102L,
                    IdentityVersion: 202L,
                    ContentLastModifiedAt: secondContentLastModifiedAt,
                    IdentityLastModifiedAt: secondIdentityLastModifiedAt
                )
            );
    }

    [Test]
    public async Task It_rejects_document_metadata_result_sets_with_an_unexpected_column_count()
    {
        using var reader = HydrationDescriptorResultTestHelper.CreateReader(
            CreateIncompleteDocumentMetadataTable(
                (
                    42L,
                    Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                    101L,
                    201L,
                    new DateTimeOffset(2026, 4, 3, 14, 10, 11, TimeSpan.Zero)
                )
            )
        );

        var act = () => HydrationReader.ReadDocumentMetadataAsync(reader, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().StartWith("Document metadata result set has 5 columns but expected");
    }

    private static DataTable CreateDocumentMetadataTable(
        params (
            long DocumentId,
            Guid DocumentUuid,
            long ContentVersion,
            long IdentityVersion,
            DateTimeOffset ContentLastModifiedAt,
            DateTimeOffset IdentityLastModifiedAt
        )[] rows
    )
    {
        DataTable table = new();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("DocumentUuid", typeof(Guid));
        table.Columns.Add("ContentVersion", typeof(long));
        table.Columns.Add("IdentityVersion", typeof(long));
        table.Columns.Add("ContentLastModifiedAt", typeof(DateTimeOffset));
        table.Columns.Add("IdentityLastModifiedAt", typeof(DateTimeOffset));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.DocumentId,
                row.DocumentUuid,
                row.ContentVersion,
                row.IdentityVersion,
                row.ContentLastModifiedAt,
                row.IdentityLastModifiedAt
            );
        }

        return table;
    }

    private static DataTable CreateIncompleteDocumentMetadataTable(
        params (
            long DocumentId,
            Guid DocumentUuid,
            long ContentVersion,
            long IdentityVersion,
            DateTimeOffset ContentLastModifiedAt
        )[] rows
    )
    {
        DataTable table = new();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("DocumentUuid", typeof(Guid));
        table.Columns.Add("ContentVersion", typeof(long));
        table.Columns.Add("IdentityVersion", typeof(long));
        table.Columns.Add("ContentLastModifiedAt", typeof(DateTimeOffset));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.DocumentId,
                row.DocumentUuid,
                row.ContentVersion,
                row.IdentityVersion,
                row.ContentLastModifiedAt
            );
        }

        return table;
    }
}
