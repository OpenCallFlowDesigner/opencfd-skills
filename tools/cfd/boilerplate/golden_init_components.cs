
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
            
