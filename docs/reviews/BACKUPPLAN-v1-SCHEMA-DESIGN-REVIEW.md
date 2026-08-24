# Backup Plan v1 Schema Design Review

## Result

**PASS**

Reviewed artifact: `docs/plan/BACKUPPLAN-v1-SCHEMA-DESIGN.md`.

The design is a complete closed-world projection of the frozen Backup Plan v1 domain and does not create the actual JSON Schema. No Schema Design Blocker was found.

## Review Checks

| Check | Result |
|---|---|
| Complete top-level document structure | PASS |
| Complete `$defs` concept inventory | PASS |
| Required/optional properties | PASS |
| Closed enum strings and discriminator encoding | PASS |
| ArchiveSpec and History inheritance encoding | PASS |
| `$schema` optional/non-authoritative boundary | PASS |
| JSON Schema vs semantic validator separation | PASS |
| Rule arrays ordered; aggregate arrays unordered | PASS |
| Deterministic writer ordering defined | PASS |
| Three complete representative JSON examples | PASS |
| Portable/local/runtime state separation | PASS |
| Frozen identity/reference graph represented | PASS |
| Semantics-version pins audited without ad-hoc fields | PASS |
| No serializer/SQLite/domain implementation introduced | PASS |

## Semantics Version Decision

Portable required pins are limited to rules, archive and output-path encoding semantics. They directly correspond to frozen versions whose meanings must remain selectable/stable for a long-lived portable document.

Fingerprint encoding, scanner, External mapping implementation, Schedule/DST fingerprinting, Privacy sub-semantics, manifest schema and storage binding versions remain in their owning runtime/baseline/artifact boundaries. Exposing them as Plan fields would be an unsupported expansion of the frozen domain.

## Non-blocking Actual-Schema Items

- Select JSON Schema draft and canonical `$id`/published `$schema` URI.
- Translate conceptual closed objects/unions to draft-specific keywords without weakening `additionalProperties: false`.
- Validate positive and negative fixtures, duplicate-property handling remaining a strict parser responsibility.
- Keep complex path/rule/reference checks in `BackupPlanDocumentV1` semantic validation.

## Decision

The next authorized design step is creating and reviewing `backupplan-v1.schema.json`. Document DTO, reader/writer, semantic mapper and persistence remain later, separate boundaries.
