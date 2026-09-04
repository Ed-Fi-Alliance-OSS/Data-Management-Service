// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

public interface IDocumentCacheRepresentationRestampCommand
{
    Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheRepresentationRestampPreviewRequest request,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheRepresentationRestampExecuteRequest request,
        CancellationToken cancellationToken = default
    );
}

public static class RepresentationRestampOperationAdmission
{
    public static bool CanExecute(DocumentCacheRepresentationRestampOperationState state) =>
        state
            is DocumentCacheRepresentationRestampOperationState.Draft
                or DocumentCacheRepresentationRestampOperationState.Incomplete;
}

public sealed record RepresentationRestampSelection
{
    public RepresentationRestampSelection(
        DocumentCacheRepresentationRestampScope canonicalScope,
        long previewDocumentCount
    )
    {
        ArgumentNullException.ThrowIfNull(canonicalScope);
        if (previewDocumentCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(previewDocumentCount),
                previewDocumentCount,
                "Representation restamp preview count cannot be negative."
            );
        }

        CanonicalScope = canonicalScope;
        PreviewDocumentCount = previewDocumentCount;
    }

    public DocumentCacheRepresentationRestampScope CanonicalScope { get; }

    public long PreviewDocumentCount { get; }
}

public sealed record RepresentationRestampMirrorRoute
{
    public RepresentationRestampMirrorRoute(
        short resourceKeyId,
        string mirrorStampTargetSchema,
        string mirrorStampTargetTable,
        bool isDescriptor
    )
    {
        if (resourceKeyId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resourceKeyId),
                resourceKeyId,
                "Representation restamp resource keys must be positive."
            );
        }

        if (
            string.IsNullOrWhiteSpace(mirrorStampTargetSchema)
            || string.IsNullOrWhiteSpace(mirrorStampTargetTable)
        )
        {
            throw new ArgumentException(
                "Representation restamp mirror routes require a compiled schema and table name."
            );
        }

        ResourceKeyId = resourceKeyId;
        MirrorStampTargetSchema = mirrorStampTargetSchema;
        MirrorStampTargetTable = mirrorStampTargetTable;
        IsDescriptor = isDescriptor;
    }

    public short ResourceKeyId { get; }

    public string MirrorStampTargetSchema { get; }

    public string MirrorStampTargetTable { get; }

    public bool IsDescriptor { get; }
}

public sealed record RepresentationRestampDocument
{
    public RepresentationRestampDocument(
        long documentId,
        Guid documentUuid,
        RepresentationRestampMirrorRoute mirrorRoute
    )
    {
        if (documentId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(documentId),
                documentId,
                "Representation restamp document ids must be positive."
            );
        }

        if (documentUuid == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(
                nameof(documentUuid),
                documentUuid,
                "Representation restamp document UUIDs must not be empty."
            );
        }

        ArgumentNullException.ThrowIfNull(mirrorRoute);
        DocumentId = documentId;
        DocumentUuid = documentUuid;
        MirrorRoute = mirrorRoute;
    }

    public long DocumentId { get; }

    public Guid DocumentUuid { get; }

    public RepresentationRestampMirrorRoute MirrorRoute { get; }
}

public sealed record RepresentationRestampPage
{
    public RepresentationRestampPage(ImmutableArray<RepresentationRestampDocument> documents)
    {
        documents = documents.IsDefault ? [] : documents;
        if (documents.Any(document => document is null))
        {
            throw new ArgumentException(
                "Representation restamp pages cannot contain null documents.",
                nameof(documents)
            );
        }

        if (documents.Zip(documents.Skip(1)).Any(pair => pair.First.DocumentId >= pair.Second.DocumentId))
        {
            throw new ArgumentException(
                "Representation restamp pages must be strictly ordered by DocumentId.",
                nameof(documents)
            );
        }

        Documents = documents;
    }

    public ImmutableArray<RepresentationRestampDocument> Documents { get; }

    public int Count => Documents.Length;

    public bool IsEmpty => Documents.IsEmpty;
}

public sealed record RepresentationRestampStamp
{
    public RepresentationRestampStamp(
        long documentId,
        long contentVersion,
        DateTimeOffset contentLastModifiedAt
    )
    {
        if (documentId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(documentId),
                documentId,
                "Document ids must be positive."
            );
        }

        if (contentVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentVersion),
                contentVersion,
                "Content versions must be positive."
            );
        }

        DocumentId = documentId;
        ContentVersion = contentVersion;
        ContentLastModifiedAt = contentLastModifiedAt;
    }

    public long DocumentId { get; }

    public long ContentVersion { get; }

    public DateTimeOffset ContentLastModifiedAt { get; }
}

public sealed record RepresentationRestampPageCommit
{
    public RepresentationRestampPageCommit(
        RepresentationRestampPage page,
        ImmutableArray<RepresentationRestampStamp> canonicalStamps,
        int canonicalStampedCount,
        int mirrorStampedCount
    )
    {
        ArgumentNullException.ThrowIfNull(page);
        canonicalStamps = canonicalStamps.IsDefault ? [] : canonicalStamps;
        if (
            canonicalStamps.Any(stamp => stamp is null)
            || canonicalStamps.Length != page.Count
            || canonicalStamps.Select(stamp => stamp.DocumentId).Distinct().Count() != canonicalStamps.Length
            || !canonicalStamps
                .Select(stamp => stamp.DocumentId)
                .Order()
                .SequenceEqual(page.Documents.Select(document => document.DocumentId))
        )
        {
            throw new ArgumentException(
                "Representation restamp page commit stamps must match the selected page exactly.",
                nameof(canonicalStamps)
            );
        }

        if (canonicalStampedCount != page.Count || mirrorStampedCount != page.Count)
        {
            throw new ArgumentException(
                "Representation restamp page commit counts must equal the selected page size."
            );
        }

        Page = page;
        CanonicalStamps = canonicalStamps;
        CanonicalStampedCount = canonicalStampedCount;
        MirrorStampedCount = mirrorStampedCount;
    }

    public RepresentationRestampPage Page { get; }

    public ImmutableArray<RepresentationRestampStamp> CanonicalStamps { get; }

    public int CanonicalStampedCount { get; }

    public int MirrorStampedCount { get; }

    public void RequireSelectedPage(RepresentationRestampPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!ReferenceEquals(Page, page))
        {
            throw new InvalidOperationException(
                "Representation restamp page commit did not originate from the selected page."
            );
        }
    }
}
