// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The database names a provisioning request may never select: the engine system
/// databases of both engines regardless of the configured provider, the names the provisioner
/// generates for itself, and the deployment's protected set (the CMS catalog database plus the
/// operator-listed <c>AdditionalProtectedDatabaseNames</c>). All comparisons are case-insensitive
/// because SQL Server's default collation folds case and the lists are small.
/// </summary>
public sealed class ProtectedDatabaseNames
{
    /// <summary>System databases of both engines.</summary>
    public static IReadOnlyList<string> Reserved { get; } =
    ["postgres", "template0", "template1", "master", "model", "msdb", "tempdb"];

    /// <summary>
    /// Prefixes of names the provisioner generates: staging databases
    /// (<c>edfi_dms_provision_stage_&lt;op&gt;</c>) and restore sessions (<c>dms-provision:&lt;op&gt;</c>).
    /// </summary>
    public static IReadOnlyList<string> ReservedPrefixes { get; } = ["edfi_dms_provision", "dms-provision"];

    private readonly HashSet<string> _protectedNames;

    private ProtectedDatabaseNames(HashSet<string> protectedNames)
    {
        _protectedNames = protectedNames;
        Names = [.. protectedNames.Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The deployment's protected set, sorted and de-duplicated case-insensitively. Suitable for a
    /// startup log line after each entry passes through the shared log sanitizer.
    /// </summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>
    /// Builds the protected set. Configured entries are trimmed; a blank catalog name or a blank
    /// entry is a configuration error and throws.
    /// </summary>
    public static ProtectedDatabaseNames Create(
        string catalogDatabaseName,
        IEnumerable<string> additionalNames
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDatabaseName);
        ArgumentNullException.ThrowIfNull(additionalNames);

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { catalogDatabaseName.Trim() };
        foreach (string additionalName in additionalNames)
        {
            if (string.IsNullOrWhiteSpace(additionalName))
            {
                throw new ArgumentException(
                    "A protected database name must not be blank.",
                    nameof(additionalNames)
                );
            }

            names.Add(additionalName.Trim());
        }

        return new ProtectedDatabaseNames(names);
    }

    public static bool IsReserved(string name) => Reserved.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool HasReservedPrefix(string name) =>
        ReservedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public bool IsProtected(string name) => _protectedNames.Contains(name);
}
