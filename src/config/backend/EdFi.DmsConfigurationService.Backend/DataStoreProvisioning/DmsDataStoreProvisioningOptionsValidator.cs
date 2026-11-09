// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Options;
using static EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.DmsDataStoreProvisioningOptions;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The static validation of <see cref="DmsDataStoreProvisioningOptions"/> (spec 4.3, 7.2), run at startup
/// through <c>ValidateOnStart</c>. A disabled section always passes. When enabled, every failed rule is
/// reported; each message names <c>DmsDataStoreProvisioning:&lt;Key&gt;</c> and the rule, and never echoes
/// a configured value, because the connection string and paths may hold credentials. A nested section,
/// collection, or allowlist entry that binding left null is reported as missing, never thrown on.
/// </summary>
/// <remarks>
/// The connection string is parsed for syntax with the provider-neutral <see cref="DbConnectionStringBuilder"/>:
/// this project references no database driver (F-C1). The engine adapter's own builder and a real
/// connection are exercised by the readiness probes (7.3 step 4), which is where provider-specific
/// parsing lives. CMS-local paths must be fully qualified under the CMS host's own rules;
/// <c>Mssql:StagingServerPath</c> is the SQL Server host's view and may follow either convention.
/// </remarks>
/// <param name="secretResolver">The registered resolver, or null when no secrets plugin is installed.</param>
public sealed partial class DmsDataStoreProvisioningOptionsValidator(ISecretResolver? secretResolver = null)
    : IValidateOptions<DmsDataStoreProvisioningOptions>
{
    private const string Section = SectionName;

    private const string SecretReferenceOpening = "${secret:";

    public const int MaximumPackageIdLength = 100;

    /// <summary>
    /// The only keys a PostgreSQL administrative connection string may carry (D-10): the <c>psql</c>
    /// handoff reproduces exactly these, so anything else would silently not apply to the restore.
    /// </summary>
    public static IReadOnlyList<string> PostgresqlConnectionStringKeys { get; } =
    [
        "Host",
        "Port",
        "Username",
        "Password",
        "Database",
        "SSL Mode",
        "Root Certificate",
        "Timeout",
        "Pooling",
        "Minimum Pool Size",
        "Maximum Pool Size",
        "Application Name",
    ];

    public ValidateOptionsResult Validate(string? name, DmsDataStoreProvisioningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];

        string? provider = ValidateProvider(failures, options.Provider);
        ValidateAdminConnectionString(failures, options.AdminConnectionString, provider);
        ValidateProtectedNames(failures, options.AdditionalProtectedDatabaseNames);
        RequireLocalPath(failures, nameof(options.TrustPolicyPath), options.TrustPolicyPath);
        RequireLocalPath(failures, nameof(options.ArtifactDirectory), options.ArtifactDirectory);
        RequireOmittedOrLocalPath(failures, nameof(options.OperationDirectory), options.OperationDirectory);
        string? dataStandardVersion = ValidateCompatibility(failures, options.Compatibility);
        ValidateTemplates(failures, options.Templates, dataStandardVersion);
        ValidateRanges(failures, options);

        if (provider == PostgresqlProvider)
        {
            ValidatePostgresql(failures, options.Postgresql);
        }
        else if (provider == MssqlProvider)
        {
            ValidateMssql(failures, options.Mssql);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static string? ValidateProvider(List<string> failures, string? provider)
    {
        if (provider is not null && Providers.Contains(provider, StringComparer.Ordinal))
        {
            return provider;
        }

        failures.Add($"{Section}:Provider must be exactly {PostgresqlProvider} or {MssqlProvider}.");
        return null;
    }

    private void ValidateAdminConnectionString(
        List<string> failures,
        string? connectionString,
        string? provider
    )
    {
        const string Key = $"{Section}:AdminConnectionString";
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            failures.Add($"{Key} is required.");
            return;
        }

        int openings = CountOccurrences(connectionString, SecretReferenceOpening);
        IReadOnlyList<SecretReferenceToken> tokens = SecretReferenceTokens.Find(connectionString);
        if (openings != tokens.Count)
        {
            failures.Add($"{Key} contains a malformed ${{secret:<name>}} reference.");
        }

        if (tokens.Count > 0 && secretResolver is null)
        {
            failures.Add(
                $"{Key} references ${{secret:<name>}} values, but no ISecretResolver is registered; install a Configuration Service secrets plugin."
            );
        }

        DbConnectionStringBuilder builder = new();
        try
        {
            builder.ConnectionString = connectionString;
        }
        catch (ArgumentException)
        {
            // The parser's message repeats the text it could not parse.
            failures.Add($"{Key} must be a syntactically valid connection string.");
            return;
        }

        if (builder.Count == 0)
        {
            failures.Add($"{Key} must contain at least one keyword.");
            return;
        }

        if (provider == PostgresqlProvider)
        {
            ValidatePostgresqlKeys(failures, builder, Key);
        }
    }

    private static void ValidatePostgresqlKeys(
        List<string> failures,
        DbConnectionStringBuilder builder,
        string key
    )
    {
        List<string> keys = [.. builder.Keys.Cast<string>()];
        if (keys.Exists(k => !PostgresqlConnectionStringKeys.Contains(k, StringComparer.OrdinalIgnoreCase)))
        {
            failures.Add(
                $"{key} may contain only the keys {string.Join(", ", PostgresqlConnectionStringKeys)} for PostgreSQL."
            );
        }

        string? database = keys.Find(k => string.Equals(k, "Database", StringComparison.OrdinalIgnoreCase));
        if (
            database is not null
            && !string.Equals(builder[database] as string, "postgres", StringComparison.OrdinalIgnoreCase)
        )
        {
            failures.Add($"{key} Database must be absent or postgres for PostgreSQL.");
        }

        // psql verifies the server only against the Root Certificate file, while Npgsql without one falls back to
        // other trust sources, so a verifying mode without one would pass readiness and fail every replay.
        if (
            builder.TryGetValue("SSL Mode", out object? sslMode)
            && (sslMode as string)?.Replace("-", string.Empty, StringComparison.Ordinal) is { } mode
            && (
                string.Equals(mode, "VerifyCA", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "VerifyFull", StringComparison.OrdinalIgnoreCase)
            )
            && !(
                builder.TryGetValue("Root Certificate", out object? root)
                && !string.IsNullOrWhiteSpace(root as string)
            )
        )
        {
            failures.Add(
                $"{key} must name a Root Certificate when SSL Mode is VerifyCA or VerifyFull for PostgreSQL, because psql verifies the server only against that file."
            );
        }
    }

    private static void ValidateProtectedNames(List<string> failures, string[]? names)
    {
        if (names is not null && Array.Exists(names, string.IsNullOrWhiteSpace))
        {
            failures.Add($"{Section}:AdditionalProtectedDatabaseNames must not contain blank entries.");
        }
    }

    /// <summary>Returns the data standard version when it is valid, so the allowlist suffix rule can use it.</summary>
    private static string? ValidateCompatibility(
        List<string> failures,
        CompatibilityExpectations? compatibility
    )
    {
        const string Key = $"{Section}:Compatibility";
        if (compatibility is null)
        {
            failures.Add($"{Key} is required.");
            return null;
        }

        string? dataStandardVersion = null;
        if (string.IsNullOrWhiteSpace(compatibility.DataStandardVersion))
        {
            failures.Add($"{Key}:DataStandardVersion is required.");
        }
        else if (!DataStandardVersionPattern().IsMatch(compatibility.DataStandardVersion))
        {
            failures.Add($"{Key}:DataStandardVersion must be a dotted numeric version such as 5.2.0.");
        }
        else
        {
            dataStandardVersion = compatibility.DataStandardVersion;
        }

        if (string.IsNullOrWhiteSpace(compatibility.EffectiveSchemaHash))
        {
            failures.Add($"{Key}:EffectiveSchemaHash is required.");
        }
        else if (!EffectiveSchemaHashPattern().IsMatch(compatibility.EffectiveSchemaHash))
        {
            failures.Add($"{Key}:EffectiveSchemaHash must be 64 lowercase hexadecimal characters.");
        }

        string[]? projects = compatibility.Projects;
        if (projects is null || projects.Length == 0)
        {
            failures.Add($"{Key}:Projects is required.");
        }
        else
        {
            if (Array.Exists(projects, string.IsNullOrWhiteSpace))
            {
                failures.Add($"{Key}:Projects must not contain blank entries.");
            }

            if (projects.Distinct(StringComparer.OrdinalIgnoreCase).Count() != projects.Length)
            {
                failures.Add($"{Key}:Projects must not repeat a schema name (compared case-insensitively).");
            }

            if (!projects.Contains("edfi", StringComparer.OrdinalIgnoreCase))
            {
                failures.Add($"{Key}:Projects must include edfi.");
            }
        }

        RequireOmittedOrNonBlank(
            failures,
            $"{Key}:ApiSchemaFormatVersion",
            compatibility.ApiSchemaFormatVersion
        );
        RequireOmittedOrNonBlank(
            failures,
            $"{Key}:RelationalMappingVersion",
            compatibility.RelationalMappingVersion
        );
        return dataStandardVersion;
    }

    private static void ValidateTemplates(
        List<string> failures,
        Dictionary<string, TemplateAllowlistEntry?>? templates,
        string? dataStandardVersion
    )
    {
        const string Key = $"{Section}:Templates";
        string[] expectedKeys = [DmsDatabaseTemplate.MinimalWireValue, DmsDatabaseTemplate.SampleWireValue];
        if (templates is null)
        {
            failures.Add($"{Key} must define exactly the entries Minimal and Sample.");
            return;
        }

        if (
            templates.Count != expectedKeys.Length
            || Array.Exists(expectedKeys, k => !templates.ContainsKey(k))
        )
        {
            failures.Add($"{Key} must define exactly the entries Minimal and Sample.");
        }

        Dictionary<string, (string Id, string Version)> valid = new(StringComparer.Ordinal);
        foreach (string wireValue in expectedKeys)
        {
            if (!templates.TryGetValue(wireValue, out TemplateAllowlistEntry? entry))
            {
                continue;
            }

            string entryKey = $"{Key}:{wireValue}";
            if (entry is null)
            {
                failures.Add($"{entryKey} is required.");
                continue;
            }

            bool idValid = ValidatePackageId(failures, entryKey, entry.PackageId, dataStandardVersion);
            bool versionValid = ValidatePackageVersion(failures, entryKey, entry.PackageVersion);
            if (idValid && versionValid)
            {
                valid[wireValue] = (entry.PackageId!, entry.PackageVersion!);
            }
        }

        if (
            valid.TryGetValue(DmsDatabaseTemplate.MinimalWireValue, out (string Id, string Version) minimal)
            && valid.TryGetValue(DmsDatabaseTemplate.SampleWireValue, out (string Id, string Version) sample)
            && string.Equals(minimal.Id, sample.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(minimal.Version, sample.Version, StringComparison.Ordinal)
        )
        {
            failures.Add($"{Key} Minimal and Sample must name different packages.");
        }
    }

    private static bool ValidatePackageId(
        List<string> failures,
        string entryKey,
        string? packageId,
        string? dataStandardVersion
    )
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            failures.Add($"{entryKey}:PackageId is required.");
            return false;
        }

        if (packageId.Length > MaximumPackageIdLength || !PackageIdPattern().IsMatch(packageId))
        {
            failures.Add(
                $"{entryKey}:PackageId must match the NuGet package id grammar and be at most {MaximumPackageIdLength} characters."
            );
            return false;
        }

        if (
            dataStandardVersion is not null
            && !packageId.EndsWith("." + dataStandardVersion, StringComparison.Ordinal)
        )
        {
            failures.Add(
                $"{entryKey}:PackageId must end with the {Section}:Compatibility:DataStandardVersion segment."
            );
            return false;
        }

        return true;
    }

    private static bool ValidatePackageVersion(List<string> failures, string entryKey, string? packageVersion)
    {
        if (string.IsNullOrWhiteSpace(packageVersion))
        {
            failures.Add($"{entryKey}:PackageVersion is required.");
            return false;
        }

        if (!PackageVersionPattern().IsMatch(packageVersion))
        {
            failures.Add(
                $"{entryKey}:PackageVersion must be a three- or four-part numeric version with an optional prerelease suffix."
            );
            return false;
        }

        return true;
    }

    private static void ValidateRanges(List<string> failures, DmsDataStoreProvisioningOptions options)
    {
        Range(
            failures,
            nameof(options.RestoreTimeout),
            options.RestoreTimeout,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(6)
        );
        Range(
            failures,
            nameof(options.CommandTimeout),
            options.CommandTimeout,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(10)
        );
        Range(
            failures,
            nameof(options.LockAcquireTimeout),
            options.LockAcquireTimeout,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(60)
        );
        Range(
            failures,
            nameof(options.LockHeartbeatInterval),
            options.LockHeartbeatInterval,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30)
        );
        Range(
            failures,
            nameof(options.SessionTerminationTimeout),
            options.SessionTerminationTimeout,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(5)
        );
        Range(failures, nameof(options.OutcomeResolutionAttempts), options.OutcomeResolutionAttempts, 1, 10);
        Range(failures, nameof(options.CleanupAttempts), options.CleanupAttempts, 1, 10);
    }

    private static void ValidatePostgresql(List<string> failures, PostgresqlOptions? postgresql)
    {
        if (postgresql is null)
        {
            failures.Add($"{Section}:Postgresql is required.");
            return;
        }

        RequireLocalPath(failures, $"Postgresql:{nameof(postgresql.PsqlPath)}", postgresql.PsqlPath);
        if (postgresql.MinimumPsqlMajorVersion < 1)
        {
            failures.Add($"{Section}:Postgresql:MinimumPsqlMajorVersion must be at least 1.");
        }
    }

    private static void ValidateMssql(List<string> failures, MssqlOptions? mssql)
    {
        if (mssql is null)
        {
            failures.Add($"{Section}:Mssql is required.");
            return;
        }

        RequireLocalPath(failures, $"Mssql:{nameof(mssql.StagingPath)}", mssql.StagingPath);
        RequireServerPath(failures, $"Mssql:{nameof(mssql.StagingServerPath)}", mssql.StagingServerPath);

        const string ModeKey = $"{Section}:Mssql:StagingFileMode";
        if (string.IsNullOrWhiteSpace(mssql.StagingFileMode))
        {
            failures.Add($"{ModeKey} is required.");
        }
        else if (!StagingFileModePattern().IsMatch(mssql.StagingFileMode))
        {
            failures.Add($"{ModeKey} must be three or four octal digits such as 0640.");
        }
        else if (HasGroupOrOtherWriteBit(mssql.StagingFileMode))
        {
            failures.Add($"{ModeKey} must not grant write permission to group or others.");
        }
    }

    /// <summary>The group and other digits are the last two; octal bit 2 is write.</summary>
    private static bool HasGroupOrOtherWriteBit(string mode) =>
        ((mode[^2] - '0') & 2) != 0 || ((mode[^1] - '0') & 2) != 0;

    /// <summary>A path the CMS process itself opens: it must be fully qualified under the CMS host's own rules.</summary>
    private static void RequireLocalPath(List<string> failures, string key, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            failures.Add($"{Section}:{key} is required.");
        }
        else if (!Path.IsPathFullyQualified(path))
        {
            failures.Add(
                $"{Section}:{key} must be a fully qualified path on the Configuration Service host."
            );
        }
    }

    /// <summary>An optional local path: absent means the default applies; present means fully qualified on the CMS host.</summary>
    private static void RequireOmittedOrLocalPath(List<string> failures, string key, string? path)
    {
        if (path is not null && (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
        {
            failures.Add(
                $"{Section}:{key} must be omitted or a fully qualified path on the Configuration Service host."
            );
        }
    }

    /// <summary>A path another host opens (the SQL Server view of the staging directory): absolute under either convention.</summary>
    private static void RequireServerPath(List<string> failures, string key, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            failures.Add($"{Section}:{key} is required.");
        }
        else if (!IsAbsoluteOnEitherConvention(path))
        {
            failures.Add($"{Section}:{key} must be an absolute path in the database server's convention.");
        }
    }

    /// <summary>A leading <c>/</c>, a UNC prefix, or a drive letter followed by a separator, independent of the platform the validator runs on.</summary>
    internal static bool IsAbsoluteOnEitherConvention(string path) =>
        path.StartsWith('/')
        || path.StartsWith(@"\\", StringComparison.Ordinal)
        || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');

    private static void RequireOmittedOrNonBlank(List<string> failures, string key, string? value)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{key} must be omitted or non-blank.");
        }
    }

    private static void Range(
        List<string> failures,
        string property,
        TimeSpan value,
        TimeSpan minimum,
        TimeSpan maximum
    )
    {
        if (value < minimum || value > maximum)
        {
            failures.Add($"{Section}:{property} must be between {Format(minimum)} and {Format(maximum)}.");
        }
    }

    private static void Range(List<string> failures, string property, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Section}:{property} must be between {minimum} and {maximum}."
                )
            );
        }
    }

    private static string Format(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (
            int index = text.IndexOf(value, StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf(value, index + 1, StringComparison.Ordinal)
        )
        {
            count++;
        }

        return count;
    }

    // \A and \z anchor the whole string: $ alone would accept a trailing line break.
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z")]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex(@"\A\d+(\.\d+){2,3}(-[0-9A-Za-z.-]+)?\z")]
    private static partial Regex PackageVersionPattern();

    [GeneratedRegex(@"\A\d+(\.\d+)+\z")]
    private static partial Regex DataStandardVersionPattern();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex EffectiveSchemaHashPattern();

    [GeneratedRegex(@"\A0?[0-7]{3}\z")]
    private static partial Regex StagingFileModePattern();
}
