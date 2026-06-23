# Changelog

All notable changes to this plugin are documented here. Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.5] - 2026-06-23

### Added
- Eight more components, each verified against a real CFD Studio build, completing
  coverage of every component used by the bundled demos:
  - §4.20 `MakeCallComponent` (Callback) — `Origin`/`Destination`/`TimeoutSeconds`
    (timeout in seconds, not ×1000).
  - §4.21 `TcxSetGlobalPropertyComponent` (Callback) — setter sibling of §4.17.
  - §4.22 `RecordComponent` (EMailDemo).
  - §4.23 `EMailSenderComponent` (EMailDemo).
  - §4.24 `LoggerComponent` (OutboundDialerDemo).
  - §4.25 `FileManagementComponent` (OutboundDialerDemo).
  - §4.26 `IncrementVariableComponent` (PlayDigitsDemo).
  - §4.27 `DateTimeConditionalComponent` (DateTimeRouting) — lowers to a
    `ConditionalComponent` with date/time conditions and `{name}_{index}` containers.
- Validator recognizes all of the above (plus `DateTimeConditionalComponentBranch`),
  and the cfd-build mapping table + tests are updated accordingly.

## [0.1.4] - 2026-06-23

### Added
- `UserComponent` support, verified against a real CFD Studio build of the
  official Callback demo:
  - SPEC.md §4.19 documents the codegen — the XML `UserComponent` references a
    `.comp` sub-flow via `RelativeFilePath`; the builder emits one
    `AbsUserComponent` inner class per `.comp` (class name = `.comp` basename)
    with its own variable map and per-public-property `Setter`/getter,
    instantiated in the parent with the
    `onlineServices/officeHoursManager/scope` constructor.
  - `cfd-build.md` XML→C# mapping table updated.
  - Validator now recognizes `UserComponent` and warns when a referenced
    `.comp` file is missing (errors when `RelativeFilePath` is absent).
  - Regression tests for recognition, missing-`.comp`, and missing-path.

## [0.1.3] - 2026-06-23

### Added
- `CreditCardComponent` support, verified against a real CFD Studio build of
  the official CreditCard demo:
  - SPEC.md §4.18 documents the codegen — the XML `CreditCardComponent` lowers
    to a `CreditCardLoopComponent` (retry loop gated on a `Validated` flag) plus
    an inner `UserInputComponent` for the card number. `HasToPauseRecording=true`
    is the PCI DTMF-masking mechanism.
  - `cfd-build.md` XML→C# mapping table updated.
  - Validator now recognizes `CreditCardComponent` (participates in duplicate-name
    and audio-reference checks instead of being silently skipped).
  - Regression tests for recognition and duplicate-name detection.

## [0.1.2] - 2026-05-17

### Fixed
- SPEC.md (the code-generator reference) described an obsolete
  YAML-input / Python-generator architecture. Rewritten to describe the
  actual cfd-design → cfd-build flow; the C# code-generation rules are
  unchanged.

## [0.1.1] - 2026-05-17

### Fixed
- README documented the slash commands without their plugin namespace —
  they are invoked as `/opencfd:cfd-design`, `/opencfd:cfd-validate`, and
  `/opencfd:cfd-build`.

## [0.1.0] - 2026-04-17

### Added
- `/cfd-design` — generate a 3CX CFD source project from a natural-language description.
- `/cfd-build` — compile a CFD source project to a deployable `.zip`.
- `/cfd-validate` — lint a CFD project for common mistakes.
- PostToolUse hook that auto-validates `.flow` / `.cfdproj` edits.
- Bundled reference: 20 official 3CX demo projects and a full component/expression spec.
- Validator: 10+ checks including variable sync, duplicate component names, GET body constraints, JsonXml `Input=` attribute, menu option flags, audio-file references, and conditional-branch ordering.
- `--strict` validator flag for enforcing variable sync across `.cfdproj` and `.flow`.
- Pytest suite with parametrized golden tests over all bundled demos.
- GitHub Actions CI across Python 3.10–3.12.
