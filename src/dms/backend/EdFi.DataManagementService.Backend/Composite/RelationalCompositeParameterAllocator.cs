// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend.Composite;

/// <summary>
/// Allocates parameter names that are unique across every logical statement in one composite command.
/// </summary>
/// <remarks>
/// <para>
/// Extends the existing single-table convention (a per-row suffix) with a statement suffix, so the same
/// column binding appearing in two co-batched statements cannot collide:
/// <c>@{name}_s{statementOrdinal}_{rowIndex}</c>.
/// </para>
/// <para>
/// Reserved names exist for two reasons. Provider carriers declare batch-local variables
/// (<c>@dms_composite_target_documentid</c>) and SqlClient rejects a batch-local sharing a name with a
/// bound parameter, so those names must never be issued. Callers also reserve compiled write-plan
/// binding names so authorization SQL cannot shadow a write binding — the concern the persister
/// previously handled with its own reserved-name list.
/// </para>
/// </remarks>
internal sealed class RelationalCompositeParameterAllocator
{
    private readonly HashSet<string> _issuedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reservedNames = new(StringComparer.OrdinalIgnoreCase);

    public RelationalCompositeParameterAllocator(IEnumerable<string>? reservedNames = null)
    {
        if (reservedNames is null)
        {
            return;
        }

        foreach (var reservedName in reservedNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reservedName);
            _reservedNames.Add(Normalize(reservedName));
        }
    }

    /// <summary>Names that may never be issued, without the leading sigil.</summary>
    public IReadOnlyCollection<string> ReservedNames => _reservedNames;

    /// <summary>Names issued so far, without the leading sigil.</summary>
    public IReadOnlyCollection<string> IssuedNames => _issuedNames;

    /// <summary>
    /// Allocates a statement- and row-scoped parameter name.
    /// </summary>
    public string Allocate(string baseName, int statementOrdinal, int rowIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentOutOfRangeException.ThrowIfNegative(statementOrdinal);
        ArgumentOutOfRangeException.ThrowIfNegative(rowIndex);

        var candidate = string.Create(
            CultureInfo.InvariantCulture,
            $"{Normalize(baseName)}_s{statementOrdinal}_{rowIndex}"
        );

        return Issue(candidate);
    }

    /// <summary>
    /// Allocates a name scoped to a statement but not to a row, for fixed parameters such as a document
    /// uuid bound once per statement.
    /// </summary>
    public string AllocateStatementScoped(string baseName, int statementOrdinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentOutOfRangeException.ThrowIfNegative(statementOrdinal);

        var candidate = string.Create(
            CultureInfo.InvariantCulture,
            $"{Normalize(baseName)}_s{statementOrdinal}"
        );

        return Issue(candidate);
    }

    private string Issue(string candidate)
    {
        if (_reservedNames.Contains(candidate))
        {
            throw new InvalidOperationException(
                $"Composite command parameter name '@{candidate}' collides with a reserved name. "
                    + "Reserved names include provider carrier variables and caller-supplied write-plan bindings."
            );
        }

        if (!_issuedNames.Add(candidate))
        {
            throw new InvalidOperationException(
                $"Composite command parameter name '@{candidate}' was already issued. "
                    + "Every logical statement must allocate through this allocator so names stay unique."
            );
        }

        return "@" + candidate;
    }

    private static string Normalize(string name) => name.TrimStart('@');
}
