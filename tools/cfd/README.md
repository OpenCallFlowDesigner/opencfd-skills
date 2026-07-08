# `cfd` — deterministic 3CX CFD builder (scaffold)

Our own `validate → build`, replacing the Windows-only 3CX Studio GUI and the
LLM-driven `cfd-build`. Correctness is enforced by **golden diff** against a real
Studio build. See `../../BUILD_TOOLING_DESIGN.md` for the full rationale.

## Layout

```
tools/cfd/
  cfd_build.py            # CLI: validate -> transpile -> package
  extract_boilerplate.py  # (re)generate the slotted template from a golden Main.cs
  boilerplate/
    Main.cs.tmpl          # fixed 3CX skeleton with 4 slots (auto-generated)
    golden_init_vars.cs        # reference bodies (auto-generated from golden)
    golden_init_components.cs
    golden_ecc.cs
  tests/test_golden.py    # correctness gate: generated Main.cs == golden (normalized)
golden/{Project}/         # ground truth captured from 3CX Studio builds
  Sources/Main.cs
  manifest.xml
  audio_manifest.txt      # wavs Studio actually bundled (pruning oracle)
```

## Usage

```bash
# build (validate + transpile + package -> Output/Release/{Project}.zip)
python3 tools/cfd/cfd_build.py references/3cx-official-demos/CreditCard \
    --extension 1975 --cfd-version 20.2.84.0

# correctness gate
python3 -m pytest tools/cfd/tests -q
python3 tools/cfd/tests/test_golden.py CreditCard   # standalone unified diff
```

## How it works

`Main.cs` is ~90% fixed boilerplate; only three regions vary per project. The template
carries the skeleton with slots; the builder fills them:

| Slot | Status | Notes |
|---|---|---|
| `NAMESPACE`, `PROJECT_NAME` | ✅ generated | project name |
| `INIT_VARIABLES` | 📌 **pinned to golden** | generator works for CreditCardCapture but not yet general (§6 scope `project$`/`callflow$`, vars in `Main.flow` vs `.cfdproj`) — Phase 1 |
| `INIT_COMPONENTS` | 📌 **pinned to golden** | Phase 1: XML-driven visitor over `Main.flow` |
| `ECC_CLASSES` | 📌 **pinned to golden** | Phase 1: emit `ExecuteCSharpCode` inner classes |

The scaffold's value today is the **pipeline + golden harness + corpus** (9 demo goldens),
not a general generator. All slots pin to golden, so `cfd build` round-trips any demo whose
golden is captured; Phase 1 flips slots to `generate` one at a time, keeping the diff green.

Flip a slot from pin → generate in `SLOT_MODE` only once its generator reproduces the
golden. The pin keeps `cfd build` producing a correct, deployable `.zip` today.

## Quirks the golden proved (a spec/LLM build gets these wrong)

- `MaxRetryCount` XML `3` → C# `2` (off-by-one).
- `HasToPauseRecording = true` auto-injected on card-number/expiration input (PCI).
- Timeouts ×1000 (`"5"` → `5000`).
- Empty conditional else-branch → `Convert.ToBoolean(true)`.
- User variables emitted in **both** `project$.` and `callflow$.` scopes, then the 13
  standard vars **repeated** (extra-indented) — the duplication quirk.

## Known gaps (Phase 1)

1. **Audio pruning** — `referenced_audio()` greps every `AudioFileName`, so it wrongly
   includes wavs from **disabled** sub-flows (e.g. `enter_credit_card_security_code.wav`
   when `IsSecurityCodeRequired=False`). Studio prunes these. Expected set is captured in
   `golden/{Project}/audio_manifest.txt` (7 wavs vs. our 8). Fix: walk the component tree
   and skip disabled sub-trees, not the raw XML text.
2. **`INIT_COMPONENTS` generator** — the real transpiler. Target: reproduce
   `golden_init_components.cs` from `Main.flow` for the 8 components this project uses
   (ExecuteCSharpCode, VariableAssignment, Logger, CreditCard→CreditCardLoop,
   WebServiceRest→WebInteraction, Conditional, PromptPlayback, DisconnectCall).
3. **`ECC_CLASSES` hash** — currently normalized out in the diff. Determine whether
   `readCallSid1018470446ECCComponent`'s number is a reproducible hash of the code; if so,
   generate it and drop the normalize rule for an exact diff.
4. **`<extension>` origin** — `1975` isn't in `.cfdproj`; passed as a build param. Confirm
   where Studio sources it and whether to store it in per-project build config.

## Regenerating the template

If a new/updated golden lands, rebuild the template + references:

```bash
python3 tools/cfd/extract_boilerplate.py CreditCard
```
