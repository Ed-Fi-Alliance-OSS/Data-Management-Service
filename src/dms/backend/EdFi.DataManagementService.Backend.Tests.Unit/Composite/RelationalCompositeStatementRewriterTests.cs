// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Composite;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Composite;

[TestFixture]
public class Given_A_Relational_Composite_Statement_Rewriter
{
    private static RelationalCompositeParameterAllocator CreateAllocator(
        IEnumerable<string>? reservedNames = null
    ) => new(reservedNames);

    [Test]
    public void It_renames_every_parameter_through_the_allocator_and_rewrites_the_sql()
    {
        var command = new RelationalCommand(
            "SELECT 1 FROM dms.\"Document\" WHERE \"DocumentId\" = @documentId AND \"Uuid\" = @uuid;",
            [new RelationalParameter("@documentId", 42L), new RelationalParameter("@uuid", "u")]
        );

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            command,
            CreateAllocator(),
            statementOrdinal: 3
        );

        rewritten.Sql.Should().Contain("@documentId_s3").And.Contain("@uuid_s3");
        rewritten.Sql.Should().NotContain("= @documentId ").And.NotContain("= @uuid;");
        rewritten
            .Parameters.Select(parameter => (parameter.Name, parameter.Value))
            .Should()
            .Equal(("@documentId_s3", (object?)42L), ("@uuid_s3", "u"));
    }

    [Test]
    public void It_preserves_the_provider_configuration_callback()
    {
        Action<DbParameter> configure = _ => { };
        var command = new RelationalCommand(
            "SELECT @ids;",
            [new RelationalParameter("@ids", new[] { Guid.Empty }, configure)]
        );

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(command, CreateAllocator(), 0);

        rewritten.Parameters.Should().ContainSingle().Which.ConfigureParameter.Should().BeSameAs(configure);
    }

    [Test]
    public void It_substitutes_a_parameter_with_a_raw_expression_and_drops_its_binding()
    {
        var command = new RelationalCommand(
            "SELECT 1 FROM edfi.\"School\" r WHERE r.\"DocumentId\" = @documentId AND r.\"Name\" = @name;",
            [new RelationalParameter("@documentId", 42L), new RelationalParameter("@name", "n")]
        );

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            command,
            CreateAllocator(),
            statementOrdinal: 1,
            new Dictionary<string, string> { ["@documentId"] = "@dms_composite_target_documentid" }
        );

        rewritten.Sql.Should().Contain("r.\"DocumentId\" = @dms_composite_target_documentid");
        rewritten.Parameters.Should().ContainSingle().Which.Name.Should().Be("@name_s1");
    }

    [Test]
    public void It_substitutes_a_token_the_command_never_declared_as_a_parameter()
    {
        var command = new RelationalCommand("SELECT 1 WHERE \"DocumentId\" = @documentId;");

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            command,
            CreateAllocator(),
            0,
            new Dictionary<string, string>
            {
                ["documentId"] =
                    "NULLIF(current_setting('dms.composite_target_documentid', true), '')::bigint",
            }
        );

        rewritten.Sql.Should().Contain("current_setting");
        rewritten.Parameters.Should().BeEmpty();
    }

    [Test]
    public void It_rejects_a_parameter_token_that_nothing_explains()
    {
        var command = new RelationalCommand(
            "SELECT @known, @unknown;",
            [new RelationalParameter("@known", 1)]
        );

        var act = () => RelationalCompositeStatementRewriter.Rewrite(command, CreateAllocator(), 0);

        act.Should().Throw<InvalidOperationException>().WithMessage("*@unknown*");
    }

    [Test]
    public void It_rejects_a_declared_parameter_the_sql_never_references()
    {
        var command = new RelationalCommand(
            "SELECT @used;",
            [new RelationalParameter("@used", 1), new RelationalParameter("@dangling", 2)]
        );

        var act = () => RelationalCompositeStatementRewriter.Rewrite(command, CreateAllocator(), 0);

        act.Should().Throw<InvalidOperationException>().WithMessage("*@dangling*");
    }

    [Test]
    public void It_does_not_rewrite_sql_server_built_in_variables()
    {
        var command = new RelationalCommand(
            "SELECT @@OPTIONS & 16384, @flag;",
            [new RelationalParameter("@flag", true)]
        );

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(command, CreateAllocator(), 0);

        rewritten.Sql.Should().Contain("@@OPTIONS & 16384");
        rewritten.Sql.Should().Contain("@flag_s0");
    }

    [Test]
    public void It_rewrites_only_executable_postgresql_parameter_tokens()
    {
        var command = new RelationalCommand(
            """
            SELECT '@string', "@quoted_identifier", $$@dollar_string$$, $tag$@tagged_string$tag$, @actual
            -- @line_comment
            /* @block_comment /* @nested_comment */ */;
            """,
            [new RelationalParameter("@actual", 1)]
        );

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(command, CreateAllocator(), 4);

        rewritten.Sql.Should().Contain("'@string'");
        rewritten.Sql.Should().Contain("\"@quoted_identifier\"");
        rewritten.Sql.Should().Contain("$$@dollar_string$$");
        rewritten.Sql.Should().Contain("$tag$@tagged_string$tag$");
        rewritten.Sql.Should().Contain("-- @line_comment");
        rewritten.Sql.Should().Contain("/* @block_comment /* @nested_comment */ */");
        rewritten.Sql.Should().Contain("@actual_s4");
        rewritten.Parameters.Should().ContainSingle().Which.Name.Should().Be("@actual_s4");
    }

    [Test]
    public void It_rewrites_only_executable_sql_server_parameter_tokens()
    {
        var command = new RelationalCommand(
            """
            SELECT '@string', "@quoted_identifier", [@bracketed]]identifier], @@OPTIONS, @actual
            -- @line_comment
            /* @block_comment */;
            """,
            [new RelationalParameter("@actual", 1)]
        );

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(command, CreateAllocator(), 5);

        rewritten.Sql.Should().Contain("'@string'");
        rewritten.Sql.Should().Contain("\"@quoted_identifier\"");
        rewritten.Sql.Should().Contain("[@bracketed]]identifier]");
        rewritten.Sql.Should().Contain("@@OPTIONS");
        rewritten.Sql.Should().Contain("-- @line_comment");
        rewritten.Sql.Should().Contain("/* @block_comment */");
        rewritten.Sql.Should().Contain("@actual_s5");
        rewritten.Parameters.Should().ContainSingle().Which.Name.Should().Be("@actual_s5");
    }

    [Test]
    public void It_rewrites_whole_tokens_so_a_prefix_name_cannot_corrupt_a_longer_one()
    {
        var command = new RelationalCommand(
            "SELECT @p1, @p10;",
            [new RelationalParameter("@p1", 1), new RelationalParameter("@p10", 10)]
        );

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(command, CreateAllocator(), 2);

        rewritten.Sql.Should().Be("SELECT @p1_s2, @p10_s2;");
    }

    [Test]
    public void It_keeps_the_allocator_names_unique_across_statements()
    {
        var allocator = CreateAllocator();
        var command = new RelationalCommand(
            "SELECT @documentId;",
            [new RelationalParameter("@documentId", 1L)]
        );

        var first = RelationalCompositeStatementRewriter.Rewrite(command, allocator, 0);
        var second = RelationalCompositeStatementRewriter.Rewrite(command, allocator, 1);

        first.Parameters[0].Name.Should().Be("@documentId_s0");
        second.Parameters[0].Name.Should().Be("@documentId_s1");
    }
}
