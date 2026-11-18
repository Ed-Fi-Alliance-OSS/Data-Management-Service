// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// The one reduced in-place update a root table has: the compiled update without the identity-projection
/// assignments. A root table therefore has exactly two update shapes, this one and the compiled
/// <see cref="TableWritePlan.UpdateSql"/>. The identity guard resolves one instance per root plan and attaches
/// it to a write whose identity is unchanged. The SQL is rendered on first use, because a root whose only
/// non-key bindings are its identity has no reduced SET list, and such a write is always a no-op.
/// </summary>
internal sealed class RelationalWriteReducedRootUpdate
{
    private readonly Lazy<string> _sql;

    private RelationalWriteReducedRootUpdate(
        ImmutableArray<int> omittedBindingIndexes,
        Func<string> renderSql
    )
    {
        OmittedBindingIndexes = omittedBindingIndexes;
        _sql = new Lazy<string>(renderSql);
    }

    /// <summary>
    /// The root plan's identity-projection binding indexes, which the update leaves out of its SET list and
    /// its parameters.
    /// </summary>
    public ImmutableArray<int> OmittedBindingIndexes { get; }

    /// <summary>
    /// The single-row update text, rendered by the emitter that compiles <see cref="TableWritePlan.UpdateSql"/>.
    /// </summary>
    public string Sql => _sql.Value;

    public static RelationalWriteReducedRootUpdate Create(
        TableWritePlan rootTableWritePlan,
        SqlDialect dialect,
        ImmutableArray<int> omittedBindingIndexes
    )
    {
        ArgumentNullException.ThrowIfNull(rootTableWritePlan);

        return new RelationalWriteReducedRootUpdate(
            omittedBindingIndexes,
            () => new WritePlanBatchSqlEmitter(dialect).EmitUpdate(rootTableWritePlan, omittedBindingIndexes)
        );
    }
}
