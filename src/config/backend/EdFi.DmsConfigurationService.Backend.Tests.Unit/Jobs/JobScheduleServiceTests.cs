// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public class JobScheduleServiceTests
{
    private const string ScheduleType = "DataStore.NightlyRefresh";

    private static JobScheduleUpsertCommand Command(
        string payloadJson,
        string jobType = JobServices.RefreshJobType,
        short payloadVersion = 1,
        string scheduleType = ScheduleType,
        int intervalMinutes = 1_440
    ) =>
        new(
            scheduleType,
            jobType,
            payloadVersion,
            payloadJson,
            intervalMinutes,
            RunFirstOccurrenceImmediately: true
        );

    [TestFixture]
    public class Given_a_valid_schedule_command
    {
        private ServiceProvider _provider = null!;
        private RecordingScheduleRepository _repository = null!;
        private JobScheduleServiceResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = JobServices.WithRefreshHandler();
            _repository = _provider.GetRequiredService<RecordingScheduleRepository>();
            _result = await _provider
                .GetRequiredService<IJobScheduleService>()
                .UpsertAsync(
                    Command("""{ "dataStoreId": 7, "dataStoreCode": "north-1" }""", payloadVersion: 2),
                    CancellationToken.None
                );
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_returns_the_schedule_id() =>
            _result.Should().Be(new JobScheduleServiceResult.Success(42));

        [Test]
        public void It_upserts_the_serializer_output_and_keeps_the_schedule_values() =>
            _repository
                .Upserted.Should()
                .ContainSingle()
                .Which.Should()
                .Be(Command("""{"dataStoreCode":"north-1","dataStoreId":7}""", payloadVersion: 2));
    }

    [TestFixture(
        "unknown type",
        "DataStore.Unknown",
        1,
        """{"dataStoreCode":"a","dataStoreId":1}""",
        "UnsupportedType",
        ""
    )]
    [TestFixture(
        "unsupported version",
        JobServices.RefreshJobType,
        3,
        """{"dataStoreCode":"a","dataStoreId":1}""",
        "UnsupportedVersion",
        ""
    )]
    [TestFixture(
        "unexpected member",
        JobServices.RefreshJobType,
        1,
        """{"dataStoreCode":"a","dataStoreId":1,"password":"hunter2"}""",
        "PayloadInvalid",
        "InvalidJson"
    )]
    [TestFixture(
        "secret in an identifier",
        JobServices.RefreshJobType,
        1,
        """{"dataStoreCode":"Server=db;Password=hunter2","dataStoreId":1}""",
        "PayloadInvalid",
        "InvalidIdentifier"
    )]
    [TestFixture(
        "malformed JSON",
        JobServices.RefreshJobType,
        1,
        """{"dataStoreCode":"a","dataStoreId":1,}""",
        "PayloadInvalid",
        "InvalidJson"
    )]
    [TestFixture(
        "validator failure",
        JobServices.RefreshJobType,
        1,
        """{"dataStoreCode":"a","dataStoreId":-4}""",
        "PayloadInvalid",
        RefreshValidator.DataStoreIdOutOfRange
    )]
    public class Given_a_schedule_command_the_checks_reject(
        string label,
        string jobType,
        int payloadVersion,
        string payloadJson,
        string expected,
        string reasonCode
    )
    {
        private ServiceProvider _provider = null!;
        private RecordingScheduleRepository _repository = null!;
        private JobScheduleServiceResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = JobServices.WithRefreshHandler();
            _repository = _provider.GetRequiredService<RecordingScheduleRepository>();
            _result = await _provider
                .GetRequiredService<IJobScheduleService>()
                .UpsertAsync(Command(payloadJson, jobType, (short)payloadVersion), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_reports_the_rejection() =>
            _result
                .Should()
                .Be(
                    expected switch
                    {
                        "UnsupportedType" => new JobScheduleServiceResult.FailureUnsupportedType(),
                        "UnsupportedVersion" => new JobScheduleServiceResult.FailureUnsupportedVersion(),
                        _ => new JobScheduleServiceResult.FailurePayloadInvalid(reasonCode),
                    },
                    label
                );

        [Test]
        public void It_never_reaches_the_repository() => _repository.Upserted.Should().BeEmpty(label);
    }

    [TestFixture]
    public class Given_a_repository_failure
    {
        private ServiceProvider _provider = null!;
        private readonly JobFailureDiagnostic _diagnostic = new(
            "Npgsql.PostgresException",
            "23514",
            "UpsertSchedule"
        );
        private JobScheduleServiceResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = JobServices.WithRefreshHandler();
            _provider.GetRequiredService<RecordingScheduleRepository>().Result =
                new JobScheduleUpsertResult.FailureUnknown(_diagnostic);
            _result = await _provider
                .GetRequiredService<IJobScheduleService>()
                .UpsertAsync(Command("""{"dataStoreCode":"a","dataStoreId":1}"""), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_returns_the_repository_failure() =>
            _result.Should().Be(new JobScheduleServiceResult.FailureUnknown(_diagnostic));
    }

    [TestFixture]
    public class Given_a_malformed_schedule_type_or_interval
    {
        private ServiceProvider _provider = null!;
        private RecordingScheduleRepository _repository = null!;
        private readonly Dictionary<string, Exception?> _thrown = [];

        [SetUp]
        public async Task Setup()
        {
            _thrown.Clear();
            _provider = JobServices.WithRefreshHandler();
            _repository = _provider.GetRequiredService<RecordingScheduleRepository>();
            IJobScheduleService service = _provider.GetRequiredService<IJobScheduleService>();
            const string payload = """{"dataStoreCode":"a","dataStoreId":1}""";
            _thrown["schedule type"] = await ThrownByAsync(() =>
                service.UpsertAsync(Command(payload, scheduleType: "Nightly Refresh"), CancellationToken.None)
            );
            _thrown["zero interval"] = await ThrownByAsync(() =>
                service.UpsertAsync(Command(payload, intervalMinutes: 0), CancellationToken.None)
            );
            _thrown["interval over 366 days"] = await ThrownByAsync(() =>
                service.UpsertAsync(Command(payload, intervalMinutes: 527_041), CancellationToken.None)
            );
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_rejects_each_as_a_programming_error()
        {
            _thrown["schedule type"].Should().BeOfType<ArgumentException>();
            _thrown["zero interval"].Should().BeOfType<ArgumentOutOfRangeException>();
            _thrown["interval over 366 days"].Should().BeOfType<ArgumentOutOfRangeException>();
        }

        [Test]
        public void It_never_reaches_the_repository() => _repository.Upserted.Should().BeEmpty();

        private static async Task<Exception?> ThrownByAsync(Func<Task> operation)
        {
            try
            {
                await operation();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }
}
