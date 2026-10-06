// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Azure.Functions.WorkerProxy.Logging;

internal sealed partial class WorkerConsoleLogReader(
    IOptions<WorkerConsoleLogOptions> options,
    WorkerConsoleLogParser parser,
    WorkerConsoleLogPocWriter writer,
    ILogger<WorkerConsoleLogReader> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly WorkerConsoleLogOptions _options = options.Value;
    private readonly WorkerConsoleLogParser _parser = parser;
    private readonly WorkerConsoleLogPocWriter _writer = writer;
    private readonly ILogger<WorkerConsoleLogReader> _logger = logger;
    private readonly Dictionary<string, long> _offsets = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (Directory.Exists(_options.Directory))
            {
                await ReadFilesAsync(stoppingToken);
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ReadFilesAsync(CancellationToken cancellationToken)
    {
        string[] files;

        try
        {
            files = Directory.GetFiles(
                _options.Directory,
                "*",
                SearchOption.TopDirectoryOnly);
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

            long offset = _offsets.GetValueOrDefault(path);
            if (stream.Length < offset)
            {
                offset = 0;
            }

            stream.Position = offset;

            using MemoryStream appendedContent = new();
            await stream.CopyToAsync(appendedContent, cancellationToken);

            byte[] bytes = appendedContent.ToArray();
            int finalNewline = Array.LastIndexOf(bytes, (byte)'\n');
            if (finalNewline < 0)
            {
                _offsets[path] = offset;
                return;
            }

            int completeLength = finalNewline + 1;
            string completeContent = Encoding.UTF8.GetString(bytes, 0, completeLength);

            foreach (string record in completeContent.Split('\n', StringSplitOptions.None))
            {
                if (record.Length == 0)
                {
                    continue;
                }

                string normalizedRecord = record.EndsWith('\r')
                    ? record[..^1]
                    : record;

                if (_parser.TryExtractMessage(normalizedRecord, out string? message))
                {
                    _writer.Write(message);
                }
            }

            _offsets[path] = offset + completeLength;
        }
        catch (FileNotFoundException)
        {
            _offsets.Remove(path);
        }
        catch (DirectoryNotFoundException)
        {
            _offsets.Remove(path);
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
    }
}
