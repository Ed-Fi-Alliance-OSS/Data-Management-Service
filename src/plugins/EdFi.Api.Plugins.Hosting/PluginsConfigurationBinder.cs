// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// Binds the <c>Plugins</c> section and validates the allowlist, before anything composes a path or
/// opens a file.
/// </summary>
internal static partial class PluginsConfigurationBinder
{
    /// <summary>The host-owned configuration section, which is the only one plugins have.</summary>
    internal const string SectionName = "Plugins";

    /// <summary>
    /// The single-path-segment rule, as it is written in messages so that an operator reading a
    /// refusal sees the same text the design states.
    /// </summary>
    internal const string AllowedNameRule = "^[A-Za-z0-9][A-Za-z0-9._-]*$";

    /// <summary>
    /// Binds and validates the section. The result is safe to compose paths from; nothing here has
    /// touched the filesystem.
    /// </summary>
    /// <exception cref="PluginLoadException">
    /// The allowlist is ambiguous, an entry is not a single path segment, or the configured directory
    /// cannot be turned into a full path.
    /// </exception>
    internal static PluginsConfiguration Bind(IConfiguration configuration)
    {
        PluginsOptions options =
            configuration.GetSection(SectionName).Get<PluginsOptions>() ?? new PluginsOptions();

        // A null Allowed is an absent allowlist rather than a malformed one: binding assigns a JSON
        // null straight over the property initializer, and an operator who wrote null asked for no
        // plugins just as surely as one who omitted the key.
        IReadOnlyList<string> allowedNames = ParseAllowed(options.Allowed);

        if (allowedNames.Count == 0)
        {
            // The shipped default. No plugin was asked for, so the configured directory is neither
            // resolved nor validated: a deployment that adopts nothing boots the same way whatever
            // Plugins:Directory says, and refusing it here would turn an unused setting into a startup
            // failure.
            return PluginsConfiguration.NoPluginsRequested;
        }

        // The allowlist is validated in full before the root is resolved, and the root is resolved
        // before anything asks the filesystem a question. An operator who wrote a bad entry learns
        // about the entry rather than about a path that could never have been composed from it.
        return PluginsConfiguration.Rooted(ResolveRoot(options.Directory), allowedNames);
    }

    /// <summary>
    /// Splits the delimited allowlist once, trims each entry, drops empty entries, and refuses an
    /// ambiguous or malformed list.
    /// </summary>
    private static IReadOnlyList<string> ParseAllowed(string? allowed)
    {
        List<string> names = [];

        if (allowed is null)
        {
            return names;
        }

        // Ambiguity is settled case-insensitively, because two entries differing only in case would
        // resolve to one directory on a case-insensitive filesystem and to two on the image's, so the
        // operator's intent cannot be recovered from the list. Every other comparison the loader makes
        // is ordinal.
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string entry in allowed.Split(','))
        {
            string name = entry.Trim();

            if (name.Length == 0)
            {
                continue;
            }

            if (!seen.Add(name))
            {
                throw new PluginLoadException(
                    PluginLoadFailure.DuplicateAllowlistEntry,
                    name,
                    $"Plugins:Allowed names '{PluginDiagnosticText.Quote(name)}' more than once. "
                        + "The allowlist has to be unambiguous about what the operator approved, and "
                        + "entries are compared case-insensitively so that two spellings of one name "
                        + "cannot mean one directory on one filesystem and two on another."
                );
            }

            names.Add(name);
        }

        // Shape is checked over the whole list rather than as each entry is read, so that a list whose
        // first entry is well formed and whose third is not still refuses the third rather than acting
        // on the first.
        foreach (string name in names)
        {
            if (!AllowedNamePattern().IsMatch(name))
            {
                throw new PluginLoadException(
                    PluginLoadFailure.InvalidAllowlistName,
                    name,
                    $"Plugins:Allowed entry '{PluginDiagnosticText.Quote(name)}' is not a plugin name. "
                        + $"Each entry has to be a single path segment matching {AllowedNameRule}, "
                        + "because a rooted or traversing entry would compose a path outside the plugin root."
                );
            }
        }

        return names;
    }

    /// <summary>
    /// Turns the configured directory into a fully qualified path, resolving a relative one against
    /// the application's base directory.
    /// </summary>
    private static string ResolveRoot(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new PluginLoadException(
                PluginLoadFailure.PluginPathUnresolvable,
                null,
                "Plugins:Directory is present but carries no path. Remove the setting to use the default "
                    + "'/app/plugins', or give it a path. An empty value is refused rather than treated "
                    + "as the default, because an operator who cleared the setting did not ask for the "
                    + "default root to be read."
            );
        }

        try
        {
            // Fully qualified rather than merely rooted: on Windows a path such as "/app/plugins" is
            // rooted and still needs a drive, and resolving it against the base directory is what
            // supplies one.
            return Path.IsPathFullyQualified(directory)
                ? Path.GetFullPath(directory)
                : Path.GetFullPath(directory, AppContext.BaseDirectory);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PluginLoadException(
                PluginLoadFailure.PluginPathUnresolvable,
                null,
                $"Plugins:Directory '{PluginDiagnosticText.Quote(directory)}' could not be resolved "
                    + "to a full path.",
                exception
            );
        }
    }

    // \z rather than $ closes the one hole the written rule has: $ also matches before a trailing
    // newline, so "Acme\n" would satisfy the rule as written. Trimming already removes that character,
    // which makes the hole unreachable here, and the anchor is exact anyway so that the pattern stays
    // correct if the trim ever moves.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedNamePattern();
}
