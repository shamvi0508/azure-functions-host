// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;

namespace Azure.Functions.WorkerProxy.Logging;

internal sealed class WorkerConsoleLogCheckpoint
{
    public string Path { get; set; } = string.Empty;

    public long Offset { get; set; }

    public string Identity { get; set; } = string.Empty;

    public Dictionary<string, WorkerConsoleLogPartialRecord> Partials { get; set; } = [];

    public WorkerConsoleLogCheckpoint Clone()
    {
        Dictionary<string, WorkerConsoleLogPartialRecord> partials = new(StringComparer.Ordinal);
        foreach ((string stream, WorkerConsoleLogPartialRecord partial) in Partials)
        {
            partials.Add(stream, partial with { });
        }

        return new()
        {
            Path = Path,
            Offset = Offset,
            Identity = Identity,
            Partials = partials
        };
    }
}

internal sealed record WorkerConsoleLogPartialRecord(DateTimeOffset Timestamp, string Message);
