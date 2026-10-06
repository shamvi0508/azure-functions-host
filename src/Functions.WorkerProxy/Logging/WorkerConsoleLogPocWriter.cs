// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.IO;

namespace Azure.Functions.WorkerProxy.Logging;

internal sealed class WorkerConsoleLogPocWriter
{
    private readonly TextWriter _output;

    public WorkerConsoleLogPocWriter()
        : this(Console.Out)
    {
    }

    internal WorkerConsoleLogPocWriter(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        _output = output;
    }

    public void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        _output.WriteLine($"WORKER_PROXY_EXTRACTED {message}");
        _output.Flush();
    }
}
