// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Functions.WorkerProxy.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Azure.Functions.WorkerProxy.Tests.Logging;

public class WorkerConsoleLogReaderTests
{
    [Fact]
    public async Task Disabled_DoesNotReadRecords()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            string path = Path.Combine(directory, "worker.log");
            await File.WriteAllTextAsync(
                path,
                "2026-10-06T10:00:00Z stdout F LanguageWorkerConsoleLog disabled-record\n");
            RecordingTextWriter output = new();
            using WorkerConsoleLogReader reader = CreateReader(directory, enabled: false, output);

            await reader.StartAsync(CancellationToken.None);

            Assert.Empty(output.Lines);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AppendedRecords_AreReadWithoutRestarting()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "worker.log");
        RecordingTextWriter output = new();
        using WorkerConsoleLogReader reader = CreateReader(directory, enabled: true, output);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        try
        {
            await File.WriteAllTextAsync(
                path,
                "2026-10-06T10:00:00Z stdout F LanguageWorkerConsoleLog first-record\n",
                timeout.Token);

            await reader.StartAsync(timeout.Token);
            await WaitForLineAsync(output, "WORKER_PROXY_EXTRACTED first-record", timeout.Token);

            const string appendedRecords = """
                2026-10-06T10:00:01Z stdout F Customer output must be ignored
                2026-10-06T10:00:02Z stdout F LanguageWorkerConsoleLog second  record

                """;

            await File.AppendAllTextAsync(
                path,
                appendedRecords,
                timeout.Token);

            await WaitForLineAsync(output, "WORKER_PROXY_EXTRACTED second  record", timeout.Token);

            Assert.DoesNotContain(
                output.Lines,
                line => string.Equals(
                    line,
                    "WORKER_PROXY_EXTRACTED Customer output must be ignored",
                    StringComparison.Ordinal));
        }
        finally
        {
            await reader.StopAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static WorkerConsoleLogReader CreateReader(
        string directory,
        bool enabled,
        RecordingTextWriter output)
    {
        WorkerConsoleLogOptions options = new()
        {
            Enabled = enabled,
            Directory = directory
        };

        return new(
            Options.Create(options),
            new WorkerConsoleLogParser(),
            new WorkerConsoleLogPocWriter(output),
            NullLogger<WorkerConsoleLogReader>.Instance);
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"{nameof(WorkerConsoleLogReaderTests)}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        return directory;
    }

    private static async Task WaitForLineAsync(
        RecordingTextWriter output,
        string expectedLine,
        CancellationToken cancellationToken)
    {
        while (!output.Lines.Any(
            line => string.Equals(line, expectedLine, StringComparison.Ordinal)))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private sealed class RecordingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public ConcurrentQueue<string> Lines { get; } = [];

        public override void WriteLine(string? value)
        {
            if (value is not null)
            {
                Lines.Enqueue(value);
            }
        }
    }
}
