// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Outcome of reading the proposed namespace value out of the finalized merged root row.
/// </summary>
internal abstract record ProposedNamespaceValueExtractionResult
{
    private ProposedNamespaceValueExtractionResult() { }

    /// <summary>
    /// The proposed namespace value was extracted. <paramref name="ProposedNamespace"/> is
    /// <see langword="null"/> when the finalized value is null/empty, which the SQL maps to a
    /// proposed-namespace-missing failure.
    /// </summary>
    public sealed record Ready(string? ProposedNamespace) : ProposedNamespaceValueExtractionResult;

    /// <summary>
    /// The planned namespace checks could not be reconciled with the finalized root row. The write
    /// fails closed as a security-configuration failure, matching the read-path namespace
    /// security-configuration mapping and the proposed relationship sibling.
    /// </summary>
    public sealed record InvalidAuthorizationPlan(string FailureMessage)
        : ProposedNamespaceValueExtractionResult;
}

/// <summary>
/// Reads the proposed namespace value from the finalized merged root row buffer, using the root
/// table's <see cref="EdFi.DataManagementService.Backend.External.Plans.TableWritePlan.ColumnBindings"/>
/// to locate the namespace column. The only exception is a namespace derived from a reference whose
/// missing-document failure was deferred: the finalized row has no value for an unresolved reference, so
/// the value the request body carries is authorized instead.
/// </summary>
internal static class ProposedNamespaceValueExtractor
{
    public static ProposedNamespaceValueExtractionResult Extract(
        IReadOnlyList<NamespaceAuthorizationCheckSpec> checks,
        RootWriteRowBuffer rootRow,
        JsonNode requestBody,
        IReadOnlySet<string> deferredMissingDocumentReferencePaths
    )
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(rootRow);

        if (checks.Count == 0)
        {
            return Invalid("Proposed namespace authorization requires at least one check spec.");
        }

        var rootTable = rootRow.TableWritePlan.TableModel.Table;
        var namespaceColumn = checks[0].NamespaceColumn;

        foreach (var check in checks)
        {
            if (check.ValueSource is not NamespaceAuthorizationCheckValueSource.Proposed)
            {
                return Invalid(
                    $"Proposed namespace authorization cannot extract check '{check.Index}' because it uses value source '{check.ValueSource}'."
                );
            }

            if (!check.RootTable.Equals(rootTable))
            {
                return Invalid(
                    $"Proposed namespace authorization check '{check.Index}' targets root table '{check.RootTable}', but the finalized root row is for '{rootTable}'."
                );
            }

            if (!check.NamespaceColumn.Equals(namespaceColumn))
            {
                return Invalid(
                    $"Proposed namespace authorization checks must reference a single namespace column. "
                        + $"Found '{check.NamespaceColumn.Value}' and '{namespaceColumn.Value}'."
                );
            }
        }

        var bindings = rootRow.TableWritePlan.ColumnBindings;
        var bindingIndex = -1;

        for (var index = 0; index < bindings.Length; index++)
        {
            if (bindings[index].Column.ColumnName.Equals(namespaceColumn))
            {
                bindingIndex = index;
                break;
            }
        }

        if (bindingIndex < 0)
        {
            return Invalid(
                $"Proposed namespace authorization could not locate a root binding for namespace column '{namespaceColumn.Value}'."
            );
        }

        var rootBinding = bindings[bindingIndex];
        var boundValue = GetBoundSqlValue(rootRow.Values[bindingIndex]);

        if (
            deferredMissingDocumentReferencePaths.Count > 0
            && TryFindDeferredBodyNamespacePath(
                rootRow.TableWritePlan,
                rootBinding,
                bindingIndex,
                boundValue,
                deferredMissingDocumentReferencePaths,
                out var bodyNamespacePath
            )
        )
        {
            RelationalWriteFlattener.TryGetRelativeLeafNode(
                requestBody,
                bodyNamespacePath,
                out var bodyNamespaceNode
            );

            return ConvertBodyNamespace(bodyNamespaceNode, bodyNamespacePath, namespaceColumn);
        }

        return boundValue switch
        {
            null => new ProposedNamespaceValueExtractionResult.Ready(null),
            string proposedNamespace => new ProposedNamespaceValueExtractionResult.Ready(
                string.IsNullOrEmpty(proposedNamespace) ? null : proposedNamespace
            ),
            _ => Invalid(
                $"Proposed namespace authorization expected a string namespace value for column "
                    + $"'{namespaceColumn.Value}' but found '{boundValue.GetType().Name}'."
            ),
        };
    }

    /// <summary>
    /// Locates the request-body path to authorize when the namespace is derived from a reference whose
    /// missing-document failure was deferred, so the finalized row carries no value for it. Returns
    /// <see langword="false"/> for every other case, which keeps the finalized-row value.
    /// </summary>
    private static bool TryFindDeferredBodyNamespacePath(
        TableWritePlan rootPlan,
        WriteColumnBinding rootBinding,
        int bindingIndex,
        object? boundValue,
        IReadOnlySet<string> deferredPaths,
        out JsonPathExpression bodyPath
    )
    {
        bodyPath = default;

        switch (rootBinding.Source)
        {
            case WriteValueSource.ReferenceDerived referenceDerived
                when deferredPaths.Contains(referenceDerived.ReferenceSource.ReferenceObjectPath.Canonical):
                bodyPath =
                    rootBinding.Column.SourceJsonPath ?? referenceDerived.ReferenceSource.ReferenceJsonPath;
                return true;

            case WriteValueSource.Precomputed when boundValue is null:
                // A null key-unified canonical means no member resolved. Take the first deferred
                // reference member in the same precedence order the flattener evaluates.
                foreach (var plan in rootPlan.KeyUnificationPlans)
                {
                    if (plan.CanonicalBindingIndex != bindingIndex)
                    {
                        continue;
                    }

                    foreach (var member in plan.MembersInOrder)
                    {
                        if (
                            member is KeyUnificationMemberWritePlan.ReferenceDerivedMember referenceMember
                            && deferredPaths.Contains(
                                referenceMember.ReferenceSource.ReferenceObjectPath.Canonical
                            )
                        )
                        {
                            bodyPath = referenceMember.RelativePath;
                            return true;
                        }
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static ProposedNamespaceValueExtractionResult ConvertBodyNamespace(
        JsonNode? bodyNode,
        JsonPathExpression bodyPath,
        DbColumnName namespaceColumn
    )
    {
        if (bodyNode is null)
        {
            return new ProposedNamespaceValueExtractionResult.Ready(null);
        }

        if (bodyNode is not JsonValue jsonValue || jsonValue.GetValueKind() is not JsonValueKind.String)
        {
            return Invalid(
                $"Proposed namespace authorization expected a string namespace value for column "
                    + $"'{namespaceColumn.Value}' but found JSON kind '{bodyNode.GetValueKind()}' at '{bodyPath.Canonical}'."
            );
        }

        var proposedNamespace = (string)
            RelationalWriteFlattener.ConvertRequestDerivedJsonValue(
                jsonValue,
                new RelationalScalarType(ScalarKind.String),
                new RelationalWriteFlattener.RequestDerivedScalarConversionContext(
                    bodyPath.Canonical,
                    $"Namespace column '{namespaceColumn.Value}'"
                )
            );

        return new ProposedNamespaceValueExtractionResult.Ready(
            string.IsNullOrEmpty(proposedNamespace) ? null : proposedNamespace
        );
    }

    private static object? GetBoundSqlValue(FlattenedWriteValue value)
    {
        if (value is FlattenedWriteValue.Literal { Value: { } literalValue } && literalValue is not DBNull)
        {
            return literalValue;
        }

        return null;
    }

    private static ProposedNamespaceValueExtractionResult.InvalidAuthorizationPlan Invalid(string message) =>
        new(message);
}
