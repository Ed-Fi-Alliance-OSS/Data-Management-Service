// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Shared member-evaluation helpers used by the flattener and the post-overlay
/// key-unification resolver. Extracted without behavior change from
/// <see cref="RelationalWriteFlattener"/>.
/// </summary>
internal static class FlattenerMemberEvaluation
{
    internal static KeyUnificationMemberEvaluation EvaluateKeyUnificationMember(
        TableWritePlan tableWritePlan,
        KeyUnificationMemberWritePlan member,
        JsonNode scopeNode,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath
    )
    {
        var absolutePath = RelationalJsonPathSupport.CombineRestrictedCanonical(
            tableWritePlan.TableModel.JsonScope,
            member.RelativePath
        );

        return member switch
        {
            KeyUnificationMemberWritePlan.ScalarMember scalarMember => EvaluateScalarKeyUnificationMember(
                tableWritePlan,
                scalarMember,
                absolutePath,
                scopeNode
            ),
            KeyUnificationMemberWritePlan.DescriptorMember descriptorMember =>
                EvaluateDescriptorKeyUnificationMember(
                    tableWritePlan,
                    descriptorMember,
                    absolutePath,
                    scopeNode,
                    resolvedReferenceLookups,
                    ordinalPath
                ),
            KeyUnificationMemberWritePlan.ReferenceDerivedMember referenceDerivedMember =>
                EvaluateReferenceDerivedKeyUnificationMember(
                    tableWritePlan,
                    referenceDerivedMember,
                    absolutePath,
                    scopeNode,
                    resolvedReferenceLookups,
                    ordinalPath
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported key-unification member kind '{member.GetType().Name}' on table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}'."
            ),
        };
    }

    private static KeyUnificationMemberEvaluation EvaluateScalarKeyUnificationMember(
        TableWritePlan tableWritePlan,
        KeyUnificationMemberWritePlan.ScalarMember member,
        string absolutePath,
        JsonNode scopeNode
    )
    {
        if (
            !RelationalWriteFlattener.TryGetRelativeLeafNode(
                scopeNode,
                member.RelativePath,
                out var memberNode
            ) || memberNode is null
        )
        {
            return KeyUnificationMemberEvaluation.Absent;
        }

        var conversionContext = RelationalWriteFlattener.CreateKeyUnificationScalarConversionContext(
            tableWritePlan,
            member,
            absolutePath
        );

        if (memberNode is not JsonValue jsonValue)
        {
            throw RelationalWriteFlattener.CreateInvalidRequestDerivedScalarException(
                conversionContext,
                member.ScalarType,
                $"encountered non-scalar JSON node type '{memberNode.GetType().Name}'"
            );
        }

        return KeyUnificationMemberEvaluation.Present(
            RelationalWriteFlattener.ConvertRequestDerivedJsonValue(
                jsonValue,
                member.ScalarType,
                conversionContext
            )
        );
    }

    private static KeyUnificationMemberEvaluation EvaluateDescriptorKeyUnificationMember(
        TableWritePlan tableWritePlan,
        KeyUnificationMemberWritePlan.DescriptorMember member,
        string absolutePath,
        JsonNode scopeNode,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath
    )
    {
        if (
            !RelationalWriteFlattener.TryGetRelativeLeafNode(
                scopeNode,
                member.RelativePath,
                out var memberNode
            ) || memberNode is null
        )
        {
            return KeyUnificationMemberEvaluation.Absent;
        }

        if (memberNode is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out _))
        {
            throw RelationalWriteFlattener.CreateRequestShapeValidationException(
                absolutePath,
                $"Key-unification member '{member.MemberPathColumn.Value}' on table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' "
                    + $"expected a descriptor URI string at path '{absolutePath}', but encountered "
                    + $"JSON value kind '{RelationalWriteFlattener.GetJsonValueKind(memberNode)}'."
            );
        }

        var descriptorId = resolvedReferenceLookups.GetDescriptorId(
            member.DescriptorResource,
            absolutePath,
            ordinalPath
        );

        if (descriptorId is null)
        {
            throw new InvalidOperationException(
                $"Key-unification member '{member.MemberPathColumn.Value}' on table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' "
                    + $"did not have a resolved descriptor id for path '{absolutePath}' and descriptor resource "
                    + $"'{RelationalWriteSupport.FormatResource(member.DescriptorResource)}'."
            );
        }

        return KeyUnificationMemberEvaluation.Present(descriptorId.Value);
    }

    private static KeyUnificationMemberEvaluation EvaluateReferenceDerivedKeyUnificationMember(
        TableWritePlan tableWritePlan,
        KeyUnificationMemberWritePlan.ReferenceDerivedMember member,
        string absolutePath,
        JsonNode scopeNode,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath
    )
    {
        if (
            !RelationalWriteFlattener.TryGetReferenceObjectNode(
                tableWritePlan,
                scopeNode,
                member.ReferenceSource,
                out var referenceNode
            ) || referenceNode is null
        )
        {
            return KeyUnificationMemberEvaluation.Absent;
        }

        var memberPathColumn = RelationalWriteFlattener.GetRequiredColumnModel(
            tableWritePlan,
            member.MemberPathColumn
        );
        var concreteAbsolutePath = RelationalWriteFlattener.MaterializeValidationPath(
            absolutePath,
            ordinalPath
        );

        return KeyUnificationMemberEvaluation.Present(
            RelationalWriteFlattener.ResolveReferenceDerivedLiteralValue(
                tableWritePlan,
                memberPathColumn,
                member.MemberPathColumn,
                member.ReferenceSource,
                concreteAbsolutePath,
                resolvedReferenceLookups,
                ordinalPath
            )
        );
    }
}

internal sealed record KeyUnificationMemberEvaluation(bool IsPresent, object? Value)
{
    public static KeyUnificationMemberEvaluation Absent { get; } = new(false, null);

    public static KeyUnificationMemberEvaluation Present(object value)
    {
        return new(true, value);
    }
}
