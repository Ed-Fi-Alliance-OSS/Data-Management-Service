// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Typed write precondition contract passed from Core to backend write flows.
/// </summary>
public abstract record WritePrecondition
{
    /// <summary>
    /// No HTTP write precondition is present on the request.
    /// </summary>
    public sealed record None : WritePrecondition;

    /// <summary>
    /// The request carries an <c>If-Match</c> precondition. When <see cref="IsWildcard"/> is false,
    /// <see cref="Value"/> is the opaque tag compared against the current representation. When true,
    /// this is the RFC 9110 §13.1.1 wildcard (<c>If-Match: *</c>): the precondition succeeds whenever a
    /// current representation of the target exists and fails (412) when it does not; <see cref="Value"/>
    /// is not compared.
    /// </summary>
    public sealed record IfMatch(string Value, bool IsWildcard = false) : WritePrecondition;

    /// <summary>
    /// The request carries an <c>If-None-Match</c> precondition (RFC 9110 §13.1.2). When
    /// <see cref="IsWildcard"/> is false, <see cref="Values"/> are the opaque tags; the write proceeds
    /// unless any tag matches the current representation's state-significant projection. When true, this
    /// is the wildcard (<c>If-None-Match: *</c>): the write proceeds only if NO current representation
    /// exists, and fails (412) when one does. Uses weak comparison per tag; server-emitted tags are always
    /// strong.
    /// </summary>
    public sealed record IfNoneMatch(IReadOnlyList<string> Values, bool IsWildcard = false)
        : WritePrecondition
    {
        public IfNoneMatch(string value, bool IsWildcard = false)
            : this([value], IsWildcard) { }

        public bool Equals(IfNoneMatch? other) =>
            other is not null
            && IsWildcard == other.IsWildcard
            && Values.SequenceEqual(other.Values, StringComparer.Ordinal);

        public override int GetHashCode()
        {
            var hashCode = new HashCode();
            hashCode.Add(IsWildcard);
            foreach (var value in Values)
            {
                hashCode.Add(value, StringComparer.Ordinal);
            }

            return hashCode.ToHashCode();
        }
    }
}
