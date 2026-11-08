# M7 bottleneck investigation

This is an evidence-led investigation ledger, not a production capacity statement.

## Initial development run — invalid baseline

Source `0119926cc77becd9035a166a45c8e2f02a031dc0`, run 42492416100, artifact 13622376346.
Environment and immutable artifact hash are recorded in `m7.md`.

The driver reached 100 active VUs and 100 simultaneous requests. It recorded 371 create attempts:
341 HTTP 201 responses rejected by its payload comparison and 30 client transport/timeouts.
No complete business transaction was reported, and the other mandatory operation cells were empty.
This is a **failed workload**, not 341 successful business transactions and not a valid latency baseline.
There were no observed HTTP 5xx responses in that failed run; that does not cancel the errors.

Source tracing established that generated order numbers contained lowercase slug/GUID text whereas
`OrderPolicy.Normalize` deliberately returns uppercase. The driver now generates canonical numbers;
core normalization is unchanged. The Portal 404 defect was independent of load and is separately fixed.

Application-plus-generator CPU was 128.333 CPU seconds over 107.501 elapsed seconds (29.845% of four
available logical CPUs), with a 413,454,336-byte process peak working set. These values exclude a
separate attribution of SQL-container CPU. They cannot establish SQL, CPU, locking or network as the
root performance bottleneck. Client timeout observations need a new valid-workload run.

## Reproducible investigation procedure

Keep the committed four-tenant population, all-Active baseline, workload and guards intact. Review
per-cell sample counts and errors before comparing latency. Separate request throughput from complete
transaction throughput; inspect tail percentiles without averaging tenant percentiles. Use the
sanitized raw timing file to examine ramp/steady-state/drain effects.

On a retained disposable environment, correlate application CPU/GC and request timing with SQL
container CPU/memory, active request counts, waits/blocking, execution plans and connection/logon
costs. Do not expose query parameter values, authentication payloads, ACL JSON or credentials in
shared traces. This first implementation reports application process resources and exact SQL image,
not a fabricated SQL wait profile.

Existing authoritative permission/schema/release checks and non-pooled runtime connections are
candidate costs visible in source, not proven bottlenecks. Do not remove current-state checks, cache
an Allow across requests, enable unsafe pooling or weaken privilege checks merely to obtain green
load numbers. A change to a core boundary requires its own reviewed plan and regression tests.

After a passing baseline exists, record the dominant measured cells, a specific supported hypothesis,
a single controlled change and the before/after artifacts on the same declared topology. Any workload
or hardware change requires a new declared baseline and target review; never retrofit targets to an
acceptance result. No latency target or optimization claim is approved by this document.
