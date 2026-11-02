// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// The lookups a request still needs: the statement request, the natural key behind each of its entries, and the
/// keys whose values cannot match any stored row and are therefore never bound.
/// </summary>
/// <param name="LookupRequest">The lookups to execute, or <see langword="null"/> when none can match.</param>
/// <param name="KeysByGroup">The key behind entry <c>[g][e]</c> of <paramref name="LookupRequest"/>.</param>
/// <param name="UnmatchableKeys">Keys resolved as misses without a database lookup.</param>
internal sealed record NaturalKeyLookupBatch(
    NaturalKeyLookupRequest? LookupRequest,
    IReadOnlyList<IReadOnlyList<ReferenceLookupKey>> KeysByGroup,
    IReadOnlyList<ReferenceLookupKey> UnmatchableKeys
)
{
    /// <summary>
    /// The batch of a request with no references, which needs no lookup and has nothing to classify.
    /// </summary>
    public static NaturalKeyLookupBatch Empty { get; } = new(null, [], []);
}

/// <summary>
/// Request-scoped resolver that finds references by natural key. Every lookup, including a miss, is memoized
/// under its structural <see cref="ReferenceLookupKey"/>, so a key is looked up at most once per request no matter
/// how many identity arrays carry it.
/// </summary>
/// <remarks>
/// Registered per request with the request's command executor, which serves
/// <see cref="IReferenceResolver.ResolveAsync"/>. A write attempt creates its own instance without one: its
/// lookup either rides the composite command or runs on the write session's executor.
/// </remarks>
internal sealed class NaturalKeyReferenceResolver(
    INaturalKeyLookupCommandBuilder commandBuilder,
    IResolvedDocumentReferenceMapFactory mapFactory,
    IRelationalCommandExecutor? executor = null
) : IReferenceResolver
{
    private readonly INaturalKeyLookupCommandBuilder _commandBuilder =
        commandBuilder ?? throw new ArgumentNullException(nameof(commandBuilder));
    private readonly IResolvedDocumentReferenceMapFactory _mapFactory =
        mapFactory ?? throw new ArgumentNullException(nameof(mapFactory));
    private readonly Dictionary<ReferenceLookupKey, NaturalKeyLookupMatch?> _matches = new(
        ReferenceLookupKeyComparer.Instance
    );

    /// <summary>
    /// Builds the lookups <paramref name="request"/> still needs, or returns <see langword="null"/> when every
    /// reference is already memoized. A composite caller embeds <see cref="NaturalKeyLookupBatch.LookupRequest"/>
    /// and passes the decoded rows to <see cref="ResolveFromRows"/>.
    /// </summary>
    public NaturalKeyLookupBatch? TryBuildLookupRequest(ReferenceResolverRequest request)
    {
        ValidateRequest(request);

        var mappingSet = request.MappingSet;
        Dictionary<QualifiedResourceName, GroupBuilder> groupsByTarget = [];
        List<GroupBuilder> groups = [];
        HashSet<ReferenceLookupKey> seen = new(ReferenceLookupKeyComparer.Instance);
        List<ReferenceLookupKey> unmatchable = [];

        foreach (var reference in request.DocumentReferences)
        {
            var target = RelationalWriteSupport.ToQualifiedResourceName(reference.ResourceInfo);
            var key = new ReferenceLookupKey(target, reference.DocumentIdentity);

            if (_matches.ContainsKey(key) || !seen.Add(key))
            {
                continue;
            }

            var probe = mappingSet.NaturalKeyProbeTargets.TryGetValue(target, out var compiledProbe)
                ? compiledProbe
                : throw new InvalidOperationException(
                    $"Document reference target '{RelationalWriteSupport.FormatResource(target)}' has no compiled "
                        + "natural-key probe."
                );
            var keyValues = ConvertKeyValues(mappingSet.Key.Dialect, probe, reference.DocumentIdentity);

            if (keyValues is null)
            {
                unmatchable.Add(key);
                continue;
            }

            GetGroup(
                    groupsByTarget,
                    groups,
                    target,
                    () =>
                        NaturalKeyLookupTarget.ForProbe(
                            probe,
                            RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, target)
                        )
                )
                .Add(key, keyValues);
        }

        foreach (var reference in request.DescriptorReferences)
        {
            var target = RelationalWriteSupport.ToQualifiedResourceName(reference.ResourceInfo);
            var key = new ReferenceLookupKey(target, reference.DocumentIdentity);

            if (_matches.ContainsKey(key) || !seen.Add(key))
            {
                continue;
            }

            var uri = DescriptorUri(reference);

            if (!NaturalKeyValueConverter.FitsDescriptorUri(mappingSet.Key.Dialect, uri))
            {
                unmatchable.Add(key);
                continue;
            }

            GetGroup(
                    groupsByTarget,
                    groups,
                    target,
                    () =>
                        NaturalKeyLookupTarget.ForDescriptor(
                            target,
                            RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, target)
                        )
                )
                .Add(key, [uri]);
        }

        if (groups.Count == 0 && unmatchable.Count == 0)
        {
            return null;
        }

        return new NaturalKeyLookupBatch(
            groups.Count == 0
                ? null
                : new NaturalKeyLookupRequest(
                    mappingSet,
                    [.. groups.Select((group, index) => group.Build(index + 1))]
                ),
            [.. groups.Select(group => group.Keys)],
            unmatchable
        );
    }

    /// <summary>
    /// Resolves <paramref name="request"/> on the request's executor.
    /// </summary>
    Task<ResolvedReferenceSet> IReferenceResolver.ResolveAsync(
        ReferenceResolverRequest request,
        CancellationToken cancellationToken
    ) =>
        ResolveAsync(
            request,
            executor
                ?? throw new InvalidOperationException(
                    "This natural-key resolver has no command executor; pass one to the explicit-executor overload."
                ),
            cancellationToken
        );

    /// <summary>
    /// Resolves <paramref name="request"/> with the standalone multi-statement command, sending no command when
    /// every reference is memoized or unmatchable.
    /// </summary>
    public async Task<ResolvedReferenceSet> ResolveAsync(
        ReferenceResolverRequest request,
        IRelationalCommandExecutor executor,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(executor);

        var batch = TryBuildLookupRequest(request);

        if (batch is not null)
        {
            var matches = batch.LookupRequest is { } lookupRequest
                ? await executor
                    .ExecuteReaderAsync(
                        _commandBuilder.BuildCommand(lookupRequest),
                        (reader, readCancellation) =>
                            NaturalKeyLookupResultReader.ReadAllResultSetsAsync(
                                reader,
                                lookupRequest,
                                readCancellation
                            ),
                        cancellationToken
                    )
                    .ConfigureAwait(false)
                : [];

            Memoize(batch, matches);
        }

        return Classify(request);
    }

    /// <summary>
    /// Classifies <paramref name="request"/> from the decoded rows of an embedded lookup built by
    /// <see cref="TryBuildLookupRequest"/>.
    /// </summary>
    public ResolvedReferenceSet ResolveFromRows(
        ReferenceResolverRequest request,
        NaturalKeyLookupBatch batch,
        NaturalKeyLookupMatch?[][] matches
    )
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(matches);
        ValidateRequest(request);

        Memoize(batch, matches);

        return Classify(request);
    }

    private void Memoize(NaturalKeyLookupBatch batch, NaturalKeyLookupMatch?[][] matches)
    {
        if (matches.Length != batch.KeysByGroup.Count)
        {
            throw new ArgumentException(
                $"The match table has {matches.Length} groups but the lookup batch has {batch.KeysByGroup.Count}.",
                nameof(matches)
            );
        }

        for (var groupIndex = 0; groupIndex < matches.Length; groupIndex++)
        {
            var keys = batch.KeysByGroup[groupIndex];

            if (matches[groupIndex].Length != keys.Count)
            {
                throw new ArgumentException(
                    $"Match group {groupIndex + 1} has {matches[groupIndex].Length} entries but the lookup batch "
                        + $"has {keys.Count}.",
                    nameof(matches)
                );
            }

            for (var entryIndex = 0; entryIndex < keys.Count; entryIndex++)
            {
                _matches[keys[entryIndex]] = matches[groupIndex][entryIndex];
            }
        }

        foreach (var key in batch.UnmatchableKeys)
        {
            _matches[key] = null;
        }
    }

    private ResolvedReferenceSet Classify(ReferenceResolverRequest request)
    {
        Dictionary<JsonPath, ResolvedDocumentReference> documentsByPath = [];
        List<DocumentReferenceFailure> documentFailures = [];

        foreach (var reference in request.DocumentReferences)
        {
            var match = GetMemoizedMatch(
                new ReferenceLookupKey(
                    RelationalWriteSupport.ToQualifiedResourceName(reference.ResourceInfo),
                    reference.DocumentIdentity
                )
            );

            if (match is null)
            {
                documentFailures.Add(
                    DocumentReferenceFailure.From(reference, DocumentReferenceFailureReason.Missing)
                );
                continue;
            }

            if (
                !request
                    .MappingSet.GetDocumentReferenceTargetMetadataOrThrow(reference.ResourceInfo)
                    .AllowsResourceKeyId(match.ResourceKeyId)
            )
            {
                documentFailures.Add(
                    DocumentReferenceFailure.From(
                        reference,
                        DocumentReferenceFailureReason.IncompatibleTargetType
                    )
                );
                continue;
            }

            if (
                !documentsByPath.TryAdd(reference.Path, new(reference, match.DocumentId, match.ResourceKeyId))
            )
            {
                throw new InvalidOperationException(
                    $"Document reference path '{reference.Path.Value}' was extracted more than once within the same request."
                );
            }
        }

        Dictionary<JsonPath, ResolvedDescriptorReference> descriptorsByPath = [];
        List<DescriptorReferenceFailure> descriptorFailures = [];

        foreach (var reference in request.DescriptorReferences)
        {
            var match = GetMemoizedMatch(
                new ReferenceLookupKey(
                    RelationalWriteSupport.ToQualifiedResourceName(reference.ResourceInfo),
                    reference.DocumentIdentity
                )
            );

            // The descriptor type is filtered in SQL, so a URI stored under another type is a miss.
            if (match is null)
            {
                descriptorFailures.Add(
                    DescriptorReferenceFailure.From(reference, DescriptorReferenceFailureReason.Missing)
                );
                continue;
            }

            var descriptorId =
                match.DescriptorId
                ?? throw new InvalidOperationException(
                    $"Descriptor reference at path '{reference.Path.Value}' resolved without a compact descriptor id."
                );

            if (
                !descriptorsByPath.TryAdd(
                    reference.Path,
                    new(reference, descriptorId, match.DocumentId, match.ResourceKeyId)
                )
            )
            {
                throw new InvalidOperationException(
                    $"Descriptor reference path '{reference.Path.Value}' was extracted more than once within the same request."
                );
            }
        }

        return new ResolvedReferenceSet(
            documentsByPath,
            descriptorsByPath,
            _mapFactory.Create(documentsByPath.Values),
            documentFailures,
            descriptorFailures
        );
    }

    private NaturalKeyLookupMatch? GetMemoizedMatch(ReferenceLookupKey key) =>
        _matches.TryGetValue(key, out var match)
            ? match
            : throw new InvalidOperationException(
                $"Natural-key resolver has no lookup for a reference to '{RelationalWriteSupport.FormatResource(key.TargetResource)}'; "
                    + "the rows were not decoded from this request's lookup batch."
            );

    /// <summary>
    /// Converts the reference's identity values into the probe's typed key values, matched by identity path.
    /// Returns <see langword="null"/> when a value cannot be stored in its key column, so no row can match it.
    /// </summary>
    private static object[]? ConvertKeyValues(
        SqlDialect dialect,
        NaturalKeyProbeTarget probe,
        DocumentIdentity identity
    )
    {
        Dictionary<string, string> valuesByPath = new(StringComparer.Ordinal);

        foreach (var element in identity.DocumentIdentityElements)
        {
            if (!valuesByPath.TryAdd(element.IdentityJsonPath.Value, element.IdentityValue))
            {
                throw new InvalidOperationException(
                    $"Reference to '{RelationalWriteSupport.FormatResource(probe.Resource)}' repeats identity path "
                        + $"'{element.IdentityJsonPath.Value}'."
                );
            }
        }

        if (valuesByPath.Count != probe.KeyColumns.Count)
        {
            throw new InvalidOperationException(
                $"Reference to '{RelationalWriteSupport.FormatResource(probe.Resource)}' carries identity paths "
                    + $"[{string.Join(", ", valuesByPath.Keys.Order(StringComparer.Ordinal))}] but its natural-key probe "
                    + $"declares [{string.Join(", ", probe.KeyColumns.Select(keyColumn => keyColumn.IdentityJsonPath.Canonical))}]."
            );
        }

        var keyValues = new object[probe.KeyColumns.Count];

        for (var index = 0; index < probe.KeyColumns.Count; index++)
        {
            var keyColumn = probe.KeyColumns[index];

            if (!valuesByPath.TryGetValue(keyColumn.IdentityJsonPath.Canonical, out var rawValue))
            {
                throw new InvalidOperationException(
                    $"Reference to '{RelationalWriteSupport.FormatResource(probe.Resource)}' is missing identity path "
                        + $"'{keyColumn.IdentityJsonPath.Canonical}'."
                );
            }

            object? value = keyColumn.Binding switch
            {
                NaturalKeyProbeKeyBinding.Descriptor => NaturalKeyValueConverter.FitsDescriptorUri(
                    dialect,
                    rawValue
                )
                    ? rawValue
                    : null,
                NaturalKeyProbeKeyBinding.Scalar scalar => NaturalKeyValueConverter.ConvertScalar(
                    dialect,
                    rawValue,
                    scalar.ScalarType
                ),
                _ => throw new InvalidOperationException(
                    $"Natural-key probe for '{RelationalWriteSupport.FormatResource(probe.Resource)}' has an "
                        + $"unsupported binding on '{keyColumn.IdentityJsonPath.Canonical}'."
                ),
            };

            if (value is null)
            {
                return null;
            }

            keyValues[index] = value;
        }

        return keyValues;
    }

    private static string DescriptorUri(DescriptorReference reference) =>
        reference.DocumentIdentity.DocumentIdentityElements is [var element]
            ? element.IdentityValue
            : throw new InvalidOperationException(
                $"Descriptor reference at path '{reference.Path.Value}' must carry exactly one identity value."
            );

    private static GroupBuilder GetGroup(
        Dictionary<QualifiedResourceName, GroupBuilder> groupsByTarget,
        List<GroupBuilder> groups,
        QualifiedResourceName target,
        Func<NaturalKeyLookupTarget> createTarget
    )
    {
        if (!groupsByTarget.TryGetValue(target, out var group))
        {
            group = new GroupBuilder(createTarget());
            groupsByTarget.Add(target, group);
            groups.Add(group);
        }

        return group;
    }

    private static void ValidateRequest(ReferenceResolverRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.MappingSet);
        ArgumentNullException.ThrowIfNull(request.DocumentReferences);
        ArgumentNullException.ThrowIfNull(request.DescriptorReferences);
    }

    private sealed class GroupBuilder(NaturalKeyLookupTarget target)
    {
        private readonly List<NaturalKeyLookupEntry> _entries = [];

        public List<ReferenceLookupKey> Keys { get; } = [];

        public void Add(ReferenceLookupKey key, IReadOnlyList<object> keyValues)
        {
            Keys.Add(key);
            _entries.Add(new NaturalKeyLookupEntry(keyValues));
        }

        public NaturalKeyLookupGroup Build(int groupOrdinal) => new(groupOrdinal, target, _entries);
    }
}
