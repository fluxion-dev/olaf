# olaf

License scanner scaffold (greenfield, no feature logic yet).

```bash
dotnet run --project src/Olaf.Cli -- --help
```

## Reliability

Resolution runs with bounded-8 concurrency and retry-once on transient HTTP failures (timeout/transport/408/429/5xx). Unresolved packages return `Unknown` with reason tokens (`offline-cache-miss`, `not-found`, `resolver-error`, …). Cancellation (`OperationCanceledException`) is always rethrown, never swallowed into `Unknown`.
