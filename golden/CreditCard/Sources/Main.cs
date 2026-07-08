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

namespace CreditCard
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
         variableMap["callflow$.WSUserName"] = new Variable("user");
            variableMap["callflow$.WSPassword"] = new Variable("pass");
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
            CreditCardLoopComponent requestCreditCard = scope.CreateComponent<CreditCardLoopComponent>("requestCreditCard");
            requestCreditCard.Condition = () => { return Convert.ToBoolean(CFDFunctions.AND(Convert.ToBoolean(CFDFunctions.LESS_THAN((IComparable)requestCreditCard.LoopCounter,(IComparable)4)),Convert.ToBoolean(CFDFunctions.NOT(Convert.ToBoolean(variableMap["requestCreditCard.Validated"].Value))))); };
            requestCreditCard.Container = scope.CreateComponent<SequenceContainerComponent>("requestCreditCard_Container");
            mainFlowComponentList.Add(requestCreditCard);
            UserInputComponent requestCreditCardRequestNumber = scope.CreateComponent<UserInputComponent>("requestCreditCardRequestNumber");
            requestCreditCardRequestNumber.HasToPauseRecording = true;
            requestCreditCardRequestNumber.AllowDtmfInput = true;
            requestCreditCardRequestNumber.MaxRetryCount = 2;
            requestCreditCardRequestNumber.FirstDigitTimeout = 5000;
            requestCreditCardRequestNumber.InterDigitTimeout = 3000;
            requestCreditCardRequestNumber.FinalDigitTimeout = 2000;
            requestCreditCardRequestNumber.MinDigits = 8;
            requestCreditCardRequestNumber.MaxDigits = 19;
            requestCreditCardRequestNumber.ValidDigitList.AddRange(new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' });
            requestCreditCardRequestNumber.StopDigitList.AddRange(new char[] { '#' });
            requestCreditCardRequestNumber.InitialPrompts.Add(new AudioFilePrompt(() => { return "enter_credit_card_number.wav"; }));
            requestCreditCardRequestNumber.SubsequentPrompts.Add(new AudioFilePrompt(() => { return "enter_credit_card_number.wav"; }));
            requestCreditCardRequestNumber.InvalidDigitPrompts.Add(new AudioFilePrompt(() => { return "invalid_digit.wav"; }));
            requestCreditCardRequestNumber.TimeoutPrompts.Add(new AudioFilePrompt(() => { return "timeout.wav"; }));
            requestCreditCard.Container.ComponentList.Add(requestCreditCardRequestNumber);
            requestCreditCard.NumberHandler = () => { return requestCreditCardRequestNumber.Buffer; };
            ConditionalComponent requestCreditCardRequestNumber_Conditional = scope.CreateComponent<ConditionalComponent>("requestCreditCardRequestNumber_Conditional");
            requestCreditCard.Container.ComponentList.Add(requestCreditCardRequestNumber_Conditional);
            requestCreditCardRequestNumber_Conditional.ConditionList.Add(() => { return requestCreditCardRequestNumber.Result == UserInputComponent.UserInputResults.ValidDigits; });
            requestCreditCardRequestNumber_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("requestCreditCardRequestNumber_Conditional_ValidInput"));
            WebInteractionComponent validateCreditCard = scope.CreateComponent<WebInteractionComponent>("validateCreditCard");
            validateCreditCard.HttpMethod = System.Net.Http.HttpMethod.Get;
            validateCreditCard.Timeout = 30000;
            validateCreditCard.UriHandler = () => { return Convert.ToString(CFDFunctions.CONCATENATE(Convert.ToString("https://webservice.example.com/validateCreditCard?number="),Convert.ToString(requestCreditCard.Number),Convert.ToString("&expiration="),Convert.ToString(requestCreditCard.Expiration),Convert.ToString("&security_code="),Convert.ToString(requestCreditCard.SecurityCode))); };
            validateCreditCard.Headers.Add(new CallFlow.CFD.Parameter("Authorization", () => { return "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((variableMap["callflow$.WSUserName"].Value) + ":" + (variableMap["callflow$.WSPassword"].Value))); }));
            requestCreditCardRequestNumber_Conditional.ContainerList[0].ComponentList.Add(validateCreditCard);
            ConditionalComponent checkValidationResult = scope.CreateComponent<ConditionalComponent>("checkValidationResult");
            requestCreditCardRequestNumber_Conditional.ContainerList[0].ComponentList.Add(checkValidationResult);
            checkValidationResult.ConditionList.Add(() => { return Convert.ToBoolean(CFDFunctions.EQUAL(validateCreditCard.ResponseContent,"1")); });
            checkValidationResult.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("validated"));
            VariableAssignmentComponent setValidationResult = scope.CreateComponent<VariableAssignmentComponent>("setValidationResult");
            setValidationResult.VariableName = "requestCreditCard.Validated";
            setValidationResult.VariableValueHandler = () => { return true; };
            checkValidationResult.ContainerList[0].ComponentList.Add(setValidationResult);
            TransferComponent transferToSales = scope.CreateComponent<TransferComponent>("transferToSales");
            transferToSales.DestinationHandler = () => { return Convert.ToString("800"); };
            transferToSales.DelayMilliseconds = 500;
            checkValidationResult.ContainerList[0].ComponentList.Add(transferToSales);
            checkValidationResult.ConditionList.Add(() => { return Convert.ToBoolean(true); });
            checkValidationResult.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("not_validated"));
            PromptPlaybackComponent playValidationError = scope.CreateComponent<PromptPlaybackComponent>("playValidationError");
            playValidationError.AllowDtmfInput = true;
            playValidationError.Prompts.Add(new AudioFilePrompt(() => { return "validation_error.wav"; }));
            checkValidationResult.ContainerList[1].ComponentList.Add(playValidationError);
            ConditionalComponent requestCreditCard_InvalidInputConditional = scope.CreateComponent<ConditionalComponent>("requestCreditCard_InvalidInputConditional");
            requestCreditCard.Container.ComponentList.Add(requestCreditCard_InvalidInputConditional);
            requestCreditCard_InvalidInputConditional.ConditionList.Add(() => { return requestCreditCardRequestNumber.Result == UserInputComponent.UserInputResults.InvalidDigits || requestCreditCardRequestNumber.Result == UserInputComponent.UserInputResults.Timeout; });
            requestCreditCard_InvalidInputConditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("requestCreditCard_InvalidInputConditional"));
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
         string logHeader = $"CreditCard - CallID {callID}";
         this.logFormatter = new LogFormatter(MyCall, logHeader, "Callflow");
         this.promptQueue = new PromptQueue(this, MyCall, "CreditCard", logHeader);
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
