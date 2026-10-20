// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

public interface IRelationalWriteTargetLookupService
{
    /// <summary>
    /// Resolves a descriptor POST target by descriptor identity through
    /// <see cref="RelationalWriteTargetLookupSupport.ResolveDescriptorForPostAsync"/>.
    /// </summary>
    Task<RelationalWriteTargetLookupResult> ResolveDescriptorForPostAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        string descriptorUri,
        DocumentUuid candidateDocumentUuid,
        CancellationToken cancellationToken = default
    );

    Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    );
}

internal sealed class RelationalWriteTargetLookupService(IRelationalCommandExecutor commandExecutor)
    : IRelationalWriteTargetLookupService
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public Task<RelationalWriteTargetLookupResult> ResolveDescriptorForPostAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        string descriptorUri,
        DocumentUuid candidateDocumentUuid,
        CancellationToken cancellationToken = default
    )
    {
        return RelationalWriteTargetLookupSupport.ResolveDescriptorForPostAsync(
            _commandExecutor,
            mappingSet,
            resource,
            descriptorUri,
            candidateDocumentUuid,
            cancellationToken
        );
    }

    public Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        return RelationalWriteTargetLookupSupport.ResolveForPutAsync(
            _commandExecutor,
            mappingSet,
            resource,
            documentUuid,
            cancellationToken
        );
    }
}

internal static class RelationalWriteTargetLookupSupport
{
    private const string ResourceKeyIdParameterName = "@resourceKeyId";
    private const string DocumentUuidParameterName = "@documentUuid";
    private const string DescriptorUriParameterName = "@descriptorUri";

    private static readonly string MssqlIdentityCollation =
        IdentityEqualityContract.SqlServer.IdentityTextCollation
        ?? throw new InvalidOperationException("The SQL Server identity text collation is not defined.");

    /// <summary>
    /// Builds the POST target predicate over the aliased <c>dms.Document</c> row <c>d</c> from the
    /// resource's own natural key: one scalar <c>=</c> subselect reads the root table's
    /// <c>DocumentId</c> through the <c>UX_&lt;R&gt;_NK</c> columns, filtered to the request's resource key.
    /// Descriptor parts compare the stored <c>int DescriptorId</c> through a lowered-URI descriptor
    /// subselect; each identity reference compares its <c>_DocumentId</c> FK through a subselect of the
    /// referenced root or abstract identity table. Every level is a scalar subselect, so a key that admits
    /// more than one row fails the statement instead of silently picking one. A value no stored row can
    /// hold binds nothing and yields the no-match predicate. Returned as a <see cref="RelationalCommand"/>
    /// whose text is the bare predicate, so the composite rewriter can rename its parameters.
    /// </summary>
    public static RelationalCommand BuildNaturalKeyCaptureTargetPredicate(
        MappingSet mappingSet,
        ResourceWritePlan writePlan,
        DocumentIdentity documentIdentity
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(documentIdentity);

        var resource = writePlan.Model.Resource;
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);
        var builder = new NaturalKeyCapturePredicateBuilder(
            mappingSet,
            writePlan.Model.DocumentReferenceBindings,
            RelationalWriteSupport.GetOwnNaturalKeyProbeOrThrow(mappingSet, resource),
            documentIdentity
        );
        RelationalParameter resourceKeyIdParameter = new(ResourceKeyIdParameterName, resourceKeyId);

        return builder.TryBuildRootSubselect(out var rootSubselect)
            ? new RelationalCommand(
                $"{builder.Column("d", "DocumentId")} = {rootSubselect}"
                    + $" AND {builder.Column("d", "ResourceKeyId")} = {ResourceKeyIdParameterName}",
                [.. builder.Parameters, resourceKeyIdParameter]
            )
            : new RelationalCommand(
                $"{builder.Column("d", "DocumentId")} IS NULL"
                    + $" AND {builder.Column("d", "ResourceKeyId")} = {ResourceKeyIdParameterName}",
                [resourceKeyIdParameter]
            );
    }

    /// <summary>
    /// Builds the target predicate over the aliased <c>dms.Document</c> row <c>d</c> for a composite
    /// captured-target statement addressing a document by its external id, matching
    /// <see cref="ResolveForPutAsync"/>'s document-uuid-and-resource lookup semantics. PUT and DELETE both
    /// address their target this way, so they capture through the same predicate.
    /// </summary>
    public static RelationalCommand BuildDocumentUuidCaptureTargetPredicate(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);
        var predicateSql = mappingSet.Key.Dialect switch
        {
            SqlDialect.Pgsql => """d."DocumentUuid" = @documentUuid AND d."ResourceKeyId" = @resourceKeyId""",
            SqlDialect.Mssql => "d.[DocumentUuid] = @documentUuid AND d.[ResourceKeyId] = @resourceKeyId",
            _ => throw new NotSupportedException(
                $"Relational document-uuid capture predicate does not support SQL dialect '{mappingSet.Key.Dialect}'."
            ),
        };

        return new RelationalCommand(
            predicateSql,
            [
                new RelationalParameter(DocumentUuidParameterName, documentUuid.Value),
                new RelationalParameter(ResourceKeyIdParameterName, resourceKeyId),
            ]
        );
    }

    /// <summary>
    /// Resolves a descriptor POST target by descriptor identity: the lowered whole URI and the descriptor's
    /// own resource key, folded exactly as <c>UX_Descriptor_UriLowered_ResourceKeyId</c> folds them, so the
    /// engine decides which casings (and, on SQL Server, which collation aliases) name the existing
    /// descriptor. The probe projects both <c>DescriptorId</c> and the owning <c>DocumentId</c>; the result
    /// carries only <c>DocumentId</c>, because every lock, update, cache and restamp is document-keyed. More
    /// than one matching row fails the lookup instead of picking one.
    /// </summary>
    public static async Task<RelationalWriteTargetLookupResult> ResolveDescriptorForPostAsync(
        IRelationalCommandExecutor commandExecutor,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        string descriptorUri,
        DocumentUuid candidateDocumentUuid,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(descriptorUri);

        var existingDocument = await ExecuteLookupAsync(
            commandExecutor,
            BuildDescriptorTargetProbeCommand(mappingSet, resource, descriptorUri),
            $"descriptor resource '{RelationalWriteSupport.FormatResource(resource)}' and the request's descriptor URI",
            cancellationToken
        );

        return existingDocument is null
            ? new RelationalWriteTargetLookupResult.CreateNew(candidateDocumentUuid)
            : new RelationalWriteTargetLookupResult.ExistingDocument(
                existingDocument.DocumentId,
                existingDocument.DocumentUuid,
                existingDocument.ObservedContentVersion
            );
    }

    public static async Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
        IRelationalCommandExecutor commandExecutor,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);
        ArgumentNullException.ThrowIfNull(mappingSet);

        var existingDocument = await TryResolveExistingDocumentByDocumentUuidAsync(
            commandExecutor,
            mappingSet,
            resource,
            documentUuid,
            cancellationToken
        );

        return existingDocument is null
            ? new RelationalWriteTargetLookupResult.NotFound()
            : new RelationalWriteTargetLookupResult.ExistingDocument(
                existingDocument.DocumentId,
                existingDocument.DocumentUuid,
                existingDocument.ObservedContentVersion
            );
    }

    private static Task<ResolvedExistingDocument?> TryResolveExistingDocumentByDocumentUuidAsync(
        IRelationalCommandExecutor commandExecutor,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken
    )
    {
        return TryResolveExistingDocumentByDocumentUuidCoreAsync(
            commandExecutor,
            mappingSet,
            resource,
            documentUuid,
            cancellationToken
        );
    }

    private static async Task<ResolvedExistingDocument?> TryResolveExistingDocumentByDocumentUuidCoreAsync(
        IRelationalCommandExecutor commandExecutor,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken
    )
    {
        var resolvedDocument = await RelationalDocumentUuidLookupSupport
            .TryResolveByDocumentUuidAndResourceAsync(
                commandExecutor,
                mappingSet,
                resource,
                documentUuid,
                cancellationToken
            )
            .ConfigureAwait(false);

        return resolvedDocument is null
            ? null
            : new ResolvedExistingDocument(
                resolvedDocument.DocumentId,
                resolvedDocument.DocumentUuid,
                resolvedDocument.ContentVersion
                    ?? throw new InvalidOperationException(
                        $"Relational PUT target lookup for document uuid '{documentUuid.Value}' returned a row without ContentVersion."
                    )
            );
    }

    private static Task<ResolvedExistingDocument?> ExecuteLookupAsync(
        IRelationalCommandExecutor commandExecutor,
        RelationalCommand command,
        string lookupDescription,
        CancellationToken cancellationToken
    )
    {
        return commandExecutor.ExecuteReaderAsync(
            command,
            async (reader, ct) =>
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }

                var resolvedDocument = new ResolvedExistingDocument(
                    reader.GetRequiredFieldValue<long>("DocumentId"),
                    new DocumentUuid(reader.GetRequiredFieldValue<Guid>("DocumentUuid")),
                    reader.GetRequiredFieldValue<long>("ContentVersion")
                );

                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"Relational write target lookup returned multiple rows for {lookupDescription}."
                    );
                }

                return resolvedDocument;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Builds the descriptor identity probe over <c>dms.Descriptor</c> joined to its owning
    /// <c>dms.Document</c>. PostgreSQL folds both sides under <see cref="DescriptorProbeTarget.PostgresqlUriFoldCollation"/>,
    /// matching the expression index; SQL Server compares the indexed
    /// <see cref="DescriptorProbeTarget.MssqlUriLoweredColumn"/> to the parameter collated under the identity
    /// collation inside <c>LOWER</c>. The resource key is a compile-time literal, as in the capture predicate.
    /// </summary>
    private static RelationalCommand BuildDescriptorTargetProbeCommand(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        string descriptorUri
    )
    {
        var dialect = mappingSet.Key.Dialect;
        var probe = mappingSet.DescriptorProbeTarget;
        var resourceKeyId = RelationalWriteSupport
            .GetResourceKeyIdOrThrow(mappingSet, resource)
            .ToString(CultureInfo.InvariantCulture);

        string Quote(string identifier) =>
            dialect switch
            {
                SqlDialect.Pgsql => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
                SqlDialect.Mssql => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]",
                _ => throw new NotSupportedException(
                    $"Relational descriptor POST target probe does not support SQL dialect '{dialect}'."
                ),
            };
        string Column(string alias, DbColumnName column) => $"{alias}.{Quote(column.Value)}";

        var uriPredicate = dialect switch
        {
            SqlDialect.Pgsql =>
                $"lower(({Column("descriptor", probe.NamespaceColumn)} || '#' || {Column("descriptor", probe.CodeValueColumn)}) "
                    + $"COLLATE {Quote(DescriptorProbeTarget.PostgresqlUriFoldCollation)}) "
                    + $"= lower({DescriptorUriParameterName} COLLATE {Quote(DescriptorProbeTarget.PostgresqlUriFoldCollation)})",
            _ => $"descriptor.{Quote(DescriptorProbeTarget.MssqlUriLoweredColumn)} "
                + $"= LOWER({DescriptorUriParameterName} COLLATE {MssqlIdentityCollation})",
        };

        return new RelationalCommand(
            $"""
            SELECT
                {Column("descriptor", probe.DescriptorIdColumn)} AS {Quote("DescriptorId")},
                {Column("descriptor", probe.DocumentIdColumn)} AS {Quote("DocumentId")},
                document.{Quote("DocumentUuid")} AS {Quote("DocumentUuid")},
                document.{Quote("ContentVersion")} AS {Quote("ContentVersion")}
            FROM {Quote(probe.DescriptorTable.Schema.Value)}.{Quote(probe.DescriptorTable.Name)} descriptor
            INNER JOIN {Quote("dms")}.{Quote("Document")} document
                ON document.{Quote("DocumentId")} = {Column("descriptor", probe.DocumentIdColumn)}
            WHERE {uriPredicate}
                AND {Column("descriptor", probe.ResourceKeyIdColumn)} = CAST({resourceKeyId} AS smallint)
            """,
            [new RelationalParameter(DescriptorUriParameterName, descriptorUri)]
        );
    }

    private sealed record ResolvedExistingDocument(
        long DocumentId,
        DocumentUuid DocumentUuid,
        long ObservedContentVersion
    );
}
