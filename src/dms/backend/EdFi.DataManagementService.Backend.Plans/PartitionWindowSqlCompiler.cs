// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles the partition-boundary statement from an already-compiled unpaged candidate relation.
/// </summary>
/// <remarks>
/// The candidate relation arrives compiled rather than as a specification, so the filters,
/// change-version predicates, and row-level authorization a partition request is calculated over are
/// byte-for-byte the ones a page of the same request would be selected over. Wrapping it in a common
/// table expression is what puts every predicate before the row numbering and the count: numbering an
/// unfiltered or unauthorized relation would corrupt every boundary, and no later predicate could
/// repair it.
///
/// One statement, and it returns starting anchor values only — <c>DocumentId</c> or
/// <c>ContentVersion</c>, whichever the candidate relation was compiled against. It hydrates nothing,
/// projects no profile, resolves no descriptor, injects no link, and computes no response total count.
/// </remarks>
public sealed class PartitionWindowSqlCompiler(SqlDialect dialect)
{
    private const string CandidatesCteName = "candidates";
    private const string RankedCteName = "ranked";
    private const string SizedCteName = "sized";

    /// <summary>
    /// Aliases local to this statement. Distinct from the candidate relation's own root and document
    /// aliases, so a reader never has to work out which scope an alias belongs to.
    /// </summary>
    private const string CandidatesAlias = "pc";

    private const string RankedAlias = "pr";
    private const string SizedAlias = "ps";

    private const string RowNumberColumnName = "row_number";
    private const string CandidateCountColumnName = "candidate_count";
    private const string PartitionSizeColumnName = "partition_size";

    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);
    private readonly IPlanSqlDialect _planSqlDialect = PlanSqlDialectFactory.Create(dialect);

    /// <summary>
    /// Compiles the partition-boundary statement.
    /// </summary>
    /// <param name="candidatePlan">
    /// The compiled unpaged candidate relation, from <see cref="PageDocumentIdSqlCompiler" /> with
    /// <see cref="PageCandidateMode.UnpagedCandidates" />.
    /// </param>
    /// <param name="mode">
    /// The candidate mode the plan was compiled with. Supplies the parameter names this statement binds,
    /// so a mode constructed with non-default names emits the names it reserved, and the anchor the
    /// relation projects, so this statement ranks and cuts on the column that relation actually has.
    /// </param>
    /// <returns>
    /// The compiled statement and its parameter inventory: the candidate relation's filter parameters,
    /// then the requested count and the minimum size. Those two are reserved but unbound by the
    /// candidate relation itself, and this is the compiler that binds them.
    /// </returns>
    public PageDocumentIdSqlPlan Compile(
        PageDocumentIdSqlPlan candidatePlan,
        PageCandidateMode.UnpagedCandidates mode
    )
    {
        ArgumentNullException.ThrowIfNull(candidatePlan);
        ArgumentNullException.ThrowIfNull(mode);

        var modeParameters = PageCandidateModeParameters.For(mode);

        ValidateCandidatePlan(candidatePlan, modeParameters);

        var writer = new SqlWriter(_sqlDialect);

        // Resolved once and threaded through every clause, so the ranking, the sizing, and the boundary
        // projection cannot name different columns within one statement.
        var anchorColumnName = PageDocumentIdSqlCompiler.ResolveOrderingColumnName(mode);

        AppendCandidatesCte(writer, candidatePlan);
        AppendRankedCte(writer, anchorColumnName);
        AppendSizedCte(writer, mode, anchorColumnName);
        AppendBoundarySelect(writer, anchorColumnName);

        return new PageDocumentIdSqlPlan(
            writer.ToString(),
            TotalCountSql: null,
            PageParametersInOrder:
            [
                .. candidatePlan.PageParametersInOrder,
                .. modeParameters.Select(static modeParameter => new QuerySqlParameter(
                    modeParameter.Role,
                    modeParameter.Name
                )),
            ],
            TotalCountParametersInOrder: null
        );
    }

    /// <summary>
    /// Ensures the supplied plan really is the unpaged candidate relation and that its parameters leave
    /// room for the two this statement binds.
    /// </summary>
    /// <remarks>
    /// A paged plan would already carry its own size or range clause and an ordering this statement
    /// replaces, so wrapping one would silently compute boundaries over a single page. A total-count
    /// plan would additionally violate the one-command requirement.
    /// </remarks>
    private static void ValidateCandidatePlan(
        PageDocumentIdSqlPlan candidatePlan,
        IReadOnlyList<PageCandidateModeParameter> modeParameters
    )
    {
        if (candidatePlan.TotalCountSql is not null || candidatePlan.TotalCountParametersInOrder is not null)
        {
            throw new ArgumentException(
                "Partition window compilation requires a candidate plan with no total-count SQL. The "
                    + "endpoint performs one command and returns no total count.",
                nameof(candidatePlan)
            );
        }

        var pagingRole = candidatePlan.PageParametersInOrder.FirstOrDefault(parameter =>
            parameter.Role
                is QuerySqlParameterRole.Offset
                    or QuerySqlParameterRole.Limit
                    or QuerySqlParameterRole.PageSize
                    or QuerySqlParameterRole.CursorInclusiveMinimum
                    or QuerySqlParameterRole.CursorInclusiveMaximum
        );

        if (pagingRole is not null)
        {
            throw new ArgumentException(
                $"Partition window compilation requires the unpaged candidate relation, but the supplied "
                    + $"plan binds the '{pagingRole.Role}' paging role.",
                nameof(candidatePlan)
            );
        }

        var collidingParameter = candidatePlan.PageParametersInOrder.FirstOrDefault(parameter =>
            modeParameters.Any(modeParameter =>
                string.Equals(modeParameter.Name, parameter.ParameterName, StringComparison.OrdinalIgnoreCase)
            )
        );

        if (collidingParameter is not null)
        {
            throw new ArgumentException(
                $"Partition window compilation cannot bind parameter '{collidingParameter.ParameterName}' "
                    + "because the candidate relation already supplies it.",
                nameof(candidatePlan)
            );
        }
    }

    private static void AppendCandidatesCte(SqlWriter writer, PageDocumentIdSqlPlan candidatePlan)
    {
        writer
            .AppendLine($"WITH {CandidatesCteName} AS (")
            .AppendLine(PlanSqlStatementText.AsEmbeddableBody(candidatePlan.PageDocumentIdSql))
            .AppendLine("),");
    }

    private void AppendRankedCte(SqlWriter writer, string anchorColumnName)
    {
        var quotedAnchor = _sqlDialect.QuoteIdentifier(anchorColumnName);

        writer.AppendLine($"{RankedCteName} AS (");

        using (writer.Indent())
        {
            writer
                .AppendLine("SELECT")
                .AppendLine($"    {CandidatesAlias}.{quotedAnchor},")
                .Append($"    ROW_NUMBER() OVER (ORDER BY {CandidatesAlias}.{quotedAnchor}) AS ")
                .AppendQuoted(RowNumberColumnName)
                .AppendLine(",")
                .Append($"    {_planSqlDialect.CandidateCountOverWindowSql} AS ")
                .AppendQuoted(CandidateCountColumnName)
                .AppendLine()
                .AppendLine($"FROM {CandidatesCteName} {CandidatesAlias}");
        }

        writer.AppendLine("),");
    }

    private void AppendSizedCte(
        SqlWriter writer,
        PageCandidateMode.UnpagedCandidates mode,
        string anchorColumnName
    )
    {
        var quotedAnchor = _sqlDialect.QuoteIdentifier(anchorColumnName);
        var quotedRowNumber = _sqlDialect.QuoteIdentifier(RowNumberColumnName);
        var quotedCandidateCount = _sqlDialect.QuoteIdentifier(CandidateCountColumnName);

        writer.AppendLine($"{SizedCteName} AS (");

        using (writer.Indent())
        {
            writer
                .AppendLine("SELECT")
                .AppendLine($"    {RankedAlias}.{quotedAnchor},")
                .AppendLine($"    {RankedAlias}.{quotedRowNumber},")
                .Append("    ");

            _planSqlDialect.AppendPartitionSizeExpression(
                writer,
                $"{RankedAlias}.{quotedCandidateCount}",
                mode.PartitionCountParameterName,
                mode.MinimumPartitionSizeParameterName
            );

            writer
                .Append(" AS ")
                .AppendQuoted(PartitionSizeColumnName)
                .AppendLine()
                .AppendLine($"FROM {RankedCteName} {RankedAlias}");
        }

        writer.AppendLine(")");
    }

    /// <summary>
    /// Selects the anchor value at candidate row 1 and at every partition-size step from it. Selecting
    /// the actual anchor value at those row numbers, rather than dividing the anchor range
    /// arithmetically, is what keeps partitions balanced when anchor values are sparse — which they
    /// always are after deletes, and which a <c>ContentVersion</c> window is by construction.
    /// </summary>
    private void AppendBoundarySelect(SqlWriter writer, string anchorColumnName)
    {
        var quotedAnchor = _sqlDialect.QuoteIdentifier(anchorColumnName);
        var quotedRowNumber = _sqlDialect.QuoteIdentifier(RowNumberColumnName);
        var quotedPartitionSize = _sqlDialect.QuoteIdentifier(PartitionSizeColumnName);

        writer
            .AppendLine($"SELECT {SizedAlias}.{quotedAnchor}")
            .AppendLine($"FROM {SizedCteName} {SizedAlias}")
            .AppendLine(
                $"WHERE ({SizedAlias}.{quotedRowNumber} - 1) % {SizedAlias}.{quotedPartitionSize} = 0"
            )
            .AppendLine($"ORDER BY {SizedAlias}.{quotedAnchor} ASC;");
    }
}
