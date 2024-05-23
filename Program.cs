// This code is licensed under MIT. Do whatever.
// It is demo code, and as such not intended to be secure, correct for all inputs, or work much beyond showing off something cool.

using gitgpt;

string chatLogFilename = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitGpt", "chatlog.json");

// First we check if any options are present.
// Reset allows the user to easily clear the message log, and is used if ChatGPT becomes unresponsive.
if (args is ["--reset"])
{
    try
    {
        File.Delete(chatLogFilename);
    }
    catch(DirectoryNotFoundException)
    {
        // Don't care.
    }
    Console.WriteLine($"Deleted \"{chatLogFilename}\"");
    return 0;
}
if (args is ["--help"] or [])
{
    Console.WriteLine("""
    Usage: gitgpt <options> [message]
    Options:
      --help    Shows this help screen
      --reset   Resets the chat session. Use this if ChatGPT refuses to respond, or execute commands.
    """);
    return 0;
}

// We create a cancellation token that is triggered in the event that the user presses ctrl+c
CancellationTokenSource cancel = new();
Console.CancelKeyPress += (_, ev) =>
{
    cancel.Cancel();
    ev.Cancel = true;
};

// Replace exception with API key please. In a non-toy application this would be configurable.
static string GetApiKey() => throw new NotImplementedException("I ran GitGPT and all I got was this lousy exception. Update the GetApiKey function to not cause a crash, please.");

// Except if the command line is switches, we just want a single command line.
var cmd = string.Join(" ", args);

// There's no message to send. Just quit.
if (cmd is not { Length: > 0 })
{
    return 1;
}

var chatLog = new ChatLog(chatLogFilename, TimeProvider.System);
await chatLog.ReadChatLog();
var completion = new ChatCompletion(GetApiKey());

chatLog.AddUserMessage(cmd);
try
{
    await completion.DoCompletion(chatLog, cancel.Token);
}
// If the user presses Ctrl+C then TaskCancelled or OperationCancelled will be thrown. TaskCancelledException inherits from OperationCancelledException so this will catch both.
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled by user.");
}

// We're done. Save all messages in the log.
if (completion.ResetRequested)
{
    chatLog.Delete();
}
else
{
    await chatLog.SaveChatLog();
}

return 0;