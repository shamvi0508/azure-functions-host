// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Azure.Functions.WorkerProxy.Logging;

internal sealed partial class WorkerConsoleLogReader(
    IOptions<WorkerConsoleLogOptions> options,
    WorkerConsoleLogParser parser,
    IWorkerSystemLogSink sink,
    ILogger<WorkerConsoleLogReader> logger) : BackgroundService
{
    private const int IdentityPrefixLength = 4096;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly WorkerConsoleLogOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    private readonly WorkerConsoleLogParser _parser =
        parser ?? throw new ArgumentNullException(nameof(parser));

    private readonly IWorkerSystemLogSink _sink =
        sink ?? throw new ArgumentNullException(nameof(sink));

    private readonly ILogger<WorkerConsoleLogReader> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly Dictionary<string, WorkerConsoleLogCheckpoint> _checkpoints =
        new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await PollAsync(stoppingToken);
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    internal async Task PollAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !Directory.Exists(_options.Directory))
        {
            return;
        }

        string[] files;

        try
        {
            files = Directory.GetFiles(
                _options.Directory,
                "*",
                SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.Ordinal);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (IOException exception)
        {
            Log.DirectoryReadFailed(_logger, exception, _options.Directory);
            return;
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.DirectoryReadFailed(_logger, exception, _options.Directory);
            return;
        }

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReadFileAsync(file, cancellationToken);
        }
    }

    private async Task ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);

            WorkerConsoleLogCheckpoint checkpoint = await GetCheckpointAsync(path, stream, cancellationToken);
            stream.Position = checkpoint.Offset;

            using MemoryStream appendedContent = new();
            await stream.CopyToAsync(appendedContent, cancellationToken);
            byte[] bytes = appendedContent.ToArray();

            int lineStart = 0;
            while (lineStart < bytes.Length)
            {
                int newline = Array.IndexOf(bytes, (byte)'\n', lineStart);
                if (newline < 0)
                {
                    return;
                }

                int lineLength = newline - lineStart;
                if (lineLength > 0 && bytes[newline - 1] == (byte)'\r')
                {
                    lineLength--;
                }

                string record = Encoding.UTF8.GetString(bytes, lineStart, lineLength);
                long nextOffset = checkpoint.Offset + newline + 1;

                if (!await ProcessRecordAsync(
                    path,
                    stream,
                    checkpoint,
                    record,
                    nextOffset,
                    cancellationToken))
                {
                    return;
                }

                checkpoint = _checkpoints[path];
                bytes = bytes[(newline + 1)..];
                lineStart = 0;
            }
        }
        catch (FileNotFoundException)
        {
            _checkpoints.Remove(path);
        }
        catch (DirectoryNotFoundException)
        {
            _checkpoints.Remove(path);
        }
        catch (IOException exception)
        {
            Log.FileReadFailed(_logger, exception, path);
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.FileReadFailed(_logger, exception, path);
        }
    }

    private async Task<bool> ProcessRecordAsync(
        string path,
        FileStream stream,
        WorkerConsoleLogCheckpoint checkpoint,
        string record,
        long nextOffset,
        CancellationToken cancellationToken)
    {
        WorkerConsoleLogCheckpoint next = checkpoint.Clone();
        next.Offset = nextOffset;

        if (!_parser.TryParse(record, out WorkerConsoleLogRecord? parsed))
        {
            await PersistCheckpointAsync(path, stream, next, cancellationToken);
            return true;
        }

        if (string.Equals(parsed.Flag, "P", StringComparison.Ordinal))
        {
            if (next.Partials.TryGetValue(parsed.Stream, out WorkerConsoleLogPartialRecord? pending))
            {
                next.Partials[parsed.Stream] = pending with { Message = pending.Message + parsed.Message };
            }
            else
            {
                next.Partials[parsed.Stream] = new(parsed.Timestamp, parsed.Message);
            }

            await PersistCheckpointAsync(path, stream, next, cancellationToken);
            return true;
        }

        if (next.Partials.Remove(parsed.Stream, out WorkerConsoleLogPartialRecord? partial))
        {
            parsed = parsed with
            {
                Timestamp = partial.Timestamp,
                Message = partial.Message + parsed.Message
            };
        }

        if (_parser.TryCreateSystemLog(parsed, out WorkerSystemLog? systemLog))
        {
            WorkerSystemLogEnqueueResult result = _sink.TryEmit(systemLog);
            if (result != WorkerSystemLogEnqueueResult.Accepted)
            {
                Log.EnqueueRejected(_logger, path, nextOffset, result);
                return false;
            }
        }

        await PersistCheckpointAsync(path, stream, next, cancellationToken);
        return true;
    }

    private async Task<WorkerConsoleLogCheckpoint> GetCheckpointAsync(
        string path,
        FileStream stream,
        CancellationToken cancellationToken)
    {
        if (_checkpoints.TryGetValue(path, out WorkerConsoleLogCheckpoint? cached))
        {
            if (await IsCheckpointValidAsync(cached, stream, cancellationToken))
            {
                return cached;
            }

            WorkerConsoleLogCheckpoint reset = CreateCheckpoint(path);
            _checkpoints[path] = reset;
            return reset;
        }

        string checkpointPath = GetCheckpointPath(path);
        WorkerConsoleLogCheckpoint checkpoint = CreateCheckpoint(path);

        if (File.Exists(checkpointPath))
        {
            try
            {
                await using FileStream checkpointStream = new(
                    checkpointPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);

                checkpoint = await JsonSerializer.DeserializeAsync(
                    checkpointStream,
                    WorkerConsoleLogJsonContext.Default.WorkerConsoleLogCheckpoint,
                    cancellationToken) ?? CreateCheckpoint(path);
            }
            catch (JsonException exception)
            {
                Log.CheckpointReadFailed(_logger, exception, path);
                checkpoint = CreateCheckpoint(path);
            }
        }

        if (!string.Equals(checkpoint.Path, path, StringComparison.Ordinal)
            || !await IsCheckpointValidAsync(checkpoint, stream, cancellationToken))
        {
            checkpoint = CreateCheckpoint(path);
        }

        _checkpoints[path] = checkpoint;
        return checkpoint;
    }

    private static WorkerConsoleLogCheckpoint CreateCheckpoint(string path)
    {
        return new() { Path = path };
    }

    private async Task<bool> IsCheckpointValidAsync(
        WorkerConsoleLogCheckpoint checkpoint,
        FileStream stream,
        CancellationToken cancellationToken)
    {
        if (checkpoint.Offset < 0 || checkpoint.Offset > stream.Length)
        {
            return false;
        }

        string identity = await ComputeIdentityAsync(stream, checkpoint.Offset, cancellationToken);

        return string.Equals(checkpoint.Identity, identity, StringComparison.Ordinal);
    }

    private async Task PersistCheckpointAsync(
        string path,
        FileStream stream,
        WorkerConsoleLogCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        checkpoint.Identity = await ComputeIdentityAsync(stream, checkpoint.Offset, cancellationToken);
        Directory.CreateDirectory(_options.CheckpointDirectory);

        string checkpointPath = GetCheckpointPath(path);
        string temporaryPath = checkpointPath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";

        try
        {
            await using (FileStream checkpointStream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    checkpointStream,
                    checkpoint,
                    WorkerConsoleLogJsonContext.Default.WorkerConsoleLogCheckpoint,
                    cancellationToken);
                checkpointStream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, checkpointPath, overwrite: true);
            _checkpoints[path] = checkpoint;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<string> ComputeIdentityAsync(
        FileStream stream,
        long offset,
        CancellationToken cancellationToken)
    {
        int length = (int)Math.Min(offset, IdentityPrefixLength);
        if (length == 0)
        {
            return string.Empty;
        }

        long position = stream.Position;
        stream.Position = 0;
        byte[] prefix = new byte[length];
        int bytesRead = await stream.ReadAtLeastAsync(prefix, length, throwOnEndOfStream: false, cancellationToken);
        stream.Position = position;

        return Convert.ToHexString(SHA256.HashData(prefix.AsSpan(0, bytesRead)));
    }

    private string GetCheckpointPath(string path)
    {
        string fileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + ".json";

        return Path.Combine(_options.CheckpointDirectory, fileName);
    }

    private static partial class Log
    {
        [LoggerMessage(
            0,
            LogLevel.Warning,
            "Worker console log directory '{Directory}' could not be read.")]
        public static partial void DirectoryReadFailed(
            ILogger logger,
            Exception exception,
            string directory);

        [LoggerMessage(
            1,
            LogLevel.Warning,
            "Worker console log file '{Path}' could not be read.")]
        public static partial void FileReadFailed(
            ILogger logger,
            Exception exception,
            string path);

        [LoggerMessage(
            2,
            LogLevel.Warning,
            "Worker console log checkpoint for '{Path}' could not be read.")]
        public static partial void CheckpointReadFailed(
            ILogger logger,
            Exception exception,
            string path);

        [LoggerMessage(
            3,
            LogLevel.Warning,
            "Worker console log '{Path}' at offset {Offset} was not admitted to the system-log pipeline: {Result}.")]
        public static partial void EnqueueRejected(
            ILogger logger,
            string path,
            long offset,
            WorkerSystemLogEnqueueResult result);
    }
}
