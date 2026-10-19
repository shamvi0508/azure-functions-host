// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;

namespace Azure.Functions.WorkerProxy.Logging;

internal sealed class WorkerConsoleLogParser
{
    private const string Prefix = "LanguageWorkerConsoleLog";

    public bool TryParse(
        string record,
        [NotNullWhen(true)] out WorkerConsoleLogRecord? parsedRecord)
    {
        ArgumentNullException.ThrowIfNull(record);

        parsedRecord = null;

        int timestampSeparator = record.IndexOf(' ');
        if (timestampSeparator < 0)
        {
            return false;
        }

        int streamSeparator = record.IndexOf(' ', timestampSeparator + 1);
        if (streamSeparator < 0)
        {
            return false;
        }

        int flagSeparator = record.IndexOf(' ', streamSeparator + 1);
        if (flagSeparator < 0)
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
            record.AsSpan(0, timestampSeparator),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset timestamp))
        {
            return false;
        }

        string stream = record[(timestampSeparator + 1)..streamSeparator];
        string flag = record[(streamSeparator + 1)..flagSeparator];
        if ((!string.Equals(stream, "stdout", StringComparison.Ordinal)
                && !string.Equals(stream, "stderr", StringComparison.Ordinal))
            || (!string.Equals(flag, "F", StringComparison.Ordinal)
                && !string.Equals(flag, "P", StringComparison.Ordinal)))
        {
            return false;
        }

        parsedRecord = new(timestamp, stream, flag, record[(flagSeparator + 1)..]);

        return true;
    }

    public bool TryCreateSystemLog(
        WorkerConsoleLogRecord record,
        [NotNullWhen(true)] out WorkerSystemLog? systemLog)
    {
        ArgumentNullException.ThrowIfNull(record);

        systemLog = null;
        if (!record.Message.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string payload = record.Message[Prefix.Length..];
        if (payload.StartsWith(' '))
        {
            payload = payload[1..];
        }

        systemLog = new(
            MapLevel(record.Stream, payload),
            string.Empty,
            payload,
            record.Timestamp,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);

        return true;
    }

    private static RpcLog.Types.Level MapLevel(string stream, string message)
    {
        if (string.Equals(stream, "stdout", StringComparison.Ordinal))
        {
            return RpcLog.Types.Level.Information;
        }

        if (message.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            return RpcLog.Types.Level.Warning;
        }

        return message.Contains("error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || message.Contains("severe", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unhandled exception", StringComparison.OrdinalIgnoreCase)
                ? RpcLog.Types.Level.Error
                : RpcLog.Types.Level.Information;
    }
}
