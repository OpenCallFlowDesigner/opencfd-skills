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

namespace Callback
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
         variableMap["project$.GlobalPropertyName"] = new Variable("CFD_SCHEDULED_CALLBACKS");
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
            MenuComponent OfferCallbackMenu = scope.CreateComponent<MenuComponent>("OfferCallbackMenu");
            OfferCallbackMenu.AllowDtmfInput = true;
            OfferCallbackMenu.MaxRetryCount = 2;
            OfferCallbackMenu.Timeout = 5000;
            OfferCallbackMenu.ValidOptionList.AddRange(new char[] { '1', '2' });
            OfferCallbackMenu.InitialPrompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("To schedule a callback press 1, otherwise press 2."); }));
            OfferCallbackMenu.SubsequentPrompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("To schedule a callback press 1, otherwise press 2."); }));
            OfferCallbackMenu.InvalidDigitPrompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Sorry, the selected option is not valid."); }));
            OfferCallbackMenu.TimeoutPrompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Sorry, we didn't receive any digit."); }));
            mainFlowComponentList.Add(OfferCallbackMenu);
            ConditionalComponent OfferCallbackMenu_Conditional = scope.CreateComponent<ConditionalComponent>("OfferCallbackMenu_Conditional");
            mainFlowComponentList.Add(OfferCallbackMenu_Conditional);
            OfferCallbackMenu_Conditional.ConditionList.Add(() => { return OfferCallbackMenu.Result == MenuComponent.MenuResults.ValidOption && OfferCallbackMenu.SelectedOption == '1'; });
            OfferCallbackMenu_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("OfferCallbackMenu_Conditional_Option1"));
            AskForDate RequestDate = new AskForDate(onlineServices, officeHoursManager, scope, "RequestDate", callflow, myCall, logHeader);
            OfferCallbackMenu_Conditional.ContainerList[0].ComponentList.Add(RequestDate);
            AskForTime RequestTime = new AskForTime(onlineServices, officeHoursManager, scope, "RequestTime", callflow, myCall, logHeader);
            RequestTime.In_SelectedDateSetter = () => { return RequestDate.Out_SelectedDate; };
            OfferCallbackMenu_Conditional.ContainerList[0].ComponentList.Add(RequestTime);
            StoreCallback StoreCallbackIn3CX = new StoreCallback(onlineServices, officeHoursManager, scope, "StoreCallbackIn3CX", callflow, myCall, logHeader);
            StoreCallbackIn3CX.In_SelectedDateTimeSetter = () => { return RequestTime.Out_SelectedDateTime; };
            OfferCallbackMenu_Conditional.ContainerList[0].ComponentList.Add(StoreCallbackIn3CX);
            OfferCallbackMenu_Conditional.ConditionList.Add(() => { return OfferCallbackMenu.Result == MenuComponent.MenuResults.ValidOption && OfferCallbackMenu.SelectedOption == '2'; });
            OfferCallbackMenu_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("OfferCallbackMenu_Conditional_Option2"));
            PromptPlaybackComponent GoodBye = scope.CreateComponent<PromptPlaybackComponent>("GoodBye");
            GoodBye.AllowDtmfInput = true;
            GoodBye.Prompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Thanks for contacting us, good bye."); }));
            OfferCallbackMenu_Conditional.ContainerList[1].ComponentList.Add(GoodBye);
            OfferCallbackMenu_Conditional.ConditionList.Add(() => { return OfferCallbackMenu.Result == MenuComponent.MenuResults.InvalidOption || OfferCallbackMenu.Result == MenuComponent.MenuResults.Timeout; });
            OfferCallbackMenu_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("OfferCallbackMenu_Conditional_TimeoutOrInvalidOption"));
            PromptPlaybackComponent GoodBye2 = scope.CreateComponent<PromptPlaybackComponent>("GoodBye2");
            GoodBye2.AllowDtmfInput = true;
            GoodBye2.Prompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Thanks for contacting us, good bye."); }));
            OfferCallbackMenu_Conditional.ContainerList[2].ComponentList.Add(GoodBye2);
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

         AbsTextToSpeechEngine textToSpeechEngine = new GoogleCloudTextToSpeechEngine(new GoogleCloudSettings("{}"));
         AbsSpeechToTextEngine speechToTextEngine = new GoogleCloudSpeechToTextEngine(new GoogleCloudSettings("{}"));
         this.onlineServices = new OnlineServices(textToSpeechEngine, speechToTextEngine);
      }

      public override void Start()
      {
         string callID = MyCall?.Caller["chid"] ?? "Unknown";
         string logHeader = $"Callback - CallID {callID}";
         this.logFormatter = new LogFormatter(MyCall, logHeader, "Callflow");
         this.promptQueue = new PromptQueue(this, MyCall, "Callback", logHeader);
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

                     lock (lockObj)
            {
                if (!isGloballyInitialized)
                {
                    InitializeDialers(MyCall);
                    isGloballyInitialized = true;
                }
            }

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


              // ------------------------------------------------------------------------------------------------------------
        // User Defined component
        // ------------------------------------------------------------------------------------------------------------
        public class AskForDate : AbsUserComponent
        {
            private OnlineServices onlineServices;
            private OfficeHoursManager officeHoursManager;
            private CfdAppScope scope;

            private ObjectExpressionHandler _HasToAskDateHandler = null;
            private ObjectExpressionHandler _Out_SelectedDateHandler = null;
            

            protected override void InitializeVariables()
            {
                componentVariableMap["callflow$.HasToAskDate"] = new Variable(true);
                componentVariableMap["callflow$.Out_SelectedDate"] = new Variable("");
                
            }

            protected override void InitializeComponents()
            {
                Dictionary<string, Variable> variableMap = componentVariableMap;
                {
            LoopComponent AskDateLoop = scope.CreateComponent<LoopComponent>("AskDateLoop");
            AskDateLoop.Condition = () => { return Convert.ToBoolean(variableMap["callflow$.HasToAskDate"].Value); };
            AskDateLoop.Container = scope.CreateComponent<SequenceContainerComponent>("AskDateLoop_Container");
            mainFlowComponentList.Add(AskDateLoop);
            VoiceInputComponent RequestDate = scope.CreateComponent<VoiceInputComponent>("RequestDate", onlineServices.SpeechToTextEngine);
            RequestDate.MaxRetryCount = 2;
            RequestDate.InputTimeout = 3000;
            RequestDate.LanguageCode = "en-US";
            RequestDate.FileNameHandler = () => { return Convert.ToString(""); };
            RequestDate.SaveToFileHandler = () => { return Convert.ToBoolean(false); };
            RequestDate.InitialPrompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Please, say for what date you want to schedule the callback."); }));
            RequestDate.SubsequentPrompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Please, say for what date you want to schedule the callback."); }));
            RequestDate.InvalidInputPrompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Sorry, we couldn't understand what you said."); }));
            RequestDate.TimeoutPrompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Sorry, we couldn't hear you."); }));
            RequestDate.Hints.Add(() => { return "$MONTH $DAY"; });
            RequestDate.Hints.Add(() => { return "$DAY of $MONTH"; });
            RequestDate.Hints.Add(() => { return "today"; });
            RequestDate.Hints.Add(() => { return "tomorrow"; });
            AskDateLoop.Container.ComponentList.Add(RequestDate);
            ConditionalComponent RequestDate_Conditional = scope.CreateComponent<ConditionalComponent>("RequestDate_Conditional");
            AskDateLoop.Container.ComponentList.Add(RequestDate_Conditional);
            RequestDate_Conditional.ConditionList.Add(() => { return RequestDate.Result == VoiceInputComponent.VoiceInputResults.ValidInput || RequestDate.Result == VoiceInputComponent.VoiceInputResults.ValidDtmfInput; });
            RequestDate_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("RequestDate_Conditional_ValidInput"));
            IsValidDate125309283ECCComponent IsValidDate = new IsValidDate125309283ECCComponent("IsValidDate", callflow, myCall, logHeader);
            IsValidDate.Parameters.Add(new CallFlow.CFD.Parameter("input", () => { return RequestDate.RecognizedText; }));
            RequestDate_Conditional.ContainerList[0].ComponentList.Add(IsValidDate);
            ConditionalComponent CheckValidDate = scope.CreateComponent<ConditionalComponent>("CheckValidDate");
            RequestDate_Conditional.ContainerList[0].ComponentList.Add(CheckValidDate);
            CheckValidDate.ConditionList.Add(() => { return Convert.ToBoolean(IsValidDate.ReturnValue); });
            CheckValidDate.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("ValidDate"));
            ConvertToDate1354349340ECCComponent ConvertToDate = new ConvertToDate1354349340ECCComponent("ConvertToDate", callflow, myCall, logHeader);
            ConvertToDate.Parameters.Add(new CallFlow.CFD.Parameter("input", () => { return RequestDate.RecognizedText; }));
            CheckValidDate.ContainerList[0].ComponentList.Add(ConvertToDate);
            VariableAssignmentComponent SetDate = scope.CreateComponent<VariableAssignmentComponent>("SetDate");
            SetDate.VariableName = "callflow$.Out_SelectedDate";
            SetDate.VariableValueHandler = () => { return ConvertToDate.ReturnValue; };
            CheckValidDate.ContainerList[0].ComponentList.Add(SetDate);
            ConvertDateToString542690857ECCComponent ConvertDateToString = new ConvertDateToString542690857ECCComponent("ConvertDateToString", callflow, myCall, logHeader);
            ConvertDateToString.Parameters.Add(new CallFlow.CFD.Parameter("datetime", () => { return variableMap["callflow$.Out_SelectedDate"].Value; }));
            CheckValidDate.ContainerList[0].ComponentList.Add(ConvertDateToString);
            PromptPlaybackComponent PlayDate = scope.CreateComponent<PromptPlaybackComponent>("PlayDate");
            PlayDate.AllowDtmfInput = false;
            PlayDate.Prompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString(CFDFunctions.CONCATENATE(Convert.ToString("We will schedule your callback on "),Convert.ToString(ConvertDateToString.ReturnValue))); }));
            CheckValidDate.ContainerList[0].ComponentList.Add(PlayDate);
            VariableAssignmentComponent ExitAskDateLoop = scope.CreateComponent<VariableAssignmentComponent>("ExitAskDateLoop");
            ExitAskDateLoop.VariableName = "callflow$.HasToAskDate";
            ExitAskDateLoop.VariableValueHandler = () => { return false; };
            CheckValidDate.ContainerList[0].ComponentList.Add(ExitAskDateLoop);
            CheckValidDate.ConditionList.Add(() => { return Convert.ToBoolean(true); });
            CheckValidDate.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("InvalidDate"));
            PromptPlaybackComponent PlayInvalidDate = scope.CreateComponent<PromptPlaybackComponent>("PlayInvalidDate");
            PlayInvalidDate.AllowDtmfInput = true;
            PlayInvalidDate.Prompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Sorry, we couldn't understand the date you said. Please try again."); }));
            CheckValidDate.ContainerList[1].ComponentList.Add(PlayInvalidDate);
            RequestDate_Conditional.ConditionList.Add(() => { return true; });
            RequestDate_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("RequestDate_Conditional_InvalidInput"));
            PromptPlaybackComponent SorryTryLater = scope.CreateComponent<PromptPlaybackComponent>("SorryTryLater");
            SorryTryLater.AllowDtmfInput = true;
            SorryTryLater.Prompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Sorry, we couldn't schedule the callback, please call again later."); }));
            RequestDate_Conditional.ContainerList[1].ComponentList.Add(SorryTryLater);
            DisconnectCallComponent DisconnectCallOnError = scope.CreateComponent<DisconnectCallComponent>("DisconnectCallOnError");
            RequestDate_Conditional.ContainerList[1].ComponentList.Add(DisconnectCallOnError);
            }
            {
            }
            {
            }
            
            }
            
            public AskForDate(OnlineServices onlineServices, OfficeHoursManager officeHoursManager,
                CfdAppScope scope, string name, ICallflow callflow, ICall myCall, string logHeader) : base(name, callflow, myCall, logHeader)
            {
                this.onlineServices = onlineServices;
                this.officeHoursManager = officeHoursManager;
                this.scope = scope;
            }
     
            protected override void GetVariableValues()
            {
                if (_HasToAskDateHandler != null) componentVariableMap["callflow$.HasToAskDate"].Set(_HasToAskDateHandler());
                if (_Out_SelectedDateHandler != null) componentVariableMap["callflow$.Out_SelectedDate"].Set(_Out_SelectedDateHandler());
                
            }
            
            public ObjectExpressionHandler HasToAskDateSetter { set { _HasToAskDateHandler = value; } }
            public object HasToAskDate { get { return componentVariableMap["callflow$.HasToAskDate"].Value; } }
            public ObjectExpressionHandler Out_SelectedDateSetter { set { _Out_SelectedDateHandler = value; } }
            public object Out_SelectedDate { get { return componentVariableMap["callflow$.Out_SelectedDate"].Value; } }
            

            private bool IsServerInHoliday(ICall myCall)
            {
                Tenant tenant = myCall.PS.GetTenant();
                return tenant != null && tenant.IsHoliday(new DateTimeOffset(DateTime.Now));
            }

            private bool IsServerOfficeHourActive(ICall myCall)
            {
		            Tenant tenant = myCall.PS.GetTenant();
		            if (tenant == null) return false;
		
		            string overrideOfficeTime = tenant.GetPropertyValue("OVERRIDEOFFICETIME");
		            if (!String.IsNullOrEmpty(overrideOfficeTime))
		            {
		                if (overrideOfficeTime == "1") // Forced to in office hours
		                    return true;
		                else if (overrideOfficeTime == "2") // Forced to out of office hours
		                    return false;
		            }
		
		            DateTime nowDt = DateTime.Now;
		            if (tenant.IsHoliday(new DateTimeOffset(nowDt))) return false;
		
		            Schedule officeHours = tenant.Hours;
		            Nullable<bool> result = officeHours.IsActiveTime(nowDt);
		            return result.GetValueOrDefault(false);
            }
        }
public class IsValidDate125309283ECCComponent : ExternalCodeExecutionComponent
            {
                public List<CallFlow.CFD.Parameter> Parameters { get; } = new List<CallFlow.CFD.Parameter>();
                public IsValidDate125309283ECCComponent(string name, ICallflow callflow, ICall myCall, string projectName) : base(name, callflow, myCall, projectName) {}
                protected override object ExecuteCode()
                {
                    return IsValidDate(Convert.ToString(Parameters[0].Value));
                }
            
            private object IsValidDate(string input)
                {
            string inputLowerCase = input.ToLower();
if (inputLowerCase.Contains("today") || inputLowerCase.Contains("tomorrow"))
    return true;

string[] validformats = new[] 
{ 
    "MMMM d",
    "MMMM d\\s\\t",
    "MMMM dn\\d",
    "MMMM dr\\d",
    "MMMM d\\t\\h",
    "d o\\f MMMM",
    "d\\s\\t o\\f MMMM",
    "dn\\d o\\f MMMM",
    "dr\\d o\\f MMMM",
    "d\\t\\h o\\f MMMM"
};
return DateTime.TryParseExact(inputLowerCase, validformats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);
    }
            }
            public class ConvertToDate1354349340ECCComponent : ExternalCodeExecutionComponent
            {
                public List<CallFlow.CFD.Parameter> Parameters { get; } = new List<CallFlow.CFD.Parameter>();
                public ConvertToDate1354349340ECCComponent(string name, ICallflow callflow, ICall myCall, string projectName) : base(name, callflow, myCall, projectName) {}
                protected override object ExecuteCode()
                {
                    return ConvertToDate(Convert.ToString(Parameters[0].Value));
                }
            
            private object ConvertToDate(string input)
                {
            DateTime nowDt = DateTime.Now;
DateTime todayDt = new DateTime(nowDt.Year, nowDt.Month, nowDt.Day, 0, 0, 0);

string inputLowerCase = input.ToLower();
if (inputLowerCase.Contains("today")) return todayDt;

if (inputLowerCase.Contains("tomorrow")) return todayDt.AddDays(1);

string[] validformats = new[] 
{ 
    "MMMM d",
    "MMMM d\\s\\t",
    "MMMM dn\\d",
    "MMMM dr\\d",
    "MMMM d\\t\\h",
    "d o\\f MMMM",
    "d\\s\\t o\\f MMMM",
    "dn\\d o\\f MMMM",
    "dr\\d o\\f MMMM",
    "d\\t\\h o\\f MMMM"
};
DateTime dt = DateTime.ParseExact(inputLowerCase, validformats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None);
return dt < todayDt ? dt.AddYears(1) : dt;
    }
            }
            public class ConvertDateToString542690857ECCComponent : ExternalCodeExecutionComponent
            {
                public List<CallFlow.CFD.Parameter> Parameters { get; } = new List<CallFlow.CFD.Parameter>();
                public ConvertDateToString542690857ECCComponent(string name, ICallflow callflow, ICall myCall, string projectName) : base(name, callflow, myCall, projectName) {}
                protected override object ExecuteCode()
                {
                    return ConvertDateToString(Parameters[0].Value);
                }
            
            private object ConvertDateToString(object datetime)
                {
            return ((DateTime)datetime).ToString("MMMM d, yyyy");    }
            }
                    // ------------------------------------------------------------------------------------------------------------
        // User Defined component
        // ------------------------------------------------------------------------------------------------------------
        public class AskForTime : AbsUserComponent
        {
            private OnlineServices onlineServices;
            private OfficeHoursManager officeHoursManager;
            private CfdAppScope scope;

            private ObjectExpressionHandler _HasToAskTimeHandler = null;
            private ObjectExpressionHandler _In_SelectedDateHandler = null;
            private ObjectExpressionHandler _Out_SelectedDateTimeHandler = null;
            

            protected override void InitializeVariables()
            {
                componentVariableMap["callflow$.HasToAskTime"] = new Variable(true);
                componentVariableMap["callflow$.In_SelectedDate"] = new Variable("");
                componentVariableMap["callflow$.Out_SelectedDateTime"] = new Variable("");
                
            }

            protected override void InitializeComponents()
            {
                Dictionary<string, Variable> variableMap = componentVariableMap;
                {
            LoopComponent AskTimeLoop = scope.CreateComponent<LoopComponent>("AskTimeLoop");
            AskTimeLoop.Condition = () => { return Convert.ToBoolean(variableMap["callflow$.HasToAskTime"].Value); };
            AskTimeLoop.Container = scope.CreateComponent<SequenceContainerComponent>("AskTimeLoop_Container");
            mainFlowComponentList.Add(AskTimeLoop);
            VoiceInputComponent RequestTime = scope.CreateComponent<VoiceInputComponent>("RequestTime", onlineServices.SpeechToTextEngine);
            RequestTime.MaxRetryCount = 2;
            RequestTime.InputTimeout = 3000;
            RequestTime.LanguageCode = "en-US";
            RequestTime.FileNameHandler = () => { return Convert.ToString(""); };
            RequestTime.SaveToFileHandler = () => { return Convert.ToBoolean(false); };
            RequestTime.InitialPrompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Please, say for what time you want to schedule your callback."); }));
            RequestTime.SubsequentPrompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Please, say for what time you want to schedule your callback, mentioning the hour and minutes. For example, 15:30."); }));
            RequestTime.InvalidInputPrompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Sorry, we couldn't understand what you said."); }));
            RequestTime.TimeoutPrompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Sorry, we couldn't hear you."); }));
            RequestTime.Hints.Add(() => { return "$TIME"; });
            AskTimeLoop.Container.ComponentList.Add(RequestTime);
            ConditionalComponent RequestTime_Conditional = scope.CreateComponent<ConditionalComponent>("RequestTime_Conditional");
            AskTimeLoop.Container.ComponentList.Add(RequestTime_Conditional);
            RequestTime_Conditional.ConditionList.Add(() => { return RequestTime.Result == VoiceInputComponent.VoiceInputResults.ValidInput || RequestTime.Result == VoiceInputComponent.VoiceInputResults.ValidDtmfInput; });
            RequestTime_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("RequestTime_Conditional_ValidInput"));
            IsValidTime2048168734ECCComponent IsValidTime = new IsValidTime2048168734ECCComponent("IsValidTime", callflow, myCall, logHeader);
            IsValidTime.Parameters.Add(new CallFlow.CFD.Parameter("selectedDate", () => { return variableMap["callflow$.In_SelectedDate"].Value; }));
            IsValidTime.Parameters.Add(new CallFlow.CFD.Parameter("input", () => { return RequestTime.RecognizedText; }));
            RequestTime_Conditional.ContainerList[0].ComponentList.Add(IsValidTime);
            ConditionalComponent CheckValidTime = scope.CreateComponent<ConditionalComponent>("CheckValidTime");
            RequestTime_Conditional.ContainerList[0].ComponentList.Add(CheckValidTime);
            CheckValidTime.ConditionList.Add(() => { return Convert.ToBoolean(IsValidTime.ReturnValue); });
            CheckValidTime.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("ValidTime"));
            ConvertToTime1722640958ECCComponent ConvertToTime = new ConvertToTime1722640958ECCComponent("ConvertToTime", callflow, myCall, logHeader);
            ConvertToTime.Parameters.Add(new CallFlow.CFD.Parameter("selectedDate", () => { return variableMap["callflow$.In_SelectedDate"].Value; }));
            ConvertToTime.Parameters.Add(new CallFlow.CFD.Parameter("input", () => { return RequestTime.RecognizedText; }));
            CheckValidTime.ContainerList[0].ComponentList.Add(ConvertToTime);
            VariableAssignmentComponent SetDateAndTime = scope.CreateComponent<VariableAssignmentComponent>("SetDateAndTime");
            SetDateAndTime.VariableName = "callflow$.Out_SelectedDateTime";
            SetDateAndTime.VariableValueHandler = () => { return ConvertToTime.ReturnValue; };
            CheckValidTime.ContainerList[0].ComponentList.Add(SetDateAndTime);
            ConvertDateAndTimeToString1220272403ECCComponent ConvertDateAndTimeToString = new ConvertDateAndTimeToString1220272403ECCComponent("ConvertDateAndTimeToString", callflow, myCall, logHeader);
            ConvertDateAndTimeToString.Parameters.Add(new CallFlow.CFD.Parameter("datetime", () => { return ConvertToTime.ReturnValue; }));
            CheckValidTime.ContainerList[0].ComponentList.Add(ConvertDateAndTimeToString);
            PromptPlaybackComponent PlayDateAndTime = scope.CreateComponent<PromptPlaybackComponent>("PlayDateAndTime");
            PlayDateAndTime.AllowDtmfInput = false;
            PlayDateAndTime.Prompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString(CFDFunctions.CONCATENATE(Convert.ToString("We will schedule your callback on "),Convert.ToString(ConvertDateAndTimeToString.ReturnValue))); }));
            CheckValidTime.ContainerList[0].ComponentList.Add(PlayDateAndTime);
            VariableAssignmentComponent ExitAskTimeLoop = scope.CreateComponent<VariableAssignmentComponent>("ExitAskTimeLoop");
            ExitAskTimeLoop.VariableName = "callflow$.HasToAskTime";
            ExitAskTimeLoop.VariableValueHandler = () => { return false; };
            CheckValidTime.ContainerList[0].ComponentList.Add(ExitAskTimeLoop);
            CheckValidTime.ConditionList.Add(() => { return Convert.ToBoolean(true); });
            CheckValidTime.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("InvalidTime"));
            PromptPlaybackComponent PlayInvalidTime = scope.CreateComponent<PromptPlaybackComponent>("PlayInvalidTime");
            PlayInvalidTime.AllowDtmfInput = true;
            PlayInvalidTime.Prompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Sorry, we couldn't understand the time you said. Please try again."); }));
            CheckValidTime.ContainerList[1].ComponentList.Add(PlayInvalidTime);
            RequestTime_Conditional.ConditionList.Add(() => { return true; });
            RequestTime_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("RequestTime_Conditional_InvalidInput"));
            PromptPlaybackComponent SorryTryLater = scope.CreateComponent<PromptPlaybackComponent>("SorryTryLater");
            SorryTryLater.AllowDtmfInput = true;
            SorryTryLater.Prompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Sorry, we couldn't schedule the callback, please call again later."); }));
            RequestTime_Conditional.ContainerList[1].ComponentList.Add(SorryTryLater);
            DisconnectCallComponent DisconnectCallOnError = scope.CreateComponent<DisconnectCallComponent>("DisconnectCallOnError");
            RequestTime_Conditional.ContainerList[1].ComponentList.Add(DisconnectCallOnError);
            }
            {
            }
            {
            }
            
            }
            
            public AskForTime(OnlineServices onlineServices, OfficeHoursManager officeHoursManager,
                CfdAppScope scope, string name, ICallflow callflow, ICall myCall, string logHeader) : base(name, callflow, myCall, logHeader)
            {
                this.onlineServices = onlineServices;
                this.officeHoursManager = officeHoursManager;
                this.scope = scope;
            }
     
            protected override void GetVariableValues()
            {
                if (_HasToAskTimeHandler != null) componentVariableMap["callflow$.HasToAskTime"].Set(_HasToAskTimeHandler());
                if (_In_SelectedDateHandler != null) componentVariableMap["callflow$.In_SelectedDate"].Set(_In_SelectedDateHandler());
                if (_Out_SelectedDateTimeHandler != null) componentVariableMap["callflow$.Out_SelectedDateTime"].Set(_Out_SelectedDateTimeHandler());
                
            }
            
            public ObjectExpressionHandler HasToAskTimeSetter { set { _HasToAskTimeHandler = value; } }
            public object HasToAskTime { get { return componentVariableMap["callflow$.HasToAskTime"].Value; } }
            public ObjectExpressionHandler In_SelectedDateSetter { set { _In_SelectedDateHandler = value; } }
            public object In_SelectedDate { get { return componentVariableMap["callflow$.In_SelectedDate"].Value; } }
            public ObjectExpressionHandler Out_SelectedDateTimeSetter { set { _Out_SelectedDateTimeHandler = value; } }
            public object Out_SelectedDateTime { get { return componentVariableMap["callflow$.Out_SelectedDateTime"].Value; } }
            

            private bool IsServerInHoliday(ICall myCall)
            {
                Tenant tenant = myCall.PS.GetTenant();
                return tenant != null && tenant.IsHoliday(new DateTimeOffset(DateTime.Now));
            }

            private bool IsServerOfficeHourActive(ICall myCall)
            {
		            Tenant tenant = myCall.PS.GetTenant();
		            if (tenant == null) return false;
		
		            string overrideOfficeTime = tenant.GetPropertyValue("OVERRIDEOFFICETIME");
		            if (!String.IsNullOrEmpty(overrideOfficeTime))
		            {
		                if (overrideOfficeTime == "1") // Forced to in office hours
		                    return true;
		                else if (overrideOfficeTime == "2") // Forced to out of office hours
		                    return false;
		            }
		
		            DateTime nowDt = DateTime.Now;
		            if (tenant.IsHoliday(new DateTimeOffset(nowDt))) return false;
		
		            Schedule officeHours = tenant.Hours;
		            Nullable<bool> result = officeHours.IsActiveTime(nowDt);
		            return result.GetValueOrDefault(false);
            }
        }
public class IsValidTime2048168734ECCComponent : ExternalCodeExecutionComponent
            {
                public List<CallFlow.CFD.Parameter> Parameters { get; } = new List<CallFlow.CFD.Parameter>();
                public IsValidTime2048168734ECCComponent(string name, ICallflow callflow, ICall myCall, string projectName) : base(name, callflow, myCall, projectName) {}
                protected override object ExecuteCode()
                {
                    return IsValidTime(Parameters[0].Value, Convert.ToString(Parameters[1].Value));
                }
            
            private object IsValidTime(object selectedDate, string input)
                {
            string dateStr = ((DateTime)selectedDate).ToString("yyyy-MM-dd");
string[] validformats = new[] 
{
    "yyyy-MM-dd H:mm",
    "yyyy-MM-dd H",
    "yyyy-MM-dd h:mm tt",
    "yyyy-MM-dd h tt"
};
return DateTime.TryParseExact(dateStr + " " + input.Replace(".", ""), validformats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);
    }
            }
            public class ConvertToTime1722640958ECCComponent : ExternalCodeExecutionComponent
            {
                public List<CallFlow.CFD.Parameter> Parameters { get; } = new List<CallFlow.CFD.Parameter>();
                public ConvertToTime1722640958ECCComponent(string name, ICallflow callflow, ICall myCall, string projectName) : base(name, callflow, myCall, projectName) {}
                protected override object ExecuteCode()
                {
                    return ConvertToTime(Parameters[0].Value, Convert.ToString(Parameters[1].Value));
                }
            
            private object ConvertToTime(object selectedDate, string input)
                {
            string dateStr = ((DateTime)selectedDate).ToString("yyyy-MM-dd");
string[] validformats = new[] 
{
    "yyyy-MM-dd H:mm",
    "yyyy-MM-dd H",
    "yyyy-MM-dd h:mm tt",
    "yyyy-MM-dd h tt"
};
return DateTime.ParseExact(dateStr + " " + input.Replace(".", ""), validformats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None);
    }
            }
            public class ConvertDateAndTimeToString1220272403ECCComponent : ExternalCodeExecutionComponent
            {
                public List<CallFlow.CFD.Parameter> Parameters { get; } = new List<CallFlow.CFD.Parameter>();
                public ConvertDateAndTimeToString1220272403ECCComponent(string name, ICallflow callflow, ICall myCall, string projectName) : base(name, callflow, myCall, projectName) {}
                protected override object ExecuteCode()
                {
                    return ConvertDateAndTimeToString(Parameters[0].Value);
                }
            
            private object ConvertDateAndTimeToString(object datetime)
                {
            return ((DateTime)datetime).ToString("MMMM d, yyyy HH:mm");    }
            }
                    // ------------------------------------------------------------------------------------------------------------
        // User Defined component
        // ------------------------------------------------------------------------------------------------------------
        public class StoreCallback : AbsUserComponent
        {
            private OnlineServices onlineServices;
            private OfficeHoursManager officeHoursManager;
            private CfdAppScope scope;

            private ObjectExpressionHandler _In_SelectedDateTimeHandler = null;
            

            protected override void InitializeVariables()
            {
                componentVariableMap["callflow$.In_SelectedDateTime"] = new Variable("");
                
            }

            protected override void InitializeComponents()
            {
                Dictionary<string, Variable> variableMap = componentVariableMap;
                {
            FormatSelectedDateTime811571705ECCComponent FormatSelectedDateTime = new FormatSelectedDateTime811571705ECCComponent("FormatSelectedDateTime", callflow, myCall, logHeader);
            FormatSelectedDateTime.Parameters.Add(new CallFlow.CFD.Parameter("datetime", () => { return variableMap["callflow$.In_SelectedDateTime"].Value; }));
            mainFlowComponentList.Add(FormatSelectedDateTime);
            TcxGetGlobalPropertyComponent GetScheduledCallbacks = scope.CreateComponent<TcxGetGlobalPropertyComponent>("GetScheduledCallbacks");
            GetScheduledCallbacks.PropertyNameHandler = () => { return Convert.ToString(variableMap["project$.GlobalPropertyName"].Value).ToUpper(); };
            mainFlowComponentList.Add(GetScheduledCallbacks);
            ConditionalComponent CheckEmpty = scope.CreateComponent<ConditionalComponent>("CheckEmpty");
            mainFlowComponentList.Add(CheckEmpty);
            CheckEmpty.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.EQUAL(CFDFunctions.LEN(Convert.ToString(GetScheduledCallbacks.PropertyValue)),0)); });
            CheckEmpty.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("IsEmpty"));
            TcxSetGlobalPropertyComponent SetScheduledCallbacks1 = scope.CreateComponent<TcxSetGlobalPropertyComponent>("SetScheduledCallbacks1");
            SetScheduledCallbacks1.PropertyNameHandler = () => { return Convert.ToString(variableMap["project$.GlobalPropertyName"].Value).ToUpper(); };
            SetScheduledCallbacks1.PropertyValueHandler = () => { return Convert.ToString(CFDFunctions.CONCATENATE(Convert.ToString(variableMap["session.ani"].Value),Convert.ToString("="),Convert.ToString(FormatSelectedDateTime.ReturnValue))); };
            CheckEmpty.ContainerList[0].ComponentList.Add(SetScheduledCallbacks1);
            CheckEmpty.ConditionList.Add(() => { return Convert.ToBoolean(true); });
            CheckEmpty.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("IsNotEmpty"));
            TcxSetGlobalPropertyComponent SetScheduledCallbacks2 = scope.CreateComponent<TcxSetGlobalPropertyComponent>("SetScheduledCallbacks2");
            SetScheduledCallbacks2.PropertyNameHandler = () => { return Convert.ToString(variableMap["project$.GlobalPropertyName"].Value).ToUpper(); };
            SetScheduledCallbacks2.PropertyValueHandler = () => { return Convert.ToString(CFDFunctions.CONCATENATE(Convert.ToString(GetScheduledCallbacks.PropertyValue),Convert.ToString(","),Convert.ToString(variableMap["session.ani"].Value),Convert.ToString("="),Convert.ToString(FormatSelectedDateTime.ReturnValue))); };
            CheckEmpty.ContainerList[1].ComponentList.Add(SetScheduledCallbacks2);
            PromptPlaybackComponent PlayConfirmationMessage = scope.CreateComponent<PromptPlaybackComponent>("PlayConfirmationMessage");
            PlayConfirmationMessage.AllowDtmfInput = true;
            PlayConfirmationMessage.Prompts.Add(new TextToSpeechAudioPrompt(myCall, logHeader, onlineServices.TextToSpeechEngine, "en-US-Standard-C", TextToSpeechAudioPrompt.TextToSpeechVoiceTypes.Standard, TextToSpeechAudioPrompt.TextToSpeechFormats.Text, () => { return Convert.ToString("Your callback has been successfully scheduled. We will contact you back shortly. Thank you."); }));
            mainFlowComponentList.Add(PlayConfirmationMessage);
            }
            {
            }
            {
            }
            
            }
            
            public StoreCallback(OnlineServices onlineServices, OfficeHoursManager officeHoursManager,
                CfdAppScope scope, string name, ICallflow callflow, ICall myCall, string logHeader) : base(name, callflow, myCall, logHeader)
            {
                this.onlineServices = onlineServices;
                this.officeHoursManager = officeHoursManager;
                this.scope = scope;
            }
     
            protected override void GetVariableValues()
            {
                if (_In_SelectedDateTimeHandler != null) componentVariableMap["callflow$.In_SelectedDateTime"].Set(_In_SelectedDateTimeHandler());
                
            }
            
            public ObjectExpressionHandler In_SelectedDateTimeSetter { set { _In_SelectedDateTimeHandler = value; } }
            public object In_SelectedDateTime { get { return componentVariableMap["callflow$.In_SelectedDateTime"].Value; } }
            

            private bool IsServerInHoliday(ICall myCall)
            {
                Tenant tenant = myCall.PS.GetTenant();
                return tenant != null && tenant.IsHoliday(new DateTimeOffset(DateTime.Now));
            }

            private bool IsServerOfficeHourActive(ICall myCall)
            {
		            Tenant tenant = myCall.PS.GetTenant();
		            if (tenant == null) return false;
		
		            string overrideOfficeTime = tenant.GetPropertyValue("OVERRIDEOFFICETIME");
		            if (!String.IsNullOrEmpty(overrideOfficeTime))
		            {
		                if (overrideOfficeTime == "1") // Forced to in office hours
		                    return true;
		                else if (overrideOfficeTime == "2") // Forced to out of office hours
		                    return false;
		            }
		
		            DateTime nowDt = DateTime.Now;
		            if (tenant.IsHoliday(new DateTimeOffset(nowDt))) return false;
		
		            Schedule officeHours = tenant.Hours;
		            Nullable<bool> result = officeHours.IsActiveTime(nowDt);
		            return result.GetValueOrDefault(false);
            }
        }
public class FormatSelectedDateTime811571705ECCComponent : ExternalCodeExecutionComponent
            {
                public List<CallFlow.CFD.Parameter> Parameters { get; } = new List<CallFlow.CFD.Parameter>();
                public FormatSelectedDateTime811571705ECCComponent(string name, ICallflow callflow, ICall myCall, string projectName) : base(name, callflow, myCall, projectName) {}
                protected override object ExecuteCode()
                {
                    return FormatSelectedDateTime(Parameters[0].Value);
                }
            
            private object FormatSelectedDateTime(object datetime)
                {
            return ((DateTime)datetime).ToString("yyyyMMddHHmmss");    }
            }
                    // Dialer initialization fields and methods
        private static readonly object lockObj = new object();
        private static bool isGloballyInitialized = false;
        private static Dictionary<string, Dialer> dialerMap = new Dictionary<string, Dialer>();

        private void InitializeDialers(ICall myCall)
        {
            bool isPredictiveDialer = false;
            int parallelDialers = 1;
            int pauseBetweenDialerExecution = 30;
            string predictiveDialerQueue = "";
            bool isPredictiveDialerOptimizedForAgents = true;

            if (isPredictiveDialer)
            {
                using (DN dn = myCall.PS.GetDNByNumber(predictiveDialerQueue))
                {
                    Queue queue = dn as Queue;
                    if (queue == null)
                        logFormatter.Error("Error initializing Predictive Dialer - Extension '" + predictiveDialerQueue + "' does not exist or is not a valid queue.");
                    else
                    {
                        int queueAgents = queue.QueueAgents.Length;
                        if (queueAgents == 0)
                            logFormatter.Error("Error initializing Predictive Dialer - Queue at extension '" + predictiveDialerQueue + "' does not have any agent, no calls will be made.");
                        else
                        {
                            int predictiveDialers = 1 + queueAgents / 10;
                            PredictiveDialerManager predictiveDialerManager = new PredictiveDialerManager(myCall.PS, predictiveDialerQueue, isPredictiveDialerOptimizedForAgents);

                            logFormatter.Info("Creating " + predictiveDialers + " dialer(s) for this Predictive Dialer.");
                            for (int dialerIndex = 0; dialerIndex < predictiveDialers; ++dialerIndex)
                            {
                                string dialerID = dialerIndex.ToString();
                                Dialer dialer = new Dialer(myCall, dialerID, predictiveDialerManager, 10, dialerIndex * 10000 / predictiveDialers);
                                dialerMap.Add(dialerID, dialer);
                            }
                        }
                    }
                }
            }
            else
            {
                logFormatter.Info("Creating " + parallelDialers + " dialers for this Power Dialer.");
                for (int dialerIndex = 0; dialerIndex < parallelDialers; ++dialerIndex)
                {
                    string dialerID = dialerIndex.ToString();
                    Dialer dialer = new Dialer(myCall, dialerID, null, pauseBetweenDialerExecution, dialerIndex * 1000 * pauseBetweenDialerExecution / parallelDialers);
                    dialerMap.Add(dialerID, dialer);
                }
            }
        }

        public class Dialer : ICallflow, ICallflowProcessor, IDisposable
        {
            private PredictiveDialerManager predictiveDialerManager;

            private CancellationTokenSource exitWorkingThreadCTS;
            private int pauseBetweenDialerExecution;
            private int initialDelayMilliseconds;

            private bool executionFinished;

            private BufferBlock<AbsEvent> eventBuffer;

            private int currentComponentIndex;
            private List<AbsComponent> mainFlowComponentList;
            private List<AbsComponent> errorFlowComponentList;
            private List<AbsComponent> currentFlowComponentList;

            private Dictionary<string, Variable> variableMap;

            private LogFormatter logFormatter;

            private OnlineServices onlineServices = null;
            private OfficeHoursManager officeHoursManager;

            private CfdAppScope scope;

            private EventResults CheckEventResult(EventResults eventResult)
            {
                if (eventResult == EventResults.MoveToNextComponent && ++currentComponentIndex == currentFlowComponentList.Count)
                {
                    logFormatter.Trace("Dialer cycle finished");
                    executionFinished = true;
                    return EventResults.Exit;
                }
                else if (eventResult == EventResults.Exit)
                {
                    logFormatter.Trace("Exiting Dialer cycle");
                    executionFinished = true;
                }

                return eventResult;
            }

            private async Task ExecuteErrorFlow()
            {
                if (currentFlowComponentList == errorFlowComponentList)
                {
                    logFormatter.Trace("Error during error handler flow, exiting dialer cycle...");
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
                        executionFinished = true;
                    }
                }
            }

            private async Task WorkingThreadLoop()
            {
                if (initialDelayMilliseconds > 0) await Task.Delay(initialDelayMilliseconds, exitWorkingThreadCTS.Token);

                while (!exitWorkingThreadCTS.IsCancellationRequested)
                {
                    try
                    {
                        if (predictiveDialerManager == null || predictiveDialerManager.HasToMakeCall())
                        {
                            logFormatter.Trace("Starting dialer cycle");
                            eventBuffer.Post(new StartEvent());
                            await EventProcessingLoop();
                        }
                        else
                            logFormatter.Trace("PredictiveDialerManager states that we must not make calls now...");
                    }
                    catch (Exception exc)
                    {
                        logFormatter.Error("Error executing working thread loop: " + exc.ToString());
                    }
                    finally
                    {
                        currentComponentIndex = 0;
                        currentFlowComponentList = mainFlowComponentList;
                        variableMap.Clear();
                        InitializeVariables();
                        foreach (AbsComponent component in mainFlowComponentList) component.ResetState();
                        foreach (AbsComponent component in errorFlowComponentList) component.ResetState();
                        await Task.Delay(1000 * pauseBetweenDialerExecution, exitWorkingThreadCTS.Token);
                    }
                }

                if (scope != null) scope.Dispose();
            }

            private void InitializeVariables()
            {
                variableMap["project$.GlobalPropertyName"] = new Variable("CFD_SCHEDULED_CALLBACKS");
            
            }

            private void InitializeComponents(ICallflow callflow, ICall myCall, string logHeader)
            {
                scope = CfdModule.Instance.CreateScope(callflow, myCall, logHeader);

                {
            TcxGetGlobalPropertyComponent GetScheduledCallbacks = scope.CreateComponent<TcxGetGlobalPropertyComponent>("GetScheduledCallbacks");
            GetScheduledCallbacks.PropertyNameHandler = () => { return Convert.ToString(variableMap["project$.GlobalPropertyName"].Value).ToUpper(); };
            mainFlowComponentList.Add(GetScheduledCallbacks);
            GetNumberToCall841324610ECCComponent GetNumberToCall = new GetNumberToCall841324610ECCComponent("GetNumberToCall", callflow, myCall, logHeader);
            GetNumberToCall.Parameters.Add(new CallFlow.CFD.Parameter("callbacks", () => { return GetScheduledCallbacks.PropertyValue; }));
            mainFlowComponentList.Add(GetNumberToCall);
            ConditionalComponent CheckNumberToCall = scope.CreateComponent<ConditionalComponent>("CheckNumberToCall");
            mainFlowComponentList.Add(CheckNumberToCall);
            CheckNumberToCall.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.GREAT_THAN((IComparable)CFDFunctions.LEN(Convert.ToString(GetNumberToCall.ReturnValue)),(IComparable)0)); });
            CheckNumberToCall.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("ValidNumber"));
            RemoveNumberToCall132736388ECCComponent RemoveNumberToCall = new RemoveNumberToCall132736388ECCComponent("RemoveNumberToCall", callflow, myCall, logHeader);
            RemoveNumberToCall.Parameters.Add(new CallFlow.CFD.Parameter("number", () => { return GetNumberToCall.ReturnValue; }));
            RemoveNumberToCall.Parameters.Add(new CallFlow.CFD.Parameter("callbacks", () => { return GetScheduledCallbacks.PropertyValue; }));
            CheckNumberToCall.ContainerList[0].ComponentList.Add(RemoveNumberToCall);
            TcxSetGlobalPropertyComponent UpdateScheduledCallbacks = scope.CreateComponent<TcxSetGlobalPropertyComponent>("UpdateScheduledCallbacks");
            UpdateScheduledCallbacks.PropertyNameHandler = () => { return Convert.ToString(variableMap["project$.GlobalPropertyName"].Value).ToUpper(); };
            UpdateScheduledCallbacks.PropertyValueHandler = () => { return Convert.ToString(RemoveNumberToCall.ReturnValue); };
            CheckNumberToCall.ContainerList[0].ComponentList.Add(UpdateScheduledCallbacks);
            MakeCallComponent MakeCallback = scope.CreateComponent<MakeCallComponent>("MakeCallback");
            MakeCallback.OriginHandler = () => { return Convert.ToString(GetNumberToCall.ReturnValue); };
            MakeCallback.DestinationHandler = () => { return Convert.ToString("802"); };
            MakeCallback.TimeoutSeconds = 30;
            CheckNumberToCall.ContainerList[0].ComponentList.Add(MakeCallback);
            }
            {
            }
            
            }

            public Dialer(ICall myCall, string dialerID, PredictiveDialerManager predictiveDialerManager, int pauseBetweenDialerExecution, int initialDelayMilliseconds)
            {
                this.predictiveDialerManager = predictiveDialerManager;

                this.pauseBetweenDialerExecution = pauseBetweenDialerExecution;
                this.initialDelayMilliseconds = initialDelayMilliseconds;

                this.executionFinished = false;

                this.eventBuffer = new BufferBlock<AbsEvent>();

                this.currentComponentIndex = 0;
                this.mainFlowComponentList = new List<AbsComponent>();
                this.errorFlowComponentList = new List<AbsComponent>();
                this.currentFlowComponentList = mainFlowComponentList;

                this.variableMap = new Dictionary<string, Variable>();
                this.officeHoursManager = new OfficeHoursManager(myCall);

                string logHeader = $"Callback - Dialer {dialerID}";
                this.logFormatter = new LogFormatter(myCall, logHeader, "Dialer");

                InitializeVariables();
                InitializeComponents(this, myCall, logHeader);

                this.exitWorkingThreadCTS = new CancellationTokenSource();
                Task.Run(() => WorkingThreadLoop());
            }

            public void PostStartEvent()
            {
                eventBuffer.Post(new StartEvent());
            }

            public void PostDTMFReceivedEvent(char digit)
            {
                throw new InvalidOperationException("DTMFReceivedEvent can't be processed by a dialer.");
            }

            public void PostPromptPlayedEvent()
            {
                throw new InvalidOperationException("PromptPlayedEvent can't be processed by a dialer.");
            }

            public void PostTransferFailedEvent()
            {
                throw new InvalidOperationException("TransferFailedEvent can't be processed by a dialer.");
            }

            public void PostMakeCallResultEvent(bool result)
            {
                eventBuffer.Post(new MakeCallResultEvent(result));
            }

            public void PostCallTerminatedEvent()
            {
                throw new InvalidOperationException("CallTerminatedEvent can't be processed by a dialer.");
            }

            public void PostTimeoutEvent(object state)
            {
                throw new InvalidOperationException("TimeoutEvent can't be processed by a dialer.");
            }

            private async Task EventProcessingLoop()
            {
                executionFinished = false;
                while (!executionFinished)
                {
                    AbsEvent evt = await eventBuffer.ReceiveAsync();
                    await evt?.ProcessEvent(this);
                }
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
                        eventResult = await currentComponent.Start(variableMap);
                    }
                    while (CheckEventResult(eventResult) == EventResults.MoveToNextComponent);
                }
                catch (Exception exc)
                {
                    logFormatter.Error("Error executing last component: " + exc.ToString());
                    await ExecuteErrorFlow();
                }
            }

            public Task ProcessDTMFReceived(char digit)
            {
                throw new InvalidOperationException("DTMFReceivedEvent can't be processed by a dialer.");
            }

            public Task ProcessPromptPlayed()
            {
                throw new InvalidOperationException("PromptPlayedEvent can't be processed by a dialer.");
            }

            public Task ProcessTransferFailed()
            {
                throw new InvalidOperationException("TransferFailedEvent can't be processed by a dialer.");
            }

            public async Task ProcessMakeCallResult(bool result)
            {
                try
                {
                    AbsComponent currentComponent = currentFlowComponentList[currentComponentIndex];
                    logFormatter.Trace("OnMakeCallResult for component '" + currentComponent.Name + "' - Result: '" + result + "'");
                    EventResults eventResult = await currentComponent.OnMakeCallResult(null, variableMap, null, null, result);
                    if (CheckEventResult(eventResult) == EventResults.MoveToNextComponent)
                        await ProcessStart();
                }
                catch (Exception exc)
                {
                    logFormatter.Error("Error executing last component: " + exc.ToString());
                    await ExecuteErrorFlow();
                }
            }

            public Task ProcessCallTerminated()
            {
                throw new InvalidOperationException("CallTerminatedEvent can't be processed by a dialer.");
            }

            public Task ProcessTimeout(object state)
            {
                throw new InvalidOperationException("TimeoutEvent can't be processed by a dialer.");
            }

            public void Dispose()
            {
                exitWorkingThreadCTS.Cancel();
            }
        }
public class GetNumberToCall841324610ECCComponent : ExternalCodeExecutionComponent
            {
                public List<CallFlow.CFD.Parameter> Parameters { get; } = new List<CallFlow.CFD.Parameter>();
                public GetNumberToCall841324610ECCComponent(string name, ICallflow callflow, ICall myCall, string projectName) : base(name, callflow, myCall, projectName) {}
                protected override object ExecuteCode()
                {
                    return GetNumberToCall(Convert.ToString(Parameters[0].Value));
                }
            
            private object GetNumberToCall(string callbacks)
                {
            string[] callbackArray = callbacks.Split(',');
foreach (string callback in callbackArray)
{
    string[] callbackParts = callback.Split('=');
    if (callbackParts.Length == 2)
    {
        string number = callbackParts[0];
        DateTime dt = DateTime.ParseExact(callbackParts[1], "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None);
        if (dt <= DateTime.Now) return number;
    }
}

return "";    }
            }
            public class RemoveNumberToCall132736388ECCComponent : ExternalCodeExecutionComponent
            {
                public List<CallFlow.CFD.Parameter> Parameters { get; } = new List<CallFlow.CFD.Parameter>();
                public RemoveNumberToCall132736388ECCComponent(string name, ICallflow callflow, ICall myCall, string projectName) : base(name, callflow, myCall, projectName) {}
                protected override object ExecuteCode()
                {
                    return RemoveNumberToCall(Convert.ToString(Parameters[0].Value), Convert.ToString(Parameters[1].Value));
                }
            
            private object RemoveNumberToCall(string number, string callbacks)
                {
            string[] callbackArray = callbacks.Split(',');
foreach (string callback in callbackArray)
{
    string[] callbackParts = callback.Split('=');
    if (callbackParts.Length == 2)
    {
        if (number == callbackParts[0])
        {
            DateTime dt = DateTime.ParseExact(callbackParts[1], "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None);
            if (dt <= DateTime.Now)
            {
                return callbacks.Replace(callback + ",", "").Replace(callback, "").Replace(",,",",");
            }
        }
    }
}

return callbacks;    }
            }
            
   }
}
