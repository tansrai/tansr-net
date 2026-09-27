# C# quickstart

This repository contains an unpublished development candidate. `0.1.0-preview.1` is a candidate version, not evidence that the packages are available on NuGet.org. The [development record](development.md) separates implementation, validation, mainline integration, signing and publication.

## Install the appropriate package

Use `Tansr.Sdk` for a service that only connects to remote Serve. Use `Tansr.Sdk.Windows` for Windows file/process tools, SQLite, DPAPI or an owned local Serve process; it includes the core dependency. Modern examples target .NET 10, while the existing WinForms example targets .NET Framework 4.8. The SDK is a library and does not install an application runtime.

Put both approved `.nupkg` files in a candidate directory. Configure a private `NuGet.Config` that maps `Tansr.*` to that directory and other dependencies to NuGet.org. `scripts/test-packages.ps1` provides the complete reproducible configuration. Use a separate package cache for candidates that share a preview version but have different bytes.

```powershell
dotnet add YourApp.csproj package Tansr.Sdk.Windows --version 0.1.0-preview.1
dotnet restore YourApp.csproj --configfile NuGet.Config --packages .packages
```

These commands assume that the local source is configured. Framework applications require the system .NET Framework runtime. Modern applications can be framework-dependent or self-contained. Windows x64 is the current package-consumption target; compilation for another RID is not a runtime-support claim.

## Run a turn

```csharp
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;

using var client = new TansrClient(new TansrClientOptions
{
    BaseUri = new Uri("https://your-serve.example"),
    TokenProvider = ct => YourLoginService.GetShortLivedTokenAsync(ct),
});
var session = await client.CreateSessionAsync(new CreateSessionOptions(), cancellationToken);
using var run = session.StartRun("Look up this order", observer: (item, ct) =>
{
    // Receive immutable events off the UI thread. Dispatch UI changes through the host.
    return Task.CompletedTask;
}, cancellationToken: cancellationToken);
await run.Acceptance;
var result = await run.Completion;
Console.WriteLine($"{result.TurnId}: {result.Reason}; aborted={result.WasAborted}");
```

`YourLoginService` is your application's authentication service. Keep long-lived appkeys on the developer's server; the client receives a short-lived user token. The token callback supplies the current credential without recreating the session. `StartRun` requires exclusive message ownership for that session; it cannot infer which concurrent client caused a turn.

If a developer-owned Serve deployment requires an additional user credential, explicitly set `TansrClientOptions.AdditionalRequestHeaders`. The client copies bounded `x-` headers at construction; they cannot replace Bearer, Host or Cookie. The local `PrincipalProvider` identity is never automatically transmitted as a user ID. Do not derive these headers from chat, model arguments or unverified identity claims. Reconfigure the client and resume the original session when this additional credential changes; ordinary Bearer renewal still uses `TokenProvider`.

Reuse the session for subsequent turns. Low-level `SendAsync` acknowledges acceptance; HTTP 202 is not completion. Cancelling observation or disposing a `SessionRun` does not interrupt the remote task. Call `session.CancelAsync` explicitly to interrupt it. A close acknowledgement does not prove that tools, persistence and memory publication have drained; observe the actual Serve cleanup facts.

The default retains SDK1. SDK2 archive offload requires explicit `SessionContract.Sdk2OffloadV1`, trusted identity/scope and discovered capabilities. Discovery failure must not silently switch protocols or storage. See [terminal services](terminal-services.md) and [device memory](device-memory.md).

## Tools, storage and presentation

Serve/kernel owns the agent loop, context assembly, authorization, memory decisions and metering. `DeviceSessionHost`, the Windows backend, durable journals and execution notifications handle authorized terminal operations. Applications do not need to implement their own polling, SSE, heartbeat or ACK state machines. Never interpret model data as an assembly path, reflection type or trusted host code.

The default transport uses separate connection pools for persistent event streams and control requests, so long-lived streams cannot occupy all control connections on .NET Framework. The SDK-owned event pool allows up to 32 connections per origin; additional requests wait within the existing request timeout. This is a transport limit, not a session or protocol limit. An injected `HttpClient` keeps the host's pool and ownership: provision capacity for both event streams and control requests, and retain the existing no-redirect and no-shared-cookie requirements.

The [examples](../README.md#示例和开发) show business tools, same-turn input, approvals, questions, MCP, model/thinking controls, media and offline drafts. Their documentation distinguishes implementation from remaining validation. Electron retains its complete embedded SDK, Node dependencies and IPC; this client introduces no second agent loop.

## Error and recovery rules

| Condition | Required handling |
|---|---|
| Unauthorized, revoked or stale binding | Reauthenticate or use the trusted recovery procedure; do not resend an old operation under a new identity. |
| Unsupported contract or capability | Explain the missing capability; do not discard parameters or silently change storage. |
| Event gap | Mark the projection incomplete and use supported history/recovery operations; do not disable gap checks. |
| Lost response after a POST | Preserve the original key, input, target and unknown status, then reconcile. Do not repeat a file write, command or paid speech segment. |
| Capacity, corruption or cleanup failure | Keep the failure visible. Do not treat a damaged store as empty, delete receipts to make room, or report unconfirmed cleanup as complete. |

Retry only where the protocol explicitly permits it. Current-context budget, accumulated usage, provider cache hits and final cost are separate observations; missing evidence remains unknown.
