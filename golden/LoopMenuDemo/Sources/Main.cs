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

namespace LoopMenuDemo
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
         variableMap["callflow$.ContinueLoopingMainMenu"] = new Variable(true);
            variableMap["callflow$.ContinueLoopingSubMenu"] = new Variable(true);
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
            LoopComponent MainLoop = scope.CreateComponent<LoopComponent>("MainLoop");
            MainLoop.Condition = () => { return Convert.ToBoolean(variableMap["callflow$.ContinueLoopingMainMenu"].Value); };
            MainLoop.Container = scope.CreateComponent<SequenceContainerComponent>("MainLoop_Container");
            mainFlowComponentList.Add(MainLoop);
            VariableAssignmentComponent NoLoopMainMenu = scope.CreateComponent<VariableAssignmentComponent>("NoLoopMainMenu");
            NoLoopMainMenu.VariableName = "callflow$.ContinueLoopingMainMenu";
            NoLoopMainMenu.VariableValueHandler = () => { return false; };
            MainLoop.Container.ComponentList.Add(NoLoopMainMenu);
            MenuComponent MainMenu = scope.CreateComponent<MenuComponent>("MainMenu");
            MainMenu.AllowDtmfInput = true;
            MainMenu.MaxRetryCount = 2;
            MainMenu.Timeout = 5000;
            MainMenu.ValidOptionList.AddRange(new char[] { '1', '2', '3' });
            MainMenu.InitialPrompts.Add(new AudioFilePrompt(() => { return "main_menu_initial_prompt.wav"; }));
            MainMenu.SubsequentPrompts.Add(new AudioFilePrompt(() => { return "main_menu_subsequent_prompt.wav"; }));
            MainMenu.InvalidDigitPrompts.Add(new AudioFilePrompt(() => { return "invalid_option.wav"; }));
            MainMenu.TimeoutPrompts.Add(new AudioFilePrompt(() => { return "timeout.wav"; }));
            MainLoop.Container.ComponentList.Add(MainMenu);
            ConditionalComponent MainMenu_Conditional = scope.CreateComponent<ConditionalComponent>("MainMenu_Conditional");
            MainLoop.Container.ComponentList.Add(MainMenu_Conditional);
            MainMenu_Conditional.ConditionList.Add(() => { return MainMenu.Result == MenuComponent.MenuResults.ValidOption && MainMenu.SelectedOption == '1'; });
            MainMenu_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("MainMenu_Conditional_Option1"));
            PromptPlaybackComponent PlayMainMenuMessage = scope.CreateComponent<PromptPlaybackComponent>("PlayMainMenuMessage");
            PlayMainMenuMessage.AllowDtmfInput = true;
            PlayMainMenuMessage.Prompts.Add(new AudioFilePrompt(() => { return "main_menu_message.wav"; }));
            MainMenu_Conditional.ContainerList[0].ComponentList.Add(PlayMainMenuMessage);
            MainMenu_Conditional.ConditionList.Add(() => { return MainMenu.Result == MenuComponent.MenuResults.ValidOption && MainMenu.SelectedOption == '2'; });
            MainMenu_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("MainMenu_Conditional_Option2"));
            VariableAssignmentComponent LoopMainMenu = scope.CreateComponent<VariableAssignmentComponent>("LoopMainMenu");
            LoopMainMenu.VariableName = "callflow$.ContinueLoopingMainMenu";
            LoopMainMenu.VariableValueHandler = () => { return true; };
            MainMenu_Conditional.ContainerList[1].ComponentList.Add(LoopMainMenu);
            MainMenu_Conditional.ConditionList.Add(() => { return MainMenu.Result == MenuComponent.MenuResults.ValidOption && MainMenu.SelectedOption == '3'; });
            MainMenu_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("MainMenu_Conditional_Option3"));
            VariableAssignmentComponent LoopSubMenu = scope.CreateComponent<VariableAssignmentComponent>("LoopSubMenu");
            LoopSubMenu.VariableName = "callflow$.ContinueLoopingSubMenu";
            LoopSubMenu.VariableValueHandler = () => { return true; };
            MainMenu_Conditional.ContainerList[2].ComponentList.Add(LoopSubMenu);
            LoopComponent SubMenuLoop = scope.CreateComponent<LoopComponent>("SubMenuLoop");
            SubMenuLoop.Condition = () => { return Convert.ToBoolean(variableMap["callflow$.ContinueLoopingSubMenu"].Value); };
            SubMenuLoop.Container = scope.CreateComponent<SequenceContainerComponent>("SubMenuLoop_Container");
            MainMenu_Conditional.ContainerList[2].ComponentList.Add(SubMenuLoop);
            VariableAssignmentComponent NoLoopSubMenu = scope.CreateComponent<VariableAssignmentComponent>("NoLoopSubMenu");
            NoLoopSubMenu.VariableName = "callflow$.ContinueLoopingSubMenu";
            NoLoopSubMenu.VariableValueHandler = () => { return false; };
            SubMenuLoop.Container.ComponentList.Add(NoLoopSubMenu);
            MenuComponent SubMenu = scope.CreateComponent<MenuComponent>("SubMenu");
            SubMenu.AllowDtmfInput = true;
            SubMenu.MaxRetryCount = 2;
            SubMenu.Timeout = 5000;
            SubMenu.ValidOptionList.AddRange(new char[] { '1', '2', '3' });
            SubMenu.InitialPrompts.Add(new AudioFilePrompt(() => { return "sub_menu_initial_prompt.wav"; }));
            SubMenu.SubsequentPrompts.Add(new AudioFilePrompt(() => { return "sub_menu_subsequent_prompt.wav"; }));
            SubMenu.InvalidDigitPrompts.Add(new AudioFilePrompt(() => { return "invalid_option.wav"; }));
            SubMenu.TimeoutPrompts.Add(new AudioFilePrompt(() => { return "timeout.wav"; }));
            SubMenuLoop.Container.ComponentList.Add(SubMenu);
            ConditionalComponent SubMenu_Conditional = scope.CreateComponent<ConditionalComponent>("SubMenu_Conditional");
            SubMenuLoop.Container.ComponentList.Add(SubMenu_Conditional);
            SubMenu_Conditional.ConditionList.Add(() => { return SubMenu.Result == MenuComponent.MenuResults.ValidOption && SubMenu.SelectedOption == '1'; });
            SubMenu_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("SubMenu_Conditional_Option1"));
            PromptPlaybackComponent PlaySubMenuMessage = scope.CreateComponent<PromptPlaybackComponent>("PlaySubMenuMessage");
            PlaySubMenuMessage.AllowDtmfInput = true;
            PlaySubMenuMessage.Prompts.Add(new AudioFilePrompt(() => { return "sub_menu_message.wav"; }));
            SubMenu_Conditional.ContainerList[0].ComponentList.Add(PlaySubMenuMessage);
            SubMenu_Conditional.ConditionList.Add(() => { return SubMenu.Result == MenuComponent.MenuResults.ValidOption && SubMenu.SelectedOption == '2'; });
            SubMenu_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("SubMenu_Conditional_Option2"));
            VariableAssignmentComponent LoopSubMenu2 = scope.CreateComponent<VariableAssignmentComponent>("LoopSubMenu2");
            LoopSubMenu2.VariableName = "callflow$.ContinueLoopingSubMenu";
            LoopSubMenu2.VariableValueHandler = () => { return true; };
            SubMenu_Conditional.ContainerList[1].ComponentList.Add(LoopSubMenu2);
            SubMenu_Conditional.ConditionList.Add(() => { return SubMenu.Result == MenuComponent.MenuResults.ValidOption && SubMenu.SelectedOption == '3'; });
            SubMenu_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("SubMenu_Conditional_Option3"));
            VariableAssignmentComponent LoopMainMenu2 = scope.CreateComponent<VariableAssignmentComponent>("LoopMainMenu2");
            LoopMainMenu2.VariableName = "callflow$.ContinueLoopingMainMenu";
            LoopMainMenu2.VariableValueHandler = () => { return true; };
            SubMenu_Conditional.ContainerList[2].ComponentList.Add(LoopMainMenu2);
            SubMenu_Conditional.ConditionList.Add(() => { return SubMenu.Result == MenuComponent.MenuResults.InvalidOption || SubMenu.Result == MenuComponent.MenuResults.Timeout; });
            SubMenu_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("SubMenu_Conditional_TimeoutOrInvalidOption"));
            DisconnectCallComponent DisconnectCall2 = scope.CreateComponent<DisconnectCallComponent>("DisconnectCall2");
            SubMenu_Conditional.ContainerList[3].ComponentList.Add(DisconnectCall2);
            MainMenu_Conditional.ConditionList.Add(() => { return MainMenu.Result == MenuComponent.MenuResults.InvalidOption || MainMenu.Result == MenuComponent.MenuResults.Timeout; });
            MainMenu_Conditional.ContainerList.Add(scope.CreateComponent<SequenceContainerComponent>("MainMenu_Conditional_TimeoutOrInvalidOption"));
            DisconnectCallComponent DisconnectCall1 = scope.CreateComponent<DisconnectCallComponent>("DisconnectCall1");
            MainMenu_Conditional.ContainerList[3].ComponentList.Add(DisconnectCall1);
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
         string logHeader = $"LoopMenuDemo - CallID {callID}";
         this.logFormatter = new LogFormatter(MyCall, logHeader, "Callflow");
         this.promptQueue = new PromptQueue(this, MyCall, "LoopMenuDemo", logHeader);
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
