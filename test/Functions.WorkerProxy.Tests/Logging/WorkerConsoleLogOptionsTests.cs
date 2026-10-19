// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Azure.Functions.WorkerProxy.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Azure.Functions.WorkerProxy.Tests.Logging;

public class WorkerConsoleLogOptionsTests
{
    [Fact]
    public void Options_UseStableDisabledDefaults()
    {
        WorkerConsoleLogOptions options = GetOptions();

        Assert.False(options.Enabled);
        Assert.Equal("/azure-functions/worker-logs", options.Directory);
        Assert.Equal(
            "/tmp/azure-functions-worker-proxy/worker-console-log-checkpoints",
            options.CheckpointDirectory);
    }

    [Fact]
    public void Options_BindFromStandardConfiguration()
    {
        WorkerConsoleLogOptions options = GetOptions(
            $"--{WorkerConsoleLogOptions.SectionName}:Enabled", bool.TrueString,
            $"--{WorkerConsoleLogOptions.SectionName}:Directory", "/mounted/logs",
            $"--{WorkerConsoleLogOptions.SectionName}:CheckpointDirectory", "/writable/checkpoints");

        Assert.True(options.Enabled);
        Assert.Equal("/mounted/logs", options.Directory);
        Assert.Equal("/writable/checkpoints", options.CheckpointDirectory);
    }

    private static WorkerConsoleLogOptions GetOptions(params string[] args)
    {
        using WebApplication app = WorkerProxyApplication.Build(
            ["--WorkerProxy:PodName", "test-worker-pod", .. args]);

        return app.Services.GetRequiredService<IOptions<WorkerConsoleLogOptions>>().Value;
    }
}
