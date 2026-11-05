// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The <c>DmsDataStoreProvisioning</c> section (spec 4.3). The feature is off unless <see cref="Enabled"/>
/// is true; <see cref="DmsDataStoreProvisioningOptionsValidator"/> enforces the static rules at startup
/// when it is. Environmental checks (reachability, privileges, files, staging visibility) belong to
/// readiness (7.3), not to this class. Nested sections and collections are nullable because
/// configuration binding preserves an explicit null; the validator reports a null section as missing
/// rather than throwing.
/// </summary>
public sealed class DmsDataStoreProvisioningOptions
{
    public const string SectionName = "DmsDataStoreProvisioning";

    public const string PostgresqlProvider = "postgresql";

    public const string MssqlProvider = "mssql";

    /// <summary>The accepted <see cref="Provider"/> tokens (D-17), matched exactly.</summary>
    public static IReadOnlyList<string> Providers { get; } = [PostgresqlProvider, MssqlProvider];

    /// <summary>Feature switch (D-1). Off by default; nothing else is validated or registered while off.</summary>
    public bool Enabled { get; set; }

    /// <summary><c>postgresql</c> or <c>mssql</c>: the target engine, independent of <c>AppSettings:Datastore</c> (AC 15).</summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Administrative connection to the target server. May contain <c>${secret:&lt;name&gt;}</c> references
    /// resolved through the registered <c>ISecretResolver</c>. Never logged. PostgreSQL values may use only
    /// the key subset in <see cref="DmsDataStoreProvisioningOptionsValidator.PostgresqlConnectionStringKeys"/> (D-10).
    /// </summary>
    public string? AdminConnectionString { get; set; }

    /// <summary>
    /// Operator-listed identity or CMS databases on the target server that may never be selected as a
    /// target. The CMS catalog database name is always added at registration time.
    /// </summary>
    public string[]? AdditionalProtectedDatabaseNames { get; set; } = [];

    /// <summary>Path of the trust policy JSON in the delivered schema (F-P5, D-4); fully qualified on the CMS host.</summary>
    public string? TrustPolicyPath { get; set; }

    /// <summary>Path of the read-only directory holding the allowlisted packages and their sibling attestations (D-3); fully qualified on the CMS host.</summary>
    public string? ArtifactDirectory { get; set; }

    /// <summary>
    /// The allowlist: exactly the two entries <c>Minimal</c> and <c>Sample</c>, keyed by wire value
    /// (case-sensitive), each naming one package id and version (AC 3).
    /// </summary>
    public Dictionary<string, TemplateAllowlistEntry?>? Templates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>What both allowlisted manifests must declare (D-20).</summary>
    public CompatibilityExpectations? Compatibility { get; set; } = new();

    /// <summary>Bound for one <c>psql</c> replay or one <c>RESTORE</c>.</summary>
    public TimeSpan RestoreTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Bound for every administrative command and catalog read.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Target-lock contention budget per attempt (D-12).</summary>
    public TimeSpan LockAcquireTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Holder-session probe cadence; a probe is skipped while a holder command is active (D-12).</summary>
    public TimeSpan LockHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Bound for proving a server session terminated (D-22).</summary>
    public TimeSpan SessionTerminationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Re-reads after an unknown outcome (6.9), each bounded by <see cref="CommandTimeout"/>.</summary>
    public int OutcomeResolutionAttempts { get; set; } = 3;

    /// <summary>Bounded cleanup retries (6.11).</summary>
    public int CleanupAttempts { get; set; } = 3;

    public PostgresqlOptions? Postgresql { get; set; } = new();

    public MssqlOptions? Mssql { get; set; } = new();

    public sealed class TemplateAllowlistEntry
    {
        public string? PackageId { get; set; }

        public string? PackageVersion { get; set; }
    }

    public sealed class CompatibilityExpectations
    {
        /// <summary>Must equal both manifests' <c>dataStandardVersion</c> and the trailing segment of both package ids.</summary>
        public string? DataStandardVersion { get; set; }

        /// <summary>The DMS deployment's expected effective schema hash, 64 lowercase hex characters (F-D2).</summary>
        public string? EffectiveSchemaHash { get; set; }

        /// <summary>The exact resource-schema set the deployment serves; must include <c>edfi</c>.</summary>
        public string[]? Projects { get; set; } = [];

        /// <summary>When set, both manifests must match.</summary>
        public string? ApiSchemaFormatVersion { get; set; }

        /// <summary>When set, both manifests must match.</summary>
        public string? RelationalMappingVersion { get; set; }
    }

    public sealed class PostgresqlOptions
    {
        /// <summary>The <c>psql</c> executable, fully qualified on the CMS host; probed at startup (D-9).</summary>
        public string? PsqlPath { get; set; } = "/usr/bin/psql";

        /// <summary>The lowest <c>psql</c> major version accepted (D-9).</summary>
        public int MinimumPsqlMajorVersion { get; set; } = 18;
    }

    public sealed class MssqlOptions
    {
        /// <summary>CMS-side staging directory, fully qualified on the CMS host, owned by the CMS process user, mode <c>2750</c> (D-18).</summary>
        public string? StagingPath { get; set; }

        /// <summary>The same directory as the SQL Server host sees it, in that host's path convention.</summary>
        public string? StagingServerPath { get; set; }

        /// <summary>Octal mode for every staged file and the probe (Unix only); no group or other write bits.</summary>
        public string? StagingFileMode { get; set; } = "0640";
    }
}
