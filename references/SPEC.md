# 3CX CFD Code Generator Specification

Version: 1.0
Last updated: 2026-04-06

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Input Format (YAML)](#2-input-format-yaml)
3. [Output Format (.zip)](#3-output-format-zip)
4. [Component Reference](#4-component-reference)
5. [Expression Language](#5-expression-language)
6. [Variable System](#6-variable-system)
7. [Boilerplate Template](#7-boilerplate-template)

---

## 1. Architecture Overview

### Pipeline

```
callflow.yaml ──> Generator (Python) ──> output.zip
                      │                      ├── manifest.xml
                      │                      ├── Sources/Main.cs
                      ├── validates against  └── Audio/*.wav
                      │   callflow.schema.json
                      └── copies from
                          audio/ directory
```

### Stages

1. **Parse**: Read YAML, validate against JSON Schema
2. **Model**: Build typed component tree from parsed data
3. **Codegen**: Render Main.cs using Jinja2 templates
   - Emit boilerplate (identical for all projects)
   - Emit `InitializeVariables()` from variable declarations
   - Emit `InitializeComponents()` by walking the component tree
   - Emit inner classes for ExecuteCSharpCode components
4. **Package**: Assemble .zip with manifest.xml, Sources/Main.cs, Audio/*.wav

### Technology

- **Language**: Python 3.10+
- **Templating**: Jinja2 (for the Main.cs boilerplate shell)
- **YAML parsing**: PyYAML or ruamel.yaml
- **Validation**: jsonschema (against callflow.schema.json)
- **Packaging**: zipfile (stdlib)
- **CLI**: typer or click

---

## 2. Input Format (YAML)

### Top-Level Keys

```yaml
name: string          # Project name (e.g., "ClaimsLookupIVR"). Used as C# namespace.
extension: string     # 3CX extension identifier (e.g., "claimslookup")
version: string       # CFD version string. Default: "20.2.84.0"

tts_engine: none | amazon_polly    # Text-to-speech engine
stt_engine: none                   # Speech-to-text engine (reserved)

# Amazon Polly config (only if tts_engine: amazon_polly)
amazon_polly:
  client_id: string
  client_secret: string
  region: string       # e.g., "us-east-2"

variables:             # User-defined variables (callflow$ scope)
  VariableName:
    type: string | bool | int
    initial: value     # Initial value (quoted for strings, true/false for bool)

audio:
  dir: string          # Path to directory containing .wav files

flows:
  main: []             # List of component steps
  error_handler: []    # List of component steps (usually empty)
  disconnect_handler: [] # List of component steps (usually empty)
```

### Component Step Format

Every step in a flow is a component with a `type` discriminator:

```yaml
- type: component_type   # Required. One of the types listed in Section 4.
  name: string           # Required. Unique component name (becomes C# variable name).
  # ... type-specific properties
```

### Nesting

Components that support branching (conditional, menu, loop) contain child `steps` lists:

```yaml
- type: conditional
  name: MyCondition
  branches:
    - name: BranchA
      condition: "EQUAL(someVar, \"1\")"
      steps:
        - type: transfer
          name: Transfer1
          destination: '"101"'
```

---

## 3. Output Format (.zip)

### Structure

```
output.zip
├── manifest.xml
├── Audio/
│   ├── prompt1.wav
│   └── prompt2.wav
└── Sources/
    └── Main.cs
```

### manifest.xml

```xml
<?xml version="1.0" encoding="utf-8"?>
<cfd_app_package>
  <name>{lowercase_name}.Main</name>
  <extension>{extension}</extension>
  <version>{version}</version>
</cfd_app_package>
```

- `name`: Project name lowercased, with `.Main` suffix (e.g., `claimslookupivr.Main`)
- `extension`: From YAML `extension` field
- `version`: From YAML `version` field (default `20.2.84.0`)

### Main.cs Structure

```
┌─────────────────────────────────────┐
│ using directives (fixed)            │
│ namespace {Name}                    │
│ {                                   │
│   class Main : ScriptBase<Main>...  │
│   {                                 │
│     ┌── Private fields (fixed)      │
│     ├── DisconnectCallAndExit...()  │  BOILERPLATE
│     ├── ExecuteErrorFlow()          │  (identical
│     ├── ExecuteDisconnectFlow()     │   across all
│     ├── CheckEventResult()          │   projects)
│     │                               │
│     ├── InitializeVariables() ◄─────┤  GENERATED (slot 1)
│     ├── InitializeComponents() ◄────┤  GENERATED (slot 2)
│     │                               │
│     ├── Constructor()               │  BOILERPLATE (with TTS/STT config)
│     ├── Start() / StartInternal()   │  BOILERPLATE (with name strings)
│     ├── Post*Event() methods        │  BOILERPLATE
│     ├── EventProcessingLoop()       │  BOILERPLATE
│     ├── Process*() methods          │  BOILERPLATE
│     │                               │
│     └── Inner classes ◄─────────────┤  GENERATED (slot 3, ExecuteCSharpCode only)
│   }                                 │
│ }                                   │
└─────────────────────────────────────┘
```

Substitution points:
1. **Namespace/name strings** — `{Name}` appears in namespace, logHeader, PromptQueue
2. **InitializeVariables()** — user-defined variables section
3. **InitializeComponents()** — the call flow component tree
4. **Constructor TTS/STT** — engine initialization (null or AmazonPolly)
5. **Inner classes** — ExternalCodeExecutionComponent subclasses (appended before closing braces)

---

## 4. Component Reference

### 4.1 PromptPlaybackComponent

**Purpose**: Play audio prompts (WAV files or TTS).

**YAML**:
```yaml
- type: prompt_playback
  name: PlayWelcome
  allow_dtmf: true          # Allow caller to interrupt with DTMF
  prompts:
    - type: audio_file
      file: welcome.wav
    # OR
    - type: tts
      voice: en-US-Standard-C
      voice_type: Standard
      format: Text
      text: '"Welcome to our system."'   # Expression (quoted string literal)
```

**Generated C#**:
```csharp
PromptPlaybackComponent PlayWelcome = scope.CreateComponent<PromptPlaybackComponent>("PlayWelcome");
PlayWelcome.AllowDtmfInput = true;
PlayWelcome.Prompts.Add(new AudioFilePrompt(() => { return "welcome.wav"; }));
// OR for TTS:
PlayWelcome.Prompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Welcome to our system."); }));
```

**Result properties**: None.

---

### 4.2 MenuComponent

**Purpose**: Present a DTMF menu with numbered options and branching.

**YAML**:
```yaml
- type: menu
  name: MainMenu
  allow_dtmf: true
  max_retries: 3             # Max retry attempts
  timeout: 5000              # Timeout in milliseconds
  valid_options: ["1", "2", "3"]
  prompts:
    initial:
      - type: audio_file
        file: main_menu.wav
    subsequent:
      - type: audio_file
        file: main_menu.wav
    invalid:
      - type: audio_file
        file: invalid.wav
    timeout:
      - type: audio_file
        file: timeout.wav
  options:
    "1":
      steps: [...]           # Components to execute for option 1
    "2":
      steps: [...]
    "3":
      steps: [...]
    timeout_or_invalid:      # Required. Fallback for timeout/invalid.
      steps: [...]
```

**Generated C#**:
```csharp
MenuComponent MainMenu = scope.CreateComponent<MenuComponent>("MainMenu");
MainMenu.AllowDtmfInput = true;
MainMenu.MaxRetryCount = {max_retries - 1};  // NOTE: CFD uses retries-1 internally
MainMenu.Timeout = {timeout};
MainMenu.ValidOptionList.AddRange(new char[] { '1', '2', '3' });
MainMenu.InitialPrompts.Add(new AudioFilePrompt(() => { return "{file}"; }));
MainMenu.SubsequentPrompts.Add(new AudioFilePrompt(() => { return "{file}"; }));
MainMenu.InvalidDigitPrompts.Add(new AudioFilePrompt(() => { return "{file}"; }));
MainMenu.TimeoutPrompts.Add(new AudioFilePrompt(() => { return "{file}"; }));
{container}.Add(MainMenu);

ConditionalComponent MainMenu_Conditional = scope.CreateComponent<ConditionalComponent>("MainMenu_Conditional");
{container}.Add(MainMenu_Conditional);

// For each numbered option:
MainMenu_Conditional.ConditionList.Add(() => { return MainMenu.Result == MenuComponent.MenuResults.ValidOption && MainMenu.SelectedOption == '{option}'; });
MainMenu_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("MainMenu_Conditional_Option{n}"));
// ... add child components to ContainerList[n].ComponentList

// For timeout_or_invalid:
MainMenu_Conditional.ConditionList.Add(() => { return MainMenu.Result == MenuComponent.MenuResults.InvalidOption || MainMenu.Result == MenuComponent.MenuResults.Timeout; });
MainMenu_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("MainMenu_Conditional_TimeoutOrInvalidOption"));
```

**Result properties**:
- `{name}.Result` — `MenuComponent.MenuResults.ValidOption | InvalidOption | Timeout`
- `{name}.SelectedOption` — `char` ('1', '2', etc.)

**Important**: The menu auto-generates a paired `ConditionalComponent` named `{name}_Conditional`. Branch containers follow the naming convention `{name}_Conditional_Option{n}` for numbered options and `{name}_Conditional_TimeoutOrInvalidOption` for the fallback.

---

### 4.3 TransferComponent

**Purpose**: Transfer the call to an extension, queue, or external number.

**YAML**:
```yaml
- type: transfer
  name: TransferToSales
  destination: '"101"'       # Expression. Quoted string = literal. Variable = dynamic.
  delay: 500                 # Delay in ms before transfer (default: 500)
```

**Generated C#**:
```csharp
TransferComponent TransferToSales = scope.CreateComponent<TransferComponent>("TransferToSales");
TransferToSales.DestinationHandler = () => { return Convert.ToString({destination_expr}); };
TransferToSales.DelayMilliseconds = {delay};
```

**Result properties**: None.

---

### 4.4 DisconnectCallComponent

**Purpose**: Disconnect the call.

**YAML**:
```yaml
- type: disconnect
  name: HangUp
```

**Generated C#**:
```csharp
DisconnectCallComponent HangUp = scope.CreateComponent<DisconnectCallComponent>("HangUp");
```

**Result properties**: None.

**Note**: The generator always auto-appends `mainAutoAddedFinalDisconnectCall` and `errorHandlerAutoAddedFinalDisconnectCall` at the end of `InitializeComponents()`.

---

### 4.5 ConditionalComponent

**Purpose**: If/else branching based on expressions.

**YAML**:
```yaml
- type: conditional
  name: CheckResult
  branches:
    - name: Success
      condition: 'EQUAL(LookupAccount.ResponseContent, "1")'
      steps: [...]
    - name: Failure          # Last branch with empty condition = else/default
      condition: ""
      steps: [...]
```

**Generated C#**:
```csharp
ConditionalComponent CheckResult = scope.CreateComponent<ConditionalComponent>("CheckResult");
{container}.Add(CheckResult);

// Branch with condition:
CheckResult.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.EQUAL({expr1}, {expr2})); });
CheckResult.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("Success"));
// ... add child components to ContainerList[0].ComponentList

// Default branch (empty condition = true):
CheckResult.ConditionList.Add(() => { return Convert.ToBoolean(true); });
CheckResult.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("Failure"));
```

**Rules**:
- Conditions and containers are added in matching pairs (index 0 = first branch, etc.)
- An empty `condition: ""` generates `Convert.ToBoolean(true)` (always-true / else branch)
- Container names use the branch `name` field

**Result properties**: None.

---

### 4.6 VariableAssignmentComponent

**Purpose**: Assign a value to a variable.

**YAML**:
```yaml
- type: variable_assignment
  name: SetFlag
  variable: callflow$.MyFlag        # Full variable path
  value: true                       # Expression (bool, string, or complex)
```

**Generated C#**:
```csharp
VariableAssignmentComponent SetFlag = scope.CreateComponent<VariableAssignmentComponent>("SetFlag");
SetFlag.VariableName = "callflow$.MyFlag";
SetFlag.VariableValueHandler = () => { return {value_expr}; };
```

**Result properties**: None.

---

### 4.7 LoopComponent

**Purpose**: Repeat a block of steps while a condition is true.

**YAML**:
```yaml
- type: loop
  name: MainLoop
  condition: callflow$.ContinueLooping   # Expression that evaluates to bool
  steps:
    - type: variable_assignment
      name: StopLoop
      variable: callflow$.ContinueLooping
      value: false
    # ... more steps
```

**Generated C#**:
```csharp
LoopComponent MainLoop = scope.CreateComponent<LoopComponent>("MainLoop");
MainLoop.Condition = () => { return Convert.ToBoolean(variableMap["callflow$.ContinueLooping"].Value); };
MainLoop.Container = scope.CreateComponent<SequenceContainerComponent>("MainLoop_Container");
{container}.Add(MainLoop);
// ... add child components to MainLoop.Container.ComponentList
```

**Result properties**: None.

---

### 4.8 UserInputComponent

**Purpose**: Collect DTMF digit input from the caller.

**YAML**:
```yaml
- type: user_input
  name: RequestPIN
  allow_dtmf: true
  max_retries: 3
  first_digit_timeout: 5000
  inter_digit_timeout: 3000
  final_digit_timeout: 2000
  min_digits: 3
  max_digits: 6
  valid_digits: ["0","1","2","3","4","5","6","7","8","9"]
  stop_digits: ["#"]
  prompts:
    initial:
      - type: audio_file
        file: enter_pin.wav
    subsequent:
      - type: audio_file
        file: enter_pin.wav
    invalid:
      - type: audio_file
        file: invalid.wav
    timeout:
      - type: audio_file
        file: timeout.wav
  branches:
    valid:
      steps: [...]
    invalid:
      steps: [...]
```

**Generated C#**:
```csharp
UserInputComponent RequestPIN = scope.CreateComponent<UserInputComponent>("RequestPIN");
RequestPIN.AllowDtmfInput = true;
RequestPIN.MaxRetryCount = {max_retries - 1};
RequestPIN.FirstDigitTimeout = {first_digit_timeout};
RequestPIN.InterDigitTimeout = {inter_digit_timeout};
RequestPIN.FinalDigitTimeout = {final_digit_timeout};
RequestPIN.MinDigits = {min_digits};
RequestPIN.MaxDigits = {max_digits};
RequestPIN.ValidDigitList.AddRange(new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' });
RequestPIN.StopDigitList.AddRange(new char[] { '#' });
RequestPIN.InitialPrompts.Add(new AudioFilePrompt(() => { return "{file}"; }));
RequestPIN.SubsequentPrompts.Add(new AudioFilePrompt(() => { return "{file}"; }));
RequestPIN.InvalidDigitPrompts.Add(new AudioFilePrompt(() => { return "{file}"; }));
RequestPIN.TimeoutPrompts.Add(new AudioFilePrompt(() => { return "{file}"; }));
{container}.Add(RequestPIN);

// Auto-generated conditional for branches:
ConditionalComponent RequestPIN_Conditional = scope.CreateComponent<ConditionalComponent>("RequestPIN_Conditional");
{container}.Add(RequestPIN_Conditional);
RequestPIN_Conditional.ConditionList.Add(() => { return RequestPIN.Result == UserInputComponent.UserInputResults.ValidDigits; });
RequestPIN_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("RequestPIN_Conditional_ValidInput"));
// ... valid branch children
RequestPIN_Conditional.ConditionList.Add(() => { return RequestPIN.Result == UserInputComponent.UserInputResults.InvalidDigits || RequestPIN.Result == UserInputComponent.UserInputResults.Timeout; });
RequestPIN_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("RequestPIN_Conditional_InvalidInput"));
// ... invalid branch children
```

**Result properties**:
- `{name}.Result` — `UserInputComponent.UserInputResults.ValidDigits | InvalidDigits | Timeout`
- `{name}.Buffer` — The collected digits as string

---

### 4.9 VoiceInputComponent

**Purpose**: Speech recognition with dictionary matching.

**YAML**:
```yaml
- type: voice_input
  name: AskDepartment
  dictionary: '"Sales", "Support", "Marketing"'
  language_code: en-US
  input_timeout: 3000
  max_retries: 3
  prompts:
    initial: [...]
    subsequent: [...]
    invalid_input: [...]
    timeout: [...]
  branches:
    valid:
      steps: [...]
    invalid:
      steps: [...]
```

**Generated C#**: Follows the same pattern as UserInputComponent with `VoiceInputComponent` class and `VoiceInputResults` enum. Additional properties:
- `Dictionary` — comma-separated quoted options
- `LanguageCode` — BCP 47 language code
- `InputTimeout` — speech input timeout

**Result properties**:
- `{name}.Result` — `VoiceInputComponent.VoiceInputResults.ValidInput | InvalidInput | Timeout | ValidDtmfInput`
- `{name}.DictionaryMatch` — The matched dictionary entry

---

### 4.10 WebInteractionComponent (Web Service REST)

**Purpose**: Make HTTP requests to REST APIs.

**YAML**:
```yaml
- type: web_service
  name: LookupAccount
  method: GET | POST | PUT | DELETE
  content_type: application/json
  timeout: 30000
  uri: 'CONCATENATE("https://api.example.com/path?q=", someVar)'
  content: '...'                    # Request body (for POST/PUT)
  auth:
    type: basic                     # basic | bearer | api_key | none
    username: callflow$.User        # For basic auth
    password: callflow$.Pass        # For basic auth
  headers:
    - name: X-Custom-Header
      value: '"some-value"'
```

**Generated C#**:
```csharp
WebInteractionComponent LookupAccount = scope.CreateComponent<WebInteractionComponent>("LookupAccount");
LookupAccount.HttpMethod = System.Net.Http.HttpMethod.Get;
LookupAccount.ContentType = "application/json";
LookupAccount.Timeout = {timeout};
LookupAccount.UriHandler = () => { return Convert.ToString({uri_expr}); };
LookupAccount.ContentHandler = () => { return Convert.ToString({content_expr}); };

// Basic auth generates an Authorization header:
LookupAccount.Headers.Add(new CallFlow.CFD.Parameter("Authorization", () => {
    return "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
        ({username_expr}) + ":" + ({password_expr})
    ));
}));

// Custom headers:
LookupAccount.Headers.Add(new CallFlow.CFD.Parameter("{header_name}", () => { return {header_value_expr}; }));
```

**Result properties**:
- `{name}.ResponseContent` — HTTP response body as string
- `{name}.ResponseStatusCode` — HTTP status code

---

### 4.11 TextAnalyzerComponent

**Purpose**: Parse JSON/XML responses and extract values into variables.

**YAML**:
```yaml
- type: text_analyzer
  name: ParseResponse
  text_type: json | xml | detect    # Default: detect
  text: LookupAccount.ResponseContent
  mappings:
    "response.field.path": callflow$.TargetVariable
    "response.array[0].name": callflow$.Name
```

**Generated C#**:
```csharp
TextAnalyzerComponent ParseResponse = scope.CreateComponent<TextAnalyzerComponent>("ParseResponse");
ParseResponse.TextType = TextAnalyzerComponent.TextTypes.{Detect|Json|Xml};
ParseResponse.TextHandler = () => { return Convert.ToString({text_expr}); };
ParseResponse.Mappings.Add("{json_path}", "{variable_name}");
ParseResponse.Mappings.Add("{json_path}", "{variable_name}");
```

**Result properties**: None (values written directly to mapped variables).

---

### 4.12 AuthenticationLoopComponent

**Purpose**: Request ID and PIN from caller, validate against external service.

**YAML**:
```yaml
- type: authentication
  name: AuthCaller
  max_retries: 3
  request_id:
    # UserInputComponent config for ID collection
    min_digits: 5
    max_digits: 8
    prompts: { initial: [...], subsequent: [...], invalid: [...], timeout: [...] }
  request_pin:
    # UserInputComponent config for PIN collection
    min_digits: 3
    max_digits: 6
    prompts: { initial: [...], subsequent: [...], invalid: [...], timeout: [...] }
  branches:
    valid:
      steps: [...]     # Both ID and PIN collected successfully
    invalid:
      steps: [...]     # Failed after retries
```

**Generated C#**: Creates an `AuthenticationLoopComponent` wrapping two `UserInputComponent` instances (for ID and PIN), with auto-generated conditionals. See Authentication demo output for the exact pattern.

**Result properties**:
- `{name}.ID` — Collected ID string
- `{name}.PIN` — Collected PIN string
- `{name}.Validated` — Boolean (set via VariableAssignment in validation logic)
- `{name}.LoopCounter` — Current retry iteration

---

### 4.13 SurveyComponent

**Purpose**: Multi-question phone survey with CSV export.

**YAML**:
```yaml
- type: survey
  name: CustomerFeedback
  allow_dtmf: true
  max_retries: 3
  timeout: 5000
  export_csv: '"/path/to/results.csv"'
  recordings_path: '"/path/to/recordings"'
  prompts:
    introductory: [...]
    goodbye: [...]
    invalid: [...]
    timeout: [...]
  parameters:
    - name: caller
      value: session.ani
  questions:
    - type: yes_no
      tag: solved
      yes_option: "1"
      no_option: "2"
      prompts: [...]
    - type: range
      tag: rating
      range_start: "1"
      range_end: "5"
      prompts: [...]
    - type: recording
      tag: comments
      max_recording_time: 60000
      offer_playback: true
      keep_option: "1"
      rerecord_option: "2"
      prompts: [...]
      pre_recording_prompts: [...]
      post_recording_prompts: [...]
```

**Generated C#**: See SurveyDemo output for the exact pattern. Key classes:
- `SurveyComponent`
- `YesNoSurveyQuestion(tag, prompts, yesChar, noChar)`
- `RangeSurveyQuestion(tag, prompts, charList)`
- `RecordingSurveyQuestion(tag, prompts, maxTime, offerPlayback, prePrompts, postPrompts, keepChar, rerecordChar)`

**Result properties**:
- `{name}.Result` — CSV string with all answers

---

### 4.14 SqlServerDatabaseAccessComponent

**Purpose**: Execute SQL queries against a SQL Server database.

**YAML**:
```yaml
- type: database
  name: ValidatePIN
  database_type: SqlServer
  server: '"localhost"'
  port: 1433
  database: '"CustomersDB"'
  username: '"dbuser"'
  password: '"dbpass"'
  statement_type: Scalar | NonQuery | Query
  sql: '"SELECT count(*) FROM customers WHERE id=@id"'
  use_connection_string: false
  timeout: 30000
  parameters:
    - name: id
      value: RequestPIN.Buffer
```

**Generated C#**:
```csharp
SqlServerDatabaseAccessComponent ValidatePIN = scope.CreateComponent<SqlServerDatabaseAccessComponent>("ValidatePIN");
ValidatePIN.ServerHandler = () => { return Convert.ToString({server_expr}); };
ValidatePIN.PortHandler = () => { return Convert.ToInt32({port}); };
ValidatePIN.DatabaseHandler = () => { return Convert.ToString({database_expr}); };
ValidatePIN.UserNameHandler = () => { return Convert.ToString({username_expr}); };
ValidatePIN.PasswordHandler = () => { return Convert.ToString({password_expr}); };
ValidatePIN.SqlStatementHandler = () => { return Convert.ToString({sql_expr}); };
ValidatePIN.Parameters.Add(new CallFlow.CFD.Parameter("{param_name}", () => { return {param_value_expr}; }));
ValidatePIN.UseConnectionString = false;
ValidatePIN.StatementType = DatabaseAccessComponent.StatementTypes.{Scalar|NonQuery};
ValidatePIN.Timeout = {timeout};
```

**Result properties**:
- `{name}.ScalarResult` — For Scalar queries, the single return value

---

### 4.15 ExecuteCSharpCodeComponent

**Purpose**: Execute arbitrary C# code inline.

**YAML**:
```yaml
- type: execute_csharp
  name: NormalizePhone
  method: NormalizePhone           # Method name (must be unique)
  returns_value: true
  parameters:
    - name: rawNumber
      type: String
      value: session.ani
  code: |
    if (rawNumber.StartsWith("+1")) return rawNumber.Substring(2);
    return rawNumber;
```

**Generated C# (inner class, appended before closing braces)**:
```csharp
public class NormalizePhone{HASH}ECCComponent : ExternalCodeExecutionComponent
{
    public List<CallFlow.CFD.Parameter> Parameters { get; } = new List<CallFlow.CFD.Parameter>();
    public NormalizePhone{HASH}ECCComponent(string name, ICallflow callflow, ICall myCall, string projectName)
        : base(name, callflow, myCall, projectName) {}
    protected override object ExecuteCode()
    {
        return NormalizePhone(Convert.ToString(Parameters[0].Value));
    }
    private object NormalizePhone(string rawNumber)
    {
        // User code inserted here:
        if (rawNumber.StartsWith("+1")) return rawNumber.Substring(2);
        return rawNumber;
    }
}
```

**Generated C# (instantiation in InitializeComponents)**:
```csharp
NormalizePhone{HASH}ECCComponent NormalizePhone = new NormalizePhone{HASH}ECCComponent("NormalizePhone", callflow, myCall, logHeader);
NormalizePhone.Parameters.Add(new CallFlow.CFD.Parameter("rawNumber", () => { return variableMap["session.ani"].Value; }));
{container}.Add(NormalizePhone);
```

**Class naming**: `{MethodName}{Hash}ECCComponent` where `{Hash}` is a deterministic integer hash. The generator can use any stable hash algorithm (e.g., `abs(hash(method_name)) % 10_000_000_000`).

**Result properties**:
- `{name}.ReturnValue` — The return value of the method (if `returns_value: true`)

---

### 4.16 CRMLookupComponent

**Purpose**: Look up contacts in the configured 3CX CRM integration.

**YAML**:
```yaml
- type: crm_lookup
  name: GetContact
  entity: Contacts
  lookup_by: EntityNumber
  data: session.ani
  response_mappings:
    "response.contacts[0].name": callflow$.ContactName
    "response.contacts[0].email": callflow$.ContactEmail
```

**Generated C#**: Creates a `CRMLookupComponent` followed by an auto-generated `TextAnalyzerComponent` for response mapping. See CRMLookupDemo output for the exact pattern.

**Result properties**:
- `{name}.Result` — Raw CRM response

---

### 4.17 TcxGetGlobalPropertyComponent

**Purpose**: Read a 3CX system global property.

**YAML**:
```yaml
- type: get_global_property
  name: GetCRMConfig
  property_name: '"CRMINT_DEFAULT"'
```

**Generated C#**:
```csharp
TcxGetGlobalPropertyComponent GetCRMConfig = scope.CreateComponent<TcxGetGlobalPropertyComponent>("GetCRMConfig");
GetCRMConfig.PropertyNameHandler = () => { return Convert.ToString({property_name_expr}).ToUpper(); };
```

**Result properties**:
- `{name}.PropertyValue` — The property value string

---

## 5. Expression Language

### CFDFunctions

Expressions in YAML map to `CFDFunctions.*` calls in generated C#. All calls are wrapped in appropriate `Convert.To*()`:

| YAML Expression | Generated C# |
|---|---|
| `CONCATENATE(a, b, ...)` | `CFDFunctions.CONCATENATE(Convert.ToString(a), Convert.ToString(b), ...)` |
| `EQUAL(a, b)` | `CFDFunctions.EQUAL(a, b)` |
| `CONTAINS(a, b)` | `CFDFunctions.CONTAINS(Convert.ToString(a), Convert.ToString(b))` |
| `NOT(x)` | `CFDFunctions.NOT(Convert.ToBoolean(x))` |
| `AND(a, b)` | `CFDFunctions.AND(Convert.ToBoolean(a), Convert.ToBoolean(b))` |
| `LESS_THAN(a, b)` | `CFDFunctions.LESS_THAN((IComparable)a, (IComparable)b)` |
| `GREAT_THAN(a, b)` | `CFDFunctions.GREAT_THAN((IComparable)a, (IComparable)b)` |
| `GREAT_THAN_OR_EQUAL(a, b)` | `CFDFunctions.GREAT_THAN_OR_EQUAL((IComparable)a, (IComparable)b)` |
| `LEN(s)` | `CFDFunctions.LEN(Convert.ToString(s))` |
| `MID(s, start, len)` | `CFDFunctions.MID(Convert.ToString(s), start, len)` |
| `TRIM(s)` | `CFDFunctions.TRIM(Convert.ToString(s))` |
| `TO_LONG(n)` | `CFDFunctions.TO_LONG(n)` |

### Wrapping Rules

- **In condition contexts** (ConditionalComponent, LoopComponent): Wrap outermost expression in `Convert.ToBoolean()`
- **In value contexts** (DestinationHandler, UriHandler, etc.): Wrap in `Convert.ToString()`
- **In numeric contexts** (PortHandler): Wrap in `Convert.ToInt32()`
- **String literals**: Appear as quoted strings in C# (`"value"`)
- **Boolean literals**: `true` / `false`
- **Variable references**: `variableMap["varname"].Value`
- **Component property references**: Direct property access (e.g., `LookupAccount.ResponseContent`)

### Expression Resolution

When a YAML expression references a variable or component property:

| Reference Pattern | Generated C# |
|---|---|
| `session.ani` | `variableMap["session.ani"].Value` |
| `callflow$.VarName` | `variableMap["callflow$.VarName"].Value` |
| `ComponentName.Result` | `ComponentName.Result` (direct reference) |
| `ComponentName.Buffer` | `ComponentName.Buffer` |
| `ComponentName.ResponseContent` | `ComponentName.ResponseContent` |
| `ComponentName.ReturnValue` | `ComponentName.ReturnValue` |
| `"literal string"` | `"literal string"` |
| `true` / `false` | `true` / `false` |
| `123` | `123` |

---

## 6. Variable System

### Session Variables (auto-generated, always present)

```csharp
variableMap["session.ani"] = new Variable(MyCall.Caller.CallerID);
variableMap["session.callid"] = new Variable(callID);
variableMap["session.dnis"] = new Variable(MyCall.DN.Number);
variableMap["session.did"] = new Variable(MyCall.Caller.CalledNumber);
variableMap["session.audioFolder"] = new Variable(Path.Combine(RecordingManager.Instance.AudioFolder, promptQueue.ProjectAudioFolder));
variableMap["session.transferingExtension"] = new Variable(MyCall.ReferredByDN?.Number ?? string.Empty);
variableMap["session.forwardingExtension"] = new Variable(MyCall.OnBehalfOf?.Number ?? string.Empty);
```

### Standard Variables (auto-generated, always present)

```csharp
variableMap["RecordResult.NothingRecorded"] = new Variable(RecordComponent.RecordResults.NothingRecorded);
variableMap["RecordResult.StopDigit"] = new Variable(RecordComponent.RecordResults.StopDigit);
variableMap["RecordResult.Completed"] = new Variable(RecordComponent.RecordResults.Completed);
variableMap["MenuResult.Timeout"] = new Variable(MenuComponent.MenuResults.Timeout);
variableMap["MenuResult.InvalidOption"] = new Variable(MenuComponent.MenuResults.InvalidOption);
variableMap["MenuResult.ValidOption"] = new Variable(MenuComponent.MenuResults.ValidOption);
variableMap["UserInputResult.Timeout"] = new Variable(UserInputComponent.UserInputResults.Timeout);
variableMap["UserInputResult.InvalidDigits"] = new Variable(UserInputComponent.UserInputResults.InvalidDigits);
variableMap["UserInputResult.ValidDigits"] = new Variable(UserInputComponent.UserInputResults.ValidDigits);
variableMap["VoiceInputResult.Timeout"] = new Variable(VoiceInputComponent.VoiceInputResults.Timeout);
variableMap["VoiceInputResult.InvalidInput"] = new Variable(VoiceInputComponent.VoiceInputResults.InvalidInput);
variableMap["VoiceInputResult.ValidInput"] = new Variable(VoiceInputComponent.VoiceInputResults.ValidInput);
variableMap["VoiceInputResult.ValidDtmfInput"] = new Variable(VoiceInputComponent.VoiceInputResults.ValidDtmfInput);
```

### User Variables

Declared in YAML `variables` section. Generated with `callflow$` prefix:

```yaml
variables:
  MyString:
    type: string
    initial: ""
  MyBool:
    type: bool
    initial: true
  MyInt:
    type: int
    initial: 0
```

Generates:
```csharp
// User variables
variableMap["callflow$.MyString"] = new Variable("");
variableMap["callflow$.MyBool"] = new Variable(true);
variableMap["callflow$.MyInt"] = new Variable(0);
```

### Standard Variables Duplication Quirk

The 3CX builder duplicates the standard variable block inside the user variables section. **The generator must reproduce this** for runtime compatibility:

```csharp
// User variables
variableMap["callflow$.MyVar"] = new Variable("");
    variableMap["RecordResult.NothingRecorded"] = new Variable(RecordComponent.RecordResults.NothingRecorded);
    variableMap["RecordResult.StopDigit"] = new Variable(RecordComponent.RecordResults.StopDigit);
    // ... (all 13 standard variables repeated with different indentation)
```

Note the 4-space extra indentation on the duplicated block — this matches the observed output.

---

## 7. Boilerplate Template

The complete Main.cs template is below. Substitution points are marked with `{{SLOT_NAME}}`.

```csharp
using CallFlow.CFD;
using CallFlow;
using MimeKit;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks.Dataflow;
using System.Threading.Tasks;
using System.Threading;
using System;
using TCX.Configuration;

namespace {{NAMESPACE}}
{
   public class Main : ScriptBase<Main>, ICallflow, ICallflowProcessor
   {
      private bool executionStarted;
      private bool executionFinished;
      private bool disconnectFlowPending;

      private BufferBlock<AbsEvent> eventBuffer;

      private int currentComponentIndex;
      private List<AbsComponent> mainFlowComponentList;
      private List<AbsComponent> disconnectFlowComponentList;
      private List<AbsComponent> errorFlowComponentList;
      private List<AbsComponent> currentFlowComponentList;

      private LogFormatter logFormatter;
      private TimerManager timerManager;
      private Dictionary<string, Variable> variableMap;
      private TempWavFileManager tempWavFileManager;
      private PromptQueue promptQueue;
      private OnlineServices onlineServices;
      private OfficeHoursManager officeHoursManager;

      private CfdAppScope scope;

      private void DisconnectCallAndExitCallflow()
      {
         if (currentFlowComponentList == disconnectFlowComponentList)
            logFormatter.Trace("Callflow finished...");
         else
         {
            logFormatter.Trace("Callflow finished, disconnecting call...");
            MyCall.Terminate();
         }
      }

      private async Task ExecuteErrorFlow()
      {
         if (currentFlowComponentList == errorFlowComponentList)
         {
            logFormatter.Trace("Error during error handler flow, exiting callflow...");
            DisconnectCallAndExitCallflow();
         }
         else if (currentFlowComponentList == disconnectFlowComponentList)
         {
            logFormatter.Trace("Error during disconnect handler flow, exiting callflow...");
            executionFinished = true;
         }
         else
         {
            currentFlowComponentList = errorFlowComponentList;
            currentComponentIndex = 0;
            if (errorFlowComponentList.Count > 0)
            {
               logFormatter.Trace("Start executing error handler flow...");
               await ProcessStart();
            }
            else
            {
               logFormatter.Trace("Error handler flow is empty...");
               DisconnectCallAndExitCallflow();
            }
         }
      }

      private async Task ExecuteDisconnectFlow()
      {
         currentFlowComponentList = disconnectFlowComponentList;
         currentComponentIndex = 0;
         disconnectFlowPending = false;
         if (disconnectFlowComponentList.Count > 0)
         {
            logFormatter.Trace("Start executing disconnect handler flow...");
            await ProcessStart();
         }
         else
         {
            logFormatter.Trace("Disconnect handler flow is empty...");
            executionFinished = true;
         }
      }

      private EventResults CheckEventResult(EventResults eventResult)
      {
         if (eventResult == EventResults.MoveToNextComponent && ++currentComponentIndex == currentFlowComponentList.Count)
         {
            DisconnectCallAndExitCallflow();
            return EventResults.Exit;
         }
         else if (eventResult == EventResults.Exit)
            DisconnectCallAndExitCallflow();

         return eventResult;
      }

      private void InitializeVariables(string callID)
      {
         // Call variables
         variableMap["session.ani"] = new Variable(MyCall.Caller.CallerID);
         variableMap["session.callid"] = new Variable(callID);
         variableMap["session.dnis"] = new Variable(MyCall.DN.Number);
         variableMap["session.did"] = new Variable(MyCall.Caller.CalledNumber);
         variableMap["session.audioFolder"] = new Variable(Path.Combine(RecordingManager.Instance.AudioFolder, promptQueue.ProjectAudioFolder));
         variableMap["session.transferingExtension"] = new Variable(MyCall.ReferredByDN?.Number ?? string.Empty);
         variableMap["session.forwardingExtension"] = new Variable(MyCall.OnBehalfOf?.Number ?? string.Empty);

         // Standard variables
         variableMap["RecordResult.NothingRecorded"] = new Variable(RecordComponent.RecordResults.NothingRecorded);
         variableMap["RecordResult.StopDigit"] = new Variable(RecordComponent.RecordResults.StopDigit);
         variableMap["RecordResult.Completed"] = new Variable(RecordComponent.RecordResults.Completed);
         variableMap["MenuResult.Timeout"] = new Variable(MenuComponent.MenuResults.Timeout);
         variableMap["MenuResult.InvalidOption"] = new Variable(MenuComponent.MenuResults.InvalidOption);
         variableMap["MenuResult.ValidOption"] = new Variable(MenuComponent.MenuResults.ValidOption);
         variableMap["UserInputResult.Timeout"] = new Variable(UserInputComponent.UserInputResults.Timeout);
         variableMap["UserInputResult.InvalidDigits"] = new Variable(UserInputComponent.UserInputResults.InvalidDigits);
         variableMap["UserInputResult.ValidDigits"] = new Variable(UserInputComponent.UserInputResults.ValidDigits);
         variableMap["VoiceInputResult.Timeout"] = new Variable(VoiceInputComponent.VoiceInputResults.Timeout);
         variableMap["VoiceInputResult.InvalidInput"] = new Variable(VoiceInputComponent.VoiceInputResults.InvalidInput);
         variableMap["VoiceInputResult.ValidInput"] = new Variable(VoiceInputComponent.VoiceInputResults.ValidInput);
         variableMap["VoiceInputResult.ValidDtmfInput"] = new Variable(VoiceInputComponent.VoiceInputResults.ValidDtmfInput);

         // User variables
         {{USER_VARIABLES}}
        }

      private void InitializeComponents(ICallflow callflow, ICall myCall, string logHeader)
      {
         scope = CfdModule.Instance.CreateScope(callflow, myCall, logHeader);

         {
{{MAIN_FLOW_COMPONENTS}}
            }
            {
{{DISCONNECT_FLOW_COMPONENTS}}
            }
            {
{{ERROR_FLOW_COMPONENTS}}
            }
            

         // Add a final DisconnectCall component to the main and error handler flows, in order to complete pending prompt playbacks...
         DisconnectCallComponent mainAutoAddedFinalDisconnectCall = scope.CreateComponent<DisconnectCallComponent>("mainAutoAddedFinalDisconnectCall");
         DisconnectCallComponent errorHandlerAutoAddedFinalDisconnectCall = scope.CreateComponent<DisconnectCallComponent>("errorHandlerAutoAddedFinalDisconnectCall");
         mainFlowComponentList.Add(mainAutoAddedFinalDisconnectCall);
         errorFlowComponentList.Add(errorHandlerAutoAddedFinalDisconnectCall);
      }

      public Main()
      {
         this.executionStarted = false;
         this.executionFinished = false;
         this.disconnectFlowPending = false;

         this.eventBuffer = new BufferBlock<AbsEvent>();

         this.currentComponentIndex = 0;
         this.mainFlowComponentList = new List<AbsComponent>();
         this.disconnectFlowComponentList = new List<AbsComponent>();
         this.errorFlowComponentList = new List<AbsComponent>();
         this.currentFlowComponentList = mainFlowComponentList;

         this.timerManager = new TimerManager();
         this.timerManager.OnTimeout += (state) => eventBuffer.Post(new TimeoutEvent(state));
         this.variableMap = new Dictionary<string, Variable>();

         {{TTS_ENGINE_INIT}}
         {{STT_ENGINE_INIT}}
         this.onlineServices = new OnlineServices(textToSpeechEngine, speechToTextEngine);
      }

      public override void Start()
      {
         string callID = MyCall?.Caller["chid"] ?? "Unknown";
         string logHeader = $"{{NAMESPACE}} - CallID {callID}";
         this.logFormatter = new LogFormatter(MyCall, logHeader, "Callflow");
         this.promptQueue = new PromptQueue(this, MyCall, "{{NAMESPACE}}", logHeader);
         this.tempWavFileManager = new TempWavFileManager(logFormatter);
         this.timerManager.CallStarted();
         this.officeHoursManager = new OfficeHoursManager(MyCall);

         logFormatter.Info($"ConnectionStatus:`{MyCall.Status}`");

         if (MyCall.Status == ConnectionStatus.Ringing)
            MyCall.AssureMedia().ContinueWith(_ => StartInternal(logHeader, callID));
         else
            StartInternal(logHeader, callID);
      }

      private void StartInternal(string logHeader, string callID)
      {
         logFormatter.Trace("SetBackgroundAudio to false");
         MyCall.SetBackgroundAudio(false, new string[] { });

         logFormatter.Trace("Initialize components");
         InitializeComponents(this, MyCall, logHeader);
         logFormatter.Trace("Initialize variables");
         InitializeVariables(callID);

         MyCall.OnTerminated += () => eventBuffer.Post(new CallTerminatedEvent());
         MyCall.OnDTMFInput += x => eventBuffer.Post(new DTMFReceivedEvent(x));

         logFormatter.Trace("Start executing main flow...");
         eventBuffer.Post(new StartEvent());
         Task.Run(() => EventProcessingLoop());

         
      }

      public void PostStartEvent()
      {
         eventBuffer.Post(new StartEvent());
      }

      public void PostDTMFReceivedEvent(char digit)
      {
         eventBuffer.Post(new DTMFReceivedEvent(digit));
      }

      public void PostPromptPlayedEvent()
      {
         eventBuffer.Post(new PromptPlayedEvent());
      }

      public void PostTransferFailedEvent()
      {
         eventBuffer.Post(new TransferFailedEvent());
      }

      public void PostMakeCallResultEvent(bool result)
      {
         eventBuffer.Post(new MakeCallResultEvent(result));
      }

      public void PostCallTerminatedEvent()
      {
         eventBuffer.Post(new CallTerminatedEvent());
      }

      public void PostTimeoutEvent(object state)
      {
         eventBuffer.Post(new TimeoutEvent(state));
      }

      private async Task EventProcessingLoop()
      {
         executionStarted = true;
         while (!executionFinished)
         {
            AbsEvent evt = await eventBuffer.ReceiveAsync();
            await evt?.ProcessEvent(this);
         }

         if (scope != null) scope.Dispose();
      }

      public async Task ProcessStart()
      {
         try
         {
            EventResults eventResult;
            do
            {
               AbsComponent currentComponent = currentFlowComponentList[currentComponentIndex];
               logFormatter.Trace("Start executing component '" + currentComponent.Name + "'");
               eventResult = await currentComponent.Start(timerManager, variableMap, tempWavFileManager, promptQueue);
            }
            while (CheckEventResult(eventResult) == EventResults.MoveToNextComponent);

            if (eventResult == EventResults.Exit) executionFinished = true;
         }
         catch (Exception exc)
         {
            logFormatter.Error("Error executing last component: " + exc.ToString());
            await ExecuteErrorFlow();
         }
      }

      public async Task ProcessDTMFReceived(char digit)
      {
         try
         {
            AbsComponent currentComponent = currentFlowComponentList[currentComponentIndex];
            logFormatter.Trace("OnDTMFReceived for component '" + currentComponent.Name + "' - Digit: '" + digit + "'");
            EventResults eventResult = CheckEventResult(await currentComponent.OnDTMFReceived(timerManager, variableMap, tempWavFileManager, promptQueue, digit));
            if (eventResult == EventResults.MoveToNextComponent)
            {
               if (disconnectFlowPending)
                  await ExecuteDisconnectFlow();
               else
                  await ProcessStart();
            }
            else if (eventResult == EventResults.Exit)
               executionFinished = true;
         }
         catch (Exception exc)
         {
            logFormatter.Error("Error executing last component: " + exc.ToString());
            await ExecuteErrorFlow();
         }
      }

      public async Task ProcessPromptPlayed()
      {
         try
         {
            promptQueue.NotifyPlayFinished();
            AbsComponent currentComponent = currentFlowComponentList[currentComponentIndex];
            logFormatter.Trace("OnPromptPlayed for component '" + currentComponent.Name + "'");
            EventResults eventResult = CheckEventResult(await currentComponent.OnPromptPlayed(timerManager, variableMap, tempWavFileManager, promptQueue));
            if (eventResult == EventResults.MoveToNextComponent)
            {
               if (disconnectFlowPending)
                  await ExecuteDisconnectFlow();
               else
                  await ProcessStart();
            }
            else if (eventResult == EventResults.Exit)
               executionFinished = true;
         }
         catch (Exception exc)
         {
            logFormatter.Error("Error executing last component: " + exc.ToString());
            await ExecuteErrorFlow();
         }
      }

      public async Task ProcessTransferFailed()
      {
         try
         {
            AbsComponent currentComponent = currentFlowComponentList[currentComponentIndex];
            logFormatter.Trace("OnTransferFailed for component '" + currentComponent.Name + "'");
            EventResults eventResult = CheckEventResult(await currentComponent.OnTransferFailed(timerManager, variableMap, tempWavFileManager, promptQueue));
            if (eventResult == EventResults.MoveToNextComponent)
            {
               if (disconnectFlowPending)
                  await ExecuteDisconnectFlow();
               else
                  await ProcessStart();
            }
            else if (eventResult == EventResults.Exit)
               executionFinished = true;
         }
         catch (Exception exc)
         {
            logFormatter.Error("Error executing last component: " + exc.ToString());
            await ExecuteErrorFlow();
         }
      }

      public async Task ProcessMakeCallResult(bool result)
      {
         try
         {
            AbsComponent currentComponent = currentFlowComponentList[currentComponentIndex];
            logFormatter.Trace("OnMakeCallResult for component '" + currentComponent.Name + "' - Result: '" + result + "'");
            EventResults eventResult = CheckEventResult(await currentComponent.OnMakeCallResult(timerManager, variableMap, tempWavFileManager, promptQueue, result));
            if (eventResult == EventResults.MoveToNextComponent)
            {
               if (disconnectFlowPending)
                  await ExecuteDisconnectFlow();
               else
                  await ProcessStart();
            }
            else if (eventResult == EventResults.Exit)
               executionFinished = true;
         }
         catch (Exception exc)
         {
            logFormatter.Error("Error executing last component: " + exc.ToString());
            await ExecuteErrorFlow();
         }
      }

      public async Task ProcessCallTerminated()
      {
         try
         {
            if (executionStarted)
            {
               // First notify the call termination to the current component
               AbsComponent currentComponent = currentFlowComponentList[currentComponentIndex];
               logFormatter.Trace("OnCallTerminated for component '" + currentComponent.Name + "'");

               // Don't wrap around CheckEventResult, because the call has been already disconnected, 
               // and the following action to execute depends on the returned value.
               EventResults eventResult = await currentComponent.OnCallTerminated(timerManager, variableMap, tempWavFileManager, promptQueue);
               if (eventResult == EventResults.MoveToNextComponent)
               {
                  // Next, if the current component has completed its job, execute the disconnect flow
                  await ExecuteDisconnectFlow();
               }
               else if (eventResult == EventResults.Wait)
               {
                  // If the user component needs more events, wait for it to finish, and signal here that we need to execute
                  // the disconnect handler flow of the callflow next...
                  disconnectFlowPending = true;
               }
               else if (eventResult == EventResults.Exit)
                  executionFinished = true;
            }
         }
         catch (Exception exc)
         {
            logFormatter.Error("Error executing last component: " + exc.ToString());
            await ExecuteErrorFlow();
         }
         finally
         {
            // Finally, delete temporary files
            tempWavFileManager.DeleteFilesAndFolders();
         }
      }

      public async Task ProcessTimeout(object state)
      {
         try
         {
            AbsComponent currentComponent = currentFlowComponentList[currentComponentIndex];
            logFormatter.Trace("OnTimeout for component '" + currentComponent.Name + "'");
            EventResults eventResult = CheckEventResult(await currentComponent.OnTimeout(timerManager, variableMap, tempWavFileManager, promptQueue, state));
            if (eventResult == EventResults.MoveToNextComponent)
            {
               if (disconnectFlowPending)
                  await ExecuteDisconnectFlow();
               else
                  await ProcessStart();
            }
            else if (eventResult == EventResults.Exit)
               executionFinished = true;
         }
         catch (Exception exc)
         {
            logFormatter.Error("Error executing last component: " + exc.ToString());
            await ExecuteErrorFlow();
         }
      }


{{INNER_CLASSES}}
      
   }
}
```

### TTS/STT Engine Initialization

**When `tts_engine: none`**:
```csharp
AbsTextToSpeechEngine textToSpeechEngine = null;
```

**When `tts_engine: amazon_polly`**:
```csharp
AbsTextToSpeechEngine textToSpeechEngine = new AmazonPollyTextToSpeechEngine(
    new AmazonPollySettings("{client_id}", "{client_secret}", "{region}", new List<string>() {  })
);
```

**STT engine** (always null for now):
```csharp
AbsSpeechToTextEngine speechToTextEngine = null;
```
