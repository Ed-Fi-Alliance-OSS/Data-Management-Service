// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Writes the own-natural-key subselect behind
/// <see cref="RelationalWriteTargetLookupSupport.BuildNaturalKeyCaptureTargetPredicate"/>. Aliases are
/// <c>root</c> for the resource's root table, <c>tN</c> for referenced targets and <c>xN</c> for descriptor
/// lookups, so none collides with the capture statement's <c>d</c>. Parameters are plain scalars named
/// <c>@nkN</c>.
/// </summary>
internal sealed class NaturalKeyCapturePredicateBuilder
{
    private const string RootAlias = "root";
    private const string ParameterPrefix = "@nk";

    private static readonly string MssqlIdentityCollation =
        IdentityEqualityContract.SqlServer.IdentityTextCollation
        ?? throw new InvalidOperationException("The SQL Server identity text collation is not defined.");

    private readonly MappingSet _mappingSet;
    private readonly SqlDialect _dialect;
    private readonly IReadOnlyList<DocumentReferenceBinding> _documentReferenceBindings;
    private readonly OwnNaturalKeyProbe _probe;
    private readonly Dictionary<string, string> _valuesByPath;
    private readonly List<RelationalParameter> _parameters = [];

    private int _targetAliasCount;
    private int _descriptorAliasCount;

    public NaturalKeyCapturePredicateBuilder(
        MappingSet mappingSet,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings,
        OwnNaturalKeyProbe probe,
        DocumentIdentity documentIdentity
    )
    {
        _mappingSet = mappingSet;
        _dialect = mappingSet.Key.Dialect;
        _documentReferenceBindings = documentReferenceBindings;
        _probe = probe;
        _valuesByPath = IndexIdentityValues(probe, documentIdentity);

        if (_dialect is not (SqlDialect.Pgsql or SqlDialect.Mssql))
        {
            throw new NotSupportedException(
                $"Relational POST capture predicate does not support SQL dialect '{_dialect}'."
            );
        }
    }

    public IReadOnlyList<RelationalParameter> Parameters => _parameters;

    /// <summary>
    /// Writes <c>(SELECT root.DocumentId FROM &lt;RootTable&gt; root WHERE ...)</c>, or returns
    /// <see langword="false"/> when a request value cannot be stored, so no row can match.
    /// </summary>
    public bool TryBuildRootSubselect([NotNullWhen(true)] out string? subselect)
    {
        subselect = null;
        List<string> predicates = [];
        HashSet<int> writtenReferenceIndexes = [];

        // Key-unified identity paths can share a column. A (column, value) pair is written once; two different
        // values for one column both stay, so the conflicting request matches nothing.
        HashSet<(string Column, string Value)> writtenValues = [];

        foreach (var keyColumn in _probe.KeyColumns)
        {
            string? operand;

            switch (keyColumn.Binding)
            {
                case NaturalKeyProbeKeyBinding.DocumentReference reference:
                    // Every identity part of one reference shares the reference's _DocumentId column.
                    if (!writtenReferenceIndexes.Add(reference.DocumentReferenceBindingIndex))
                    {
                        continue;
                    }

                    operand = TryBuildReferenceSubselect(reference.DocumentReferenceBindingIndex);
                    break;
                default:
                    var rawValue = ValueAt(keyColumn.IdentityJsonPath.Canonical);

                    if (!writtenValues.Add((keyColumn.Column.Value, rawValue)))
                    {
                        continue;
                    }

                    operand = TryBuildValueOperand(keyColumn, rawValue, _probe.Resource);
                    break;
            }

            if (operand is null)
            {
                return false;
            }

            predicates.Add($"{Column(RootAlias, keyColumn.Column.Value)} = {operand}");
        }

        subselect = Subselect(
            Column(RootAlias, _probe.DocumentIdColumn.Value),
            _probe.RootTable,
            RootAlias,
            predicates
        );
        return true;
    }

    public string Column(string alias, string column) => $"{alias}.{Quote(column)}";

    /// <summary>
    /// Maps each key of the referenced target to the reference's identity binding with the same target
    /// identity path, and reads the value at that binding's reference path. One reference path can feed two
    /// key-unified target columns, which is why the mapping goes through the binding, not the path name.
    /// </summary>
    private string? TryBuildReferenceSubselect(int documentReferenceBindingIndex)
    {
        var binding = _documentReferenceBindings[documentReferenceBindingIndex];
        var target = _mappingSet.NaturalKeyProbeTargets.TryGetValue(binding.TargetResource, out var probe)
            ? probe
            : throw new InvalidOperationException(
                $"Identity reference target '{RelationalWriteSupport.FormatResource(binding.TargetResource)}' of "
                    + $"'{RelationalWriteSupport.FormatResource(_probe.Resource)}' has no compiled natural-key probe."
            );
        var alias = "t" + (++_targetAliasCount).ToString(CultureInfo.InvariantCulture);
        List<string> predicates = [];
        HashSet<(string Column, string Value)> writtenValues = [];

        foreach (var keyColumn in target.KeyColumns)
        {
            var identityBinding =
                binding.IdentityBindings.FirstOrDefault(identityBinding =>
                    identityBinding.IdentityJsonPath.Canonical == keyColumn.IdentityJsonPath.Canonical
                )
                ?? throw new InvalidOperationException(
                    $"Reference '{binding.ReferenceObjectPath.Canonical}' of "
                        + $"'{RelationalWriteSupport.FormatResource(_probe.Resource)}' has no identity binding for "
                        + $"target key '{keyColumn.IdentityJsonPath.Canonical}'."
                );
            var rawValue = ValueAt(identityBinding.ReferenceJsonPath.Canonical);

            if (!writtenValues.Add((keyColumn.Column.Value, rawValue)))
            {
                continue;
            }

            var operand = TryBuildValueOperand(keyColumn, rawValue, target.Resource);

            if (operand is null)
            {
                return null;
            }

            predicates.Add($"{Column(alias, keyColumn.Column.Value)} = {operand}");
        }

        return Subselect(Column(alias, target.DocumentIdColumn.Value), target.Table, alias, predicates);
    }

    private string? TryBuildValueOperand(
        NaturalKeyProbeKeyColumn keyColumn,
        string rawValue,
        QualifiedResourceName owner
    ) =>
        keyColumn.Binding switch
        {
            NaturalKeyProbeKeyBinding.Scalar scalar => NaturalKeyValueConverter.ConvertScalar(
                _dialect,
                rawValue,
                scalar.ScalarType
            )
                is { } value
                ? AddParameter(value)
                : null,
            NaturalKeyProbeKeyBinding.Descriptor descriptor => TryBuildDescriptorSubselect(
                descriptor.DescriptorResource,
                rawValue
            ),
            _ => throw new InvalidOperationException(
                $"Natural-key probe for '{RelationalWriteSupport.FormatResource(owner)}' has an unsupported "
                    + $"binding '{keyColumn.Binding.GetType().Name}' on '{keyColumn.IdentityJsonPath.Canonical}'."
            ),
        };

    /// <summary>
    /// Resolves a descriptor URI to its <c>int DescriptorId</c> with the same fold the descriptor's unique
    /// index uses, so the lookup seeks <c>UX_Descriptor_UriLowered_ResourceKeyId</c>.
    /// </summary>
    private string? TryBuildDescriptorSubselect(QualifiedResourceName descriptorResource, string uri)
    {
        if (
            !NaturalKeyValueConverter.FitsDescriptorUri(_dialect, uri)
            || (_dialect == SqlDialect.Pgsql && uri.Contains('\0'))
        )
        {
            return null;
        }

        var descriptorProbe = _mappingSet.DescriptorProbeTarget;
        var alias = "x" + (++_descriptorAliasCount).ToString(CultureInfo.InvariantCulture);
        var foldPredicate = BuildDescriptorUriFoldPredicate(
            _dialect,
            descriptorProbe,
            alias,
            AddParameter(uri)
        );
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(_mappingSet, descriptorResource);

        return Subselect(
            Column(alias, descriptorProbe.DescriptorIdColumn.Value),
            descriptorProbe.DescriptorTable,
            alias,
            [
                foldPredicate,
                $"{Column(alias, descriptorProbe.ResourceKeyIdColumn.Value)} = "
                    + $"CAST({resourceKeyId.ToString(CultureInfo.InvariantCulture)} AS smallint)",
            ]
        );
    }

    private string AddParameter(object value)
    {
        var name = ParameterPrefix + (_parameters.Count + 1).ToString(CultureInfo.InvariantCulture);
        _parameters.Add(new RelationalParameter(name, value));
        return name;
    }

    private string Subselect(
        string selectColumn,
        DbTableName table,
        string alias,
        IReadOnlyList<string> predicates
    ) => $"(SELECT {selectColumn} FROM {QuoteTable(table)} {alias} WHERE {string.Join(" AND ", predicates)})";

    private string ValueAt(string identityJsonPath) =>
        _valuesByPath.TryGetValue(identityJsonPath, out var value)
            ? value
            : throw new InvalidOperationException(
                $"Identity of '{RelationalWriteSupport.FormatResource(_probe.Resource)}' is missing path "
                    + $"'{identityJsonPath}'."
            );

    /// <summary>
    /// Compares the descriptor row aliased <paramref name="alias"/> to a whole-URI parameter, folding the
    /// parameter exactly as <c>UX_Descriptor_UriLowered_ResourceKeyId</c> folds the stored URI: PostgreSQL
    /// lowers both sides under <see cref="DescriptorProbeTarget.PostgresqlUriFoldCollation"/>, matching the
    /// expression index; SQL Server compares the indexed <see cref="DescriptorProbeTarget.MssqlUriLoweredColumn"/>
    /// to the parameter collated under the identity collation inside <c>LOWER</c>. Every descriptor probe uses
    /// this one predicate, so no request can miss a probe yet violate the index.
    /// </summary>
    internal static string BuildDescriptorUriFoldPredicate(
        SqlDialect dialect,
        DescriptorProbeTarget descriptorProbe,
        string alias,
        string parameterName
    )
    {
        string Column(string column) => $"{alias}.{QuoteIdentifier(dialect, column)}";

        return dialect switch
        {
            SqlDialect.Pgsql => $"lower(({Column(descriptorProbe.NamespaceColumn.Value)} || '#' || "
                + $"{Column(descriptorProbe.CodeValueColumn.Value)}) COLLATE {PostgresqlFoldCollation}) "
                + $"= lower({parameterName} COLLATE {PostgresqlFoldCollation})",
            SqlDialect.Mssql => $"{Column(DescriptorProbeTarget.MssqlUriLoweredColumn)} "
                + $"= LOWER({parameterName} COLLATE {MssqlIdentityCollation})",
            _ => throw new NotSupportedException(
                $"Relational descriptor URI probe does not support SQL dialect '{dialect}'."
            ),
        };
    }

    internal static string QuoteIdentifier(SqlDialect dialect, string identifier) =>
        dialect switch
        {
            SqlDialect.Pgsql => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
            SqlDialect.Mssql => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]",
            _ => throw new NotSupportedException($"Relational SQL does not support SQL dialect '{dialect}'."),
        };

    private static string PostgresqlFoldCollation =>
        "\"" + DescriptorProbeTarget.PostgresqlUriFoldCollation + "\"";

    private string Quote(string identifier) => QuoteIdentifier(_dialect, identifier);

    private string QuoteTable(DbTableName table) => $"{Quote(table.Schema.Value)}.{Quote(table.Name)}";

    /// <summary>
    /// Indexes the request identity by path and requires it to carry exactly the own probe's identity paths.
    /// </summary>
    private static Dictionary<string, string> IndexIdentityValues(
        OwnNaturalKeyProbe probe,
        DocumentIdentity documentIdentity
    )
    {
        Dictionary<string, string> valuesByPath = new(StringComparer.Ordinal);

        foreach (var element in documentIdentity.DocumentIdentityElements)
        {
            if (!valuesByPath.TryAdd(element.IdentityJsonPath.Value, element.IdentityValue))
            {
                throw new InvalidOperationException(
                    $"Identity of '{RelationalWriteSupport.FormatResource(probe.Resource)}' repeats path "
                        + $"'{element.IdentityJsonPath.Value}'."
                );
            }
        }

        var probePaths = probe
            .KeyColumns.Select(keyColumn => keyColumn.IdentityJsonPath.Canonical)
            .ToHashSet(StringComparer.Ordinal);

        if (!probePaths.SetEquals(valuesByPath.Keys))
        {
            throw new InvalidOperationException(
                $"Identity of '{RelationalWriteSupport.FormatResource(probe.Resource)}' carries paths "
                    + $"[{string.Join(", ", valuesByPath.Keys.Order(StringComparer.Ordinal))}] but its own "
                    + $"natural-key probe declares [{string.Join(", ", probePaths.Order(StringComparer.Ordinal))}]."
            );
        }

        return valuesByPath;
    }
}
