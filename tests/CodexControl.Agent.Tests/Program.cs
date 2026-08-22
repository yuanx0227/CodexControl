using CodexControl.Agent.Tests;

if (FakeCodexCommand.CanHandle(args))
{
    return await FakeCodexCommand.RunAsync(args).ConfigureAwait(false);
}

if (IntegrationHostCommand.CanHandle(args))
{
    return await IntegrationHostCommand.RunAsync(args).ConfigureAwait(false);
}

if (SoakCommand.CanHandle(args))
{
    return await SoakCommand.RunAsync(args).ConfigureAwait(false);
}

return await TestRunner.RunAsync().ConfigureAwait(false);
