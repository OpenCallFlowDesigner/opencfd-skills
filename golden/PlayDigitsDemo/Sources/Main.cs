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

namespace PlayDigitsDemo
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
            UserInputComponent requestInput = scope.CreateComponent<UserInputComponent>("requestInput");
            requestInput.AllowDtmfInput = true;
            requestInput.MaxRetryCount = 2;
            requestInput.FirstDigitTimeout = 5000;
            requestInput.InterDigitTimeout = 3000;
            requestInput.FinalDigitTimeout = 2000;
            requestInput.MinDigits = 3;
            requestInput.MaxDigits = 6;
            requestInput.ValidDigitList.AddRange(new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' });
            requestInput.StopDigitList.AddRange(new char[] { '#' });
            requestInput.InitialPrompts.Add(new AudioFilePrompt(() => { return "InitialPrompt.wav"; }));
            requestInput.SubsequentPrompts.Add(new AudioFilePrompt(() => { return "SubsequentPrompt.wav"; }));
            requestInput.InvalidDigitPrompts.Add(new AudioFilePrompt(() => { return "InvalidInput.wav"; }));
            requestInput.TimeoutPrompts.Add(new AudioFilePrompt(() => { return "Timeout.wav"; }));
            mainFlowComponentList.Add(requestInput);
            ConditionalComponent requestInput_Conditional = scope.CreateComponent<ConditionalComponent>("requestInput_Conditional");
            mainFlowComponentList.Add(requestInput_Conditional);
            requestInput_Conditional.ConditionList.Add(() => { return requestInput.Result == UserInputComponent.UserInputResults.ValidDigits; });
            requestInput_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("requestInput_Conditional_ValidInput"));
            PlayDigits playEnteredDigits = new PlayDigits(onlineServices, officeHoursManager, scope, "playEnteredDigits", callflow, myCall, logHeader);
            playEnteredDigits.DigitsSetter = () => { return requestInput.Buffer; };
            requestInput_Conditional.ContainerList[0].ComponentList.Add(playEnteredDigits);
            requestInput_Conditional.ConditionList.Add(() => { return requestInput.Result == UserInputComponent.UserInputResults.InvalidDigits || requestInput.Result == UserInputComponent.UserInputResults.Timeout; });
            requestInput_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("requestInput_Conditional_InvalidInput"));
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

         AbsTextToSpeechEngine textToSpeechEngine = null;
         AbsSpeechToTextEngine speechToTextEngine = null;
         this.onlineServices = new OnlineServices(textToSpeechEngine, speechToTextEngine);
      }

      public override void Start()
      {
         string callID = MyCall?.Caller["chid"] ?? "Unknown";
         string logHeader = $"PlayDigitsDemo - CallID {callID}";
         this.logFormatter = new LogFormatter(MyCall, logHeader, "Callflow");
         this.promptQueue = new PromptQueue(this, MyCall, "PlayDigitsDemo", logHeader);
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


              // ------------------------------------------------------------------------------------------------------------
        // User Defined component
        // ------------------------------------------------------------------------------------------------------------
        public class PlayDigits : AbsUserComponent
        {
            private OnlineServices onlineServices;
            private OfficeHoursManager officeHoursManager;
            private CfdAppScope scope;

            private ObjectExpressionHandler _DigitsHandler = null;
            private ObjectExpressionHandler _IndexHandler = null;
            

            protected override void InitializeVariables()
            {
                componentVariableMap["callflow$.Digits"] = new Variable("");
                componentVariableMap["callflow$.Index"] = new Variable(0);
                
            }

            protected override void InitializeComponents()
            {
                Dictionary<string, Variable> variableMap = componentVariableMap;
                {
            LoopComponent digitsLoop = scope.CreateComponent<LoopComponent>("digitsLoop");
            digitsLoop.Condition = () => { return Convert.ToBoolean(CFDFunctions.LESS_THAN((IComparable)variableMap["callflow$.Index"].Value,(IComparable)CFDFunctions.LEN(Convert.ToString(variableMap["callflow$.Digits"].Value)))); };
            digitsLoop.Container = scope.CreateComponent<SequenceContainerComponent>("digitsLoop_Container");
            mainFlowComponentList.Add(digitsLoop);
            PromptPlaybackComponent playDigit = scope.CreateComponent<PromptPlaybackComponent>("playDigit");
            playDigit.AllowDtmfInput = true;
            playDigit.Prompts.Add(new AudioFilePrompt(() => { return Convert.ToString(CFDFunctions.CONCATENATE(Convert.ToString(CFDFunctions.MID(Convert.ToString(variableMap["callflow$.Digits"].Value),Convert.ToInt32(variableMap["callflow$.Index"].Value),Convert.ToInt32(1))),Convert.ToString(".wav"))); }));
            digitsLoop.Container.ComponentList.Add(playDigit);
            IncrementVariableComponent incrementIndex = scope.CreateComponent<IncrementVariableComponent>("incrementIndex");
            incrementIndex.VariableName = "callflow$.Index";
            digitsLoop.Container.ComponentList.Add(incrementIndex);
            }
            {
            }
            {
            }
            
            }
            
            public PlayDigits(OnlineServices onlineServices, OfficeHoursManager officeHoursManager,
                CfdAppScope scope, string name, ICallflow callflow, ICall myCall, string logHeader) : base(name, callflow, myCall, logHeader)
            {
                this.onlineServices = onlineServices;
                this.officeHoursManager = officeHoursManager;
                this.scope = scope;
            }
     
            protected override void GetVariableValues()
            {
                if (_DigitsHandler != null) componentVariableMap["callflow$.Digits"].Set(_DigitsHandler());
                if (_IndexHandler != null) componentVariableMap["callflow$.Index"].Set(_IndexHandler());
                
            }
            
            public ObjectExpressionHandler DigitsSetter { set { _DigitsHandler = value; } }
            public object Digits { get { return componentVariableMap["callflow$.Digits"].Value; } }
            public ObjectExpressionHandler IndexSetter { set { _IndexHandler = value; } }
            public object Index { get { return componentVariableMap["callflow$.Index"].Value; } }
            

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

   }
}
