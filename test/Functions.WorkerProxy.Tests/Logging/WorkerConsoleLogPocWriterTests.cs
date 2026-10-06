// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.IO;
using Azure.Functions.WorkerProxy.Logging;
using Xunit;

namespace Azure.Functions.WorkerProxy.Tests.Logging;

public class WorkerConsoleLogPocWriterTests
{
    [Fact]
    public void Write_WritesExpectedConfirmation()
    {
        using StringWriter output = new();
        WorkerConsoleLogPocWriter writer = new(output);

        writer.Write("PODR-MOUNT-POC-001");

        Assert.Equal(
            $"WORKER_PROXY_EXTRACTED PODR-MOUNT-POC-001{Environment.NewLine}",
            output.ToString());
    }
}
