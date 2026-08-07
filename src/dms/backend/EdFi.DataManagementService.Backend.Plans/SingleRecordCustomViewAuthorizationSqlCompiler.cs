// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles co-batched single-record custom view-based authorization SQL. Each check that can be decided in
/// SQL becomes one <c>SELECT CASE</c> statement; the first failure raises AUTH1 with payload
/// <c>cv1|&lt;index&gt;|&lt;kind&gt;</c> and aborts the batch.
/// </summary>
/// <remarks>
/// <para>
/// Membership is always tested as <c>&lt;value&gt; IN (SELECT cv.DocumentId FROM auth.{StrategyName} cv)</c>
/// rather than a join, per auth.md §"Sub-queries instead of joins": with no <c>DISTINCT</c> the plans stay
/// simpler, and a single-record check cannot produce duplicate rows anyway.
/// </para>
/// <para>
/// Branch order is the contract. A stored check answers authorized, then uninitialized, then stale target,
/// then mismatch; a proposed check answers missing, then authorized, then mismatch. Each branch maps to one
/// ProblemDetails category, so reordering them would silently change which error a caller sees.
/// </para>
/// <para>
/// Unlike the GET-many compiler, a transitive path is walked by correlating joins to the addressed row rather
/// than by an uncorrelated subquery that re-scans the root table. A single-record check already binds its
/// target, so there is nothing to re-scan.
/// </para>
/// </remarks>
public sealed class SingleRecordCustomViewAuthorizationSqlCompiler(SqlDialect dialect)
{
    private const string RootAlias = "r";
    private const string AuthViewAlias = "cv";
    private const string JoinAliasPrefix = "t";

    private readonly SqlDialect _dialect = dialect;
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);

    public SingleRecordCustomViewAuthorizationSqlPlan Compile(SingleRecordCustomViewAuthorizationSqlSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Checks);
        PlanSqlWriterExtensions.ValidateBareParameterName(
            spec.DocumentIdParameterName,
            nameof(spec.DocumentIdParameterName)
        );

        if (spec.Checks.Count == 0)
        {
            throw new ArgumentException(
                "Single-record custom view authorization requires at least one check spec.",
                nameof(spec)
            );
        }

        ValidateChecks(spec);
        var writer = new SqlWriter(_sqlDialect);
        List<int> emittedCheckIndexes = [];
        List<CustomViewAuthorizationProposedValueSqlParameter> proposedValueParameters = [];
        var hasStoredCheck = false;

        foreach (var check in spec.Checks)
        {
            switch (check.CheckTarget)
            {
                case CustomViewAuthorizationCheckTarget.Stored stored:
                    AppendStoredCheckSql(writer, check, stored, spec.DocumentIdParameterName);
                    hasStoredCheck = true;
                    break;

                case CustomViewAuthorizationCheckTarget.Proposed proposed:
                    AppendProposedCheckSql(writer, check, proposed);
                    proposedValueParameters.Add(
                        new CustomViewAuthorizationProposedValueSqlParameter(
                            check.Index,
                            proposed.Binding.ParameterSeed
                        )
                    );
                    break;

                // Decided in C#, not SQL: the answer depends on whether a target was captured and on the
                // paired stored check's outcome. Emitting nothing keeps the batch honest — a statement here
                // could only guess.
                case CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable:
                    continue;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(spec),
                        check.CheckTarget.GetType().Name,
                        "Unsupported custom view authorization check target."
                    );
            }

            if (spec.RowGuardPredicateSql is { } rowGuardPredicateSql)
            {
                writer.Append(" WHERE ");
                writer.Append(rowGuardPredicateSql);
            }

            writer.AppendLine(";");
            emittedCheckIndexes.Add(check.Index);
        }

        return new SingleRecordCustomViewAuthorizationSqlPlan(
            emittedCheckIndexes.Count == 0 ? string.Empty : writer.ToString(),
            BuildParametersInOrder(spec, hasStoredCheck, proposedValueParameters),
            proposedValueParameters,
            emittedCheckIndexes
        );
    }

    /// <summary>
    /// Enforces the invariants the payload contract and the emitted SQL depend on: index equals position, a
    /// resolved path exists, every check addresses the same root table, and every proposed binding names a
    /// usable parameter.
    /// </summary>
    private static void ValidateChecks(SingleRecordCustomViewAuthorizationSqlSpec spec)
    {
        DbTableName? rootTable = null;

        for (var position = 0; position < spec.Checks.Count; position++)
        {
            var check = spec.Checks[position];

            // The cv1 payload reports only an index, and the failure mapper resolves it positionally against
            // this same list. A gap or reorder here would report a denial as some other check's category.
            if (check.Index != position)
            {
                throw new ArgumentException(
                    $"Custom view authorization check at position {position} carries index {check.Index}; indexes must match emission position.",
                    nameof(spec)
                );
            }

            if (check.PathToBasisResource.Count == 0)
            {
                throw new ArgumentException(
                    $"Custom view authorization check '{check.Index}' has no path to its basis resource.",
                    nameof(spec)
                );
            }

            var checkRootTable = ResolveCheckRootTable(check);

            if (rootTable is null)
            {
                rootTable = checkRootTable;
            }
            else if (!rootTable.Equals(checkRootTable))
            {
                throw new ArgumentException(
                    $"Custom view authorization check specs must share one root table. Found '{checkRootTable}' and '{rootTable}'.",
                    nameof(spec)
                );
            }

            if (check.CheckTarget is CustomViewAuthorizationCheckTarget.Proposed proposed)
            {
                PlanSqlWriterExtensions.ValidateBareParameterName(
                    proposed.Binding.ParameterSeed,
                    $"{nameof(spec)}.{nameof(spec.Checks)}[{position}].Binding.ParameterSeed"
                );
            }
        }
    }

    private static DbTableName ResolveCheckRootTable(SingleRecordCustomViewAuthorizationCheckSpec check) =>
        check.CheckTarget switch
        {
            CustomViewAuthorizationCheckTarget.Stored stored => stored.RootTable,
            CustomViewAuthorizationCheckTarget.Proposed proposed => proposed.RootTable,
            CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable selfBasis => selfBasis.RootTable,
            _ => throw new ArgumentOutOfRangeException(
                nameof(check),
                check.CheckTarget.GetType().Name,
                "Unsupported custom view authorization check target."
            ),
        };

    private void AppendStoredCheckSql(
        SqlWriter writer,
        SingleRecordCustomViewAuthorizationCheckSpec check,
        CustomViewAuthorizationCheckTarget.Stored stored,
        string documentIdParameterName
    )
    {
        var steps = check.PathToBasisResource;
        var isSelfBasis = steps.Count == 1 && steps[0].SourceColumnName.Equals(stored.RootDocumentIdColumn);

        writer.AppendLine("SELECT CASE");

        using (writer.Indent())
        {
            // Authorized: the addressed row's basis DocumentId is in the view.
            writer.Append("WHEN EXISTS (");
            AppendStoredPathSelect(writer, check, stored, documentIdParameterName, useLeftJoins: false);
            writer.Append(" AND ");
            AppendTerminalValueInAuthView(writer, check, ResolveTerminalAlias(steps));
            writer.AppendLine(") THEN 1");

            // A self-basis path terminates on the root's own DocumentId, which is never null, so the
            // uninitialized branch is unreachable and is not emitted for it.
            if (!isSelfBasis)
            {
                writer.Append("WHEN EXISTS (");
                AppendStoredPathSelect(writer, check, stored, documentIdParameterName, useLeftJoins: true);
                writer.Append(" AND ");
                AppendAnyPathValueIsNull(writer, steps);
                writer.Append(") THEN ");
                AppendAuth1Throw(
                    writer,
                    check.Index,
                    CustomViewAuthorizationAuth1FailureKind.StoredBasisValueNull
                );
                writer.AppendLine();
            }

            // No row for the target DocumentId at all. The target was deleted between the unlocked target
            // lookup and this check; read paths re-resolve and surface a 404, and locked write and delete
            // paths row-lock the target first so they never reach this branch.
            writer.Append("WHEN NOT EXISTS (");
            AppendRootRowByDocumentId(writer, stored, documentIdParameterName);
            writer.Append(") THEN ");
            AppendAuth1Throw(
                writer,
                check.Index,
                CustomViewAuthorizationAuth1FailureKind.StoredTargetMissing
            );
            writer.AppendLine();

            writer.Append("ELSE ");
            AppendAuth1Throw(
                writer,
                check.Index,
                CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow
            );
            writer.AppendLine();
        }

        writer.Append("END");
    }

    private void AppendProposedCheckSql(
        SqlWriter writer,
        SingleRecordCustomViewAuthorizationCheckSpec check,
        CustomViewAuthorizationCheckTarget.Proposed proposed
    )
    {
        var steps = check.PathToBasisResource;

        writer.AppendLine("SELECT CASE");

        using (writer.Indent())
        {
            // The request body supplies no basis value at all.
            writer.Append("WHEN ");
            writer.AppendParameter(proposed.Binding.ParameterSeed);
            writer.Append(" IS NULL THEN ");
            AppendAuth1Throw(
                writer,
                check.Index,
                CustomViewAuthorizationAuth1FailureKind.ProposedBasisValueMissing
            );
            writer.AppendLine();

            if (steps.Count == 1)
            {
                // The bound value is already the basis DocumentId, so no traversal is needed.
                writer.Append("WHEN ");
                writer.AppendParameter(proposed.Binding.ParameterSeed);
                AppendInAuthView(writer, check);
                writer.AppendLine(" THEN 1");
            }
            else
            {
                writer.Append("WHEN EXISTS (");
                AppendProposedPathSelect(writer, check, proposed, useLeftJoins: false);
                writer.Append(" AND ");
                AppendTerminalValueInAuthView(writer, check, ResolveTerminalAlias(steps));
                writer.AppendLine(") THEN 1");

                // The referenced row resolves but carries no onward basis value. From the caller's side the
                // submitted data still supplies nothing authorizable, so this reports the same
                // proposed-value-missing category rather than the stored-data one.
                writer.Append("WHEN EXISTS (");
                AppendProposedPathSelect(writer, check, proposed, useLeftJoins: true);
                writer.Append(" AND ");
                AppendAnyPathValueIsNull(writer, steps, skipFirstStep: true);
                writer.Append(") THEN ");
                AppendAuth1Throw(
                    writer,
                    check.Index,
                    CustomViewAuthorizationAuth1FailureKind.ProposedBasisValueMissing
                );
                writer.AppendLine();
            }

            writer.Append("ELSE ");
            AppendAuth1Throw(
                writer,
                check.Index,
                CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow
            );
            writer.AppendLine();
        }

        writer.Append("END");
    }

    /// <summary>
    /// <c>SELECT 1 FROM &lt;root&gt; r [JOIN each hop] WHERE r.DocumentId = @documentId</c>. Joins are
    /// <c>LEFT</c> when the caller is probing for a null anywhere along the path, so a missing hop row reads
    /// as a null rather than eliminating the row.
    /// </summary>
    private static void AppendStoredPathSelect(
        SqlWriter writer,
        SingleRecordCustomViewAuthorizationCheckSpec check,
        CustomViewAuthorizationCheckTarget.Stored stored,
        string documentIdParameterName,
        bool useLeftJoins
    )
    {
        writer.Append("SELECT 1 FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(stored.RootTable));
        writer.Append($" {RootAlias}");
        AppendPathJoins(writer, check.PathToBasisResource, RootAlias, firstStepIndex: 0, useLeftJoins);
        writer.Append($" WHERE {RootAlias}.");
        writer.AppendQuoted(stored.RootDocumentIdColumn.Value);
        writer.Append(" = ");
        writer.AppendParameter(documentIdParameterName);
    }

    /// <summary>
    /// <c>SELECT 1 FROM &lt;firstHopTarget&gt; t1 [JOIN remaining hops] WHERE t1.&lt;key&gt; = @proposed</c>.
    /// The first hop is not joined: its value arrives as the bound parameter, so traversal starts at the row
    /// that value addresses.
    /// </summary>
    private static void AppendProposedPathSelect(
        SqlWriter writer,
        SingleRecordCustomViewAuthorizationCheckSpec check,
        CustomViewAuthorizationCheckTarget.Proposed proposed,
        bool useLeftJoins
    )
    {
        var steps = check.PathToBasisResource;
        var firstStep = steps[0];
        var firstHopTable =
            firstStep.TargetTable
            ?? throw new InvalidOperationException(
                "Transitive custom view authorization path steps must carry a target table."
            );
        var firstHopColumn =
            firstStep.TargetColumnName
            ?? throw new InvalidOperationException(
                "Transitive custom view authorization path steps must carry a target column."
            );
        var firstHopAlias = BuildJoinAlias(0);

        writer.Append("SELECT 1 FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(firstHopTable));
        writer.Append($" {firstHopAlias}");
        AppendPathJoins(writer, steps, firstHopAlias, firstStepIndex: 1, useLeftJoins);
        writer.Append($" WHERE {firstHopAlias}.");
        writer.AppendQuoted(firstHopColumn.Value);
        writer.Append(" = ");
        writer.AppendParameter(proposed.Binding.ParameterSeed);
    }

    /// <summary>
    /// Emits one join per non-terminal hop from <paramref name="firstStepIndex"/> onward, chaining each hop's
    /// target table to the previous alias's foreign key.
    /// </summary>
    private static void AppendPathJoins(
        SqlWriter writer,
        IReadOnlyList<ColumnPathStep> steps,
        string sourceAlias,
        int firstStepIndex,
        bool useLeftJoins
    )
    {
        var currentSourceAlias = sourceAlias;

        for (var stepIndex = firstStepIndex; stepIndex < steps.Count - 1; stepIndex++)
        {
            var step = steps[stepIndex];
            var targetTable =
                step.TargetTable
                ?? throw new InvalidOperationException(
                    "Transitive custom view authorization path steps must carry a target table."
                );
            var targetColumn =
                step.TargetColumnName
                ?? throw new InvalidOperationException(
                    "Transitive custom view authorization path steps must carry a target column."
                );
            var joinAlias = BuildJoinAlias(stepIndex);

            writer.Append(useLeftJoins ? " LEFT JOIN " : " JOIN ");
            writer.AppendRelation(new SqlRelationRef.PhysicalTable(targetTable));
            writer.Append($" {joinAlias} ON {joinAlias}.");
            writer.AppendQuoted(targetColumn.Value);
            writer.Append($" = {currentSourceAlias}.");
            writer.AppendQuoted(step.SourceColumnName.Value);

            currentSourceAlias = joinAlias;
        }
    }

    private static void AppendRootRowByDocumentId(
        SqlWriter writer,
        CustomViewAuthorizationCheckTarget.Stored stored,
        string documentIdParameterName
    )
    {
        writer.Append("SELECT 1 FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(stored.RootTable));
        writer.Append($" {RootAlias} WHERE {RootAlias}.");
        writer.AppendQuoted(stored.RootDocumentIdColumn.Value);
        writer.Append(" = ");
        writer.AppendParameter(documentIdParameterName);
    }

    private static void AppendTerminalValueInAuthView(
        SqlWriter writer,
        SingleRecordCustomViewAuthorizationCheckSpec check,
        string terminalAlias
    )
    {
        writer.Append($"{terminalAlias}.");
        writer.AppendQuoted(check.PathToBasisResource[^1].SourceColumnName.Value);
        AppendInAuthView(writer, check);
    }

    private static void AppendInAuthView(SqlWriter writer, SingleRecordCustomViewAuthorizationCheckSpec check)
    {
        writer.Append($" IN (SELECT {AuthViewAlias}.");
        writer.AppendQuoted(check.AuthViewDocumentIdColumn.Value);
        writer.Append(" FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(check.AuthView));
        writer.Append($" {AuthViewAlias})");
    }

    /// <summary>
    /// A disjunction over every followed value on the path. Covers a null foreign key at any hop and, because
    /// the probe uses left joins, a hop row that does not exist.
    /// </summary>
    private static void AppendAnyPathValueIsNull(
        SqlWriter writer,
        IReadOnlyList<ColumnPathStep> steps,
        bool skipFirstStep = false
    )
    {
        writer.Append("(");

        var appendedAny = false;

        for (var stepIndex = skipFirstStep ? 1 : 0; stepIndex < steps.Count; stepIndex++)
        {
            if (appendedAny)
            {
                writer.Append(" OR ");
            }

            writer.Append($"{ResolveAliasForStep(stepIndex)}.");
            writer.AppendQuoted(steps[stepIndex].SourceColumnName.Value);
            writer.Append(" IS NULL");
            appendedAny = true;
        }

        writer.Append(")");
    }

    /// <summary>
    /// The alias owning the terminal step's source column: the root when the path is a single hop, otherwise
    /// the join alias introduced by the preceding hop.
    /// </summary>
    private static string ResolveTerminalAlias(IReadOnlyList<ColumnPathStep> steps) =>
        ResolveAliasForStep(steps.Count - 1);

    private static string ResolveAliasForStep(int stepIndex) =>
        stepIndex == 0 ? RootAlias : BuildJoinAlias(stepIndex - 1);

    private static string BuildJoinAlias(int stepIndex) => $"{JoinAliasPrefix}{stepIndex + 1}";

    private void AppendAuth1Throw(
        SqlWriter writer,
        int emittedIndex,
        CustomViewAuthorizationAuth1FailureKind failureKind
    )
    {
        var payload = CustomViewAuthorizationAuth1FailurePayloadCodec.Encode(
            new CustomViewAuthorizationAuth1FailurePayload(emittedIndex, failureKind)
        );

        switch (_dialect)
        {
            case SqlDialect.Pgsql:
                writer.AppendQuoted("dms");
                writer.Append(".");
                writer.AppendQuoted("throw_error");
                writer.Append("('");
                writer.Append(CustomViewAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode);
                writer.Append("', '");
                writer.Append(payload);
                writer.Append("')");
                return;

            case SqlDialect.Mssql:
                writer.Append("CAST('");
                writer.Append(CustomViewAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode);
                writer.Append(" - ");
                writer.Append(payload);
                writer.Append("' AS INT)");
                return;

            default:
                throw new NotSupportedException(
                    $"Single-record custom view authorization SQL does not support SQL dialect '{_dialect}'."
                );
        }
    }

    private static IReadOnlyList<QuerySqlParameter> BuildParametersInOrder(
        SingleRecordCustomViewAuthorizationSqlSpec spec,
        bool hasStoredCheck,
        IReadOnlyList<CustomViewAuthorizationProposedValueSqlParameter> proposedValueParameters
    )
    {
        List<QuerySqlParameter> parameters = [];

        if (hasStoredCheck)
        {
            parameters.Add(new QuerySqlParameter(QuerySqlParameterRole.Filter, spec.DocumentIdParameterName));
        }

        parameters.AddRange(
            proposedValueParameters.Select(static proposedValueParameter => new QuerySqlParameter(
                QuerySqlParameterRole.Filter,
                proposedValueParameter.ParameterName
            ))
        );

        return parameters;
    }
}
