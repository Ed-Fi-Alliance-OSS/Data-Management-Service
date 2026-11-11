// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>What readiness found of <c>edfi_dms_enqueue_owner</c> (D-16), judged as the delivered DO block judges it.</summary>
internal enum PostgresqlEnqueueOwnerState
{
    /// <summary>The role does not exist; the first replay creates it, which needs <c>CREATEROLE</c>.</summary>
    Absent,

    /// <summary>The role exists in the locked-down state the template expects.</summary>
    LockedDown,

    /// <summary>The role may log in, inherits, or holds an elevated attribute.</summary>
    UnsafeAttributes,

    /// <summary>The role holds an outgoing membership with an admin, inherit, or set option.</summary>
    UnsafeMemberships,
}

/// <summary>The server facts readiness judges, read on one maintenance connection.</summary>
/// <param name="ServerVersionNum"><c>server_version_num</c>.</param>
/// <param name="Template0Encoding">The encoding of <c>template0</c>, which every staging database copies, or null when it does not exist.</param>
/// <param name="Privileges">The admin role's attributes and predefined-role privileges, or null when the server is below the floor and they were not read.</param>
/// <param name="GssAuthenticated">Whether the server authenticated the reading session through GSSAPI (<c>pg_stat_gssapi</c>).</param>
internal sealed record PostgresqlEnvironmentFacts(
    int ServerVersionNum,
    string? Template0Encoding,
    PostgresqlAdminPrivileges? Privileges,
    bool GssAuthenticated = false
);

/// <summary>
/// The admin role (<c>current_user</c>) as readiness sees it. The predefined-role privileges are read with
/// <c>pg_has_role(…, 'USAGE')</c>, the privileges the role actually exercises (membership with
/// inheritance), which is what the server checks when it signals a backend or shows another role's
/// <c>backend_start</c>.
/// </summary>
internal sealed record PostgresqlAdminPrivileges(
    bool IsSuperuser,
    bool CanCreateDatabase,
    bool CanCreateRole,
    bool HasSignalBackend,
    bool HasReadAllStats,
    PostgresqlEnqueueOwnerState EnqueueOwner
);

/// <summary>
/// The PostgreSQL environmental validation (D-9, D-16, D-22): the reads and the rules
/// that turn their facts into readiness reasons. Every reason carries a fixed probe name and a diagnostic
/// with a fixed operation; nothing read from the server other than a SQLSTATE reaches a reason.
/// </summary>
internal static class PostgresqlEnvironment
{
    /// <summary>Connecting to the maintenance database, or reading the facts on it.</summary>
    public const string TargetServerProbe = "TargetServer";

    /// <summary>D-9: <c>server_version_num &gt;= 180000</c>.</summary>
    public const string ServerVersionProbe = "ServerVersion";

    /// <summary>D-9, D-5: <c>template0</c>, the template of every staging database, is <c>UTF8</c>; <c>template1</c> is not read.</summary>
    public const string Template0EncodingProbe = "Template0Encoding";

    /// <summary>
    /// The artifact's replay requires a superuser: it ends with <c>ALTER FUNCTION … OWNER TO
    /// edfi_dms_enqueue_owner</c>, which a non-superuser may run only when the new owner holds <c>CREATE</c> on
    /// the function's schema, and the artifact grants that owner <c>USAGE</c> on <c>dms</c> only (the security
    /// gate refuses any other grant to it), so no grant to the administrator can make the replay succeed.
    /// </summary>
    public const string SuperuserProbe = "Superuser";

    /// <summary>CREATE, RENAME and DROP DATABASE: <c>rolcreatedb</c> or superuser.</summary>
    public const string CreateDatabasePrivilegeProbe = "CreateDatabasePrivilege";

    /// <summary>D-22 termination: <c>pg_signal_backend</c> or superuser.</summary>
    public const string SignalBackendPrivilegeProbe = "SignalBackendPrivilege";

    /// <summary>
    /// D-22 identity: <c>pg_read_all_stats</c> or superuser, without which <c>backend_start</c> of another
    /// role's session is hidden and no session can be proven terminated.
    /// </summary>
    public const string ReadAllStatsPrivilegeProbe = "ReadAllStatsPrivilege";

    /// <summary>D-16: <c>edfi_dms_enqueue_owner</c> present and locked down, or creatable.</summary>
    public const string EnqueueOwnerProbe = "EnqueueOwnerRole";

    /// <summary>D-9: <c>psql --version</c> runs, parses, and meets the floor.</summary>
    public const string PsqlProbe = "PsqlVersion";

    /// <summary>The probe lock is acquired, holds through one heartbeat, and is released.</summary>
    public const string TargetLockProbe = "TargetLock";

    /// <summary>
    /// Neither client authenticates with a client certificate: <c>psql</c> is given <c>sslcertmode=disable</c>, so a
    /// configuration in which Npgsql would send one is refused (<see cref="EvaluateClientCertificate"/>).
    /// </summary>
    public const string ClientCertificateProbe = "ClientCertificate";

    /// <summary>
    /// Without a configured <c>Password</c>, Npgsql would not find one in this process's environment either: <c>psql</c>
    /// receives only the configured password, so a password Npgsql found elsewhere is refused
    /// (<see cref="EvaluateAmbientPassword"/>).
    /// </summary>
    public const string AmbientPasswordProbe = "AmbientPassword";

    /// <summary>
    /// The server did not authenticate the readiness session to the maintenance database through GSSAPI: that login
    /// comes from this process's Kerberos credentials (<c>KRB5CCNAME</c>, <c>KRB5_CONFIG</c>, a client keytab), not
    /// from the connection string, and <c>psql</c> runs with a cleared environment that does not carry them.
    /// <c>pg_hba.conf</c> is matched per database, so this proves nothing about the staging database <c>psql</c>
    /// connects to; every replay checks that database itself first
    /// (<see cref="PostgresqlDmsTargetEngine.RestoreGssAuthenticationStep"/>).
    /// </summary>
    public const string GssAuthenticationProbe = "GssAuthentication";

    /// <summary>The lowest <c>server_version_num</c> accepted (D-9).</summary>
    public const int MinimumServerVersionNum = 180000;

    public const string RequiredEncoding = "UTF8";

    public const string ConnectOperation = TargetServerProbe + ".Connect";
    public const string ReadFactsOperation = TargetServerProbe + ".ReadFacts";
    public const string ArtifactOwnershipOperation = SuperuserProbe + ".ArtifactOwnership";
    public const string EnqueueOwnerMissingOperation = EnqueueOwnerProbe + ".MissingWithoutCreateRole";
    public const string EnqueueOwnerAttributesOperation = EnqueueOwnerProbe + ".Attributes";
    public const string EnqueueOwnerMembershipsOperation = EnqueueOwnerProbe + ".Memberships";
    public const string PsqlUnparsableOperation = PsqlVersionProbe.ProbeStep + ".Unparsable";
    public const string PsqlBelowFloorOperation = PsqlVersionProbe.ProbeStep + ".BelowFloor";
    public const string LockHeartbeatOperation = TargetLockProbe + ".Heartbeat";

    /// <summary>Whether the server authenticated the reading session itself through GSSAPI.</summary>
    private const string GssAuthenticatedExpression =
        "COALESCE((SELECT g.gss_authenticated FROM pg_catalog.pg_stat_gssapi g WHERE g.pid = pg_catalog.pg_backend_pid()), false)";

    private const string ServerQuery =
        "SELECT pg_catalog.current_setting('server_version_num')::int, (SELECT pg_catalog.pg_encoding_to_char(d.encoding) FROM pg_catalog.pg_database d WHERE d.datname = 'template0'), "
        + GssAuthenticatedExpression
        + ";";

    private const string GssAuthenticatedQuery = "SELECT " + GssAuthenticatedExpression + ";";

    /// <summary>
    /// The admin role and the enqueue owner in one read. The owner predicates are the delivered DO block's
    /// (<see cref="PostgresqlStagingQueries.EnsureEnqueueOwner"/>), read rather than enforced, so readiness
    /// changes nothing on the server. The membership options exist from PostgreSQL 16, so this runs only on
    /// a server that meets the floor.
    /// </summary>
    private const string PrivilegeQuery = """
        SELECT r.rolsuper, r.rolcreatedb, r.rolcreaterole,
               pg_catalog.pg_has_role(r.oid, 'pg_signal_backend', 'USAGE'),
               pg_catalog.pg_has_role(r.oid, 'pg_read_all_stats', 'USAGE'),
               o.oid IS NOT NULL,
               COALESCE(o.rolcanlogin OR o.rolinherit OR o.rolsuper OR o.rolcreatedb OR o.rolcreaterole OR o.rolreplication OR o.rolbypassrls, false),
               EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_auth_members m
                   WHERE m.member = o.oid
                     AND (m.admin_option OR m.inherit_option OR m.set_option)
               )
        FROM pg_catalog.pg_roles r
        LEFT JOIN pg_catalog.pg_roles o ON o.rolname = 'edfi_dms_enqueue_owner'
        WHERE r.rolname = current_user;
        """;

    /// <summary>The three admin privileges, in the order they are reported; superuser satisfies all of them.</summary>
    private static readonly (string Probe, Func<PostgresqlAdminPrivileges, bool> Holds)[] _privilegeRules =
    [
        (CreateDatabasePrivilegeProbe, privileges => privileges.CanCreateDatabase),
        (SignalBackendPrivilegeProbe, privileges => privileges.HasSignalBackend),
        (ReadAllStatsPrivilegeProbe, privileges => privileges.HasReadAllStats),
    ];

    /// <summary>Reads the facts on an open maintenance connection; the privileges only on a server that meets the floor.</summary>
    public static async Task<PostgresqlEnvironmentFacts> ReadFactsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        int serverVersionNum;
        string? template0Encoding;
        bool gssAuthenticated;
        await using (NpgsqlCommand command = new(ServerQuery, connection))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The server environment query returned no row.");
            }

            serverVersionNum = await reader.GetFieldValueAsync<int>(0, cancellationToken);
            template0Encoding = await reader.IsDBNullAsync(1, cancellationToken)
                ? null
                : await reader.GetFieldValueAsync<string>(1, cancellationToken);
            gssAuthenticated = await reader.GetFieldValueAsync<bool>(2, cancellationToken);
        }

        if (serverVersionNum < MinimumServerVersionNum)
        {
            return new PostgresqlEnvironmentFacts(
                serverVersionNum,
                template0Encoding,
                null,
                gssAuthenticated
            );
        }

        await using NpgsqlCommand privilegeCommand = new(PrivilegeQuery, connection);
        await using NpgsqlDataReader privileges = await privilegeCommand.ExecuteReaderAsync(
            cancellationToken
        );
        if (!await privileges.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The admin role query returned no row.");
        }

        bool ownerExists = await privileges.GetFieldValueAsync<bool>(5, cancellationToken);
        bool ownerUnsafe = await privileges.GetFieldValueAsync<bool>(6, cancellationToken);
        bool ownerMemberships = await privileges.GetFieldValueAsync<bool>(7, cancellationToken);
        PostgresqlEnqueueOwnerState owner = (ownerExists, ownerUnsafe, ownerMemberships) switch
        {
            (false, _, _) => PostgresqlEnqueueOwnerState.Absent,
            (true, true, _) => PostgresqlEnqueueOwnerState.UnsafeAttributes,
            (true, false, true) => PostgresqlEnqueueOwnerState.UnsafeMemberships,
            _ => PostgresqlEnqueueOwnerState.LockedDown,
        };
        return new PostgresqlEnvironmentFacts(
            serverVersionNum,
            template0Encoding,
            new PostgresqlAdminPrivileges(
                await privileges.GetFieldValueAsync<bool>(0, cancellationToken),
                await privileges.GetFieldValueAsync<bool>(1, cancellationToken),
                await privileges.GetFieldValueAsync<bool>(2, cancellationToken),
                await privileges.GetFieldValueAsync<bool>(3, cancellationToken),
                await privileges.GetFieldValueAsync<bool>(4, cancellationToken),
                owner
            ),
            gssAuthenticated
        );
    }

    /// <summary>Whether the server authenticated <paramref name="connection"/>'s own session through GSSAPI.</summary>
    public static async Task<bool> IsGssAuthenticatedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = new(GssAuthenticatedQuery, connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// The server rules, every failing one reported: the floors (D-9) as <c>EngineIncompatible</c>; an
    /// administrator that is not a superuser as <c>AdminPrivilegeMissing</c> (<see cref="SuperuserProbe"/>: the
    /// artifact's ownership statements cannot replay without one); the three operation privileges as
    /// <c>AdminPrivilegeMissing</c>, each satisfied by superuser and still named, so each operation's own need
    /// is visible; the enqueue owner missing without <c>CREATEROLE</c> as <c>AdminPrivilegeMissing</c>, present
    /// but not locked down as <c>RoleStateUnsafe</c> (D-16, which superuser does not excuse, as the DO block
    /// does not); a session authenticated through GSSAPI as <c>StartupValidationFailed</c>
    /// (<see cref="GssAuthenticationProbe"/>).
    /// </summary>
    public static IReadOnlyList<DmsReadinessReason> Evaluate(PostgresqlEnvironmentFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        List<DmsReadinessReason> reasons = [];
        if (facts.ServerVersionNum < MinimumServerVersionNum)
        {
            reasons.Add(Reason(DmsProvisioningFailureCode.EngineIncompatible, ServerVersionProbe));
        }

        if (!string.Equals(facts.Template0Encoding, RequiredEncoding, StringComparison.Ordinal))
        {
            reasons.Add(Reason(DmsProvisioningFailureCode.EngineIncompatible, Template0EncodingProbe));
        }

        if (facts.GssAuthenticated)
        {
            reasons.Add(Reason(DmsProvisioningFailureCode.StartupValidationFailed, GssAuthenticationProbe));
        }

        if (facts.Privileges is not { } privileges)
        {
            return reasons;
        }

        if (!privileges.IsSuperuser)
        {
            reasons.Add(
                Reason(
                    DmsProvisioningFailureCode.AdminPrivilegeMissing,
                    SuperuserProbe,
                    ArtifactOwnershipOperation
                )
            );
            reasons.AddRange(
                _privilegeRules
                    .Where(rule => !rule.Holds(privileges))
                    .Select(rule => Reason(DmsProvisioningFailureCode.AdminPrivilegeMissing, rule.Probe))
            );
        }

        if (EnqueueOwnerReason(privileges) is { } ownerReason)
        {
            reasons.Add(ownerReason);
        }

        return reasons;
    }

    /// <summary>
    /// D-16: a missing role is fine when the admin may create it; a present one must be locked down whoever
    /// the admin is.
    /// </summary>
    private static DmsReadinessReason? EnqueueOwnerReason(PostgresqlAdminPrivileges privileges) =>
        privileges.EnqueueOwner switch
        {
            PostgresqlEnqueueOwnerState.Absent when !(privileges.IsSuperuser || privileges.CanCreateRole) =>
                Reason(
                    DmsProvisioningFailureCode.AdminPrivilegeMissing,
                    EnqueueOwnerProbe,
                    EnqueueOwnerMissingOperation
                ),
            PostgresqlEnqueueOwnerState.UnsafeAttributes => Reason(
                DmsProvisioningFailureCode.RoleStateUnsafe,
                EnqueueOwnerProbe,
                EnqueueOwnerAttributesOperation
            ),
            PostgresqlEnqueueOwnerState.UnsafeMemberships => Reason(
                DmsProvisioningFailureCode.RoleStateUnsafe,
                EnqueueOwnerProbe,
                EnqueueOwnerMembershipsOperation
            ),
            _ => null,
        };

    /// <summary>
    /// The <c>psql</c> rule: an executable that cannot run, does not exit cleanly, or prints no
    /// <c>psql (PostgreSQL)</c> version is <c>StartupValidationFailed</c>; one below the floor is
    /// <c>EngineIncompatible</c>.
    /// </summary>
    public static DmsReadinessReason? EvaluatePsql(PsqlVersionProbeResult result, int minimumMajorVersion)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result switch
        {
            PsqlVersionProbeResult.Parsed parsed when parsed.Major >= minimumMajorVersion => null,
            PsqlVersionProbeResult.Parsed => Reason(
                DmsProvisioningFailureCode.EngineIncompatible,
                PsqlProbe,
                PsqlBelowFloorOperation
            ),
            PsqlVersionProbeResult.Unparsable => Reason(
                DmsProvisioningFailureCode.StartupValidationFailed,
                PsqlProbe,
                PsqlUnparsableOperation
            ),
            PsqlVersionProbeResult.Failed failed => new DmsReadinessReason(
                DmsProvisioningFailureCode.StartupValidationFailed,
                PsqlProbe,
                new DmsProvisioningDiagnostic(
                    failed.ExceptionTypeChain,
                    null,
                    $"{PsqlVersionProbe.ProbeStep}.{failed.Reason}"
                )
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(result), "Unknown psql probe result."),
        };
    }

    /// <summary>
    /// The client-certificate rule: Npgsql 8 sends a client certificate whenever it negotiates TLS (any SSL mode but
    /// <c>disable</c>) and finds <c>PGSSLCERT</c> set, even empty, or, without it, <c>postgresql.crt</c> in
    /// <c>$HOME/.postgresql</c> (<c>%APPDATA%\postgresql</c> on Windows). The administrative connection string admits no
    /// certificate key, and <c>psql</c> runs with a cleared environment that cannot reproduce that choice, so the replay
    /// sends none; a server that accepted Npgsql's certificate would refuse every replay. Such a configuration is
    /// <c>StartupValidationFailed</c> instead of being <c>Ready</c>.
    /// </summary>
    /// <param name="libpqSslMode">The SSL mode in <c>libpq</c> spelling.</param>
    /// <param name="sslCertificateVariable">This process's <c>PGSSLCERT</c>, or null when unset.</param>
    /// <param name="homeDirectory">This process's <c>HOME</c> (<c>APPDATA</c> on Windows), or null when unset.</param>
    /// <param name="fileExists">Whether a file exists at a path.</param>
    public static DmsReadinessReason? EvaluateClientCertificate(
        string libpqSslMode,
        string? sslCertificateVariable,
        string? homeDirectory,
        Func<string, bool> fileExists
    )
    {
        ArgumentNullException.ThrowIfNull(libpqSslMode);
        ArgumentNullException.ThrowIfNull(fileExists);
        bool npgsqlSendsOne =
            libpqSslMode != "disable"
            && (
                sslCertificateVariable is not null
                || (homeDirectory is not null && fileExists(DefaultClientCertificatePath(homeDirectory)))
            );
        return npgsqlSendsOne
            ? Reason(DmsProvisioningFailureCode.StartupValidationFailed, ClientCertificateProbe)
            : null;
    }

    /// <summary>Npgsql's default client certificate under <paramref name="homeDirectory"/>.</summary>
    public static string DefaultClientCertificatePath(string homeDirectory) =>
        Path.Combine(
            homeDirectory,
            OperatingSystem.IsWindows() ? "postgresql" : ".postgresql",
            "postgresql.crt"
        );

    /// <summary>
    /// The ambient-password rule. Without a configured password Npgsql 8 uses <c>PGPASSWORD</c> when it is set, and
    /// otherwise the matching entry of the password file <c>PGPASSFILE</c> names, or of the default one
    /// (<c>$HOME/.pgpass</c>, <c>%APPDATA%\postgresql\pgpass.conf</c> on Windows) when it exists. <c>psql</c> runs with
    /// a cleared environment and a password file holding only the configured password, empty here, so a server that
    /// accepted Npgsql's password would refuse every replay. A configuration in which Npgsql would find a non-empty
    /// <c>PGPASSWORD</c> or an existing password file is <c>StartupValidationFailed</c>; passwordless
    /// authentication without those sources is unaffected. Nothing read is reported.
    /// </summary>
    /// <param name="configuredPassword">The connection string's password; null or empty when none is configured.</param>
    /// <param name="passwordVariable">This process's <c>PGPASSWORD</c>, or null when unset.</param>
    /// <param name="passwordFileVariable">This process's <c>PGPASSFILE</c>, or null when unset.</param>
    /// <param name="homeDirectory">This process's <c>HOME</c> (<c>APPDATA</c> on Windows), or null when unset.</param>
    /// <param name="fileExists">Whether a file exists at a path.</param>
    public static DmsReadinessReason? EvaluateAmbientPassword(
        string? configuredPassword,
        string? passwordVariable,
        string? passwordFileVariable,
        string? homeDirectory,
        Func<string, bool> fileExists
    )
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!string.IsNullOrEmpty(configuredPassword))
        {
            return null;
        }

        string? passwordFile =
            passwordFileVariable ?? (homeDirectory is null ? null : DefaultPasswordFilePath(homeDirectory));
        bool npgsqlFindsOne = passwordVariable is not null
            ? passwordVariable.Length > 0
            : passwordFile is not null && fileExists(passwordFile);
        return npgsqlFindsOne
            ? Reason(DmsProvisioningFailureCode.StartupValidationFailed, AmbientPasswordProbe)
            : null;
    }

    /// <summary>Npgsql's default password file under <paramref name="homeDirectory"/>.</summary>
    public static string DefaultPasswordFilePath(string homeDirectory) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(homeDirectory, "postgresql", "pgpass.conf")
            : Path.Combine(homeDirectory, ".pgpass");

    private static DmsReadinessReason Reason(
        DmsProvisioningFailureCode code,
        string probe,
        string? operation = null
    ) => new(code, probe, DmsProvisioningDiagnostic.ForOperation(operation ?? probe));
}
