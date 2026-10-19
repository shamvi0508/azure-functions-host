// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;

namespace Azure.Functions.WorkerProxy.Logging;

/// <summary>
/// Formats worker system logs using the Functions platform ingestion contract.
/// </summary>
internal sealed class WorkerSystemLogFormatter
{
    internal const string Prefix = "MS_FUNCTIONS_WORKER_POD_LOGS ";
    internal const string Source = "Worker.LanguageWorker";
    internal const int MaximumDetailsLength = 10_000;

    public string Format(RpcLog rpcLog, DateTimeOffset receiptTimestamp)
    {
        return Format(WorkerSystemLog.FromRpcLog(rpcLog, receiptTimestamp));
    }

    /// <summary>
    /// Formats a worker system log for platform ingestion.
    /// </summary>
    /// <param name="log">The normalized worker system log to format.</param>
    /// <returns>The complete single-line ingestion record.</returns>
    public string Format(WorkerSystemLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        string details = log.Details;
        if (details.Length > MaximumDetailsLength)
        {
            details = details[..MaximumDetailsLength];
        }

        string[] fields =
        [
            MapLevel(log.Level).ToString(CultureInfo.InvariantCulture),
            string.Empty,
            string.Empty,
            string.Empty,
            SanitizeUnquoted(log.EventId),
            Source,
            Quote(details),
            Quote(log.Message),
            string.Empty,
            log.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            SanitizeUnquoted(log.ExceptionType),
            Quote(log.ExceptionMessage),
            SanitizeUnquoted(log.InvocationId),
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty
        ];

        return Prefix + string.Join(',', fields);
    }

    private static int MapLevel(RpcLog.Types.Level level)
    {
        EventLevel eventLevel = level switch
        {
            RpcLog.Types.Level.Trace or RpcLog.Types.Level.Debug => EventLevel.Verbose,
            RpcLog.Types.Level.Information => EventLevel.Informational,
            RpcLog.Types.Level.Warning => EventLevel.Warning,
            RpcLog.Types.Level.Error => EventLevel.Error,
            RpcLog.Types.Level.Critical => EventLevel.Critical,
            _ => EventLevel.LogAlways
        };

        return (int)eventLevel;
    }

    private static string Quote(string? value)
    {
        return $"\"{SanitizeQuoted(value)}\"";
    }

    private static string SanitizeQuoted(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('"', '\'');
    }

    private static string SanitizeUnquoted(string? value)
    {
        return SanitizeQuoted(value).Replace(',', ' ');
    }
}
