@{
    # Console tools: colored Write-Host output, short verbs and plain-text secrets read from the user on purpose
    ExcludeRules = @(
        'PSAvoidUsingWriteHost',
        'PSUseApprovedVerbs',
        'PSUseSingularNouns',
        'PSUseShouldProcessForStateChangingFunctions',
        'PSAvoidUsingConvertToSecureStringWithPlainText',
        'PSAvoidUsingPlainTextForPassword',
        'PSReviewUnusedParameter',
        'PSAvoidGlobalVars',
        'PSAvoidUsingPositionalParameters'
    )
}
