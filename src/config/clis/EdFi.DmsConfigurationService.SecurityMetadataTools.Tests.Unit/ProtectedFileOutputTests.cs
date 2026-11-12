// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

[TestFixture(false)]
[TestFixture(true)]
public class Given_successful_ProtectedFileOutput(bool existingOutput)
{
    private CommandWorkspace _workspace = null!;
    private const string Content = "{\n  \"name\": \"École 東京\"\n}";

    [SetUp]
    public void Setup()
    {
        _workspace = new();
        if (existingOutput)
        {
            File.WriteAllBytes(_workspace.OutputPath, CommandWorkspace.Sentinel);
        }

        ProtectedFileOutput.Write(_workspace.OutputPath, Content, force: existingOutput);
    }

    [TearDown]
    public void Teardown() => _workspace.Dispose();

    [Test]
    public void It_publishes_exact_utf8_bytes_without_a_bom_or_added_newline() =>
        File.ReadAllBytes(_workspace.OutputPath).Should().Equal(Encoding.UTF8.GetBytes(Content));

    [Test]
    public void It_leaves_only_the_complete_destination() =>
        Directory.GetFiles(_workspace.DirectoryPath).Should().Equal(_workspace.OutputPath);
}

[TestFixture("no-force", true)]
[TestFixture("missing-parent", false)]
[TestFixture("create-failure", false)]
[TestFixture("create-failure", true)]
[TestFixture("staged-write-failure", false)]
[TestFixture("staged-write-failure", true)]
[TestFixture("replacement-failure", false)]
[TestFixture("replacement-failure", true)]
[TestFixture("destination-race", false)]
public class Given_failed_ProtectedFileOutput(string failure, bool existingOutput)
{
    private CommandWorkspace _workspace = null!;
    private string _destination = string.Empty;
    private FailingFileOperations _files = null!;
    private Exception _failure = null!;

    [SetUp]
    public void Setup()
    {
        _workspace = new();
        _destination =
            failure == "missing-parent"
                ? Path.Combine(_workspace.DirectoryPath, "missing", "claims.json")
                : _workspace.OutputPath;
        if (existingOutput)
        {
            File.WriteAllBytes(_destination, CommandWorkspace.Sentinel);
        }

        _files = new(failure);
        Action write = () =>
            ProtectedFileOutput.Write(
                _destination,
                "complete document",
                force: failure != "no-force" && failure != "destination-race",
                _files
            );
        _failure = write.Should().Throw<IOException>().Which;
    }

    [TearDown]
    public void Teardown() => _workspace.Dispose();

    [Test]
    public void It_preserves_existing_destination_bytes_or_creates_no_output()
    {
        if (existingOutput || failure == "destination-race")
        {
            File.ReadAllBytes(_destination).Should().Equal(CommandWorkspace.Sentinel);
        }
        else
        {
            File.Exists(_destination).Should().BeFalse();
        }
    }

    [Test]
    public void It_leaves_no_staging_files_or_new_directories()
    {
        string[] expected = existingOutput || failure == "destination-race" ? [_destination] : [];
        Directory
            .GetFiles(_workspace.DirectoryPath, "*", SearchOption.AllDirectories)
            .Should()
            .Equal(expected);
        Directory.GetDirectories(_workspace.DirectoryPath).Should().BeEmpty();
    }

    [Test]
    public void It_exercises_the_requested_failure_boundary()
    {
        switch (failure)
        {
            case "no-force":
                _failure.Message.Should().Be("Output file already exists. Use --force to replace it.");
                _files.CreateCount.Should().Be(0);
                break;
            case "missing-parent":
                _failure.Should().BeOfType<DirectoryNotFoundException>();
                break;
            case "create-failure":
                _failure.Message.Should().Be("Injected create failure");
                break;
            case "staged-write-failure":
                _failure.Message.Should().Be("Injected write failure");
                _files.PartialBytes.Should().Equal(Encoding.UTF8.GetBytes("com"));
                _files.MoveCount.Should().Be(0);
                break;
            case "replacement-failure":
            case "destination-race":
                _files.MoveCount.Should().Be(1);
                _files.StagedBytes.Should().Equal(Encoding.UTF8.GetBytes("complete document"));
                break;
        }
    }

    private sealed class FailingFileOperations(string failure) : IProtectedFileOperations
    {
        public int CreateCount { get; private set; }
        public int MoveCount { get; private set; }
        public byte[] PartialBytes { get; private set; } = [];
        public byte[] StagedBytes { get; private set; } = [];

        public bool Exists(string path) => File.Exists(path);

        public Stream CreateNew(string path)
        {
            CreateCount++;
            if (failure == "create-failure")
            {
                throw new IOException("Injected create failure");
            }

            return failure == "staged-write-failure"
                ? new PartialWriteStream(path)
                : new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        }

        public void Move(string source, string destination, bool overwrite)
        {
            MoveCount++;
            Path.GetDirectoryName(source).Should().Be(Path.GetDirectoryName(destination));
            StagedBytes = File.ReadAllBytes(source);
            if (failure == "replacement-failure")
            {
                throw new IOException("Injected replacement failure");
            }

            if (failure == "destination-race")
            {
                File.WriteAllBytes(destination, CommandWorkspace.Sentinel);
            }

            File.Move(source, destination, overwrite);
        }

        public void Delete(string path)
        {
            if (failure == "staged-write-failure")
            {
                PartialBytes = File.ReadAllBytes(path);
            }

            File.Delete(path);
        }
    }

    private sealed class PartialWriteStream(string path) : Stream
    {
        private readonly FileStream _inner = new(path, FileMode.CreateNew, FileAccess.Write);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer[..Math.Min(3, buffer.Length)]);
            throw new IOException("Injected write failure");
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

[TestFixture]
public class Given_conversion_validation_before_ProtectedFileOutput
{
    private CommandWorkspace _workspace = null!;
    private CommandResult _result = null!;

    [SetUp]
    public async Task Setup()
    {
        _workspace = new();
        File.Copy(CommandWorkspace.Fixture("Validation", "malformed.xml"), _workspace.InputPath);
        await File.WriteAllBytesAsync(_workspace.OutputPath, CommandWorkspace.Sentinel);
        _result = await _workspace.ConvertAsync(force: false);
    }

    [TearDown]
    public void Teardown() => _workspace.Dispose();

    [Test]
    public void It_reports_input_failure_before_existing_destination_failure()
    {
        _result.ExitCode.Should().Be(1);
        _result.Error.Should().Contain("Invalid security metadata XML").And.NotContain("already exists");
        _result.Output.Should().BeEmpty();
    }

    [Test]
    public void It_preserves_the_destination_and_creates_no_staging_files()
    {
        File.ReadAllBytes(_workspace.OutputPath).Should().Equal(CommandWorkspace.Sentinel);
        Directory.GetFiles(_workspace.DirectoryPath, "*.tmp").Should().BeEmpty();
    }
}
