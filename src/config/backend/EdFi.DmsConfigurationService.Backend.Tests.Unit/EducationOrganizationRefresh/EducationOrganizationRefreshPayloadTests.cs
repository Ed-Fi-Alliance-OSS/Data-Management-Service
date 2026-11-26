// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationRefresh;

/// <summary>
/// The refresh payload contract, checked through the job runtime's own registration and command
/// validator, so the persisted form and every rejection are what an enqueue or a schedule upsert would see.
/// </summary>
public class EducationOrganizationRefreshPayloadTests
{
    private sealed class NeverRunHandler : IJobHandler<EducationOrganizationRefreshPayload>
    {
        public Task ExecuteAsync(
            JobExecutionContext context,
            EducationOrganizationRefreshPayload payload,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("The payload tests never run the handler.");
    }

    private static ServiceProvider Services()
    {
        ServiceCollection services = new();
        services.AddJobHandler<
            NeverRunHandler,
            EducationOrganizationRefreshPayload,
            EducationOrganizationRefreshPayloadValidator
        >(EducationOrganizationRefreshJobTypes.Refresh, EducationOrganizationRefreshJobTypes.PayloadVersion);
        return services.BuildServiceProvider();
    }

    [TestFixture]
    public class Given_the_job_and_schedule_types
    {
        [Test]
        public void It_names_the_refresh_job_type() =>
            EducationOrganizationRefreshJobTypes.Refresh.Should().Be("EducationOrganization.Refresh");

        [Test]
        public void It_names_the_scheduled_refresh_type() =>
            EducationOrganizationRefreshJobTypes
                .ScheduledRefresh.Should()
                .Be("EducationOrganization.ScheduledRefresh");

        [Test]
        public void It_uses_payload_version_1() =>
            EducationOrganizationRefreshJobTypes.PayloadVersion.Should().Be(1);

        [Test]
        public void It_uses_valid_job_keys() =>
            new[]
            {
                EducationOrganizationRefreshJobTypes.Refresh,
                EducationOrganizationRefreshJobTypes.ScheduledRefresh,
            }
                .Should()
                .OnlyContain(key => JobKeySyntax.IsValid(key));

        [Test]
        public void It_satisfies_the_payload_contract() =>
            JobPayloadContract.Violations(typeof(EducationOrganizationRefreshPayload)).Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_valid_refresh_payload
    {
        private ServiceProvider _services = null!;

        [SetUp]
        public void Setup() => _services = Services();

        [TearDown]
        public void TearDown() => _services.Dispose();

        [TestCase("""{"scope":1,"dataStoreId":0}""", EducationOrganizationRefreshScope.All, 0)]
        [TestCase(""" { "dataStoreId": 0, "scope": 1 } """, EducationOrganizationRefreshScope.All, 0)]
        [TestCase("""{"scope":2,"dataStoreId":7}""", EducationOrganizationRefreshScope.One, 7)]
        [TestCase(
            """{"scope":2,"dataStoreId":2147483647}""",
            EducationOrganizationRefreshScope.One,
            int.MaxValue
        )]
        public void It_accepts_it_and_persists_the_serializer_form(
            string json,
            EducationOrganizationRefreshScope scope,
            int dataStoreId
        )
        {
            JobPayloadCheck check = _services
                .GetRequiredService<JobCommandValidator>()
                .Check(
                    EducationOrganizationRefreshJobTypes.Refresh,
                    EducationOrganizationRefreshJobTypes.PayloadVersion,
                    json
                );

            JobPayloadCheck.Valid valid = check.Should().BeOfType<JobPayloadCheck.Valid>().Subject;
            valid.Payload.Should().Be(new EducationOrganizationRefreshPayload(scope, dataStoreId));
            valid.PayloadJson.Should().Be($$"""{"scope":{{(int)scope}},"dataStoreId":{{dataStoreId}}}""");
        }

        [Test]
        public void It_writes_a_refresh_of_every_data_store_with_numeric_scope_1() =>
            JobPayloadSerializer
                .Serialize(new EducationOrganizationRefreshPayload(EducationOrganizationRefreshScope.All, 0))
                .Should()
                .Be(new JobPayloadWriteResult.Success("""{"scope":1,"dataStoreId":0}"""));

        [Test]
        public void It_writes_a_refresh_of_one_data_store_with_numeric_scope_2() =>
            JobPayloadSerializer
                .Serialize(new EducationOrganizationRefreshPayload(EducationOrganizationRefreshScope.One, 12))
                .Should()
                .Be(new JobPayloadWriteResult.Success("""{"scope":2,"dataStoreId":12}"""));
    }

    [TestFixture]
    public class Given_an_invalid_refresh_payload
    {
        private ServiceProvider _services = null!;

        [SetUp]
        public void Setup() => _services = Services();

        [TearDown]
        public void TearDown() => _services.Dispose();

        [TestCase("""{"scope":1,"dataStoreId":7}""", "DataStoreIdNotAllowed")]
        [TestCase("""{"scope":1,"dataStoreId":-1}""", "DataStoreIdNotAllowed")]
        [TestCase("""{"scope":2,"dataStoreId":0}""", "InvalidDataStoreId")]
        [TestCase("""{"scope":2,"dataStoreId":-3}""", "InvalidDataStoreId")]
        [TestCase("""{"scope":2,"dataStoreId":-2147483648}""", "InvalidDataStoreId")]
        [TestCase("""{"scope":0,"dataStoreId":0}""", "UndefinedEnumValue")]
        [TestCase("""{"scope":3,"dataStoreId":5}""", "UndefinedEnumValue")]
        [TestCase("""{"scope":"All","dataStoreId":0}""", "InvalidJson")]
        [TestCase("""{"scope":1,"dataStoreId":"0"}""", "InvalidJson")]
        [TestCase("""{"dataStoreId":0}""", "InvalidJson")]
        [TestCase("""{"scope":1}""", "InvalidJson")]
        [TestCase("""{"Scope":1,"DataStoreId":0}""", "InvalidJson")]
        [TestCase("""{"scope":1,"dataStoreId":0,"tenant":"Tenant1"}""", "InvalidJson")]
        [TestCase("""{"scope":1,"dataStoreId":0.5}""", "InvalidJson")]
        [TestCase("[]", "NotAnObject")]
        public void It_rejects_it_with_a_fixed_reason(string json, string reason) =>
            _services
                .GetRequiredService<JobCommandValidator>()
                .Check(
                    EducationOrganizationRefreshJobTypes.Refresh,
                    EducationOrganizationRefreshJobTypes.PayloadVersion,
                    json
                )
                .Should()
                .Be(new JobPayloadCheck.PayloadInvalid(reason));

        [Test]
        public void It_rejects_an_unregistered_payload_version() =>
            _services
                .GetRequiredService<JobCommandValidator>()
                .Check(EducationOrganizationRefreshJobTypes.Refresh, 2, """{"scope":1,"dataStoreId":0}""")
                .Should()
                .BeOfType<JobPayloadCheck.UnsupportedVersion>();
    }

    [TestFixture]
    public class Given_the_payload_validator
    {
        private readonly EducationOrganizationRefreshPayloadValidator _validator = new();

        [TestCase(EducationOrganizationRefreshScope.All, 0)]
        [TestCase(EducationOrganizationRefreshScope.One, 1)]
        [TestCase(EducationOrganizationRefreshScope.One, int.MaxValue)]
        public void It_accepts_a_consistent_payload(
            EducationOrganizationRefreshScope scope,
            int dataStoreId
        ) => _validator.Validate(new(scope, dataStoreId)).Should().BeEmpty();

        [TestCase(EducationOrganizationRefreshScope.All, 1, "DataStoreIdNotAllowed")]
        [TestCase(EducationOrganizationRefreshScope.All, int.MinValue, "DataStoreIdNotAllowed")]
        [TestCase(EducationOrganizationRefreshScope.One, 0, "InvalidDataStoreId")]
        [TestCase(EducationOrganizationRefreshScope.One, int.MinValue, "InvalidDataStoreId")]
        [TestCase((EducationOrganizationRefreshScope)0, 0, "InvalidScope")]
        [TestCase((EducationOrganizationRefreshScope)3, 5, "InvalidScope")]
        public void It_reports_one_fixed_reason(
            EducationOrganizationRefreshScope scope,
            int dataStoreId,
            string reason
        ) => _validator.Validate(new(scope, dataStoreId)).Should().Equal(reason);
    }
}
