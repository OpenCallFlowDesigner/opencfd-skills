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

namespace SurveyDemo
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
            SurveyComponent CustomerFeedback = scope.CreateComponent<SurveyComponent>("CustomerFeedback");
            CustomerFeedback.AllowDtmfInput = true;
            CustomerFeedback.MaxRetryCount = 2;
            CustomerFeedback.Timeout = 5000;
            CustomerFeedback.ExportToCSVFileHandler = () => { return Convert.ToString("/surveydemo/results/results.csv"); };
            CustomerFeedback.IntroductoryPrompts.Add(new AudioFilePrompt(() => { return "welcome.wav"; }));
            CustomerFeedback.GoodbyePrompts.Add(new AudioFilePrompt(() => { return "good_bye.wav"; }));
            CustomerFeedback.InvalidDigitPrompts.Add(new AudioFilePrompt(() => { return "invalid_input.wav"; }));
            CustomerFeedback.TimeoutPrompts.Add(new AudioFilePrompt(() => { return "timeout.wav"; }));
            CustomerFeedback.Parameters.Add(new CallFlow.CFD.Parameter("caller", () => { return variableMap["session.ani"].Value; }));
            CustomerFeedback.SurveyQuestions.Add(new YesNoSurveyQuestion("solved", new List<AbsPrompt>(), '1', '2'));
            CustomerFeedback.SurveyQuestions[CustomerFeedback.SurveyQuestions.Count - 1].Prompts.Add(new AudioFilePrompt(() => { return "question_1.wav"; }));
            CustomerFeedback.SurveyQuestions.Add(new RangeSurveyQuestion("rating", new List<AbsPrompt>(), new List<char> { '1','2','3','4','5' }));
            CustomerFeedback.SurveyQuestions[CustomerFeedback.SurveyQuestions.Count - 1].Prompts.Add(new AudioFilePrompt(() => { return "question_2.wav"; }));
            CustomerFeedback.SurveyQuestions.Add(new RecordingSurveyQuestion("comments", new List<AbsPrompt>(), 60000, true, new List<AbsPrompt>(), new List<AbsPrompt>(), '1', '2'));
            CustomerFeedback.SurveyQuestions[CustomerFeedback.SurveyQuestions.Count - 1].Prompts.Add(new AudioFilePrompt(() => { return "question_3.wav"; }));
            CustomerFeedback.SurveyQuestions[CustomerFeedback.SurveyQuestions.Count - 1].Prompts.Add(new AudioFilePrompt(() => { return Convert.ToString("beep.wav"); }));
            ((RecordingSurveyQuestion)CustomerFeedback.SurveyQuestions[CustomerFeedback.SurveyQuestions.Count - 1]).OfferPlaybackPreRecordingPrompts.Add(new AudioFilePrompt(() => { return "question_3_pre_recording.wav"; }));
            ((RecordingSurveyQuestion)CustomerFeedback.SurveyQuestions[CustomerFeedback.SurveyQuestions.Count - 1]).OfferPlaybackPostRecordingPrompts.Add(new AudioFilePrompt(() => { return "question_3_post_recording.wav"; }));
            CustomerFeedback.RecordingsPathHandler = () => { return Convert.ToString("/surveydemo/recordings"); };
            mainFlowComponentList.Add(CustomerFeedback);
            GetSolvedResponse2117402651ECCComponent GetSolvedResponse = new GetSolvedResponse2117402651ECCComponent("GetSolvedResponse", callflow, myCall, logHeader);
            GetSolvedResponse.Parameters.Add(new CallFlow.CFD.Parameter("surveyResult", () => { return CustomerFeedback.Result; }));
            mainFlowComponentList.Add(GetSolvedResponse);
            GetRatingResponse271680934ECCComponent GetRatingResponse = new GetRatingResponse271680934ECCComponent("GetRatingResponse", callflow, myCall, logHeader);
            GetRatingResponse.Parameters.Add(new CallFlow.CFD.Parameter("surveyResult", () => { return CustomerFeedback.Result; }));
            mainFlowComponentList.Add(GetRatingResponse);
            GetRecordingResponse1590449837ECCComponent GetRecordingResponse = new GetRecordingResponse1590449837ECCComponent("GetRecordingResponse", callflow, myCall, logHeader);
            GetRecordingResponse.Parameters.Add(new CallFlow.CFD.Parameter("surveyResult", () => { return CustomerFeedback.Result; }));
            mainFlowComponentList.Add(GetRecordingResponse);
            SqlServerDatabaseAccessComponent AddResultToDatabase = scope.CreateComponent<SqlServerDatabaseAccessComponent>("AddResultToDatabase");
            AddResultToDatabase.ServerHandler = () => { return Convert.ToString("DATABASE_SERVER"); };
            AddResultToDatabase.PortHandler = () => { return Convert.ToInt32(1433); };
            AddResultToDatabase.DatabaseHandler = () => { return Convert.ToString("DATABASE_NAME"); };
            AddResultToDatabase.UserNameHandler = () => { return Convert.ToString("DATABASE_USERNAME"); };
            AddResultToDatabase.PasswordHandler = () => { return Convert.ToString("DATABASE_PASSWORD"); };
            AddResultToDatabase.SqlStatementHandler = () => { return Convert.ToString("INSERT INTO survey_results (caller, solved, rating, recording) VALUES (@caller, @solved, @rating, @recording)"); };
            AddResultToDatabase.Parameters.Add(new CallFlow.CFD.Parameter("caller", () => { return variableMap["session.ani"].Value; }));
            AddResultToDatabase.Parameters.Add(new CallFlow.CFD.Parameter("solved", () => { return GetSolvedResponse.ReturnValue; }));
            AddResultToDatabase.Parameters.Add(new CallFlow.CFD.Parameter("rating", () => { return GetRatingResponse.ReturnValue; }));
            AddResultToDatabase.Parameters.Add(new CallFlow.CFD.Parameter("recording", () => { return GetRecordingResponse.ReturnValue; }));
            AddResultToDatabase.UseConnectionString = false;
            AddResultToDatabase.StatementType = DatabaseAccessComponent.StatementTypes.NonQuery;
            AddResultToDatabase.Timeout = 30000;
            mainFlowComponentList.Add(AddResultToDatabase);
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
         string logHeader = $"SurveyDemo - CallID {callID}";
         this.logFormatter = new LogFormatter(MyCall, logHeader, "Callflow");
         this.promptQueue = new PromptQueue(this, MyCall, "SurveyDemo", logHeader);
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


      public class GetSolvedResponse2117402651ECCComponent : ExternalCodeExecutionComponent
            {
                public List<CallFlow.CFD.Parameter> Parameters { get; } = new List<CallFlow.CFD.Parameter>();
                public GetSolvedResponse2117402651ECCComponent(string name, ICallflow callflow, ICall myCall, string projectName) : base(name, callflow, myCall, projectName) {}
                protected override object ExecuteCode()
                {
                    return GetSolvedResponse(Convert.ToString(Parameters[0].Value));
                }
            
            private object GetSolvedResponse(string surveyResult)
                {
            return surveyResult.Split(',')[1];    }
            }
            public class GetRatingResponse271680934ECCComponent : ExternalCodeExecutionComponent
            {
                public List<CallFlow.CFD.Parameter> Parameters { get; } = new List<CallFlow.CFD.Parameter>();
                public GetRatingResponse271680934ECCComponent(string name, ICallflow callflow, ICall myCall, string projectName) : base(name, callflow, myCall, projectName) {}
                protected override object ExecuteCode()
                {
                    return GetRatingResponse(Convert.ToString(Parameters[0].Value));
                }
            
            private object GetRatingResponse(string surveyResult)
                {
            return surveyResult.Split(',')[2];    }
            }
            public class GetRecordingResponse1590449837ECCComponent : ExternalCodeExecutionComponent
            {
                public List<CallFlow.CFD.Parameter> Parameters { get; } = new List<CallFlow.CFD.Parameter>();
                public GetRecordingResponse1590449837ECCComponent(string name, ICallflow callflow, ICall myCall, string projectName) : base(name, callflow, myCall, projectName) {}
                protected override object ExecuteCode()
                {
                    return GetRecordingResponse(Convert.ToString(Parameters[0].Value));
                }
            
            private object GetRecordingResponse(string surveyResult)
                {
            return surveyResult.Split(',')[3];    }
            }
            
   }
}
