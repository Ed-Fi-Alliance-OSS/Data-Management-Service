// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel;
using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Outcome of planning the namespace-authorization portion of a request.
/// </summary>
public abstract record NamespaceAuthorizationPlanOutcome
{
    private NamespaceAuthorizationPlanOutcome() { }

    /// <summary>One or more namespace checks the SQL layer must execute, in emission order.</summary>
    public sealed record Plan(IReadOnlyList<NamespaceAuthorizationCheckSpec> Checks)
        : NamespaceAuthorizationPlanOutcome;

    /// <summary>
    /// The resource is configured with <c>NamespaceBased</c> but no securable element resolves to
    /// the resource's concrete root-table column. Maps to a 500 Security Configuration Error.
    /// </summary>
    public sealed record NoUsableRootColumn(QualifiedResourceName Resource)
        : NamespaceAuthorizationPlanOutcome;

    /// <summary>
    /// The client has no namespace prefixes assigned. Maps to a 403 <c>§2.9</c> ProblemDetails at
    /// planner/preflight time — no DB roundtrip is issued.
    /// </summary>
    public sealed record NoPrefixesConfigured(string StrategyName) : NamespaceAuthorizationPlanOutcome;
}

/// <summary>
/// Plans namespace-authorization checks for a single CRUD operation. Filters out securable element
/// resolutions that land on child-collection tables and operates only on the resource's concrete
/// root-table column.
/// </summary>
/// <remarks>
/// Outcome precedence (within the namespace planner):
/// <list type="number">
/// <item>No resolved Namespace securable element targets the root table → <see cref="NamespaceAuthorizationPlanOutcome.NoUsableRootColumn"/>.</item>
/// <item>The client has no namespace prefixes assigned → <see cref="NamespaceAuthorizationPlanOutcome.NoPrefixesConfigured"/>.</item>
/// <item>Otherwise → <see cref="NamespaceAuthorizationPlanOutcome.Plan"/> with one or two checks driven by <paramref name="operation"/>.</item>
/// </list>
/// Filtering compares the resolved <c>(table)</c> against the resource's root table — it does not
/// rely on JSON path string heuristics.
/// </remarks>
public static class NamespaceAuthorizationPlanner
{
    public static NamespaceAuthorizationPlanOutcome Plan(
        ConcreteResourceModel resource,
        NamespaceAuthorizationOperation operation,
        RelationalAuthorizationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);

        var rootColumn = ResolveSingleRootTableNamespaceColumn(resource);

        if (rootColumn is not { } namespaceColumn)
        {
            return new NamespaceAuthorizationPlanOutcome.NoUsableRootColumn(
                resource.RelationalModel.Resource
            );
        }

        if (context.NamespacePrefixes.Count == 0)
        {
            return new NamespaceAuthorizationPlanOutcome.NoPrefixesConfigured(
                AuthorizationStrategyNameConstants.NamespaceBased
            );
        }

        var rootTable = resource.RelationalModel.Root.Table;
        return new NamespaceAuthorizationPlanOutcome.Plan(BuildChecks(operation, rootTable, namespaceColumn));
    }

    /// <summary>
    /// Iterates the resource's Namespace securable element paths, resolves each via
    /// <see cref="SecurableElementLocationResolver.ResolvePreferred"/>, and returns the first
    /// column whose resolved source table equals the resource's concrete root table. Unresolved
    /// or child-collection paths are skipped — neither category aborts namespace planning, and
    /// unrelated securable element metadata (EdOrg, Student, Contact, Staff) is never touched.
    /// Returns <see langword="null"/> when no root-table Namespace column survives.
    /// </summary>
    private static DbColumnName? ResolveSingleRootTableNamespaceColumn(ConcreteResourceModel resource)
    {
        var rootTable = resource.RelationalModel.Root.Table;

        // Descriptor resources persist their Namespace on the shared dms.Descriptor root table and carry
        // empty securable-element metadata. They are root tables by definition, so the namespace column
        // comes directly from the descriptor column contract. A SharedDescriptorTable resource with no
        // descriptor metadata is impossible metadata: fall through so the planner fails closed with
        // NoUsableRootColumn rather than authorizing against an unknown column.
        if (
            resource.StorageKind == ResourceStorageKind.SharedDescriptorTable
            && resource.DescriptorMetadata is { } descriptorMetadata
        )
        {
            return descriptorMetadata.ColumnContract.Namespace;
        }

        foreach (var namespacePath in resource.SecurableElements.Namespace)
        {
            var step = SecurableElementLocationResolver.ResolvePreferred(resource, namespacePath);

            if (step is null)
            {
                continue;
            }

            if (step.SourceTable == rootTable)
            {
                return step.SourceColumnName;
            }
        }

        return null;
    }

    private static IReadOnlyList<NamespaceAuthorizationCheckSpec> BuildChecks(
        NamespaceAuthorizationOperation operation,
        DbTableName rootTable,
        DbColumnName namespaceColumn
    ) =>
        operation switch
        {
            NamespaceAuthorizationOperation.ReadSingle
            or NamespaceAuthorizationOperation.ReadMany
            or NamespaceAuthorizationOperation.Delete =>
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    NamespaceAuthorizationCheckValueSource.Stored,
                    rootTable,
                    namespaceColumn
                ),
            ],
            NamespaceAuthorizationOperation.Update =>
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    NamespaceAuthorizationCheckValueSource.Stored,
                    rootTable,
                    namespaceColumn
                ),
                new NamespaceAuthorizationCheckSpec(
                    1,
                    NamespaceAuthorizationCheckValueSource.Proposed,
                    rootTable,
                    namespaceColumn
                ),
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Unsupported namespace authorization operation."
            ),
        };
}
