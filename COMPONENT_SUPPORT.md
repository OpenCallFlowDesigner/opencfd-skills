# Component Support Matrix

Coverage of 3CX Call Flow Designer components across the toolchain, mapped against the
official component list ([3CX CFD Components manual](https://www.3cx.com/docs/manual/cfd-components/)).

> The official manual page is Cloudflare-gated, so the component universe below is taken
> from `references/SPEC.md §4`, which was reverse-engineered from that manual. Display
> names are best-effort matches to the 3CX UI.

## What the columns mean

- **Spec** — documented in `references/SPEC.md §4.x` (XML → C# mapping, fields, boilerplate).
- **Validate** — has a dedicated lint in `scripts/validate_project.py` (beyond generic checks).
- **Golden** — appears in a captured, byte-exact Studio build under `golden/` — a verified
  reference the builder round-trips and Phase-1 codegen can target.
- **Status** — **Full** = documented *and* golden-verified; **Partial** = documented only,
  no golden build yet to verify the mapping.

> **Builder caveat:** the `tools/cfd` builder currently *pins* every `Main.cs` region to
> golden (no per-component generator yet). So "Golden ✓" means the component is reproduced
> byte-for-byte from a real build; independent transpilation of each component is Phase 1.

## Matrix

| 3CX component | Our C# class | Spec | Validate | Golden | Status |
|---|---|:--:|:--:|:--:|:--:|
| Play / Prompt Playback | `PromptPlaybackComponent` | §4.1 | audio + wav-format | ✓ | **Full** |
| Menu | `MenuComponent` | §4.2 | menu options | ✓ | **Full** |
| Transfer | `TransferComponent` | §4.3 | transfer dest | ✓ | **Full** |
| Disconnect Call | `DisconnectCallComponent` | §4.4 | — | ✓ | **Full** |
| Conditional | `ConditionalComponent` | §4.5 | branch order | ✓ | **Full** |
| Variable Assignment | `VariableAssignmentComponent` | §4.6 | var assign | ✓ | **Full** |
| Loop | `LoopComponent` | §4.7 | — | ✓ | **Full** |
| User Input | `UserInputComponent` | §4.8 | — | ✓ | **Full** |
| Voice Input (speech recog.) | `VoiceInputComponent` | §4.9 | — | ✓ | **Full** |
| Web Service (REST) | `WebInteractionComponent` | §4.10 | GET body | ✓ | **Full** |
| JSON/XML Parser | `TextAnalyzerComponent` | §4.11 | parser input | ✓ | **Full** |
| Authenticate | `AuthenticationLoopComponent` | §4.12 | — | ✓ | **Full** |
| Survey | `SurveyComponent` | §4.13 | — | ✓ | **Full** |
| Database Access | `SqlServerDatabaseAccessComponent` | §4.14 | — | ✓ | **Full** |
| Execute C# Code | `ExecuteCSharpCodeComponent` (ECC) | §4.15 | C# code | ✓ | **Full** |
| CRM Lookup | `CRMLookupComponent` | §4.16 | — | ✓ | **Full** |
| Get Global Property | `TcxGetGlobalPropertyComponent` | §4.17 | — | ✓ | **Full** |
| Credit Card | `CreditCardLoopComponent` | §4.18 | — | ✓ | **Full** |
| Component (custom `.comp`) | `AbsUserComponent` subclass | §4.19 | comp refs | ✓ | **Full** |
| Make Call | `MakeCallComponent` | §4.20 | — | ✓ | **Full** |
| Set Global Property | `TcxSetGlobalPropertyComponent` | §4.21 | — | ✓ | **Full** |
| Record | `RecordComponent` | §4.22 | — | ✓ | **Full** |
| Send Email | `EMailSenderComponent` | §4.23 | — | ✓ | **Full** |
| Increment Variable | `IncrementVariableComponent` | §4.26 | — | ✓ | **Full** |
| Date/Time Conditional | `ConditionalComponent` (date/time) | §4.27 | — | ✓¹ | **Full** |
| Logger | `LoggerComponent` | §4.24 | — | —² | **Partial** |
| File Management | `FileManagementComponent` | §4.25 | — | —² | **Partial** |

**Totals:** 25 Full · 2 Partial · 27 documented.

¹ Date/Time Conditional is emitted as a plain `ConditionalComponent`; the `DateTimeRouting`
demo golden exercises it, but it isn't independently distinguishable in the generated C#.

² Logger and File Management were demo-verified during 0.1.5 dev against `OutboundDialerDemo`,
but that demo's build emits `Sources/Callflow.cs` (not `Main.cs`), so no golden is captured
in `golden/` yet. Spec + validator cover them; only the golden oracle is missing.

## Not separately modeled

- **Text-to-Speech / Speech-to-Text** — 3CX ships `TextToSpeechDemo` / `SpeechToTextDemo`,
  but TTS/STT are handled through prompt playback (TTS prompts) and `VoiceInputComponent`
  (STT) rather than as standalone components. No golden build captured yet — treat as
  **Partial** until one is.
- Any 3CX component not listed above is currently **unmapped** (no SPEC section, validator
  check, or golden). If you hit one, capture a Studio build of a demo that uses it and add
  a `golden/{Demo}/` entry — that's the on-ramp.

## How to promote Partial → Full

1. Build a demo that uses the component in 3CX Studio; export the `.zip`.
2. Extract `Sources/Main.cs` + `manifest.xml` + `audio_manifest.txt` into `golden/{Demo}/`.
3. Add the demo to `GOLDEN_PROJECTS` in `tools/cfd/tests/test_golden.py`.
4. (Phase 1) Implement the component's generator and flip its `SLOT_MODE` to `generate`.
