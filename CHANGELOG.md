# Changelog

All notable changes to this plugin are documented here. Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
