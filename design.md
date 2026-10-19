# Forward Pre-gRPC Worker System Logs Through Worker Proxy

## Summary

Language workers can write important system logs before their gRPC connection to the Functions Host is established. Those early logs currently travel through the worker process console rather than the normal RPC system-log path.

This change enables Worker Proxy to recover those pre-gRPC system logs from the Podr-managed worker log directory and forward them through the same Functions-compatible system-log pipeline used for RPC system logs.

The result is one consistent ingestion contract for worker system logs regardless of whether they were emitted before or after gRPC initialization.

## Dependency

This work builds on [Azure/azure-functions-host#12076](https://github.com/Azure/azure-functions-host/pull/12076), which provides the shared Worker Proxy system-log pipeline:

- Functions-compatible `MS_FUNCTIONS_WORKER_POD_LOGS` formatting;
- bounded buffering;
- serialized stdout delivery;
- existing RPC system-log routing behavior.

This change extends that pipeline to pre-gRPC console system logs without changing the established RPC behavior.

## Problem

Worker system logs can be generated during worker startup before the worker has connected to the Functions Host over gRPC.

At that point:

- the worker cannot send an `RpcLog`;
- the message is written to worker stdout or stderr;
- Podr captures it in a CRI-formatted log file;
- the log is not yet flowing through Worker Proxy's system-log ingestion path.

We need to recover only the worker's explicitly marked system logs while leaving ordinary customer console output untouched.

## Design

Podr remains responsible for capturing container output and controlling filesystem access. It mounts the `functionsworker` log directory read-only into `functionsworkerproxy`.

Worker Proxy:

1. observes the mounted worker log directory;
2. reads newly appended CRI records;
3. identifies records beginning with `LanguageWorkerConsoleLog`;
4. ignores ordinary customer output;
5. maps the selected record into the common worker-system-log model;
6. sends it through the existing Functions formatter, bounded queue, and stdout writer;
7. advances a durable checkpoint only after the shared output pipeline accepts the record.

## End-to-End Flow

```text
Language worker starts
    |
    | writes an early system log to stdout/stderr
    v
Podr captures container output as CRI records
    |
    | exposes the functionsworker log directory
    | to functionsworkerproxy as read-only
    v
Worker Proxy reads newly appended records
    |
    | selects only LanguageWorkerConsoleLog records
    | preserves timestamp, stream, and message
    v
Common WorkerSystemLog representation
    |
    v
Shared Functions system-log formatter
    |
    v
Shared bounded stdout queue and single writer
    |
    v
MS_FUNCTIONS_WORKER_POD_LOGS record on Worker Proxy stdout
    |
    v
Podr captures the Worker Proxy output for platform ingestion
```

Once gRPC is established, RPC system logs continue through the existing path introduced by Azure/azure-functions-host#12076.

```text
Pre-gRPC console record ─┐
                         ├─> common system-log pipeline ─> Worker Proxy stdout
Post-gRPC RpcLog ────────┘
```

## Log Selection

The worker marks system console logs with:

```text
LanguageWorkerConsoleLog
```

Worker Proxy matches this marker:

- at the first character of the CRI message;
- case-insensitively;
- without trimming the message first.

This ensures leading whitespace does not accidentally convert customer output into a system log.

Messages without the marker are ignored by this path. They remain ordinary worker/customer console output and are not emitted as `MS_FUNCTIONS_WORKER_POD_LOGS`.

## Configuration

The feature is disabled by default.

```text
WorkerConsoleLogs__Enabled=true
WorkerConsoleLogs__Directory=/azure-functions/worker-logs
WorkerConsoleLogs__CheckpointDirectory=/tmp/azure-functions-worker-proxy/worker-console-log-checkpoints
```

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables pre-gRPC worker console system-log forwarding |
| `Directory` | Read-only Podr-mounted `functionsworker` log directory |
| `CheckpointDirectory` | Writable location for file identity, offsets, and partial-record state |

Worker Proxy does not accept or derive an arbitrary host path. Podr controls the source directory and exposes only the trusted worker log directory inside the container.

## Startup and Shutdown

The shared stdout writer starts before the console reader.

During shutdown, the console reader stops before the writer. This keeps the output pipeline available while the reader is active and allows accepted records to drain through the existing shutdown behavior.

## Behavior That Remains Unchanged

The existing RPC system-log modes remain unchanged:

| Mode | Worker Proxy output | RPC forwarded to Host |
| --- | ---: | ---: |
| `Disabled` | No | Yes |
| `Mirror` | Yes | Yes |
| `Consume` | Yes | No |

The console reader has its own disabled-by-default configuration and does not alter how RPC system logs are forwarded or consumed.

## Validation

The Worker Proxy test suite covers:

- disabled and configured behavior;
- CRI field and message preservation;
- system-log selection and customer-output filtering;
- severity mapping;
- final Functions CSV output;
- appends without restart;
- process restart recovery;
- output rejection and retry without checkpoint advancement;
- truncation, replacement, same-length replacement, and rotation;
- partial-record recovery;
- incomplete final lines;
- unchanged RPC system-log behavior;
- concurrent console and RPC stdout serialization;
- hosted-service ordering.

Validation results:

```text
Worker Proxy tests: 558 passed, 0 failed
Worker Proxy build: 0 warnings, 0 errors
Source formatting verification: passed
Test formatting verification: passed
git diff --check: passed
```

## Deployment Limitation

The default checkpoint location is container-local:

```text
/tmp/azure-functions-worker-proxy/worker-console-log-checkpoints
```

It survives a Worker Proxy process restart within the same container, but not a pod or container replacement.

If checkpoint recovery across pod replacement is required, the deployment must provide a persistent writable location and configure:

```text
WorkerConsoleLogs__CheckpointDirectory=<persistent-writable-path>
```

The Podr-mounted worker log directory remains read-only.

## Expected Outcome

With the Podr mount and Worker Proxy configuration enabled:

- early language-worker system logs are not lost before gRPC initialization;
- customer console output is not incorrectly routed into the system-log table;
- pre-gRPC and post-gRPC worker system logs use the same Functions ingestion contract;
- concurrent output remains serialized;
- temporary output backpressure does not advance the source checkpoint;
- Worker Proxy can recover its position after restart and continue reading appended worker logs.
