// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>Keep owning document keys outside the compact key range in provider fixtures.</summary>
public static class CompactDescriptorSeedSupport
{
    public const string PostgresqlSeparateDocumentIdsSql = """
        DO $seed$
        BEGIN
            IF COALESCE(pg_sequence_last_value(pg_get_serial_sequence('dms."Document"', 'DocumentId')::regclass), 0) < 5000000000 THEN
                PERFORM setval(pg_get_serial_sequence('dms."Document"', 'DocumentId'), 5000000000, true);
            END IF;
        END;
        $seed$;
        """;

    public const string MssqlSeparateDocumentIdsSql = """
        IF IDENT_CURRENT(N'dms.Document') < 5000000000
            DBCC CHECKIDENT (N'dms.Document', RESEED, 5000000000) WITH NO_INFOMSGS;
        """;
}

public sealed record SeededDescriptor(int DescriptorId, long DocumentId);

/// <summary>Whole-string cases shared by compact descriptor database regressions.</summary>
public sealed record CompactDescriptorUriCase(
    string ProjectName,
    string ResourceName,
    string Namespace,
    string CodeValue
)
{
    public string Uri => $"{Namespace}#{CodeValue}";

    public static IReadOnlyList<CompactDescriptorUriCase> Cases { get; } =
    [
        new("Ed-Fi", "KindDescriptor", "uri://Example.org/SchoolTypeDescriptor", "MiXeD"),
        new("Ed-Fi", "TermDescriptor", "uri://Example.org/SchoolTypeDescriptor", "MiXeD"),
        new("Sample", "KindDescriptor", "uri://Example.org/SchoolTypeDescriptor", "MiXeD"),
        new("Ed-Fi", "SchoolTypeDescriptor", "uri://example.org/a#b", "c"),
        new("Ed-Fi", "SchoolTypeDescriptor", "uri://example.org/a", "b#c"),
        new("Ed-Fi", "SchoolTypeDescriptor", "uri://example.org/a ", "c"),
        new("Ed-Fi", "SchoolTypeDescriptor", "uri://example.org/a", "c"),
    ];
}
