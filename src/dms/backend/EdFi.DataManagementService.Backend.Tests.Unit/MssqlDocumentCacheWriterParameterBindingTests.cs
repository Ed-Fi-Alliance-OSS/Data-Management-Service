// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_MssqlDocumentCacheWriterParameterBinding
{
    [Test]
    public void It_binds_resource_key_metadata_parameters_as_unicode_nvarchar()
    {
        IReadOnlyDictionary<string, SqlParameter> parameters = BuildCandidateParameters();

        parameters["@projectName"].SqlDbType.Should().Be(SqlDbType.NVarChar);
        parameters["@projectName"].Size.Should().Be(256);
        parameters["@projectName"].Value.Should().Be("Ed-Fi 学");

        parameters["@resourceName"].SqlDbType.Should().Be(SqlDbType.NVarChar);
        parameters["@resourceName"].Size.Should().Be(256);
        parameters["@resourceName"].Value.Should().Be("Person ñ");

        parameters["@resourceVersion"].SqlDbType.Should().Be(SqlDbType.NVarChar);
        parameters["@resourceVersion"].Size.Should().Be(32);
        parameters["@resourceVersion"].Value.Should().Be("5.0.0-学");

        parameters["@streamEtag"].SqlDbType.Should().Be(SqlDbType.VarChar);
        parameters["@streamEtag"].Size.Should().Be(64);
        parameters["@streamEtag"].Value.Should().Be("etag-23");
    }

    private static IReadOnlyDictionary<string, SqlParameter> BuildCandidateParameters()
    {
        DocumentCacheMaterializationCandidate candidate = new(
            17,
            new DocumentUuid(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            "Ed-Fi 学",
            "Person ñ",
            "5.0.0-学",
            23,
            new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
            "etag-23",
            new JsonObject { ["value"] = "candidate" }
        );

        using SqlCommand command = new();
        typeof(MssqlDocumentCacheWriter)
            .GetMethod("AddCandidateParameters", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [command, candidate]);

        return command.Parameters.Cast<SqlParameter>().ToDictionary(parameter => parameter.ParameterName);
    }
}
