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

namespace CRMLookupDemo
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
         variableMap["callflow$.FirstName"] = new Variable("");
            variableMap["callflow$.LastName"] = new Variable("");
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
            
        }

      private void InitializeComponents(ICallflow callflow, ICall myCall, string logHeader)
      {
         scope = CfdModule.Instance.CreateScope(callflow, myCall, logHeader);

         {
            TcxGetGlobalPropertyComponent GetConfiguredCRM = scope.CreateComponent<TcxGetGlobalPropertyComponent>("GetConfiguredCRM");
            GetConfiguredCRM.PropertyNameHandler = () => { return Convert.ToString("CRMINT_DEFAULT").ToUpper(); };
            mainFlowComponentList.Add(GetConfiguredCRM);
            ConditionalComponent SelectCRM = scope.CreateComponent<ConditionalComponent>("SelectCRM");
            mainFlowComponentList.Add(SelectCRM);
            SelectCRM.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.CONTAINS(Convert.ToString(GetConfiguredCRM.PropertyValue),Convert.ToString("amocrm"))); });
            SelectCRM.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("amoCRM"));
            CRMLookupComponent GetContactFromAmoCRM = scope.CreateComponent<CRMLookupComponent>("GetContactFromAmoCRM");
            GetContactFromAmoCRM.EntityType = CRMLookupComponent.EntityTypes.Contacts;
            GetContactFromAmoCRM.QueryType = CRMLookupComponent.QueryTypes.LookupNumber;
            GetContactFromAmoCRM.DataHandler = () => { return Convert.ToString(variableMap["session.ani"].Value); };
            SelectCRM.ContainerList[0].ComponentList.Add(GetContactFromAmoCRM);
            TextAnalyzerComponent GetContactFromAmoCRM_ResponseAnalyzer = scope.CreateComponent<TextAnalyzerComponent>("GetContactFromAmoCRM");
            GetContactFromAmoCRM_ResponseAnalyzer.TextType = TextAnalyzerComponent.TextTypes.Detect;
            GetContactFromAmoCRM_ResponseAnalyzer.TextHandler = () => { return Convert.ToString(GetContactFromAmoCRM.Result); };
            GetContactFromAmoCRM_ResponseAnalyzer.Mappings.Add("response.contacts[0].name", "callflow$.FirstName");
            SelectCRM.ContainerList[0].ComponentList.Add(GetContactFromAmoCRM_ResponseAnalyzer);
            SelectCRM.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.CONTAINS(Convert.ToString(GetConfiguredCRM.PropertyValue),Convert.ToString("Bitrix"))); });
            SelectCRM.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("Bitrix"));
            CRMLookupComponent GetContactFromBitrix = scope.CreateComponent<CRMLookupComponent>("GetContactFromBitrix");
            GetContactFromBitrix.EntityType = CRMLookupComponent.EntityTypes.Contacts;
            GetContactFromBitrix.QueryType = CRMLookupComponent.QueryTypes.LookupNumber;
            GetContactFromBitrix.DataHandler = () => { return Convert.ToString(variableMap["session.ani"].Value); };
            SelectCRM.ContainerList[1].ComponentList.Add(GetContactFromBitrix);
            TextAnalyzerComponent GetContactFromBitrix_ResponseAnalyzer = scope.CreateComponent<TextAnalyzerComponent>("GetContactFromBitrix");
            GetContactFromBitrix_ResponseAnalyzer.TextType = TextAnalyzerComponent.TextTypes.Detect;
            GetContactFromBitrix_ResponseAnalyzer.TextHandler = () => { return Convert.ToString(GetContactFromBitrix.Result); };
            GetContactFromBitrix_ResponseAnalyzer.Mappings.Add("result[0].NAME", "callflow$.FirstName");
            GetContactFromBitrix_ResponseAnalyzer.Mappings.Add("result[0].LAST_NAME", "callflow$.LastName");
            SelectCRM.ContainerList[1].ComponentList.Add(GetContactFromBitrix_ResponseAnalyzer);
            SelectCRM.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.CONTAINS(Convert.ToString(GetConfiguredCRM.PropertyValue),Convert.ToString("connectWise"))); });
            SelectCRM.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("ConnectWise"));
            CRMLookupComponent GetContactFromConnectWise = scope.CreateComponent<CRMLookupComponent>("GetContactFromConnectWise");
            GetContactFromConnectWise.EntityType = CRMLookupComponent.EntityTypes.Contacts;
            GetContactFromConnectWise.QueryType = CRMLookupComponent.QueryTypes.LookupNumber;
            GetContactFromConnectWise.DataHandler = () => { return Convert.ToString(variableMap["session.ani"].Value); };
            SelectCRM.ContainerList[2].ComponentList.Add(GetContactFromConnectWise);
            TextAnalyzerComponent GetContactFromConnectWise_ResponseAnalyzer = scope.CreateComponent<TextAnalyzerComponent>("GetContactFromConnectWise");
            GetContactFromConnectWise_ResponseAnalyzer.TextType = TextAnalyzerComponent.TextTypes.Detect;
            GetContactFromConnectWise_ResponseAnalyzer.TextHandler = () => { return Convert.ToString(GetContactFromConnectWise.Result); };
            GetContactFromConnectWise_ResponseAnalyzer.Mappings.Add("[0].firstName", "callflow$.FirstName");
            GetContactFromConnectWise_ResponseAnalyzer.Mappings.Add("[0].lastName", "callflow$.LastName");
            SelectCRM.ContainerList[2].ComponentList.Add(GetContactFromConnectWise_ResponseAnalyzer);
            SelectCRM.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.CONTAINS(Convert.ToString(GetConfiguredCRM.PropertyValue),Convert.ToString("Microsoft Dynamics 365"))); });
            SelectCRM.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("Dynamics365"));
            CRMLookupComponent GetContactFromDynamics365 = scope.CreateComponent<CRMLookupComponent>("GetContactFromDynamics365");
            GetContactFromDynamics365.EntityType = CRMLookupComponent.EntityTypes.Contacts;
            GetContactFromDynamics365.QueryType = CRMLookupComponent.QueryTypes.LookupNumber;
            GetContactFromDynamics365.DataHandler = () => { return Convert.ToString(variableMap["session.ani"].Value); };
            SelectCRM.ContainerList[3].ComponentList.Add(GetContactFromDynamics365);
            TextAnalyzerComponent GetContactFromDynamics365_ResponseAnalyzer = scope.CreateComponent<TextAnalyzerComponent>("GetContactFromDynamics365");
            GetContactFromDynamics365_ResponseAnalyzer.TextType = TextAnalyzerComponent.TextTypes.Detect;
            GetContactFromDynamics365_ResponseAnalyzer.TextHandler = () => { return Convert.ToString(GetContactFromDynamics365.Result); };
            GetContactFromDynamics365_ResponseAnalyzer.Mappings.Add("value[0].firstname", "callflow$.FirstName");
            GetContactFromDynamics365_ResponseAnalyzer.Mappings.Add("value[0].lastname", "callflow$.LastName");
            SelectCRM.ContainerList[3].ComponentList.Add(GetContactFromDynamics365_ResponseAnalyzer);
            SelectCRM.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.CONTAINS(Convert.ToString(GetConfiguredCRM.PropertyValue),Convert.ToString("EveryoneAPI"))); });
            SelectCRM.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("EveryoneAPI"));
            CRMLookupComponent GetContactFromEveryoneAPI = scope.CreateComponent<CRMLookupComponent>("GetContactFromEveryoneAPI");
            GetContactFromEveryoneAPI.EntityType = CRMLookupComponent.EntityTypes.Contacts;
            GetContactFromEveryoneAPI.QueryType = CRMLookupComponent.QueryTypes.LookupNumber;
            GetContactFromEveryoneAPI.DataHandler = () => { return Convert.ToString(variableMap["session.ani"].Value); };
            SelectCRM.ContainerList[4].ComponentList.Add(GetContactFromEveryoneAPI);
            TextAnalyzerComponent GetContactFromEveryoneAPI_ResponseAnalyzer = scope.CreateComponent<TextAnalyzerComponent>("GetContactFromEveryoneAPI");
            GetContactFromEveryoneAPI_ResponseAnalyzer.TextType = TextAnalyzerComponent.TextTypes.Detect;
            GetContactFromEveryoneAPI_ResponseAnalyzer.TextHandler = () => { return Convert.ToString(GetContactFromEveryoneAPI.Result); };
            GetContactFromEveryoneAPI_ResponseAnalyzer.Mappings.Add("data.expanded_name.first", "callflow$.FirstName");
            GetContactFromEveryoneAPI_ResponseAnalyzer.Mappings.Add("data.expanded_name.last", "callflow$.LastName");
            SelectCRM.ContainerList[4].ComponentList.Add(GetContactFromEveryoneAPI_ResponseAnalyzer);
            SelectCRM.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.CONTAINS(Convert.ToString(GetConfiguredCRM.PropertyValue),Convert.ToString("Freshdesk"))); });
            SelectCRM.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("Freshdesk"));
            CRMLookupComponent GetContactFromFreshdesk = scope.CreateComponent<CRMLookupComponent>("GetContactFromFreshdesk");
            GetContactFromFreshdesk.EntityType = CRMLookupComponent.EntityTypes.Contacts;
            GetContactFromFreshdesk.QueryType = CRMLookupComponent.QueryTypes.LookupNumber;
            GetContactFromFreshdesk.DataHandler = () => { return Convert.ToString(variableMap["session.ani"].Value); };
            SelectCRM.ContainerList[5].ComponentList.Add(GetContactFromFreshdesk);
            TextAnalyzerComponent GetContactFromFreshdesk_ResponseAnalyzer = scope.CreateComponent<TextAnalyzerComponent>("GetContactFromFreshdesk");
            GetContactFromFreshdesk_ResponseAnalyzer.TextType = TextAnalyzerComponent.TextTypes.Detect;
            GetContactFromFreshdesk_ResponseAnalyzer.TextHandler = () => { return Convert.ToString(GetContactFromFreshdesk.Result); };
            GetContactFromFreshdesk_ResponseAnalyzer.Mappings.Add("[0].name", "callflow$.FirstName");
            SelectCRM.ContainerList[5].ComponentList.Add(GetContactFromFreshdesk_ResponseAnalyzer);
            SelectCRM.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.CONTAINS(Convert.ToString(GetConfiguredCRM.PropertyValue),Convert.ToString("Freshsales"))); });
            SelectCRM.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("Freshsales"));
            CRMLookupComponent GetContactFromFreshsales = scope.CreateComponent<CRMLookupComponent>("GetContactFromFreshsales");
            GetContactFromFreshsales.EntityType = CRMLookupComponent.EntityTypes.Contacts;
            GetContactFromFreshsales.QueryType = CRMLookupComponent.QueryTypes.LookupNumber;
            GetContactFromFreshsales.DataHandler = () => { return Convert.ToString(variableMap["session.ani"].Value); };
            SelectCRM.ContainerList[6].ComponentList.Add(GetContactFromFreshsales);
            TextAnalyzerComponent GetContactFromFreshsales_ResponseAnalyzer = scope.CreateComponent<TextAnalyzerComponent>("GetContactFromFreshsales");
            GetContactFromFreshsales_ResponseAnalyzer.TextType = TextAnalyzerComponent.TextTypes.Detect;
            GetContactFromFreshsales_ResponseAnalyzer.TextHandler = () => { return Convert.ToString(GetContactFromFreshsales.Result); };
            GetContactFromFreshsales_ResponseAnalyzer.Mappings.Add("contacts.contacts[0].first_name", "callflow$.FirstName");
            GetContactFromFreshsales_ResponseAnalyzer.Mappings.Add("contacts.contacts[0].last_name", "callflow$.LastName");
            SelectCRM.ContainerList[6].ComponentList.Add(GetContactFromFreshsales_ResponseAnalyzer);
            SelectCRM.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.CONTAINS(Convert.ToString(GetConfiguredCRM.PropertyValue),Convert.ToString("Freshworks CRM"))); });
            SelectCRM.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("FreshworksCRM"));
            CRMLookupComponent GetContactFromFreshworksCRM = scope.CreateComponent<CRMLookupComponent>("GetContactFromFreshworksCRM");
            GetContactFromFreshworksCRM.EntityType = CRMLookupComponent.EntityTypes.Contacts;
            GetContactFromFreshworksCRM.QueryType = CRMLookupComponent.QueryTypes.LookupNumber;
            GetContactFromFreshworksCRM.DataHandler = () => { return Convert.ToString(variableMap["session.ani"].Value); };
            SelectCRM.ContainerList[7].ComponentList.Add(GetContactFromFreshworksCRM);
            TextAnalyzerComponent GetContactFromFreshworksCRM_ResponseAnalyzer = scope.CreateComponent<TextAnalyzerComponent>("GetContactFromFreshworksCRM");
            GetContactFromFreshworksCRM_ResponseAnalyzer.TextType = TextAnalyzerComponent.TextTypes.Detect;
            GetContactFromFreshworksCRM_ResponseAnalyzer.TextHandler = () => { return Convert.ToString(GetContactFromFreshworksCRM.Result); };
            GetContactFromFreshworksCRM_ResponseAnalyzer.Mappings.Add("contacts.contacts[0].first_name", "callflow$.FirstName");
            GetContactFromFreshworksCRM_ResponseAnalyzer.Mappings.Add("contacts.contacts[0].last_name", "callflow$.LastName");
            SelectCRM.ContainerList[7].ComponentList.Add(GetContactFromFreshworksCRM_ResponseAnalyzer);
            SelectCRM.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.CONTAINS(Convert.ToString(GetConfiguredCRM.PropertyValue),Convert.ToString("HubSpot"))); });
            SelectCRM.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("Hubspot"));
            CRMLookupComponent GetContactFromHubspot = scope.CreateComponent<CRMLookupComponent>("GetContactFromHubspot");
            GetContactFromHubspot.EntityType = CRMLookupComponent.EntityTypes.Contacts;
            GetContactFromHubspot.QueryType = CRMLookupComponent.QueryTypes.LookupNumber;
            GetContactFromHubspot.DataHandler = () => { return Convert.ToString(variableMap["session.ani"].Value); };
            SelectCRM.ContainerList[8].ComponentList.Add(GetContactFromHubspot);
            TextAnalyzerComponent GetContactFromHubspot_ResponseAnalyzer = scope.CreateComponent<TextAnalyzerComponent>("GetContactFromHubspot");
            GetContactFromHubspot_ResponseAnalyzer.TextType = TextAnalyzerComponent.TextTypes.Detect;
            GetContactFromHubspot_ResponseAnalyzer.TextHandler = () => { return Convert.ToString(GetContactFromHubspot.Result); };
            GetContactFromHubspot_ResponseAnalyzer.Mappings.Add("properties.firstname", "callflow$.FirstName");
            GetContactFromHubspot_ResponseAnalyzer.Mappings.Add("properties.lastname", "callflow$.LastName");
            SelectCRM.ContainerList[8].ComponentList.Add(GetContactFromHubspot_ResponseAnalyzer);
            SelectCRM.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.CONTAINS(Convert.ToString(GetConfiguredCRM.PropertyValue),Convert.ToString("Nutshell"))); });
            SelectCRM.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("Nutshell"));
            CRMLookupComponent GetContactFromNutshell = scope.CreateComponent<CRMLookupComponent>("GetContactFromNutshell");
            GetContactFromNutshell.EntityType = CRMLookupComponent.EntityTypes.Contacts;
            GetContactFromNutshell.QueryType = CRMLookupComponent.QueryTypes.LookupNumber;
            GetContactFromNutshell.DataHandler = () => { return Convert.ToString(variableMap["session.ani"].Value); };
            SelectCRM.ContainerList[9].ComponentList.Add(GetContactFromNutshell);
            TextAnalyzerComponent GetContactFromNutshell_ResponseAnalyzer = scope.CreateComponent<TextAnalyzerComponent>("GetContactFromNutshell");
            GetContactFromNutshell_ResponseAnalyzer.TextType = TextAnalyzerComponent.TextTypes.Detect;
            GetContactFromNutshell_ResponseAnalyzer.TextHandler = () => { return Convert.ToString(GetContactFromNutshell.Result); };
            GetContactFromNutshell_ResponseAnalyzer.Mappings.Add("result.name.givenName", "callflow$.FirstName");
            GetContactFromNutshell_ResponseAnalyzer.Mappings.Add("result.name.familyName", "callflow$.LastName");
            SelectCRM.ContainerList[9].ComponentList.Add(GetContactFromNutshell_ResponseAnalyzer);
            SelectCRM.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.CONTAINS(Convert.ToString(GetConfiguredCRM.PropertyValue),Convert.ToString("Salesforce"))); });
            SelectCRM.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("Salesforce"));
            CRMLookupComponent GetContactFromSalesforce = scope.CreateComponent<CRMLookupComponent>("GetContactFromSalesforce");
            GetContactFromSalesforce.EntityType = CRMLookupComponent.EntityTypes.Contacts;
            GetContactFromSalesforce.QueryType = CRMLookupComponent.QueryTypes.LookupNumber;
            GetContactFromSalesforce.DataHandler = () => { return Convert.ToString(variableMap["session.ani"].Value); };
            SelectCRM.ContainerList[10].ComponentList.Add(GetContactFromSalesforce);
            TextAnalyzerComponent GetContactFromSalesforce_ResponseAnalyzer = scope.CreateComponent<TextAnalyzerComponent>("GetContactFromSalesforce");
            GetContactFromSalesforce_ResponseAnalyzer.TextType = TextAnalyzerComponent.TextTypes.Detect;
            GetContactFromSalesforce_ResponseAnalyzer.TextHandler = () => { return Convert.ToString(GetContactFromSalesforce.Result); };
            GetContactFromSalesforce_ResponseAnalyzer.Mappings.Add("FirstName", "callflow$.FirstName");
            GetContactFromSalesforce_ResponseAnalyzer.Mappings.Add("LastName", "callflow$.LastName");
            SelectCRM.ContainerList[10].ComponentList.Add(GetContactFromSalesforce_ResponseAnalyzer);
            SelectCRM.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.CONTAINS(Convert.ToString(GetConfiguredCRM.PropertyValue),Convert.ToString("Vtiger"))); });
            SelectCRM.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("Vtiger"));
            CRMLookupComponent GetContactFromVtiger = scope.CreateComponent<CRMLookupComponent>("GetContactFromVtiger");
            GetContactFromVtiger.EntityType = CRMLookupComponent.EntityTypes.Contacts;
            GetContactFromVtiger.QueryType = CRMLookupComponent.QueryTypes.LookupNumber;
            GetContactFromVtiger.DataHandler = () => { return Convert.ToString(variableMap["session.ani"].Value); };
            SelectCRM.ContainerList[11].ComponentList.Add(GetContactFromVtiger);
            TextAnalyzerComponent GetContactFromVtiger_ResponseAnalyzer = scope.CreateComponent<TextAnalyzerComponent>("GetContactFromVtiger");
            GetContactFromVtiger_ResponseAnalyzer.TextType = TextAnalyzerComponent.TextTypes.Detect;
            GetContactFromVtiger_ResponseAnalyzer.TextHandler = () => { return Convert.ToString(GetContactFromVtiger.Result); };
            GetContactFromVtiger_ResponseAnalyzer.Mappings.Add("result[0].firstname", "callflow$.FirstName");
            GetContactFromVtiger_ResponseAnalyzer.Mappings.Add("result[0].lastname", "callflow$.LastName");
            SelectCRM.ContainerList[11].ComponentList.Add(GetContactFromVtiger_ResponseAnalyzer);
            SelectCRM.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.CONTAINS(Convert.ToString(GetConfiguredCRM.PropertyValue),Convert.ToString("Zendesk"))); });
            SelectCRM.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("Zendesk"));
            CRMLookupComponent GetContactFromZendesk = scope.CreateComponent<CRMLookupComponent>("GetContactFromZendesk");
            GetContactFromZendesk.EntityType = CRMLookupComponent.EntityTypes.Contacts;
            GetContactFromZendesk.QueryType = CRMLookupComponent.QueryTypes.LookupNumber;
            GetContactFromZendesk.DataHandler = () => { return Convert.ToString(variableMap["session.ani"].Value); };
            SelectCRM.ContainerList[12].ComponentList.Add(GetContactFromZendesk);
            TextAnalyzerComponent GetContactFromZendesk_ResponseAnalyzer = scope.CreateComponent<TextAnalyzerComponent>("GetContactFromZendesk");
            GetContactFromZendesk_ResponseAnalyzer.TextType = TextAnalyzerComponent.TextTypes.Detect;
            GetContactFromZendesk_ResponseAnalyzer.TextHandler = () => { return Convert.ToString(GetContactFromZendesk.Result); };
            GetContactFromZendesk_ResponseAnalyzer.Mappings.Add("users[0].name", "callflow$.FirstName");
            SelectCRM.ContainerList[12].ComponentList.Add(GetContactFromZendesk_ResponseAnalyzer);
            SelectCRM.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.CONTAINS(Convert.ToString(GetConfiguredCRM.PropertyValue),Convert.ToString("Zoho"))); });
            SelectCRM.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("Zoho"));
            CRMLookupComponent GetContactFromZoho = scope.CreateComponent<CRMLookupComponent>("GetContactFromZoho");
            GetContactFromZoho.EntityType = CRMLookupComponent.EntityTypes.Contacts;
            GetContactFromZoho.QueryType = CRMLookupComponent.QueryTypes.LookupNumber;
            GetContactFromZoho.DataHandler = () => { return Convert.ToString(variableMap["session.ani"].Value); };
            SelectCRM.ContainerList[13].ComponentList.Add(GetContactFromZoho);
            TextAnalyzerComponent GetContactFromZoho_ResponseAnalyzer = scope.CreateComponent<TextAnalyzerComponent>("GetContactFromZoho");
            GetContactFromZoho_ResponseAnalyzer.TextType = TextAnalyzerComponent.TextTypes.Detect;
            GetContactFromZoho_ResponseAnalyzer.TextHandler = () => { return Convert.ToString(GetContactFromZoho.Result); };
            GetContactFromZoho_ResponseAnalyzer.Mappings.Add("data[0].First_Name", "callflow$.FirstName");
            GetContactFromZoho_ResponseAnalyzer.Mappings.Add("data[0].Last_Name", "callflow$.LastName");
            SelectCRM.ContainerList[13].ComponentList.Add(GetContactFromZoho_ResponseAnalyzer);
            PromptPlaybackComponent PlayWelcome = scope.CreateComponent<PromptPlaybackComponent>("PlayWelcome");
            PlayWelcome.AllowDtmfInput = true;
            PlayWelcome.Prompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "Naja", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString(CFDFunctions.CONCATENATE(Convert.ToString("Hello "),Convert.ToString(variableMap["callflow$.FirstName"].Value),Convert.ToString(" "),Convert.ToString(variableMap["callflow$.LastName"].Value),Convert.ToString("! Thanks for calling!"))); }));
            mainFlowComponentList.Add(PlayWelcome);
            }
            {
            }
            {
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

         AbsTextToSpeechEngine textToSpeechEngine = new AmazonPollyTextToSpeechEngine(new AmazonPollySettings("45412", "7874547", "us-east-2", new List<string>() {  }));
         AbsSpeechToTextEngine speechToTextEngine = null;
         this.onlineServices = new OnlineServices(textToSpeechEngine, speechToTextEngine);
      }

      public override void Start()
      {
         string callID = MyCall?.Caller["chid"] ?? "Unknown";
         string logHeader = $"CRMLookupDemo - CallID {callID}";
         this.logFormatter = new LogFormatter(MyCall, logHeader, "Callflow");
         this.promptQueue = new PromptQueue(this, MyCall, "CRMLookupDemo", logHeader);
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


      
   }
}
