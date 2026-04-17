---
description: Generate a 3CX CFD source project from a call flow description
---

You are generating a 3CX Call Flow Designer (CFD) source project. The user will describe a call flow and you will produce the XML source files that open in CFD Studio.

## What to generate

```
projects/{ProjectName}/
├── {ProjectName}.cfdproj       ← Project manifest
├── Main.flow                   ← Call flow definition (XML)
├── Audio/                      ← Directory for WAV files
├── Libraries/                  ← Empty (for external libs)
├── Output/Release/             ← Build output lands here
└── Makefile                    ← make build / make clean
```

Projects are generated into `projects/` at the user's current working directory (not inside the plugin).

## Step 1: Understand the call flow

Ask the user to describe their call flow if they haven't already. Identify which components are needed. Common patterns:

- **IVR menu**: MenuComponent + ConditionalComponent + TransferComponent
- **API lookup + routing**: WebServiceRestComponent + JsonXmlParserComponent + ConditionalComponent
- **Caller authentication**: AuthenticationComponent or UserInputComponent + WebServiceRestComponent
- **Custom logic**: ExecuteCSharpCodeComponent

## Step 2: Generate the .cfdproj

```xml
<?xml version="1.0" encoding="utf-8"?>
<Graphical_Application_Designer_Project>
  <Version>2.1</Version>
  <Files>
    <File path="Main.flow" type="callflow" />
  </Files>
  <Variables>
    <ArrayOfVariable xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <Variable>
        <Name>{VarName}</Name>
        <InitialValue>{quoted_value}</InitialValue>
        <ShowScopeProperty>false</ShowScopeProperty>
        <DebuggerVisible>true</DebuggerVisible>
        <HelpText />
      </Variable>
    </ArrayOfVariable>
  </Variables>
  <DebugBuildSuccessful>False</DebugBuildSuccessful>
  <ReleaseBuildSuccessful>False</ReleaseBuildSuccessful>
  <DebugBuildNumber>0</DebugBuildNumber>
  <ReleaseBuildNumber>0</ReleaseBuildNumber>
  <ChangedSinceLastDebugBuild>True</ChangedSinceLastDebugBuild>
  <AmazonClientID></AmazonClientID>
  <AmazonClientSecret></AmazonClientSecret>
  <AmazonRegion>us-east-2</AmazonRegion>
</Graphical_Application_Designer_Project>
```

## Step 3: Generate Main.flow

**CRITICAL: The `<Variables>` section must mirror the variables from `.cfdproj` exactly.**

```xml
<?xml version="1.0" encoding="utf-8"?>
<File>
  <Version>2.1</Version>
  <Variables>
    <ArrayOfVariable xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <!-- REQUIRED: All user-defined variables must be listed here too -->
      <Variable>
        <Name>{VarName}</Name>
        <InitialValue>{quoted_value}</InitialValue>
        <ShowScopeProperty>false</ShowScopeProperty>
        <DebuggerVisible>true</DebuggerVisible>
        <HelpText />
      </Variable>
    </ArrayOfVariable>
  </Variables>
  <Flows>
    <MainFlow>
      <ns0:MainFlow Description="Callflow execution path." DebugModeActive="False"
        x:Name="Main"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ns0="clr-namespace:TCX.CFD.Classes.Components;Assembly=3CX Call Flow Designer, Version=20.0.0.0, Culture=neutral, PublicKeyToken=7cb95a1a133e706e">

        <!-- Components go here -->

      </ns0:MainFlow>
    </MainFlow>
    <ErrorHandlerFlow>
      <ns0:ErrorHandlerFlow Description="Execution path when an error ocurrs."
        DebugModeActive="False" x:Name="Main"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ns0="clr-namespace:TCX.CFD.Classes.Components;Assembly=3CX Call Flow Designer, Version=20.0.0.0, Culture=neutral, PublicKeyToken=7cb95a1a133e706e" />
    </ErrorHandlerFlow>
    <DisconnectHandlerFlow>
      <ns0:DisconnectHandlerFlow Description="Execution path since the call gets disconnected."
        DebugModeActive="False" x:Name="Main"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ns0="clr-namespace:TCX.CFD.Classes.Components;Assembly=3CX Call Flow Designer, Version=20.0.0.0, Culture=neutral, PublicKeyToken=7cb95a1a133e706e" />
    </DisconnectHandlerFlow>
  </Flows>
</File>
```

## XML Component Reference

All components share: `DebugModeActive="False"`, `x:Name="{name}"`, optionally `Tag=""`.

### Prompt encoding

Prompts are encoded as escaped XML inside an attribute:
```xml
PromptList="&lt;?xml version=&quot;1.0&quot; encoding=&quot;utf-16&quot;?&gt;&lt;ArrayOfPrompt xmlns:xsd=&quot;http://www.w3.org/2001/XMLSchema&quot; xmlns:xsi=&quot;http://www.w3.org/2001/XMLSchema-instance&quot;&gt;&lt;Prompt xsi:type=&quot;AudioFilePrompt&quot;&gt;&lt;AudioFileName&gt;{filename}.wav&lt;/AudioFileName&gt;&lt;/Prompt&gt;&lt;/ArrayOfPrompt&gt;"
```

For TTS prompts, use `xsi:type="TextToSpeechAudioPrompt"` with `VoiceName`, `VoiceType`, `Format`, and `Text` elements.

### PromptPlaybackComponent
```xml
<ns0:PromptPlaybackComponent AcceptDtmfInput="True" DebugModeActive="False"
  PromptList="{escaped_prompt_xml}" x:Name="{name}" />
```

### MenuComponent
```xml
<ns0:MenuComponent AcceptDtmfInput="True" IsValidOption_Star="False" Timeout="{seconds}"
  x:Name="{name}" MaxRetryCount="{count}"
  IsValidOption_0="False" IsValidOption_1="True" IsValidOption_2="True" IsValidOption_3="False"
  IsValidOption_4="False" IsValidOption_5="False" IsValidOption_6="False" IsValidOption_7="False"
  IsValidOption_8="False" IsValidOption_9="False" IsValidOption_Pound="False"
  InitialPromptList="{escaped}" SubsequentPromptList="{escaped}"
  InvalidDigitPromptList="{escaped}" TimeoutPromptList="{escaped}"
  DebugModeActive="False">
  <ns0:MenuComponentBranch Option="Option1" Description="{desc}" DebugModeActive="False" x:Name="{branch_name}">
    <!-- child components -->
  </ns0:MenuComponentBranch>
  <ns0:MenuComponentBranch Option="Option2" ... />
  <ns0:MenuComponentBranch Option="TimeoutOrInvalidOption" ... />
</ns0:MenuComponent>
```
- Set `IsValidOption_{N}="True"` for each valid digit option.
- Timeout is in **seconds** (not milliseconds like C#).

### TransferComponent
```xml
<ns0:TransferComponent TransferToVoicemail="False" Destination="{expression}"
  DebugModeActive="False" DelayMilliseconds="500" x:Name="{name}" />
```
- Literal destinations: `Destination="&quot;101&quot;"`
- Dynamic destinations: `Destination="callflow$.CaseManagerExt"`

### DisconnectCallComponent
```xml
<ns0:DisconnectCallComponent x:Name="{name}" DebugModeActive="False" />
```

### ConditionalComponent
```xml
<ns0:ConditionalComponent Tag="" DebugModeActive="False" x:Name="{name}">
  <ns0:ConditionalComponentBranch Condition="{expression}" Description="{desc}"
    Tag="" DebugModeActive="False" x:Name="{branch_name}">
    <!-- child components -->
  </ns0:ConditionalComponentBranch>
  <ns0:ConditionalComponentBranch Condition="" Description="Default/else"
    Tag="" DebugModeActive="False" x:Name="{branch_name}">
    <!-- child components (empty Condition = else/default) -->
  </ns0:ConditionalComponentBranch>
</ns0:ConditionalComponent>
```

### VariableAssignmentComponent
```xml
<ns0:VariableAssignmentComponent VariableName="callflow$.{VarName}"
  DebugModeActive="False" Expression="{value_expression}" x:Name="{name}" />
```

### LoopComponent
```xml
<ns0:LoopComponent Condition="{expression}" Description="{desc}"
  DebugModeActive="False" x:Name="{name}">
  <!-- child components -->
</ns0:LoopComponent>
```

### UserInputComponent
```xml
<ns0:UserInputComponent AcceptDtmfInput="True" FinalDigitTimeout="{sec}" StopDigit="DigitPound"
  MinDigits="{n}" MaxDigits="{n}" MaxRetryCount="{n}" FirstDigitTimeout="{sec}"
  InterDigitTimeout="{sec}"
  IsValidDigit_0="True" IsValidDigit_1="True" ... IsValidDigit_9="True"
  IsValidDigit_Star="False" IsValidDigit_Pound="False"
  InitialPromptList="{escaped}" SubsequentPromptList="{escaped}"
  InvalidDigitPromptList="{escaped}" TimeoutPromptList="{escaped}"
  DebugModeActive="False" x:Name="{name}">
  <ns0:ComponentBranch DisplayedText="Valid Input" Description="..." DebugModeActive="False" x:Name="{branch_name}">
    <!-- child components -->
  </ns0:ComponentBranch>
  <ns0:ComponentBranch DisplayedText="Invalid Input" Description="..." DebugModeActive="False" x:Name="{branch_name}">
    <!-- child components -->
  </ns0:ComponentBranch>
</ns0:UserInputComponent>
```
- Timeouts in **seconds** in XML (converted to milliseconds in C#).

### WebServiceRestComponent
```xml
<ns0:WebServiceRestComponent Timeout="{seconds}" x:Name="{name}"
  HttpRequestType="GET" URI="{expression}"
  ContentType="{content_type}" Content="{expression}"
  AuthenticationType="None" AuthenticationUserName="" AuthenticationPassword=""
  AuthenticationApiKey="" AuthenticationOAuth2AccessToken=""
  HeaderList="{escaped_parameter_xml}"
  DebugModeActive="False" />
```
**IMPORTANT for GET requests:** Both `Content` and `ContentType` must be empty strings (`Content="" ContentType=""`).

### JsonXmlParserComponent
```xml
<ns0:JsonXmlParserComponent TextType="JSON" Input="{source_expression}"
  ResponseMappingsList="{escaped_mapping_xml}"
  Tag="" DebugModeActive="False" x:Name="{name}" />
```
**IMPORTANT:** The input attribute is `Input=` (NOT `Text=`).

ResponseMappingsList escaped XML:
```xml
<ArrayOfResponseMapping>
  <ResponseMapping>
    <Path>{json.path}</Path>
    <Variable>callflow$.{VarName}</Variable>
  </ResponseMapping>
</ArrayOfResponseMapping>
```

### ExecuteCSharpCodeComponent
```xml
<ns0:ExecuteCSharpCodeComponent ReturnsValue="True"
  Code="{escaped_csharp_code}"
  ParameterList="{escaped_parameter_xml}"
  MethodName="{method_name}" Tag="" DebugModeActive="False" x:Name="{name}" />
```

ParameterList escaped XML:
```xml
<ArrayOfScriptParameter>
  <ScriptParameter>
    <Name>{param_name}</Name>
    <Value>{source_expression}</Value>
    <Type>{String|Int32|Boolean}</Type>
  </ScriptParameter>
</ArrayOfScriptParameter>
```

### DatabaseAccessComponent
```xml
<ns0:DatabaseAccessComponent Port="{port}" Timeout="{seconds}" UserName="{expr}"
  x:Name="{name}" Password="{expr}" UseConnectionString="False"
  StatementType="Scalar" Database="{expr}" DatabaseType="SqlServer"
  SqlStatement="{constant_sql}" ParameterList="{escaped_param_xml}"
  ConnectionString="" Server="{expr}" DebugModeActive="False" />
```
- SQL must be a constant string (no CONCATENATE). Use ParameterList for variable parts.

### SurveyComponent, CRMLookupComponent, AuthenticationComponent

See the bundled reference demos under `${CLAUDE_PLUGIN_ROOT}/references/3cx-official-demos/` — specifically `SurveyDemo/Main.flow`, `CRMLookupDemo/Main.flow`, and `Authentication/Main.flow`.

## Step 4: List required audio files

After generating the source, list all `.wav` files referenced by prompts that the user needs to provide.

## Step 5: Generate the Makefile

```makefile
PROJECT_NAME = {ProjectName}
OUTPUT_DIR = Output/Release

build:
	@echo "Building $(PROJECT_NAME)..."
	@mkdir -p $(OUTPUT_DIR)
	@echo "Build complete."

clean:
	rm -rf $(OUTPUT_DIR)
```

## After generating

Run the validator:
```bash
python3 "${CLAUDE_PLUGIN_ROOT}/scripts/validate_project.py" projects/{ProjectName}
```

Now generate the project based on the user's description:

$ARGUMENTS
