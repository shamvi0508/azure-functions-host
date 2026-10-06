// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Diagnostics.CodeAnalysis;

namespace Azure.Functions.WorkerProxy.Logging;

internal sealed class WorkerConsoleLogParser
{
    private const string Prefix = "LanguageWorkerConsoleLog";

    public bool TryExtractMessage(
        string record,
        [NotNullWhen(true)] out string? extractedMessage)
    {
        ArgumentNullException.ThrowIfNull(record);

        extractedMessage = null;

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

        string message = record[(flagSeparator + 1)..];
        if (!message.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string payload = message[Prefix.Length..];
        if (payload.StartsWith(' '))
        {
            payload = payload[1..];
        }

        extractedMessage = payload;

        return true;
    }
}
