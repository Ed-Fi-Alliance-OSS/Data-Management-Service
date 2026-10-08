// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Identity;

/// <summary>
/// The tenant, route-qualifier, and authenticated-client context an identity operation runs under,
/// plus a per-request correlation value.
/// Namespace mapping and async job ownership use the v1 equivalence rules below. These are explicit
/// identity contract choices, consistent with DMS route matching; the identity pipeline does not
/// inherit datastore authorization or select a datastore to establish them.
/// <list type="bullet">
/// <item>
/// <see cref="Tenant"/>: <see cref="StringComparer.OrdinalIgnoreCase"/> for names; a null tenant
/// denotes single-tenant mode and equals only null, never an empty string or a named tenant.
/// </item>
/// <item>
/// Route qualifier names in <see cref="RouteQualifiers"/>: <see cref="StringComparer.OrdinalIgnoreCase"/>;
/// equivalent names represent one key.
/// </item>
/// <item>
/// Route qualifier values in <see cref="RouteQualifiers"/>: <see cref="StringComparer.OrdinalIgnoreCase"/>;
/// no trimming and no additional Unicode normalization.
/// </item>
/// <item>
/// The qualifier dictionary as a whole: the same set of names with corresponding equivalent values,
/// independent of enumeration or insertion order; a missing qualifier is different from a present one.
/// </item>
/// <item>
/// <see cref="ClientId"/>: <see cref="StringComparer.Ordinal"/>, case-sensitive and unchanged from
/// the authenticated claim.
/// </item>
/// </list>
/// DMS preserves tenant and qualifier spelling exactly as passed, applying no trimming,
/// culture-dependent casing, or Unicode normalization of its own. A provider must apply the rules
/// above itself rather than rely on raw tuple/string equality or on the comparer of an
/// arbitrary supplied dictionary, because the comparer of a caller-supplied dictionary is not
/// something a provider may rely on. Provider persistence must preserve the same equivalence, with
/// unambiguous composite-key encoding and no culture-dependent casing.
/// Equivalent contexts must select the same identity namespace. A provider may deliberately map
/// other, non-equivalent contexts to a shared namespace, but async job ownership still checks the
/// complete issuing tenant/qualifier/client context under these rules; <see cref="TraceId"/> is
/// excluded from equality and is correlation only, never an ownership, cache, or idempotency key.
/// These rules do not case-fold or rewrite UniqueIds or job tokens; those values retain their own
/// documented semantics, and DMS continues passing them through unchanged.
/// </summary>
public sealed record IdentityRequestContext
{
    /// <summary>
    /// The tenant the request belongs to, or null in single-tenant mode. Compared with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>; null equals only null, never an empty string
    /// or a named tenant.
    /// </summary>
    public string? Tenant { get; init; }

    /// <summary>
    /// The route qualifiers (for example district and school year) the request was routed through.
    /// Defaults to <c>new Dictionary&lt;string, string&gt;(StringComparer.OrdinalIgnoreCase)</c> rather
    /// than the ordinal-comparer default, because qualifier-name equality is pinned to
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> and DMS constructs the map case-insensitively,
    /// rejecting configuration whose qualifier names collide under that comparer rather than silently
    /// overwriting one. A provider must not rely on the comparer of a caller-supplied dictionary
    /// instance; it must apply the documented name, value, and set-equality rules itself regardless of
    /// which comparer the concrete dictionary instance happens to carry.
    /// </summary>
    public IReadOnlyDictionary<string, string> RouteQualifiers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The authenticated client's <c>client_id</c>. Required because every identity operation is
    /// authenticated, so there is no request shape in which it is absent. It is stable across token
    /// refresh, unlike the token's <c>jti</c>, which makes it usable as a job-ownership key. Compared
    /// with <see cref="StringComparer.Ordinal"/>, case-sensitive and unchanged from the authenticated
    /// claim.
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// A per-request correlation value. It is excluded from context equality and is never an
    /// ownership, cache, or idempotency key.
    /// </summary>
    public required string TraceId { get; init; }
}
