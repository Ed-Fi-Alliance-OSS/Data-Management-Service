// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Classifies the data source kind for a single tracked-change value column in an insert plan.
/// </summary>
internal enum TrackedChangeValueSourceKind
{
    /// <summary>
    /// The value is read directly from a physical column on the source table.
    /// </summary>
    DirectColumn,

    /// <summary>
    /// The value is read from a descriptor join (Namespace or CodeValue) at the table level.
    /// </summary>
    DescriptorJoin,

    /// <summary>
    /// The value is read from a person (Student/Contact/Staff) join at the table level.
    /// </summary>
    PersonJoin,
}

/// <summary>
/// A resolved data source for one <see cref="TrackedChangeColumnInfo"/> value column entry, carrying
/// the kind of source, the physical column name when applicable, and the join index into the owning
/// <see cref="TrackedChangeTableInfo.DescriptorJoins"/> or <see cref="TrackedChangeTableInfo.PersonJoins"/>
/// list when the kind requires a table-level join.
/// </summary>
/// <param name="Column">The tracked-change column metadata this source resolves.</param>
/// <param name="Kind">The source kind (direct column, descriptor join, or person join).</param>
/// <param name="SourceColumn">
/// The physical column name on the live source table to read the value from.
/// Non-null only when <paramref name="Kind"/> is <see cref="TrackedChangeValueSourceKind.DirectColumn"/>.
/// </param>
/// <param name="JoinIndex">
/// Zero-based index into <see cref="TrackedChangeTableInfo.DescriptorJoins"/> (for
/// <see cref="TrackedChangeValueSourceKind.DescriptorJoin"/>) or
/// <see cref="TrackedChangeTableInfo.PersonJoins"/> (for
/// <see cref="TrackedChangeValueSourceKind.PersonJoin"/>).
/// <c>-1</c> for <see cref="TrackedChangeValueSourceKind.DirectColumn"/>.
/// </param>
internal sealed record TrackedChangeValueSource(
    TrackedChangeColumnInfo Column,
    TrackedChangeValueSourceKind Kind,
    DbColumnName? SourceColumn,
    int JoinIndex
);

/// <summary>
/// A fully-resolved plan for inserting rows into a <c>tracked_changes_*</c> table from a
/// <see cref="TriggerKindParameters.DocumentStamping"/> trigger. Dialect emitters render
/// the plan mechanically without re-deriving resolution logic.
/// </summary>
/// <param name="Table">The tracked-change table metadata.</param>
/// <param name="Values">
/// The resolved value sources in the same order as
/// <see cref="TrackedChangeTableInfo.ValueColumnsInTableOrder"/>.
/// </param>
/// <param name="IdColumn">The physical column name of the <c>Id</c> system column.</param>
/// <param name="ChangeVersionColumn">
/// The physical column name of the <c>ChangeVersion</c> system column.
/// </param>
internal sealed record TrackedChangeInsertPlan(
    TrackedChangeTableInfo Table,
    IReadOnlyList<TrackedChangeValueSource> Values,
    DbColumnName IdColumn,
    DbColumnName ChangeVersionColumn
);

/// <summary>
/// Resolves a <see cref="TrackedChangeTableInfo"/> against its source <see cref="DbTableModel"/> into a
/// <see cref="TrackedChangeInsertPlan"/> that dialect emitters can render mechanically, and renders the
/// tombstone and key-change <c>INSERT … SELECT</c> statements for PostgreSQL and SQL Server stamping
/// triggers.
/// </summary>
internal static class TrackedChangeTriggerBodyEmitter
{
    /// <summary>
    /// Builds a <see cref="TrackedChangeInsertPlan"/> by resolving each value column in
    /// <paramref name="tableInfo"/>.<see cref="TrackedChangeTableInfo.ValueColumnsInTableOrder"/> to its
    /// physical data source, and by locating the <c>Id</c> and <c>ChangeVersion</c> system columns.
    /// </summary>
    /// <param name="tableInfo">The derived tracked-change table inventory entry.</param>
    /// <param name="sourceTableModel">
    /// The live source table model whose columns are matched against the tracked-change column paths.
    /// </param>
    /// <returns>A fully-resolved insert plan ready for dialect-specific SQL rendering.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a scalar column's <see cref="TrackedChangeColumnInfo.SourceJsonPath"/> matches zero or
    /// more than one source column; when a descriptor join name or person join name is not found in the
    /// table-level join lists; when a person join path step has a null
    /// <see cref="ColumnPathStep.TargetTable"/> or <see cref="ColumnPathStep.TargetColumnName"/>; or
    /// when the <c>Id</c> or <c>ChangeVersion</c> system column is absent.
    /// </exception>
    internal static TrackedChangeInsertPlan BuildPlan(
        TrackedChangeTableInfo tableInfo,
        DbTableModel sourceTableModel
    )
    {
        ArgumentNullException.ThrowIfNull(tableInfo);
        ArgumentNullException.ThrowIfNull(sourceTableModel);

        // Validate every person join path up-front so errors surface regardless of
        // which columns happen to reference a given join.
        foreach (var personJoin in tableInfo.PersonJoins)
        {
            ValidatePersonJoinPath(personJoin, tableInfo);
        }

        var values = new List<TrackedChangeValueSource>(tableInfo.ValueColumnsInTableOrder.Count);

        foreach (var column in tableInfo.ValueColumnsInTableOrder)
        {
            var source = ResolveValueSource(column, tableInfo, sourceTableModel);
            values.Add(source);
        }

        var idColumn = RequireSystemColumn(tableInfo, TrackedChangeSystemColumnRole.Id);
        var changeVersionColumn = RequireSystemColumn(tableInfo, TrackedChangeSystemColumnRole.ChangeVersion);

        return new TrackedChangeInsertPlan(tableInfo, values, idColumn, changeVersionColumn);
    }

    // ── Per-column resolution ───────────────────────────────────────

    private static TrackedChangeValueSource ResolveValueSource(
        TrackedChangeColumnInfo column,
        TrackedChangeTableInfo tableInfo,
        DbTableModel sourceTableModel
    )
    {
        switch (column.Role)
        {
            case TrackedChangeColumnRole.Scalar:
                return ResolveScalar(column, tableInfo, sourceTableModel);

            case TrackedChangeColumnRole.DescriptorNamespace:
            case TrackedChangeColumnRole.DescriptorCodeValue:
                return ResolveDescriptorJoin(column, tableInfo);

            case TrackedChangeColumnRole.PersonDocumentId:
                if (IsDirectSelfPersonDocumentId(column))
                {
                    return ResolveScalar(column, tableInfo, sourceTableModel);
                }

                return ResolvePersonJoin(column, tableInfo);

            default:
                throw new InvalidOperationException(
                    $"Tracked-change plan for table '{tableInfo.Table.Schema.Value}.{tableInfo.Table.Name}': "
                        + $"column '{column.OldColumnName.Value}' has unrecognized role '{column.Role}'."
                );
        }
    }

    private static bool IsDirectSelfPersonDocumentId(TrackedChangeColumnInfo column) =>
        column.PersonJoinName is null
        && column.CanonicalStorageColumn is { } canonicalColumn
        && canonicalColumn.Equals(new DbColumnName("DocumentId"));

    private static TrackedChangeValueSource ResolveScalar(
        TrackedChangeColumnInfo column,
        TrackedChangeTableInfo tableInfo,
        DbTableModel sourceTableModel
    )
    {
        // Prefer CanonicalStorageColumn when present (key-unified scalar).
        if (column.CanonicalStorageColumn is { } canonicalColumn)
        {
            if (!sourceTableModel.Columns.Any(c => c.ColumnName.Equals(canonicalColumn)))
            {
                throw new InvalidOperationException(
                    $"Tracked-change plan for table '{tableInfo.Table.Schema.Value}.{tableInfo.Table.Name}': "
                        + $"canonical storage column '{canonicalColumn.Value}' for tracked column "
                        + $"'{column.OldColumnName.Value}' does not exist on the source table."
                );
            }

            return new TrackedChangeValueSource(
                column,
                TrackedChangeValueSourceKind.DirectColumn,
                canonicalColumn,
                JoinIndex: -1
            );
        }

        // Otherwise resolve by matching SourceJsonPath.Canonical (Ordinal) against the source table.
        var matches = sourceTableModel
            .Columns.Where(c =>
                c.SourceJsonPath is { } path
                && string.Equals(path.Canonical, column.SourceJsonPath, StringComparison.Ordinal)
            )
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"Tracked-change plan for table '{tableInfo.Table.Schema.Value}.{tableInfo.Table.Name}': "
                    + $"no source column found with SourceJsonPath '{column.SourceJsonPath}'."
            );
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Tracked-change plan for table '{tableInfo.Table.Schema.Value}.{tableInfo.Table.Name}': "
                    + $"multiple source columns match SourceJsonPath '{column.SourceJsonPath}'."
            );
        }

        return new TrackedChangeValueSource(
            column,
            TrackedChangeValueSourceKind.DirectColumn,
            matches[0].ColumnName,
            JoinIndex: -1
        );
    }

    private static TrackedChangeValueSource ResolveDescriptorJoin(
        TrackedChangeColumnInfo column,
        TrackedChangeTableInfo tableInfo
    )
    {
        var joinName = column.DescriptorJoinName;
        var joinIndex = -1;
        for (int i = 0; i < tableInfo.DescriptorJoins.Count; i++)
        {
            if (
                string.Equals(
                    tableInfo.DescriptorJoins[i].DescriptorJoinName,
                    joinName,
                    StringComparison.Ordinal
                )
            )
            {
                joinIndex = i;
                break;
            }
        }

        if (joinIndex < 0)
        {
            throw new InvalidOperationException(
                $"Tracked-change plan for table '{tableInfo.Table.Schema.Value}.{tableInfo.Table.Name}': "
                    + $"descriptor join '{joinName}' referenced by column '{column.OldColumnName.Value}' "
                    + "was not found in DescriptorJoins."
            );
        }

        return new TrackedChangeValueSource(
            column,
            TrackedChangeValueSourceKind.DescriptorJoin,
            SourceColumn: null,
            JoinIndex: joinIndex
        );
    }

    private static TrackedChangeValueSource ResolvePersonJoin(
        TrackedChangeColumnInfo column,
        TrackedChangeTableInfo tableInfo
    )
    {
        var joinName = column.PersonJoinName;
        var joinIndex = -1;
        for (int i = 0; i < tableInfo.PersonJoins.Count; i++)
        {
            if (string.Equals(tableInfo.PersonJoins[i].PersonJoinName, joinName, StringComparison.Ordinal))
            {
                joinIndex = i;
                break;
            }
        }

        if (joinIndex < 0)
        {
            throw new InvalidOperationException(
                $"Tracked-change plan for table '{tableInfo.Table.Schema.Value}.{tableInfo.Table.Name}': "
                    + $"person join '{joinName}' referenced by column '{column.OldColumnName.Value}' "
                    + "was not found in PersonJoins."
            );
        }

        return new TrackedChangeValueSource(
            column,
            TrackedChangeValueSourceKind.PersonJoin,
            SourceColumn: null,
            JoinIndex: joinIndex
        );
    }

    private static void ValidatePersonJoinPath(
        TrackedChangePersonJoinInfo join,
        TrackedChangeTableInfo tableInfo
    )
    {
        if (join.JoinPath.Count == 0)
        {
            throw new InvalidOperationException(
                $"Tracked-change plan for table '{tableInfo.Table.Schema.Value}.{tableInfo.Table.Name}': "
                    + $"person join '{join.PersonJoinName}' has an empty join path; "
                    + "every step must have a non-null target."
            );
        }

        foreach (var step in join.JoinPath)
        {
            if (step.TargetTable is null || step.TargetColumnName is null)
            {
                throw new InvalidOperationException(
                    $"Tracked-change plan for table '{tableInfo.Table.Schema.Value}.{tableInfo.Table.Name}': "
                        + $"person join '{join.PersonJoinName}' has a path step "
                        + $"(source column '{step.SourceColumnName.Value}') with a null target table "
                        + "or target column name; all person join steps must have a fully-specified target."
                );
            }
        }
    }

    // ── Image bindings ──────────────────────────────────────────────

    /// <summary>
    /// Binds a SQL row reference keyword (<c>OLD</c> / <c>NEW</c> for PostgreSQL; <c>del</c> /
    /// <c>i</c> for SQL Server) to the alias prefix used for descriptor and person join aliases
    /// (<c>old</c> / <c>new</c>). Shared by the PostgreSQL and SQL Server renderers.
    /// </summary>
    private sealed record ImageBinding(string RowRef, string AliasPrefix);

    private static readonly ImageBinding PgsqlOldImage = new("OLD", "old");
    private static readonly ImageBinding PgsqlNewImage = new("NEW", "new");
    private static readonly ImageBinding MssqlOldImage = new("del", "old");
    private static readonly ImageBinding MssqlNewImage = new("i", "new");

    /// <summary>
    /// The plpgsql local the stamping trigger puts the tracked-change row's change version in. The
    /// identity-diff branch (key change) fills it from its stamp
    /// <c>UPDATE … RETURNING … INTO STRICT</c>, because a key change bumps a row that survives; the
    /// DELETE branch (tombstone) fills it with a fresh <c>nextval</c> off the change-version
    /// sequence, because nothing survives a delete to be stamped.
    /// </summary>
    private const string PgsqlStampedContentVersionLocal = "_stampedContentVersion";

    // ── PostgreSQL rendering entry points ───────────────────────────

    /// <summary>
    /// Emits a PostgreSQL <c>INSERT INTO … SELECT</c> statement that writes a tombstone row into the
    /// tracked-change table when a document is deleted. Values come from the <c>OLD</c> row image —
    /// including the tracked <c>Id</c>, which reads the root row's own <c>DocumentUuid</c> mirror;
    /// the change version comes from the plpgsql local <c>_stampedContentVersion</c> the DELETE
    /// branch assigned from the change-version sequence, because the <c>OLD</c> image carries the
    /// pre-delete <c>ContentVersion</c>. <c>New*</c> columns are omitted (they default to NULL).
    /// </summary>
    /// <param name="writer">The <see cref="SqlWriter"/> to append the statement to.</param>
    /// <param name="dialect">The SQL dialect for identifier quoting and table qualification.</param>
    /// <param name="plan">The resolved insert plan produced by <see cref="BuildPlan"/>.</param>
    internal static void EmitPgsqlTombstoneInsert(
        SqlWriter writer,
        ISqlDialect dialect,
        TrackedChangeInsertPlan plan
    ) => EmitPgsqlInsert(writer, dialect, plan, newImage: null);

    /// <summary>
    /// Emits a PostgreSQL <c>INSERT INTO … SELECT</c> statement that writes a key-change row into the
    /// tracked-change table when a document's identity columns change. Old values come from the
    /// <c>OLD</c> row image and new values from <c>NEW</c>; the change version is read from the
    /// plpgsql local <c>_stampedContentVersion</c> captured from the content-stamp RETURNING clause.
    /// </summary>
    /// <param name="writer">The <see cref="SqlWriter"/> to append the statement to.</param>
    /// <param name="dialect">The SQL dialect for identifier quoting and table qualification.</param>
    /// <param name="plan">The resolved insert plan produced by <see cref="BuildPlan"/>.</param>
    internal static void EmitPgsqlKeyChangeInsert(
        SqlWriter writer,
        ISqlDialect dialect,
        TrackedChangeInsertPlan plan
    ) => EmitPgsqlInsert(writer, dialect, plan, newImage: PgsqlNewImage);

    // ── SQL Server rendering entry points ───────────────────────────

    /// <summary>
    /// Emits a SQL Server <c>INSERT INTO … SELECT</c> statement that writes a tombstone row into the
    /// tracked-change table when a document is deleted. Old values come from the <c>deleted</c>
    /// pseudo-table (alias <c>del</c>) — including the tracked <c>Id</c>, which reads the root row's
    /// own <c>DocumentUuid</c> mirror; <c>New*</c> columns are omitted (they default to NULL);
    /// <c>ChangeVersion</c> is a fresh <c>NEXT VALUE FOR</c> off the change-version sequence,
    /// allocated once per deleted row. Nothing survives the delete to carry a post-delete stamp, and
    /// sourcing one from <c>dms.Document</c> would make the delete statement order load-bearing.
    /// </summary>
    /// <param name="writer">The <see cref="SqlWriter"/> to append the statement to.</param>
    /// <param name="dialect">The SQL dialect for identifier quoting and table qualification.</param>
    /// <param name="plan">The resolved insert plan produced by <see cref="BuildPlan"/>.</param>
    /// <param name="sequenceName">The qualified change-version sequence to allocate from.</param>
    internal static void EmitMssqlTombstoneInsert(
        SqlWriter writer,
        ISqlDialect dialect,
        TrackedChangeInsertPlan plan,
        string sequenceName
    )
    {
        // INSERT INTO <qualified tracked table> (
        writer.AppendLine($"INSERT INTO {dialect.QualifyTable(plan.Table.Table)} (");

        EmitInsertColumnsAndSelect(
            writer,
            dialect,
            plan,
            oldImage: MssqlOldImage,
            newImage: null,
            changeVersionSql: $"NEXT VALUE FOR {sequenceName}"
        );

        // FROM deleted del … optional old-image joins
        // The terminating `;` is appended to the last line of the block, not on its own line.
        var fixedLines = new[] { "FROM deleted del" };
        EmitMssqlFromJoinBlock(writer, dialect, plan, fixedLines, MssqlOldImage);
    }

    /// <summary>
    /// Emits a SQL Server <c>INSERT INTO … SELECT</c> statement that writes a key-change row into the
    /// tracked-change table when a document's identity columns change. The SELECT joins
    /// <c>@identityChangedDocs idc</c> to <c>inserted i</c> and <c>deleted del</c>; old values come
    /// from <c>del</c>, new from <c>i</c> — including the tracked <c>Id</c>, which reads the root
    /// row's own <c>DocumentUuid</c> mirror; <c>ChangeVersion</c> is read from
    /// <c>idc.[ContentVersion]</c>.
    /// </summary>
    /// <param name="writer">The <see cref="SqlWriter"/> to append the statement to.</param>
    /// <param name="dialect">The SQL dialect for identifier quoting and table qualification.</param>
    /// <param name="plan">The resolved insert plan produced by <see cref="BuildPlan"/>.</param>
    /// <param name="keyColumn">The physical column on the source table carrying the document key.</param>
    internal static void EmitMssqlKeyChangeInsert(
        SqlWriter writer,
        ISqlDialect dialect,
        TrackedChangeInsertPlan plan,
        DbColumnName keyColumn
    )
    {
        // INSERT INTO <qualified tracked table> (
        writer.AppendLine($"INSERT INTO {dialect.QualifyTable(plan.Table.Table)} (");

        EmitInsertColumnsAndSelect(
            writer,
            dialect,
            plan,
            oldImage: MssqlOldImage,
            newImage: MssqlNewImage,
            changeVersionSql: $"idc.{dialect.QuoteIdentifier("ContentVersion")}"
        );

        // FROM @identityChangedDocs idc … fixed joins … old-image and new-image joins
        // The terminating `;` is appended to the last line of the block, not on its own line.
        var fixedLines = new[]
        {
            "FROM @identityChangedDocs idc",
            $"INNER JOIN inserted i ON i.{dialect.QuoteIdentifier(keyColumn.Value)} = idc.{dialect.QuoteIdentifier("DocumentId")}",
            $"INNER JOIN deleted del ON del.{dialect.QuoteIdentifier(keyColumn.Value)} = i.{dialect.QuoteIdentifier(keyColumn.Value)}",
        };
        EmitMssqlFromJoinBlock(writer, dialect, plan, fixedLines, MssqlOldImage, MssqlNewImage);
    }

    // ── SQL Server FROM/JOIN block helper ──────────────────────────

    /// <summary>
    /// Emits the FROM clause, fixed INNER JOIN lines, and all image-driven INNER JOIN lines for a
    /// SQL Server INSERT statement, appending <c>;</c> directly to the last emitted line so the
    /// terminator is never on a line by itself.
    /// </summary>
    /// <param name="writer">The <see cref="SqlWriter"/> to write into.</param>
    /// <param name="dialect">The SQL dialect for identifier quoting.</param>
    /// <param name="plan">The resolved insert plan.</param>
    /// <param name="fixedLines">The pre-built FROM and fixed INNER JOIN lines, in order.</param>
    /// <param name="images">The image bindings whose joins should follow the fixed lines, in order.</param>
    private static void EmitMssqlFromJoinBlock(
        SqlWriter writer,
        ISqlDialect dialect,
        TrackedChangeInsertPlan plan,
        IReadOnlyList<string> fixedLines,
        params ImageBinding[] images
    )
    {
        // Collect all lines: fixed lines first, then join lines for each image binding.
        var allLines = new List<string>(fixedLines);
        foreach (var image in images)
        {
            allLines.AddRange(BuildJoinLines(dialect, plan, image));
        }

        // Emit all but the last with AppendLine; terminate the last with `;` on the same line.
        for (int k = 0; k < allLines.Count - 1; k++)
        {
            writer.AppendLine(allLines[k]);
        }

        writer.AppendLine($"{allLines[allLines.Count - 1]};");
    }

    /// <summary>
    /// Builds the JOIN clause strings for all descriptor and person joins required by the
    /// plan under the given image binding, without writing to a <see cref="SqlWriter"/>.
    /// </summary>
    private static IEnumerable<string> BuildJoinLines(
        ISqlDialect dialect,
        TrackedChangeInsertPlan plan,
        ImageBinding image
    )
    {
        // Descriptor joins
        for (int i = 0; i < plan.Table.DescriptorJoins.Count; i++)
        {
            var join = plan.Table.DescriptorJoins[i];
            var alias = $"{image.AliasPrefix}Dj{i}";
            var qualifiedDescriptor = dialect.QualifyTable(DmsTableNames.Descriptor);
            var joinKeyword = JoinKeyword(DescriptorJoinIsNullable(plan, i));
            yield return $"{joinKeyword} {qualifiedDescriptor} {alias} ON {alias}.{dialect.QuoteIdentifier("DocumentId")} = {image.RowRef}.{dialect.QuoteIdentifier(join.SourceColumn.Value)}";
        }

        // Person joins
        for (int i = 0; i < plan.Table.PersonJoins.Count; i++)
        {
            var join = plan.Table.PersonJoins[i];
            var joinKeyword = JoinKeyword(PersonJoinIsNullable(plan, i));
            for (int j = 0; j < join.JoinPath.Count; j++)
            {
                var step = join.JoinPath[j];
                var alias = $"{image.AliasPrefix}Pj{i}s{j}";
                var leftRef = j == 0 ? image.RowRef : $"{image.AliasPrefix}Pj{i}s{j - 1}";
                var qualifiedTarget = dialect.QualifyTable(step.TargetTable!.Value);
                yield return $"{joinKeyword} {qualifiedTarget} {alias} ON {alias}.{dialect.QuoteIdentifier(step.TargetColumnName!.Value.Value)} = {leftRef}.{dialect.QuoteIdentifier(step.SourceColumnName.Value)}";
            }
        }
    }

    private static string JoinKeyword(bool nullableJoin) => nullableJoin ? "LEFT JOIN" : "INNER JOIN";

    private static bool DescriptorJoinIsNullable(TrackedChangeInsertPlan plan, int joinIndex)
    {
        return plan.Values.Any(value =>
            value.Kind == TrackedChangeValueSourceKind.DescriptorJoin
            && value.JoinIndex == joinIndex
            && value.Column.IsOldColumnNullable
        );
    }

    private static bool PersonJoinIsNullable(TrackedChangeInsertPlan plan, int joinIndex)
    {
        return plan.Values.Any(value =>
            value.Kind == TrackedChangeValueSourceKind.PersonJoin
            && value.JoinIndex == joinIndex
            && value.Column.IsOldColumnNullable
        );
    }

    // ── Core INSERT emitter ─────────────────────────────────────────

    /// <summary>
    /// Emits a single PostgreSQL <c>INSERT INTO … SELECT [FROM … JOIN …]</c> statement for either a
    /// tombstone (<paramref name="newImage"/> == null) or a key-change row
    /// (<paramref name="newImage"/> != null). Every value comes from a row image, a descriptor/person
    /// join off a row image, or the plpgsql <c>_stampedContentVersion</c> local — so the statement
    /// needs a <c>FROM</c> clause only when there is a join to hang off something. When there is, a
    /// neutral one-row anchor supplies it, which keeps every join keyword and <c>ON</c> clause exactly
    /// as it was when the joins hung off <c>dms.Document</c>.
    /// </summary>
    private static void EmitPgsqlInsert(
        SqlWriter writer,
        ISqlDialect dialect,
        TrackedChangeInsertPlan plan,
        ImageBinding? newImage
    )
    {
        // Old-image joins first, then new-image joins (key-change only).
        var joinLines = new List<string>(BuildJoinLines(dialect, plan, PgsqlOldImage));
        if (newImage is not null)
        {
            joinLines.AddRange(BuildJoinLines(dialect, plan, newImage));
        }

        // INSERT INTO <qualified tracked table> (
        writer.AppendLine($"INSERT INTO {dialect.QualifyTable(plan.Table.Table)} (");

        EmitInsertColumnsAndSelect(
            writer,
            dialect,
            plan,
            oldImage: PgsqlOldImage,
            newImage: newImage,
            changeVersionSql: PgsqlStampedContentVersionLocal,
            // With no FROM clause to follow, the SELECT list carries the terminator.
            terminateSelectList: joinLines.Count == 0
        );

        if (joinLines.Count == 0)
        {
            return;
        }

        writer.AppendLine("FROM (SELECT 1) AS anchor");

        // The terminating `;` is appended to the last join line, not on its own line.
        for (int k = 0; k < joinLines.Count - 1; k++)
        {
            writer.AppendLine(joinLines[k]);
        }

        writer.AppendLine($"{joinLines[^1]};");
    }

    // ── Shared column-list + SELECT-list helper ─────────────────────

    /// <summary>
    /// Emits the column list and SELECT list sections shared by all INSERT renderers.
    /// Called directly after the <c>INSERT INTO … (</c> header line; manages its own
    /// indentation scopes for the column list and SELECT list.
    /// Emits: indented Old* columns, optional New* columns, Id column, ChangeVersion column;
    /// then closing paren, SELECT keyword, and indented old-image expressions, optional new-image
    /// expressions, the tracked Id read off a row image, and the ChangeVersion expression.
    /// </summary>
    /// <param name="writer">The <see cref="SqlWriter"/> to write into.</param>
    /// <param name="dialect">The SQL dialect for identifier quoting.</param>
    /// <param name="plan">The resolved insert plan.</param>
    /// <param name="oldImage">The image binding for old values (e.g. <c>OLD</c> / <c>del</c>).</param>
    /// <param name="newImage">The image binding for new values, or <c>null</c> for a tombstone.</param>
    /// <param name="changeVersionSql">The SQL expression for the ChangeVersion value in the SELECT list.</param>
    /// <param name="terminateSelectList">
    /// When <see langword="true"/>, appends the statement terminator to the ChangeVersion line because
    /// no FROM/JOIN block follows to carry it.
    /// </param>
    private static void EmitInsertColumnsAndSelect(
        SqlWriter writer,
        ISqlDialect dialect,
        TrackedChangeInsertPlan plan,
        ImageBinding oldImage,
        ImageBinding? newImage,
        string changeVersionSql,
        bool terminateSelectList = false
    )
    {
        var idSql = dialect.QuoteIdentifier(plan.IdColumn.Value);
        // The tracked Id is the document's UUID, mirrored onto the root row itself. A tombstone only
        // has the old image; a key change reports the post-change document, so it reads the new one.
        var idImage = newImage ?? oldImage;

        using (writer.Indent())
        {
            // Old value columns
            foreach (var value in plan.Values)
            {
                writer.AppendLine($"{dialect.QuoteIdentifier(value.Column.OldColumnName.Value)},");
            }

            // New value columns (key-change only; tombstones leave them NULL by default)
            if (newImage is not null)
            {
                foreach (var value in plan.Values)
                {
                    writer.AppendLine($"{dialect.QuoteIdentifier(value.Column.NewColumnName.Value)},");
                }
            }

            // Id column
            writer.AppendLine($"{idSql},");

            // ChangeVersion column — no trailing comma
            writer.AppendLine($"{dialect.QuoteIdentifier(plan.ChangeVersionColumn.Value)}");
        }

        // Close the column list, open SELECT
        writer.AppendLine(")");
        writer.AppendLine("SELECT");

        using (writer.Indent())
        {
            // Old-image value expressions
            foreach (var value in plan.Values)
            {
                var expr = ValueExpression(dialect, plan, value, oldImage);
                writer.AppendLine($"{expr},");
            }

            // New-image value expressions (key-change only)
            if (newImage is not null)
            {
                foreach (var value in plan.Values)
                {
                    var expr = ValueExpression(dialect, plan, value, newImage);
                    writer.AppendLine($"{expr},");
                }
            }

            // Id — the row image's own DocumentUuid mirror
            writer.AppendLine($"{idImage.RowRef}.{dialect.QuoteIdentifier("DocumentUuid")},");

            // ChangeVersion expression — no trailing comma
            writer.AppendLine(terminateSelectList ? $"{changeVersionSql};" : changeVersionSql);
        }
    }

    // ── Shared helpers (image-agnostic, shared by the PostgreSQL and SQL Server renderers) ──

    /// <summary>
    /// Returns the SQL expression that reads a single tracked-change value from the given row image.
    /// </summary>
    private static string ValueExpression(
        ISqlDialect dialect,
        TrackedChangeInsertPlan plan,
        TrackedChangeValueSource value,
        ImageBinding image
    )
    {
        return value.Kind switch
        {
            TrackedChangeValueSourceKind.DirectColumn =>
                $"{image.RowRef}.{dialect.QuoteIdentifier(value.SourceColumn!.Value.Value)}",

            TrackedChangeValueSourceKind.DescriptorJoin => value.Column.Role
            == TrackedChangeColumnRole.DescriptorNamespace
                ? $"{image.AliasPrefix}Dj{value.JoinIndex}.{dialect.QuoteIdentifier("Namespace")}"
                : $"{image.AliasPrefix}Dj{value.JoinIndex}.{dialect.QuoteIdentifier("CodeValue")}",

            TrackedChangeValueSourceKind.PersonJoin => PersonValueExpression(dialect, plan, value, image),

            _ => throw new InvalidOperationException(
                $"Unrecognized TrackedChangeValueSourceKind '{value.Kind}'."
            ),
        };
    }

    /// <summary>
    /// Returns the SQL expression for a PersonJoin value source: the last step's target column
    /// on the deepest join alias.
    /// </summary>
    private static string PersonValueExpression(
        ISqlDialect dialect,
        TrackedChangeInsertPlan plan,
        TrackedChangeValueSource value,
        ImageBinding image
    )
    {
        var join = plan.Table.PersonJoins[value.JoinIndex];
        var lastStepIndex = join.JoinPath.Count - 1;
        var lastStep = join.JoinPath[lastStepIndex];
        var alias = $"{image.AliasPrefix}Pj{value.JoinIndex}s{lastStepIndex}";
        return $"{alias}.{dialect.QuoteIdentifier(lastStep.TargetColumnName!.Value.Value)}";
    }

    // ── Descriptor tombstone renderer ────────────────────────────────

    /// <summary>
    /// Maps the canonical JSON path of a descriptor identity column to the physical column name
    /// on <c>dms.Descriptor</c> used by the descriptor tombstone INSERT.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DescriptorSourceColumnsByJsonPath =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["$.namespace"] = "Namespace",
            ["$.codeValue"] = "CodeValue",
        };

    /// <summary>
    /// Emits an <c>INSERT INTO … SELECT</c> tombstone statement for the shared descriptor
    /// tracked-change table. Called from within the <c>DELETE</c> branch of the descriptor
    /// stamping trigger (both PostgreSQL and SQL Server).
    /// Descriptors deliberately have no key-change counterpart: descriptor identity is
    /// immutable per the Change Queries design (descriptor <c>/keyChanges</c> endpoints
    /// always return an empty array), so the descriptor stamping trigger only tombstones.
    /// </summary>
    /// <param name="writer">The <see cref="SqlWriter"/> to append the statement to.</param>
    /// <param name="dialect">The SQL dialect for identifier quoting and table qualification.</param>
    /// <param name="tableInfo">
    /// The <see cref="TrackedChangeTableInfo"/> for the shared descriptor tracked-change table
    /// (<see cref="TrackedChangeTableKind.SharedDescriptor"/>).
    /// </param>
    /// <param name="imageRef">
    /// The row reference prefix: <c>"OLD"</c> for a PostgreSQL row-level trigger, or <c>"del"</c>
    /// for a SQL Server statement-level trigger whose row comes from the <c>deleted</c> pseudo-table.
    /// </param>
    /// <param name="fromDeletedSet">
    /// <c>false</c> (PostgreSQL): there is no FROM clause — every value is an <c>OLD</c> field or the
    /// plpgsql <c>_stampedContentVersion</c> local.
    /// <c>true</c> (SQL Server): the FROM clause is <c>deleted del</c>, and the change version is a
    /// <c>NEXT VALUE FOR</c> allocated per deleted row.
    /// </param>
    /// <param name="sequenceName">
    /// The qualified change-version sequence the SQL Server rendering allocates from. Unused by the
    /// PostgreSQL rendering, which reads the local the DELETE branch already assigned.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the <see cref="TrackedChangeSystemColumnRole.Id"/>,
    /// <see cref="TrackedChangeSystemColumnRole.ChangeVersion"/>, or
    /// <see cref="TrackedChangeSystemColumnRole.Discriminator"/> system column is absent, or when a
    /// value column has a role other than <see cref="TrackedChangeColumnRole.Scalar"/> or a
    /// <see cref="TrackedChangeColumnInfo.SourceJsonPath"/> not present in
    /// <see cref="DescriptorSourceColumnsByJsonPath"/>.
    /// </exception>
    internal static void EmitDescriptorTombstoneInsert(
        SqlWriter writer,
        ISqlDialect dialect,
        TrackedChangeTableInfo tableInfo,
        string imageRef,
        bool fromDeletedSet,
        string sequenceName
    )
    {
        var discriminatorColumn = RequireSystemColumn(tableInfo, TrackedChangeSystemColumnRole.Discriminator);
        var idColumn = RequireSystemColumn(tableInfo, TrackedChangeSystemColumnRole.Id);
        var changeVersionColumn = RequireSystemColumn(tableInfo, TrackedChangeSystemColumnRole.ChangeVersion);

        // INSERT INTO <qualified tracked table> (
        writer.AppendLine($"INSERT INTO {dialect.QualifyTable(tableInfo.Table)} (");
        using (writer.Indent())
        {
            // Discriminator first
            writer.AppendLine($"{dialect.QuoteIdentifier(discriminatorColumn.Value)},");

            // Old value columns in table order; new values are omitted for tombstones.
            foreach (var column in tableInfo.ValueColumnsInTableOrder)
            {
                writer.AppendLine($"{dialect.QuoteIdentifier(column.OldColumnName.Value)},");
            }

            // Id column
            writer.AppendLine($"{dialect.QuoteIdentifier(idColumn.Value)},");

            // ChangeVersion — no trailing comma
            writer.AppendLine($"{dialect.QuoteIdentifier(changeVersionColumn.Value)}");
        }

        writer.AppendLine(")");
        writer.AppendLine("SELECT");

        using (writer.Indent())
        {
            // Discriminator value from image ref
            writer.AppendLine($"{imageRef}.{dialect.QuoteIdentifier(discriminatorColumn.Value)},");

            // Old value column expressions — each resolved via DescriptorSourceColumnsByJsonPath
            foreach (var column in tableInfo.ValueColumnsInTableOrder)
            {
                if (column.Role != TrackedChangeColumnRole.Scalar)
                {
                    throw new InvalidOperationException(
                        $"Descriptor tombstone emitter for table "
                            + $"'{tableInfo.Table.Schema.Value}.{tableInfo.Table.Name}': "
                            + $"column '{column.OldColumnName.Value}' has role '{column.Role}' "
                            + "which has no fixed dms.Descriptor source column mapping; "
                            + "only Scalar columns are supported."
                    );
                }

                if (!DescriptorSourceColumnsByJsonPath.TryGetValue(column.SourceJsonPath, out var sourceCol))
                {
                    throw new InvalidOperationException(
                        $"Descriptor tombstone emitter for table "
                            + $"'{tableInfo.Table.Schema.Value}.{tableInfo.Table.Name}': "
                            + $"column '{column.OldColumnName.Value}' with path '{column.SourceJsonPath}' "
                            + "has no fixed dms.Descriptor source column mapping; "
                            + "register the path in DescriptorSourceColumnsByJsonPath."
                    );
                }

                writer.AppendLine($"{imageRef}.{dialect.QuoteIdentifier(sourceCol)},");
            }

            // Id — the deleted descriptor row's own DocumentUuid mirror
            writer.AppendLine($"{imageRef}.{dialect.QuoteIdentifier("DocumentUuid")},");

            // ChangeVersion — a fresh sequence value, never the deleted row's stale copy.
            // No trailing comma; on PostgreSQL this line also carries the statement terminator,
            // because no FROM clause follows it.
            writer.AppendLine(
                fromDeletedSet ? $"NEXT VALUE FOR {sequenceName}" : $"{PgsqlStampedContentVersionLocal};"
            );
        }

        if (fromDeletedSet)
        {
            // SQL Server statement trigger: the deleted pseudo-table is the whole FROM clause, so it
            // carries the terminator. The change version above comes from the sequence, so the
            // tombstone needs neither the @stamped capture table nor a surviving dms.Document row.
            writer.AppendLine("FROM deleted del;");
        }
    }

    // ── System column resolution ────────────────────────────────────

    private static DbColumnName RequireSystemColumn(
        TrackedChangeTableInfo tableInfo,
        TrackedChangeSystemColumnRole role
    )
    {
        var column = tableInfo.SystemColumns.FirstOrDefault(c => c.Role == role);
        if (column is null)
        {
            throw new InvalidOperationException(
                $"Tracked-change plan for table '{tableInfo.Table.Schema.Value}.{tableInfo.Table.Name}': "
                    + $"required system column with role '{role}' was not found in SystemColumns."
            );
        }

        return column.ColumnName;
    }
}
