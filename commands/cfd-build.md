---
description: Compile a CFD source project to a deployable .zip package
---

You are compiling a 3CX CFD source project into a deployable `.zip` package. Read the source files (`.cfdproj` + `Main.flow`) and generate the C# code, manifest, and package.

## What to produce

```
projects/{ProjectName}/Output/Release/{ProjectName}.zip
├── manifest.xml
├── Sources/Main.cs
└── Audio/*.wav
```

## Step 1: Read the source

Read the `.cfdproj` and `Main.flow` from the project directory. If the user specified a project, use that. Otherwise look for the project in `$ARGUMENTS` or ask.

## Step 2: Read the spec

Read `${CLAUDE_PLUGIN_ROOT}/references/SPEC.md` — specifically Section 4 (Component Reference), Section 5 (Expression Language), Section 6 (Variable System), and Section 7 (Boilerplate Template). The boilerplate template in Section 7 is the exact C# skeleton to use.

## Step 3: Generate manifest.xml

```xml
<?xml version="1.0" encoding="utf-8"?>
<cfd_app_package>
  <name>{lowercase_name}.Main</name>
  <extension>{extension}</extension>
  <version>{version}</version>
</cfd_app_package>
```

## Step 4: Generate Sources/Main.cs

Use the boilerplate from SPEC.md Section 7. Fill in the substitution slots:

### Slot 1: InitializeVariables()

1. Session variables (always present, exact boilerplate)
2. Standard variables — 13 entries (RecordResult, MenuResult, UserInputResult, VoiceInputResult)
3. User variables from `.cfdproj` — `variableMap["callflow$.{Name}"] = new Variable({initial});`
4. **Duplication quirk**: After user variables, repeat all 13 standard variables with 4-space extra indentation

### Slot 2: InitializeComponents()

Walk the XML component tree and generate C# for each component. Key rules:

1. **All handlers are lambdas**: `() => { return {expression}; }`
2. **Expression wrapping**:
   - Conditions: `Convert.ToBoolean({expr})`
   - String values: `Convert.ToString({expr})`
   - Integer values: `Convert.ToInt32({expr})`
   - Variable access: `variableMap["varname"].Value`
   - Component results: direct reference (e.g., `ComponentName.ResponseContent`)
3. **MenuComponent generates a paired ConditionalComponent** — Named `{MenuName}_Conditional` with containers `{MenuName}_Conditional_Option{N}` and `{MenuName}_Conditional_TimeoutOrInvalidOption`
4. **UserInputComponent generates a paired ConditionalComponent** — Named `{Name}_Conditional` with `_ValidInput` and `_InvalidInput` containers
5. **Timeout conversion**: XML seconds × 1000 = C# milliseconds
6. **Auto-append DisconnectCall**: Always add `mainAutoAddedFinalDisconnectCall` and `errorHandlerAutoAddedFinalDisconnectCall`

### Slot 3: Inner classes (ExecuteCSharpCode only)

Class name: `{MethodName}{Hash}ECCComponent` extending `ExternalCodeExecutionComponent`. Instantiated with `new`, not `scope.CreateComponent`.

### XML → C# class mapping

| XML Component | C# Class |
|---|---|
| `WebServiceRestComponent` | `WebInteractionComponent` |
| `JsonXmlParserComponent` | `TextAnalyzerComponent` |
| `DatabaseAccessComponent` | `SqlServerDatabaseAccessComponent` |
| `AuthenticationComponent` | `AuthenticationLoopComponent` |
| `ExecuteCSharpCodeComponent` | Inner class extending `ExternalCodeExecutionComponent` |
| `CRMLookupComponent` | `CRMLookupComponent` + auto-paired `TextAnalyzerComponent` |
| `CreditCardComponent` | `CreditCardLoopComponent` (retry loop) + inner `UserInputComponent` for the card number — see SPEC §4.18 |

## Step 5: Package

Create the `.zip` in `Output/Release/` containing `manifest.xml`, `Sources/Main.cs`, and `Audio/*.wav`.

## After building

Run the validator first:
```bash
python3 "${CLAUDE_PLUGIN_ROOT}/scripts/validate_project.py" projects/{ProjectName}
```

Now build the project:

$ARGUMENTS
