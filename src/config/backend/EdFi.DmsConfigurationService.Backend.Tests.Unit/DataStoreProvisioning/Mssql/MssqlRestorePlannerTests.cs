// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>D-11: header and file-list rules in their order, destinations carrying the operation tag, and the quoted restore statement.</summary>
public class MssqlRestorePlannerTests
{
    private const string OperationValue = "0123456789ab";
    private const string Stem = "edfi_dms_provision_stage_0123456789ab__0123456789ab";
    private const string LinuxData = "/var/opt/mssql/data/";
    private const string LinuxLog = "/var/opt/mssql/log";
    private const string ServerPath = "/var/opt/dms-staging/dms-provision-0123456789ab.bak";

    private static DmsOperationId Operation() =>
        DmsOperationId.TryParse(OperationValue, out DmsOperationId operation)
            ? operation
            : throw new InvalidOperationException("The fixed operation id did not parse.");

    private static DmsDatabaseName Name(string value) =>
        DmsDatabaseName.TryCreate(value, out DmsDatabaseName name)
            ? name
            : throw new InvalidOperationException("The name does not satisfy the grammar.");

    private static readonly DmsDatabaseName _target = Name("EdFi_Target");

    private static MssqlBackupHeader FullHeader(string? databaseName = "edfi_template_source") =>
        new(1, 1, databaseName);

    private static MssqlBackupFile Data(string logicalName, long fileId) => new(logicalName, "D", fileId);

    private static MssqlBackupFile Log(string logicalName, long fileId) => new(logicalName, "L", fileId);

    private static MssqlRestorePlanResult Plan(
        IReadOnlyList<MssqlBackupHeader> headers,
        IReadOnlyList<MssqlBackupFile> files,
        string dataPath = LinuxData,
        string logPath = LinuxLog
    ) =>
        MssqlRestorePlanner.Plan(
            new MssqlBackupDescription(headers, files),
            Operation(),
            _target,
            dataPath,
            logPath
        );

    private static MssqlRestorePlanResult PlanFiles(params MssqlBackupFile[] files) =>
        Plan([FullHeader()], files);

    private static MssqlBackupDefect? DefectOf(MssqlRestorePlanResult result) =>
        (result as MssqlRestorePlanResult.Refused)?.Defect;

    [TestFixture]
    public class Given_a_single_data_and_log_file_backup
    {
        private MssqlRestorePlan _plan = null!;

        [SetUp]
        public void Setup() =>
            _plan = (
                (MssqlRestorePlanResult.Planned)PlanFiles(
                    Data("edfi_template", 1),
                    Log("edfi_template_log", 2)
                )
            ).Plan;

        [Test]
        public void It_moves_the_primary_file_to_the_tagged_mdf_in_the_default_data_path() =>
            _plan.Moves[0].Should().Be(new MssqlFileMove("edfi_template", $"/var/opt/mssql/data/{Stem}.mdf"));

        [Test]
        public void It_moves_the_log_file_to_the_tagged_ldf_in_the_default_log_path() =>
            _plan
                .Moves[1]
                .Should()
                .Be(new MssqlFileMove("edfi_template_log", $"/var/opt/mssql/log/{Stem}_log.ldf"));

        [Test]
        public void It_names_the_primary_destination_as_the_instance_evidence() =>
            _plan.PrimaryDataFilePath.Should().Be($"/var/opt/mssql/data/{Stem}.mdf");

        [Test]
        public void It_restores_into_the_staging_database() =>
            _plan.Staging.Value.Should().Be("edfi_dms_provision_stage_0123456789ab");

        [Test]
        public void It_builds_the_restore_statement_without_replace() =>
            _plan
                .RestoreStatement(ServerPath)
                .Should()
                .Be(
                    $"RESTORE DATABASE [edfi_dms_provision_stage_0123456789ab] FROM DISK = N'{ServerPath}' WITH MOVE N'edfi_template' TO N'/var/opt/mssql/data/{Stem}.mdf', MOVE N'edfi_template_log' TO N'/var/opt/mssql/log/{Stem}_log.ldf', STATS = 10;"
                );
    }

    [TestFixture]
    public class Given_a_multi_file_backup_on_a_windows_server
    {
        private IReadOnlyList<MssqlFileMove> _moves = [];

        [SetUp]
        public void Setup() =>
            _moves = (
                (MssqlRestorePlanResult.Planned)Plan(
                    [FullHeader()],
                    [Data("primary", 1), Log("main_log", 2), Data("Extra_01", 3), Log("second_log", 4)],
                    @"C:\SQL\DATA\",
                    @"\\logserver\sqllogs"
                )
            )
                .Plan
                .Moves;

        [Test]
        public void It_keeps_the_file_list_order() =>
            _moves
                .Select(move => move.LogicalName)
                .Should()
                .Equal("primary", "main_log", "Extra_01", "second_log");

        [Test]
        public void It_places_the_primary_file_with_the_windows_separator() =>
            _moves[0].PhysicalPath.Should().Be($@"C:\SQL\DATA\{Stem}.mdf");

        [Test]
        public void It_names_the_first_log_file_log() =>
            _moves[1].PhysicalPath.Should().Be($@"\\logserver\sqllogs\{Stem}_log.ldf");

        [Test]
        public void It_suffixes_another_data_file_with_its_logical_name() =>
            _moves[2].PhysicalPath.Should().Be($@"C:\SQL\DATA\{Stem}_Extra_01.ndf");

        [Test]
        public void It_suffixes_another_log_file_with_its_logical_name() =>
            _moves[3].PhysicalPath.Should().Be($@"\\logserver\sqllogs\{Stem}_second_log.ldf");
    }

    [TestFixture]
    public class Given_a_file_list_whose_primary_file_is_not_listed_first
    {
        private IReadOnlyList<MssqlFileMove> _moves = [];

        [SetUp]
        public void Setup() =>
            _moves = (
                (MssqlRestorePlanResult.Planned)PlanFiles(Data("extra", 3), Data("primary", 1), Log("l", 2))
            )
                .Plan
                .Moves;

        [Test]
        public void It_gives_the_mdf_name_to_file_one() =>
            _moves[1].Should().Be(new MssqlFileMove("primary", $"/var/opt/mssql/data/{Stem}.mdf"));

        [Test]
        public void It_gives_an_ndf_name_to_the_file_listed_first() =>
            _moves[0].Should().Be(new MssqlFileMove("extra", $"/var/opt/mssql/data/{Stem}_extra.ndf"));
    }

    [TestFixture]
    public class Given_header_vectors
    {
        private readonly Dictionary<string, MssqlBackupDefect?> _defects = [];

        [SetUp]
        public void Setup()
        {
            MssqlBackupFile[] files = [Data("d", 1), Log("l", 2)];
            _defects.Clear();
            _defects["none"] = DefectOf(Plan([], files));
            _defects["two sets"] = DefectOf(Plan([FullHeader(), FullHeader()], files));
            _defects["differential"] = DefectOf(Plan([new MssqlBackupHeader(5, 1, "source")], files));
            _defects["log backup"] = DefectOf(Plan([new MssqlBackupHeader(2, 1, "source")], files));
            _defects["null type"] = DefectOf(Plan([new MssqlBackupHeader(null, 1, "source")], files));
            _defects["position 2"] = DefectOf(Plan([new MssqlBackupHeader(1, 2, "source")], files));
            _defects["recorded null"] = DefectOf(Plan([FullHeader(null)], files));
            _defects["recorded empty"] = DefectOf(Plan([FullHeader("")], files));
            _defects["recorded staging"] = DefectOf(
                Plan([FullHeader("EDFI_DMS_PROVISION_STAGE_0123456789AB")], files)
            );
            _defects["recorded target"] = DefectOf(Plan([FullHeader("edfi_target")], files));
            _defects["recorded other"] = DefectOf(Plan([FullHeader("EdFi_Target_2")], files));
        }

        [Test]
        public void It_refuses_a_header_with_no_backup_set() =>
            _defects["none"].Should().Be(MssqlBackupDefect.BackupSetCount);

        [Test]
        public void It_refuses_a_header_with_two_backup_sets() =>
            _defects["two sets"].Should().Be(MssqlBackupDefect.BackupSetCount);

        [Test]
        public void It_refuses_a_differential_backup() =>
            _defects["differential"].Should().Be(MssqlBackupDefect.NotFullDatabaseBackup);

        [Test]
        public void It_refuses_a_log_backup() =>
            _defects["log backup"].Should().Be(MssqlBackupDefect.NotFullDatabaseBackup);

        [Test]
        public void It_refuses_a_missing_backup_type() =>
            _defects["null type"].Should().Be(MssqlBackupDefect.NotFullDatabaseBackup);

        [Test]
        public void It_refuses_a_set_at_another_position() =>
            _defects["position 2"].Should().Be(MssqlBackupDefect.PositionNotOne);

        [Test]
        public void It_refuses_a_missing_recorded_name() =>
            _defects["recorded null"].Should().Be(MssqlBackupDefect.RecordedNameMissing);

        [Test]
        public void It_refuses_an_empty_recorded_name() =>
            _defects["recorded empty"].Should().Be(MssqlBackupDefect.RecordedNameMissing);

        [Test]
        public void It_refuses_the_staging_name_ignoring_case() =>
            _defects["recorded staging"].Should().Be(MssqlBackupDefect.RecordedNameIsStaging);

        [Test]
        public void It_refuses_the_target_name_ignoring_case() =>
            _defects["recorded target"].Should().Be(MssqlBackupDefect.RecordedNameIsTarget);

        [Test]
        public void It_accepts_a_name_that_only_starts_with_the_target_name() =>
            _defects["recorded other"].Should().BeNull();
    }

    [TestFixture]
    public class Given_file_type_and_count_vectors
    {
        private readonly Dictionary<string, MssqlBackupDefect?> _defects = [];

        [SetUp]
        public void Setup()
        {
            _defects.Clear();
            _defects["filestream"] = DefectOf(
                PlanFiles(Data("d", 1), Log("l", 2), new MssqlBackupFile("fs", "S", 65537))
            );
            _defects["full-text"] = DefectOf(
                PlanFiles(Data("d", 1), Log("l", 2), new MssqlBackupFile("ft", "F", 3))
            );
            _defects["lowercase d"] = DefectOf(PlanFiles(new MssqlBackupFile("d", "d", 1), Log("l", 2)));
            _defects["null type"] = DefectOf(
                PlanFiles(Data("d", 1), Log("l", 2), new MssqlBackupFile("x", null, 3))
            );
            _defects["no data"] = DefectOf(PlanFiles(Log("l", 2)));
            _defects["no log"] = DefectOf(PlanFiles(Data("d", 1)));
            _defects["empty"] = DefectOf(PlanFiles());
            _defects["16 files"] = DefectOf(PlanFiles(Files(14)));
            _defects["17 files"] = DefectOf(PlanFiles(Files(15)));
            _defects["no primary"] = DefectOf(PlanFiles(Data("d", 3), Log("l", 2)));
            _defects["primary is a log"] = DefectOf(PlanFiles(Log("l", 1), Data("d", 3)));
            _defects["17 files with filestream"] = DefectOf(
                PlanFiles([.. Files(14), new MssqlBackupFile("fs", "S", 99)])
            );
            _defects["17 files with bad name"] = DefectOf(PlanFiles([.. Files(14), Data("bad-name", 99)]));
        }

        /// <summary>The primary data file, one log file, and <paramref name="extraData"/> more data files.</summary>
        private static MssqlBackupFile[] Files(int extraData) =>
            [
                Data("d", 1),
                Log("l", 2),
                .. Enumerable.Range(0, extraData).Select(index => Data($"extra{index}", index + 3)),
            ];

        [Test]
        public void It_refuses_a_filestream_file() =>
            _defects["filestream"].Should().Be(MssqlBackupDefect.UnsupportedFileType);

        [Test]
        public void It_refuses_a_full_text_file() =>
            _defects["full-text"].Should().Be(MssqlBackupDefect.UnsupportedFileType);

        [Test]
        public void It_compares_the_type_exactly() =>
            _defects["lowercase d"].Should().Be(MssqlBackupDefect.UnsupportedFileType);

        [Test]
        public void It_refuses_a_missing_type() =>
            _defects["null type"].Should().Be(MssqlBackupDefect.UnsupportedFileType);

        [Test]
        public void It_refuses_a_backup_without_a_data_file() =>
            _defects["no data"].Should().Be(MssqlBackupDefect.NoDataFile);

        [Test]
        public void It_refuses_a_backup_without_a_log_file() =>
            _defects["no log"].Should().Be(MssqlBackupDefect.NoLogFile);

        [Test]
        public void It_refuses_an_empty_file_list() =>
            _defects["empty"].Should().Be(MssqlBackupDefect.NoDataFile);

        [Test]
        public void It_accepts_sixteen_files() => _defects["16 files"].Should().BeNull();

        [Test]
        public void It_refuses_seventeen_files() =>
            _defects["17 files"].Should().Be(MssqlBackupDefect.TooManyFiles);

        [Test]
        public void It_refuses_a_backup_whose_file_one_is_not_a_data_file() =>
            _defects["primary is a log"].Should().Be(MssqlBackupDefect.PrimaryFileMissing);

        [Test]
        public void It_refuses_a_backup_without_file_one() =>
            _defects["no primary"].Should().Be(MssqlBackupDefect.PrimaryFileMissing);

        [Test]
        public void It_reports_the_type_rule_before_the_count_rule() =>
            _defects["17 files with filestream"].Should().Be(MssqlBackupDefect.UnsupportedFileType);

        [Test]
        public void It_reports_the_count_rule_before_the_name_rule() =>
            _defects["17 files with bad name"].Should().Be(MssqlBackupDefect.TooManyFiles);
    }

    [TestFixture]
    public class Given_logical_name_vectors
    {
        private readonly Dictionary<string, MssqlBackupDefect?> _defects = [];

        private static readonly string[] _invalid =
        [
            "",
            "a-b",
            "a b",
            "a.b",
            "it's",
            "a]b",
            "..",
            "a/b",
            @"a\b",
            "caf\u00e9",
            "a\n",
            "\u0661",
        ];

        private static readonly string[] _valid = ["a", "A_9", "_", "Ed_Fi_Template_01", "0"];

        [SetUp]
        public void Setup()
        {
            _defects.Clear();
            foreach (string name in _invalid.Concat(_valid))
            {
                // Each name is tested on a data file that is not the primary, a log file that is not the first, and file one.
                _defects["extra data " + name] = DefectOf(
                    PlanFiles(Data("d", 1), Log("l", 2), Data(name, 3))
                );
                _defects["extra log " + name] = DefectOf(PlanFiles(Data("d", 1), Log("l", 2), Log(name, 3)));
                _defects["primary " + name] = DefectOf(PlanFiles(Data(name, 1), Log("l", 2)));
            }

            _defects["null"] = DefectOf(
                PlanFiles(Data("d", 1), Log("l", 2), new MssqlBackupFile(null, "D", 3))
            );
        }

        [Test]
        public void It_refuses_every_invalid_name_on_every_file() =>
            _invalid
                .SelectMany(name => new[] { "extra data " + name, "extra log " + name, "primary " + name })
                .Select(key => _defects[key])
                .Should()
                .AllBeEquivalentTo(MssqlBackupDefect.LogicalNameInvalid);

        [Test]
        public void It_accepts_every_valid_name_on_every_file() =>
            _valid
                .SelectMany(name => new[] { "extra data " + name, "extra log " + name, "primary " + name })
                .Select(key => _defects[key])
                .Should()
                .AllSatisfy(defect => defect.Should().BeNull());

        [Test]
        public void It_refuses_a_missing_name() =>
            _defects["null"].Should().Be(MssqlBackupDefect.LogicalNameInvalid);
    }

    [TestFixture]
    public class Given_files_that_would_share_a_destination
    {
        private MssqlBackupDefect? _secondLogNamedLog;
        private MssqlBackupDefect? _dataNamesDifferingInCase;
        private MssqlBackupDefect? _dataAndLogOneDirectory;

        [SetUp]
        public void Setup()
        {
            _secondLogNamedLog = DefectOf(PlanFiles(Data("d", 1), Log("main_log", 2), Log("log", 3)));
            _dataNamesDifferingInCase = DefectOf(
                PlanFiles(Data("d", 1), Log("l", 2), Data("Extra", 3), Data("extra", 4))
            );
            _dataAndLogOneDirectory = DefectOf(
                Plan(
                    [FullHeader()],
                    [Data("d", 1), Log("l", 2), Data("x", 3), Log("x", 4)],
                    LinuxData,
                    LinuxData
                )
            );
        }

        [Test]
        public void It_refuses_a_second_log_whose_name_gives_the_first_logs_destination() =>
            _secondLogNamedLog.Should().Be(MssqlBackupDefect.PhysicalNameCollision);

        [Test]
        public void It_refuses_data_files_whose_names_differ_only_in_case() =>
            _dataNamesDifferingInCase.Should().Be(MssqlBackupDefect.PhysicalNameCollision);

        [Test]
        public void It_accepts_one_logical_name_for_a_data_and_a_log_file_by_extension() =>
            _dataAndLogOneDirectory.Should().BeNull();
    }

    [TestFixture]
    public class Given_paths_and_names_with_quotes
    {
        private string _restore = "";
        private string _header = "";
        private string _fileList = "";
        private MssqlRestorePlanResult.Refused _refused = null!;

        [SetUp]
        public void Setup()
        {
            const string QuotedServerPath = "/srv/o'brien/dms-provision-0123456789ab.bak";
            _restore = (
                (MssqlRestorePlanResult.Planned)Plan(
                    [FullHeader()],
                    [Data("d", 1), Log("l", 2)],
                    "/data/it's/",
                    "/log/'"
                )
            ).Plan.RestoreStatement(QuotedServerPath);
            _header = MssqlRestorePlanner.HeaderOnlyStatement(QuotedServerPath);
            _fileList = MssqlRestorePlanner.FileListOnlyStatement(QuotedServerPath);
            _refused = (MssqlRestorePlanResult.Refused)PlanFiles(Data("d", 1));
        }

        [Test]
        public void It_doubles_quotes_in_every_restore_literal() =>
            _restore
                .Should()
                .Be(
                    $"RESTORE DATABASE [edfi_dms_provision_stage_0123456789ab] FROM DISK = N'/srv/o''brien/dms-provision-0123456789ab.bak' WITH MOVE N'd' TO N'/data/it''s/{Stem}.mdf', MOVE N'l' TO N'/log/''/{Stem}_log.ldf', STATS = 10;"
                );

        [Test]
        public void It_doubles_quotes_in_the_header_statement() =>
            _header
                .Should()
                .Be("RESTORE HEADERONLY FROM DISK = N'/srv/o''brien/dms-provision-0123456789ab.bak';");

        [Test]
        public void It_doubles_quotes_in_the_file_list_statement() =>
            _fileList
                .Should()
                .Be("RESTORE FILELISTONLY FROM DISK = N'/srv/o''brien/dms-provision-0123456789ab.bak';");

        [Test]
        public void It_names_only_the_rule_in_a_refusal() =>
            _refused.Diagnostic.Operation.Should().Be("PlanRestore.NoLogFile");
    }
}
