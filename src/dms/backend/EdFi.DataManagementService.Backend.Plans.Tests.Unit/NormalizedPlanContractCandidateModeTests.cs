// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_The_Normalized_Plan_Contract_Codec_And_Candidate_Modes
{
    /// <remarks>
    /// Encode-then-decode, not encode-decode-encode. The candidate mode is a compile input that the
    /// compiled plan does not carry, so the declaration is supplied by the caller on the way out and
    /// validated on the way back in; a decoded plan cannot re-declare it.
    /// </remarks>
    [Test]
    public void It_should_preserve_page_parameters_through_a_declared_cursor_encoding()
    {
        var plan = Compile(new PageCandidateMode.Cursor());

        var decoded = NormalizedPlanContractCodec.Decode(
            NormalizedPlanContractCodec.Encode(plan, PageCandidateModeDto.Cursor)
        );

        decoded
            .PageParametersInOrder.Select(parameter => (parameter.Role, parameter.ParameterName))
            .Should()
            .Equal(plan.PageParametersInOrder.Select(parameter => (parameter.Role, parameter.ParameterName)));
    }

    [Test]
    public void It_should_preserve_filter_only_parameters_through_a_declared_unpaged_encoding()
    {
        var plan = Compile(new PageCandidateMode.UnpagedCandidates());

        var decoded = NormalizedPlanContractCodec.Decode(
            NormalizedPlanContractCodec.Encode(plan, PageCandidateModeDto.UnpagedCandidates)
        );

        decoded
            .PageParametersInOrder.Select(parameter => parameter.Role)
            .Should()
            .AllSatisfy(role => role.Should().Be(QuerySqlParameterRole.Filter));
    }

    [Test]
    public void It_should_omit_the_candidate_mode_property_for_a_traditional_plan()
    {
        // Omission is what keeps every pre-existing traditional plan's canonical JSON, and therefore its
        // hash, byte-identical.
        var json = NormalizedPlanDtoJson.EmitCanonicalJson(
            NormalizedPlanContractCodec.Encode(Compile(new PageCandidateMode.Traditional()))
        );

        json.Should().NotContain("candidate_mode");
    }

    [Test]
    public void It_should_emit_a_canonical_cursor_candidate_mode_token()
    {
        var json = NormalizedPlanDtoJson.EmitCanonicalJson(
            NormalizedPlanContractCodec.Encode(
                Compile(new PageCandidateMode.Cursor()),
                PageCandidateModeDto.Cursor
            )
        );

        json.Should().Contain("\"candidate_mode\": \"cursor\"");
    }

    [Test]
    public void It_should_emit_a_canonical_unpaged_candidates_mode_token()
    {
        var json = NormalizedPlanDtoJson.EmitCanonicalJson(
            NormalizedPlanContractCodec.Encode(
                Compile(new PageCandidateMode.UnpagedCandidates()),
                PageCandidateModeDto.UnpagedCandidates
            )
        );

        json.Should().Contain("\"candidate_mode\": \"unpaged_candidates\"");
    }

    [Test]
    public void It_should_reject_a_cursor_plan_declared_as_traditional()
    {
        // Rejected by Encode, before any DTO exists. Canonical-JSON and hash callers never decode, so a
        // plan declared as a mode it was not compiled in must not survive long enough to be hashed.
        var act = () => NormalizedPlanContractCodec.Encode(Compile(new PageCandidateMode.Cursor()));

        act.Should()
            .Throw<ArgumentException>()
            .WithParameterName("PageParametersInOrder")
            .WithMessage("*candidate mode 'Traditional'*");
    }

    [Test]
    public void It_should_reject_total_count_sql_declared_with_a_cursor_mode()
    {
        var act = () =>
            NormalizedPlanContractCodec.Decode(
                new PageDocumentIdSqlPlanDto(
                    PageDocumentIdSql: "SELECT r.\"DocumentId\" FROM \"edfi\".\"School\" r",
                    TotalCountSql: "SELECT COUNT(1) FROM \"edfi\".\"School\" r",
                    PageParametersInOrder:
                    [
                        new QuerySqlParameterDto(
                            QuerySqlParameterRoleDto.CursorInclusiveMinimum,
                            "cursorInclusiveMinimum"
                        ),
                        new QuerySqlParameterDto(
                            QuerySqlParameterRoleDto.CursorInclusiveMaximum,
                            "cursorInclusiveMaximum"
                        ),
                        new QuerySqlParameterDto(QuerySqlParameterRoleDto.PageSize, "pageSize"),
                    ],
                    TotalCountParametersInOrder: [],
                    CandidateMode: PageCandidateModeDto.Cursor
                )
            );

        act.Should().Throw<ArgumentException>().WithMessage("*only valid for traditional paging*");
    }

    [Test]
    public void It_should_reject_total_count_sql_declared_with_an_unpaged_candidates_mode()
    {
        // The partition consumer counts the candidate relation it wraps; a count query on the candidate
        // plan itself would be a second, separately-filtered count of the same rows.
        var act = () =>
            NormalizedPlanContractCodec.Decode(
                new PageDocumentIdSqlPlanDto(
                    PageDocumentIdSql: "SELECT r.\"DocumentId\" FROM \"edfi\".\"School\" r",
                    TotalCountSql: "SELECT COUNT(1) FROM \"edfi\".\"School\" r",
                    PageParametersInOrder:
                    [
                        new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
                    ],
                    TotalCountParametersInOrder:
                    [
                        new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
                    ],
                    CandidateMode: PageCandidateModeDto.UnpagedCandidates
                )
            );

        act.Should().Throw<ArgumentException>().WithMessage("*only valid for traditional paging*");
    }

    [Test]
    public void It_should_reject_a_traditional_plan_declared_as_unpaged_candidates()
    {
        // Without the declared mode this plan's Offset/Limit roles could not be distinguished from a
        // legitimate filters-only candidate inventory.
        var act = () =>
            NormalizedPlanContractCodec.Decode(
                NormalizedPlanContractCodec.Encode(
                    Compile(new PageCandidateMode.Traditional()),
                    PageCandidateModeDto.UnpagedCandidates
                )
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithParameterName("PageParametersInOrder")
            .WithMessage("*must be filter roles only*");
    }

    [Test]
    public void It_should_reject_a_traditional_plan_that_lost_its_paging_roles()
    {
        var act = () =>
            NormalizedPlanContractCodec.Decode(
                new PageDocumentIdSqlPlanDto(
                    PageDocumentIdSql: "SELECT r.\"DocumentId\" FROM \"edfi\".\"School\" r",
                    TotalCountSql: null,
                    PageParametersInOrder:
                    [
                        new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
                    ],
                    TotalCountParametersInOrder: null
                )
            );

        act.Should().Throw<ArgumentException>().WithParameterName("PageParametersInOrder");
    }

    [Test]
    public void It_should_reject_cursor_roles_in_the_wrong_order()
    {
        var act = () =>
            NormalizedPlanContractCodec.Decode(
                new PageDocumentIdSqlPlanDto(
                    PageDocumentIdSql: "SELECT r.\"DocumentId\" FROM \"edfi\".\"School\" r",
                    TotalCountSql: null,
                    PageParametersInOrder:
                    [
                        new QuerySqlParameterDto(QuerySqlParameterRoleDto.PageSize, "pageSize"),
                        new QuerySqlParameterDto(
                            QuerySqlParameterRoleDto.CursorInclusiveMinimum,
                            "cursorMin"
                        ),
                        new QuerySqlParameterDto(
                            QuerySqlParameterRoleDto.CursorInclusiveMaximum,
                            "cursorMax"
                        ),
                    ],
                    TotalCountParametersInOrder: null,
                    CandidateMode: PageCandidateModeDto.Cursor
                )
            );

        act.Should().Throw<ArgumentException>().WithParameterName("PageParametersInOrder");
    }

    [Test]
    public void It_should_reject_a_filter_role_after_the_paging_roles()
    {
        var act = () =>
            NormalizedPlanContractCodec.Decode(
                new PageDocumentIdSqlPlanDto(
                    PageDocumentIdSql: "SELECT r.\"DocumentId\" FROM \"edfi\".\"School\" r",
                    TotalCountSql: null,
                    PageParametersInOrder:
                    [
                        new QuerySqlParameterDto(QuerySqlParameterRoleDto.Offset, "offset"),
                        new QuerySqlParameterDto(QuerySqlParameterRoleDto.Limit, "limit"),
                        new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
                    ],
                    TotalCountParametersInOrder: null
                )
            );

        act.Should().Throw<ArgumentException>().WithParameterName("PageParametersInOrder");
    }

    [Test]
    public void It_should_reject_a_mixture_of_traditional_and_cursor_roles()
    {
        var act = () =>
            NormalizedPlanContractCodec.Decode(
                new PageDocumentIdSqlPlanDto(
                    PageDocumentIdSql: "SELECT r.\"DocumentId\" FROM \"edfi\".\"School\" r",
                    TotalCountSql: null,
                    PageParametersInOrder:
                    [
                        new QuerySqlParameterDto(QuerySqlParameterRoleDto.Offset, "offset"),
                        new QuerySqlParameterDto(QuerySqlParameterRoleDto.Limit, "limit"),
                        new QuerySqlParameterDto(QuerySqlParameterRoleDto.PageSize, "pageSize"),
                    ],
                    TotalCountParametersInOrder: null
                )
            );

        act.Should().Throw<ArgumentException>().WithParameterName("PageParametersInOrder");
    }

    [Test]
    public void It_should_reject_reserved_partition_roles_in_a_page_inventory()
    {
        var act = () =>
            NormalizedPlanContractCodec.Decode(
                new PageDocumentIdSqlPlanDto(
                    PageDocumentIdSql: "SELECT r.\"DocumentId\" FROM \"edfi\".\"School\" r",
                    TotalCountSql: null,
                    PageParametersInOrder:
                    [
                        new QuerySqlParameterDto(QuerySqlParameterRoleDto.PartitionCount, "number"),
                    ],
                    TotalCountParametersInOrder: null,
                    CandidateMode: PageCandidateModeDto.UnpagedCandidates
                )
            );

        act.Should().Throw<ArgumentException>().WithParameterName("PageParametersInOrder");
    }

    private static PageDocumentIdSqlPlan Compile(PageCandidateMode mode)
    {
        return new PageDocumentIdSqlCompiler(SqlDialect.Pgsql).Compile(
            CandidateModeTestSpecs.CreateSpec(mode)
        );
    }
}
