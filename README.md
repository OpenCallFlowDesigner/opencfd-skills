# opencfd

A Claude Code plugin for generating, validating, and packaging [3CX Call Flow Designer](https://www.3cx.com/docs/call-flow-designer/) projects from natural-language descriptions.

Describe a call flow in plain English; opencfd produces the `.cfdproj` and `Main.flow` XML that opens directly in CFD Studio and compiles to a deployable `.zip`.

## What you get

| Command | What it does |
|---|---|
| `/opencfd:cfd-design` | Generate a full CFD source project (`.cfdproj`, `Main.flow`, `Audio/`, `Makefile`) from a call flow description |
| `/opencfd:cfd-build` | Compile a source project into a deployable `.zip` (`manifest.xml` + `Sources/Main.cs` + `Audio/*.wav`) |
| `/opencfd:cfd-validate` | Lint a project for common CFD Studio errors |

Plus:
- **Auto-validation** on every `.flow` / `.cfdproj` write — the hook catches mistakes before you do.
- **20 bundled reference demos** from 3CX (Menu, IVR, CRM lookup, Authentication, Database, Survey, Dialer, etc.) that the model consults when generating.
- **Full component + expression spec** (`references/SPEC.md`) covering the C# boilerplate template, variable system, and expression wrapping rules.

## Install

opencfd is an open-source (MIT) Claude Code plugin, hosted at [github.com/OpenCallFlowDesigner/opencfd-skills](https://github.com/OpenCallFlowDesigner/opencfd-skills).

```bash
# Inside Claude Code
/plugin marketplace add OpenCallFlowDesigner/opencfd-skills
/plugin install opencfd@opencfd
```

## Usage

### Generate a project

```
/opencfd:cfd-design An IVR that plays a welcome message, asks "Sales (1) or Support (2)?",
            transfers 1 to extension 200, transfers 2 to extension 300, and
            disconnects after an invalid entry.
```

opencfd generates the project in `projects/{Name}/` under your current working directory. Open the `.cfdproj` in CFD Studio, drop any referenced `.wav` files into `Audio/`, and you're ready to build.

### Validate

```
/opencfd:cfd-validate projects/MyIVR
```

Or call the validator directly:

```bash
python3 scripts/validate_project.py projects/MyIVR          # default: warnings for variable mismatches
python3 scripts/validate_project.py --strict projects/MyIVR # promotes mismatches to errors
```

### Build

```
/opencfd:cfd-build projects/MyIVR
```

Produces `projects/MyIVR/Output/Release/MyIVR.zip` — the deployable package you upload through the 3CX management console.

## What the validator catches

- **Variable sync** — variables referenced in `Main.flow` but declared nowhere (error); variables declared in only one of `.cfdproj` / `.flow` (warning by default, error in `--strict`).
- **Duplicate component names** — every `x:Name` must be unique across the flow.
- **GET body constraints** — `WebServiceRestComponent` with `HttpRequestType="GET"` must have empty `Content` and `ContentType`.
- **JsonXml parser attribute** — must use `Input=`, not `Text=`.
- **Menu option flags** — `IsValidOption_{N}` flags must match the branches actually defined.
- **Audio file references** — every `.wav` referenced in prompts should exist in `Audio/`.
- **Conditional branch ordering** — empty-condition branches before the end shadow later branches.
- **Required attributes** — `TransferComponent.Destination`, `ExecuteCSharpCodeComponent.Code`, etc.

Every one of the 20 bundled 3CX demos passes the validator in default mode — no false positives.

## Development

```bash
python -m pytest tests/ -v                                     # run the test suite
python scripts/validate_project.py references/3cx-official-demos/MenuDemo
```

The validator is standard-library only — no Python dependencies. The 20
bundled 3CX demos under `references/3cx-official-demos/` are the golden test
corpus; any change that regresses one of them must be fixed before merge.

## License

[MIT](./LICENSE)
