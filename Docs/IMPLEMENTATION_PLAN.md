# IMPLEMENTATION_PLAN.md

## Phase 1 — Critical Fixes (target: before any production traffic)

**Goal**: close the security and data-integrity gaps that make the current write path unsafe.

| Task | Depends on | Effort | Risk if skipped |
|---|---|---|---|
| Build `ITableNameValidator`/`SchemaCatalogService`, wire into `TransactionService` insert/update/delete/child paths (P0-1) | none | 1–2 days | Arbitrary table write / SQL injection |
| Wire `IValidationService` into `TransactionAsync` before any SQL is built (P0-3) | none | 1–2 days | No business-rule enforcement |
| Add row-count verification after UPDATE/DELETE (P1-3) | none | 0.5 day | Silent no-op failures reported as success |
| Stop returning raw `ex.Message` to HTTP clients; log full detail, return generic message + correlation id (P0-4) | Observability correlation-id middleware (can start minimal, expand in Phase 3) | 0.5 day | Information disclosure |
| Idempotency store (Redis + DB fallback) wired into `TransactionAsync` (P0-2) | none | 3–5 days | Duplicate execution on retry |
| Clamp `FetchConfig.Count` to `DatabaseConfig.MaxPageSize` server-side | none | 0.5 day | Unbounded memory/DB load per request |
| Decide Oracle: implement minimally or remove from supported providers until Phase 2 (P1-1) | none | 0.5 day (removal) or 3–5 days (implement) | Silent runtime failure if misconfigured |

**Phase 1 total estimate**: ~2–3 weeks with 1 engineer, less with parallelization across 2.

**Risks**: Table-name allow-list and idempotency both touch the same hot method (`TransactionAsync`); sequence them (allow-list first, since it's a pure security gate; idempotency second, wrapping the whole method) to avoid merge conflicts and re-testing the same code twice.

---

## Phase 2 — Performance

**Goal**: remove the metadata-read hot path and unnecessary round trips identified in `PERFORMANCE_AUDIT.md`.

| Task | Depends on | Effort | Risk if skipped |
|---|---|---|---|
| Two-tier cache (`ITieredCache`) for `FieldMapper`/`QueryDefinition`/allowed-table-list | Phase 1 schema catalog (shares the same cached data) | 2–3 days | Metadata tables become a hot contention point under load |
| Replace `SQL_CALC_FOUND_ROWS` + second query with `COUNT(*) OVER()` | Multi-database query builder (Phase 3) if done together, or standalone for MySQL-only now | 1 day | Slower pagination on large tables |
| Batch sibling child-record inserts where operation type is uniformly Insert | none | 2–3 days | Slower deeply-nested transaction payloads |
| `MySqlDataSource`/`OracleDataSource` + pool metrics | Observability metrics plumbing (Phase 3, can stub early) | 1–2 days | Poor diagnosability of pool exhaustion |

**Phase 2 total estimate**: ~1.5–2 weeks.

**Risks**: Caching introduces staleness windows; confirm with the business how fresh `de_field_mappers`/`de_query_definitions` changes need to be (a schema/query-definition edit taking up to 15 minutes to propagate should be acceptable for an internal admin tool, but confirm before shipping).

---

## Phase 3 — Enterprise Features

**Goal**: audit trail, observability, resiliency — the features an enterprise deployment is expected to have from day one, but which don't block initial safe operation once Phase 1 lands.

| Task | Depends on | Effort | Risk if skipped |
|---|---|---|---|
| `de_audit_log` schema + `IAuditService`, wired transactionally into insert/update/delete | Phase 1 (shares the transaction scope) | 3–5 days | No compliance/forensic trail |
| Optimistic concurrency (row-version compare-and-swap on update) (P1-4) | Audit service (reads before-image at the same point) | 1–2 days | Lost-update races under concurrent edits |
| OpenTelemetry tracing + metrics + correlation-id middleware | none (can start in parallel with Phase 1) | 3–5 days | Poor production diagnosability |
| Polly resiliency wrap (retry/circuit-breaker/timeout) around all DB calls, including whole-transaction retry for deadlocks | Multi-database `IsTransient` predicate (Phase 4, or a MySQL-only version now) | 2–3 days | Transient failures surface as user-facing errors instead of self-healing |
| API authentication/authorization (`[Authorize]`, JWT/OAuth2) | none | 2–3 days | Open, unauthenticated write endpoints |

**Phase 3 total estimate**: ~3–4 weeks.

**Risks**: Audit-in-transaction adds write latency; measure before assuming it's negligible, and have the outbox-pattern fallback (mentioned in `AUDIT_LOGGING_DESIGN.md`) ready as a documented escape hatch if it becomes a bottleneck.

---

## Phase 4 — Platform Enhancements

**Goal**: full multi-database support and remaining polish.

| Task | Depends on | Effort | Risk if skipped |
|---|---|---|---|
| `IDbProviderStrategy` abstraction (MySQL, SQL Server, PostgreSQL, Oracle, SQLite) | none structurally, but touches the same files as Phase 1/2 work — do last to avoid rebasing churn | 5–8 days | Continued per-provider `switch` sprawl; Oracle/other providers remain second-class |
| `IQueryBuilder` abstraction, apply `FilterConditions`/`SortField`/`SearchText` from `FetchConfig` (P1-2) | Provider strategy (for provider-correct SQL) and table/column allow-list (P0-1, extended to columns) | 3–4 days | Silent no-op filtering/sorting remains |
| Consolidate duplicate `DatabaseProvider` enum, fix Core namespace layering (P2-4) | Provider strategy introduces the canonical enum anyway | 1 day | Ongoing maintainer confusion |
| Remove or fix `new Random()`-based auto-value fabrication (P2-5) | Validation service wired in (Phase 1) so missing-required-field is a validation error instead | 1 day | Masked client bugs |
| SQL firewall hardening for direct-query path (allow-list bias) (P2-3) | none | 2–4 days | Blocklist bypass risk remains for the direct-query feature |

**Phase 4 total estimate**: ~2.5–3.5 weeks.

---

## Overall Sequencing Rationale

Phase 1 is non-negotiable before production because it addresses an active SQL-injection-class vulnerability and a data-duplication risk — these are correctness/security issues, not "nice to have" engineering. Phase 2 is next because caching and round-trip reduction are low-risk, high-value, and don't require the provider abstraction to land first. Phase 3 (audit/observability/resiliency/auth) is what most people mean by "enterprise-ready" and should land before this is exposed beyond a small trusted internal caller set. Phase 4 (multi-database) is explicitly last because it's the largest, most invasive refactor (touches almost every file in `DataEngine.Core`/`ReaderService`/`TransactionService`) and benefits from doing it once, after the Phase 1–3 logic has stabilized, rather than rebasing security-critical fixes on top of a mid-flight architectural rewrite.
