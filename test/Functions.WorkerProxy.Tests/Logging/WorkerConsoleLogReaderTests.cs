// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Functions.WorkerProxy.Logging;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Azure.Functions.WorkerProxy.Tests.Logging;

public class WorkerConsoleLogReaderTests
{
    [Fact]
    public async Task Disabled_DoesNotReadRecords()
    {
        using TestDirectory directory = new();
        string path = directory.CreateLogFile("worker.log", Cri("disabled-record"));
        Mock<IWorkerSystemLogSink> sink = CreateSink();
        using WorkerConsoleLogReader reader = CreateReader(directory, enabled: false, sink.Object);

        await reader.PollAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        sink.Verify(instance => instance.TryEmit(It.IsAny<WorkerSystemLog>()), Times.Never());
        Assert.Empty(Directory.GetFiles(directory.CheckpointPath));
    }

    [Fact]
    public async Task AppendedRecords_AreReadWithoutRestartingAndCustomerOutputIsIgnored()
    {
        using TestDirectory directory = new();
        string path = directory.CreateLogFile("worker.log", Cri("first-record"));
        List<WorkerSystemLog> emitted = [];
        Mock<IWorkerSystemLogSink> sink = CreateSink(emitted);
        using WorkerConsoleLogReader reader = CreateReader(directory, enabled: true, sink.Object);

        await reader.PollAsync(CancellationToken.None);
        string appendedRecords =
            CriRecord("stdout", "F", "Customer output must be ignored")
            + Cri("second  record");
        await File.AppendAllTextAsync(
            path,
            appendedRecords);
        await reader.PollAsync(CancellationToken.None);

        Assert.Equal(["first-record", "second  record"], emitted.Select(log => log.Message));
    }

    [Fact]
    public async Task Restart_ResumesFromDurableCheckpoint()
    {
        using TestDirectory directory = new();
        string path = directory.CreateLogFile("worker.log", Cri("before-restart"));
        List<WorkerSystemLog> emitted = [];
        Mock<IWorkerSystemLogSink> sink = CreateSink(emitted);

        using (WorkerConsoleLogReader first = CreateReader(directory, enabled: true, sink.Object))
        {
            await first.PollAsync(CancellationToken.None);
        }

        await File.AppendAllTextAsync(path, Cri("after-restart"));

        using (WorkerConsoleLogReader second = CreateReader(directory, enabled: true, sink.Object))
        {
            await second.PollAsync(CancellationToken.None);
        }

        Assert.Equal(["before-restart", "after-restart"], emitted.Select(log => log.Message));
    }

    [Fact]
    public async Task QueueRejection_DoesNotAdvanceCheckpoint()
    {
        using TestDirectory directory = new();
        directory.CreateLogFile("worker.log", Cri("retry-record"));
        Mock<IWorkerSystemLogSink> sink = new();
        sink.SetupSequence(instance => instance.TryEmit(It.IsAny<WorkerSystemLog>()))
            .Returns(WorkerSystemLogEnqueueResult.QueueFull)
            .Returns(WorkerSystemLogEnqueueResult.Accepted);

        using (WorkerConsoleLogReader first = CreateReader(directory, enabled: true, sink.Object))
        {
            await first.PollAsync(CancellationToken.None);
        }

        using (WorkerConsoleLogReader second = CreateReader(directory, enabled: true, sink.Object))
        {
            await second.PollAsync(CancellationToken.None);
        }

        sink.Verify(
            instance => instance.TryEmit(
                It.Is<WorkerSystemLog>(log => string.Equals(log.Message, "retry-record", StringComparison.Ordinal))),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ReplacementAndTruncation_ResetCheckpoint()
    {
        using TestDirectory directory = new();
        string path = directory.CreateLogFile("worker.log", Cri("original-record-with-long-message"));
        List<WorkerSystemLog> emitted = [];
        Mock<IWorkerSystemLogSink> sink = CreateSink(emitted);
        using WorkerConsoleLogReader reader = CreateReader(directory, enabled: true, sink.Object);

        await reader.PollAsync(CancellationToken.None);
        await File.WriteAllTextAsync(path, Cri("replacement"));
        await reader.PollAsync(CancellationToken.None);

        Assert.Equal(["original-record-with-long-message", "replacement"], emitted.Select(log => log.Message));
    }

    [Fact]
    public async Task SameLengthReplacement_IsDetectedByFileIdentity()
    {
        using TestDirectory directory = new();
        string path = directory.CreateLogFile("worker.log", Cri("original-one"));
        List<WorkerSystemLog> emitted = [];
        Mock<IWorkerSystemLogSink> sink = CreateSink(emitted);
        using WorkerConsoleLogReader reader = CreateReader(directory, enabled: true, sink.Object);

        await reader.PollAsync(CancellationToken.None);
        await File.WriteAllTextAsync(path, Cri("replaced-two"));
        await reader.PollAsync(CancellationToken.None);

        Assert.Equal(["original-one", "replaced-two"], emitted.Select(log => log.Message));
    }

    [Fact]
    public async Task Rotation_DiscoversReplacementFile()
    {
        using TestDirectory directory = new();
        string path = directory.CreateLogFile("worker.log", Cri("before-rotation"));
        List<WorkerSystemLog> emitted = [];
        Mock<IWorkerSystemLogSink> sink = CreateSink(emitted);
        using WorkerConsoleLogReader reader = CreateReader(directory, enabled: true, sink.Object);

        await reader.PollAsync(CancellationToken.None);
        File.Move(path, Path.Combine(directory.LogPath, "worker.log.1"));
        await File.WriteAllTextAsync(path, Cri("after-rotation"));
        await reader.PollAsync(CancellationToken.None);

        Assert.Contains(emitted, log => string.Equals(log.Message, "after-rotation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PartialRecord_IsRecoveredAcrossRestartAndEmittedWhenComplete()
    {
        using TestDirectory directory = new();
        string path = directory.CreateLogFile(
            "worker.log",
            CriRecord("stderr", "P", "LanguageWorkerConsole"));
        List<WorkerSystemLog> emitted = [];
        Mock<IWorkerSystemLogSink> sink = CreateSink(emitted);

        using (WorkerConsoleLogReader first = CreateReader(directory, enabled: true, sink.Object))
        {
            await first.PollAsync(CancellationToken.None);
        }

        Assert.Empty(emitted);
        await File.AppendAllTextAsync(path, CriRecord("stderr", "F", "Log severe  failure"));

        using (WorkerConsoleLogReader second = CreateReader(directory, enabled: true, sink.Object))
        {
            await second.PollAsync(CancellationToken.None);
        }

        WorkerSystemLog log = Assert.Single(emitted);
        Assert.Equal("severe  failure", log.Message);
        Assert.Equal(RpcLog.Types.Level.Error, log.Level);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero), log.Timestamp);
    }

    [Fact]
    public async Task IncompleteFinalLine_IsNotCheckpointedUntilNewlineArrives()
    {
        using TestDirectory directory = new();
        string path = directory.CreateLogFile(
            "worker.log",
            "2026-10-06T10:00:00Z stdout F LanguageWorkerConsoleLog incomplete");
        List<WorkerSystemLog> emitted = [];
        Mock<IWorkerSystemLogSink> sink = CreateSink(emitted);
        using WorkerConsoleLogReader reader = CreateReader(directory, enabled: true, sink.Object);

        await reader.PollAsync(CancellationToken.None);
        Assert.Empty(emitted);

        await File.AppendAllTextAsync(path, "-complete\n");
        await reader.PollAsync(CancellationToken.None);

        Assert.Equal("incomplete-complete", Assert.Single(emitted).Message);
    }

    private static Mock<IWorkerSystemLogSink> CreateSink(List<WorkerSystemLog>? emitted = null)
    {
        Mock<IWorkerSystemLogSink> sink = new();
        sink.Setup(instance => instance.TryEmit(It.IsAny<WorkerSystemLog>()))
            .Callback<WorkerSystemLog>(log => emitted?.Add(log))
            .Returns(WorkerSystemLogEnqueueResult.Accepted);

        return sink;
    }

    private static WorkerConsoleLogReader CreateReader(
        TestDirectory directory,
        bool enabled,
        IWorkerSystemLogSink sink)
    {
        WorkerConsoleLogOptions options = new()
        {
            Enabled = enabled,
            Directory = directory.LogPath,
            CheckpointDirectory = directory.CheckpointPath
        };

        return new(
            Options.Create(options),
            new WorkerConsoleLogParser(),
            sink,
            NullLogger<WorkerConsoleLogReader>.Instance);
    }

    private static string Cri(string message)
    {
        return CriRecord("stdout", "F", $"LanguageWorkerConsoleLog {message}");
    }

    private static string CriRecord(string stream, string flag, string message)
    {
        return $"2026-10-06T10:00:00Z {stream} {flag} {message}\n";
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"{nameof(WorkerConsoleLogReaderTests)}-{Guid.NewGuid():N}");

        public TestDirectory()
        {
            Directory.CreateDirectory(LogPath);
            Directory.CreateDirectory(CheckpointPath);
        }

        public string LogPath => Path.Combine(_path, "logs");

        public string CheckpointPath => Path.Combine(_path, "checkpoints");

        public string CreateLogFile(string name, string content)
        {
            string path = Path.Combine(LogPath, name);
            File.WriteAllText(path, content);

            return path;
        }

        public void Dispose()
        {
            Directory.Delete(_path, recursive: true);
        }
    }
}
