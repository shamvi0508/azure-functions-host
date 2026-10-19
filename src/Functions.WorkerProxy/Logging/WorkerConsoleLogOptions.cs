// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

namespace Azure.Functions.WorkerProxy.Logging;

internal sealed class WorkerConsoleLogOptions
{
    public const string SectionName = "WorkerConsoleLogs";

    public bool Enabled { get; set; }

    public string Directory { get; set; } = "/azure-functions/worker-logs";

    public string CheckpointDirectory { get; set; } =
        "/tmp/azure-functions-worker-proxy/worker-console-log-checkpoints";
}
