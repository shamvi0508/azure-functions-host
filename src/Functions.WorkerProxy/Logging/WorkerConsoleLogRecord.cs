// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;

namespace Azure.Functions.WorkerProxy.Logging;

internal sealed record WorkerConsoleLogRecord(
    DateTimeOffset Timestamp,
    string Stream,
    string Flag,
    string Message);
