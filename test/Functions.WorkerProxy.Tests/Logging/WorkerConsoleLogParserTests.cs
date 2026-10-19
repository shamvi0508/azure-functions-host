// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using Azure.Functions.WorkerProxy.Logging;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Xunit;

namespace Azure.Functions.WorkerProxy.Tests.Logging;

public class WorkerConsoleLogParserTests
{
    private readonly WorkerConsoleLogParser _parser = new();

    [Fact]
    public void TryParse_ValidCriRecordPreservesFieldsAndMessageSpaces()
    {
        bool result = _parser.TryParse(
            "2026-10-06T10:00:00.123456789Z stderr P LanguageWorkerConsoleLog spaces inside  the message",
            out WorkerConsoleLogRecord? record);

        Assert.True(result);
        WorkerConsoleLogRecord parsed = Assert.IsType<WorkerConsoleLogRecord>(record);
        Assert.Equal(DateTimeOffset.Parse("2026-10-06T10:00:00.1234568Z"), parsed.Timestamp);
        Assert.Equal("stderr", parsed.Stream);
        Assert.Equal("P", parsed.Flag);
        Assert.Equal("LanguageWorkerConsoleLog spaces inside  the message", parsed.Message);
    }

    [Theory]
    [InlineData("not-a-timestamp stdout F LanguageWorkerConsoleLog message")]
    [InlineData("2026-10-06T10:00:00Z invalid F LanguageWorkerConsoleLog message")]
    [InlineData("2026-10-06T10:00:00Z stdout invalid LanguageWorkerConsoleLog message")]
    [InlineData("2026-10-06T10:00:00Z stdout")]
    public void TryParse_InvalidCriRecordReturnsFalse(string record)
    {
        Assert.False(_parser.TryParse(record, out WorkerConsoleLogRecord? parsed));
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData("stdout", "LanguageWorkerConsoleLog message", RpcLog.Types.Level.Information, "message")]
    [InlineData("stderr", "languageworkerconsolelog warning text", RpcLog.Types.Level.Warning, "warning text")]
    [InlineData("stderr", "LanguageWorkerConsoleLog severe failure", RpcLog.Types.Level.Error, "severe failure")]
    [InlineData("stderr", "LanguageWorkerConsoleLog ordinary text", RpcLog.Types.Level.Information, "ordinary text")]
    [InlineData("stdout", "LanguageWorkerConsoleLog spaces inside  message", RpcLog.Types.Level.Information, "spaces inside  message")]
    public void TryCreateSystemLog_MatchingMessageMapsHostCompatibleLevel(
        string stream,
        string message,
        RpcLog.Types.Level expectedLevel,
        string expectedMessage)
    {
        DateTimeOffset timestamp = new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
        WorkerConsoleLogRecord record = new(timestamp, stream, "F", message);

        bool result = _parser.TryCreateSystemLog(record, out WorkerSystemLog? systemLog);

        Assert.True(result);
        WorkerSystemLog parsed = Assert.IsType<WorkerSystemLog>(systemLog);
        Assert.Equal(timestamp, parsed.Timestamp);
        Assert.Equal(expectedLevel, parsed.Level);
        Assert.Equal(expectedMessage, parsed.Message);
        Assert.Equal(string.Empty, parsed.EventId);
        Assert.Equal(string.Empty, parsed.InvocationId);
    }

    [Theory]
    [InlineData("Customer output must be ignored")]
    [InlineData(" LanguageWorkerConsoleLog leading-whitespace")]
    public void TryCreateSystemLog_UnprefixedMessageReturnsFalse(string message)
    {
        WorkerConsoleLogRecord record = new(DateTimeOffset.UnixEpoch, "stdout", "F", message);

        Assert.False(_parser.TryCreateSystemLog(record, out WorkerSystemLog? systemLog));
        Assert.Null(systemLog);
    }
}
