// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Azure.Functions.WorkerProxy.Logging;
using Xunit;

namespace Azure.Functions.WorkerProxy.Tests.Logging;

public class WorkerConsoleLogParserTests
{
    private readonly WorkerConsoleLogParser _parser = new();

    [Theory]
    [InlineData(
        "2026-10-06T10:00:00Z stdout F LanguageWorkerConsoleLog PODR-MOUNT-POC-001",
        "PODR-MOUNT-POC-001")]
    [InlineData(
        "2026-10-06T10:00:00Z stderr F languageworkerconsolelog case-insensitive",
        "case-insensitive")]
    [InlineData(
        "2026-10-06T10:00:00Z stdout F LanguageWorkerConsoleLog spaces inside  the message",
        "spaces inside  the message")]
    public void TryExtractMessage_MatchingCriRecord_ReturnsExtractedMessage(
        string record,
        string expectedMessage)
    {
        bool result = _parser.TryExtractMessage(record, out string? message);

        Assert.True(result);
        Assert.Equal(expectedMessage, message);
    }

    [Theory]
    [InlineData("2026-10-06T10:00:00Z stdout F Customer output must be ignored")]
    [InlineData("2026-10-06T10:00:00Z stdout F  LanguageWorkerConsoleLog leading-whitespace")]
    [InlineData("2026-10-06T10:00:00Z stdout")]
    public void TryExtractMessage_NonMatchingRecord_ReturnsFalse(string record)
    {
        bool result = _parser.TryExtractMessage(record, out string? message);

        Assert.False(result);
        Assert.Null(message);
    }
}
