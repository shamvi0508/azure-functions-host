// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;

namespace Azure.Functions.WorkerProxy.Logging;

internal sealed record WorkerSystemLog(
    RpcLog.Types.Level Level,
    string EventId,
    string Message,
    DateTimeOffset Timestamp,
    string Details,
    string ExceptionType,
    string ExceptionMessage,
    string InvocationId)
{
    public static WorkerSystemLog FromRpcLog(RpcLog rpcLog, DateTimeOffset receiptTimestamp)
    {
        ArgumentNullException.ThrowIfNull(rpcLog);

        return new(
            rpcLog.Level,
            rpcLog.EventId,
            rpcLog.Message,
            receiptTimestamp,
            rpcLog.Exception?.StackTrace ?? string.Empty,
            rpcLog.Exception?.Type ?? string.Empty,
            rpcLog.Exception?.Message ?? string.Empty,
            rpcLog.InvocationId);
    }
}
