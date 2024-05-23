// This code is licensed under MIT. Do whatever.
// It is demo code, and as such not intended to be secure, correct for all inputs, or work much beyond showing off something cool.

using OpenAI;
using LibGit2Sharp;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.Builders;
using System.Text;
using System.Text.Json;
using OpenAI.ObjectModels.SharedModels;

var DefaultSerializerOptions = new JsonSerializerOptions() { WriteIndented = true };
string chatLogFilename = Path.Combine($"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}", "CapGpt", "chatlog.json");

// First we check if any options are present.
// Reset allows the user to easily clear the message log, and is used if ChatGPT becomes unresponsive.
if(args is ["--reset"])
{
    Console.WriteLine(RestartSession());
    return 0;
}
if(args is ["--help"])
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

// Convenience function to get current time.
static DateTimeOffset Now() => TimeProvider.System.GetLocalNow();

// Replace exception with API key please. In a non-toy application this would be configurable.
static string GetApiKey() => throw new NotImplementedException("I ran GitGPT and all I got was this lousy exception. Update the GetApiKey function to not cause a crash, please.");

// Create the OpenAI service. We default to GPT 4o, which is at the time of writing the newest version.
var service = new OpenAI.Managers.OpenAIService(new OpenAiOptions {  ApiKey = GetApiKey(), DefaultModelId = Models.Gpt_4o });

// First thing we do is fetch old messages.
List<ChatMessage> chatMessages = await ReadChatLog();

// Except if the command line is switches, we just want a single command line.
var cmd = string.Join(" ", args);

// This variable is used to determine if Save should do anything or not. If the session is restarted, then saving would write back all the deleted messages again.
bool sessionRestarted = false;

// There's no message to send. Just quit.
if(cmd is not { Length: > 0 })
{
    return 1;
}

AddUserMessage(cmd);
try
{
    await DoCompletion();
}
// If the user presses Ctrl+C then TaskCancelled or OperationCancelled will be thrown. TaskCancelledException inherits from OperationCancelledException so this will catch both.
catch(OperationCanceledException)
{
    Console.WriteLine("Cancelled by user.");
}
// We're done. Save all messages in the log.
await SaveChatLog(chatMessages);

return 0;

async Task<List<ChatMessage>> ReadChatLog()
{
    // We unconditionally create the directory. This function is no-op if the directory already exists.
    Directory.CreateDirectory(Path.GetDirectoryName(chatLogFilename) ?? throw new InvalidOperationException());
    using var fs = File.Open(chatLogFilename, FileMode.OpenOrCreate);
    if(fs.Length == 0)
    {
        return StartNewChat();
    }
    return await JsonSerializer.DeserializeAsync<List<ChatMessage>>(fs) ?? [];
}

async Task SaveChatLog(List<ChatMessage> messages)
{
    if(sessionRestarted)
    {
        return;
    }
    using var fs = File.Open(chatLogFilename, FileMode.Create);
    await JsonSerializer.SerializeAsync(fs, messages, DefaultSerializerOptions);
}

ToolDefinition DefineRestartTool()
{
    var fun = new FunctionDefinitionBuilder(nameof(RestartSession), "Restarts the chat session.");
    return ToolDefinition.DefineFunction(fun.Build());
}

ToolDefinition DefineGitStatusTool()
{
    var fun = new FunctionDefinitionBuilder(nameof(GitStatus), "Get the current status of git in the current directory.");
    return ToolDefinition.DefineFunction(fun.Build());
}

ToolDefinition DefineGitStageTool() => DefineFunctionCall(nameof(GitStage), "Stages files for commit.", b => b.AddParameter("files", PropertyDefinition.DefineArray(PropertyDefinition.DefineString("Name of file to stage. This can use a glob syntax."))));
ToolDefinition DefineGitUnstageTool() => DefineFunctionCall(nameof(GitUnstage), "Unstages files.", b => b.AddParameter("files", PropertyDefinition.DefineArray(PropertyDefinition.DefineString("Name of file to unstage. This can use a glob syntax."))));
ToolDefinition DefineGitCommitTool() => DefineFunctionCall(nameof(GitCommit), "Commits staged changes with the specified commit message.", 
    b => b.AddParameter("commitMessage", PropertyDefinition.DefineString("The commit message to use.")));

//ToolDefinition DefineUnstageTool() => DefineFunctionCall(nameo)

ToolDefinition DefineFunctionCall(string name, string? description, Action<FunctionDefinitionBuilder> action)
{
    var builder  = new FunctionDefinitionBuilder(name, description);
    action(builder);
    return ToolDefinition.DefineFunction(builder.Build());
}

async Task DoCompletion()
{
    var request = new ChatCompletionCreateRequest
    {
         Messages = chatMessages,
         Temperature = 0.8f, // Temperature decides "creativity" of output. Temperature of 0 makes output deterministic.
         Tools = [DefineRestartTool(), DefineGitStatusTool(), DefineGitStageTool(), DefineGitUnstageTool(), DefineGitCommitTool()]
    };

    var completion = await service.ChatCompletion.CreateCompletion(request, cancellationToken: cancel.Token);

    // If there were no choices from ChatGPT then nothing to do.
    if(completion is not { Choices.Count : > 0 })
    {
        return;
    }

    // We just pick the first one.
    var choice = completion.Choices[0];

    chatMessages.Add(ChatMessage.FromAssistant(choice.Message.Content ?? "", toolCalls: choice.Message.ToolCalls));

    if(choice.Message is {Content: { Length: > 0 } message })
    {
        Console.WriteLine(message);
    }
    if(choice.Message.ToolCalls is { Count: > 0 } tools)
    {
        foreach(var tool in tools)
        {
            var toolCallResponse = ProcessTool(tool);
            chatMessages.Add(ChatMessage.FromTool(toolCallResponse, tool.Id!));
        }

        // The agent might not be finished, and it might not have produced a message, so we'll run it back to ChatGPT to see if it wants to do more, or write something.
        await DoCompletion();
    }
}

string ProcessTool(ToolCall tool)
{
    return tool switch
    {
        { FunctionCall.Name: nameof(RestartSession) } => RestartSession(),
        { FunctionCall.Name: nameof(GitStatus)} => GitStatus(),
        { FunctionCall.Name: nameof(GitStage)} => GitStage([..((JsonElement)tool.FunctionCall.ParseArguments()["files"]).EnumerateArray().Select(x => x!.ToString())]),
        { FunctionCall.Name: nameof(GitUnstage) } => GitUnstage([.. ((JsonElement)tool.FunctionCall.ParseArguments()["files"]).EnumerateArray().Select(x => x!.ToString())]),
        { FunctionCall.Name: nameof(GitCommit)} => GitCommit(tool.FunctionCall.ParseArguments()["commitMessage"].ToString()!),
        _ => $"{tool.FunctionCall!.Name} could not be found."
    };
}

string GitCommit(string commitMessage)
{
    var result = new StringBuilder();
    var repo = new Repository(Environment.CurrentDirectory);
    var signature = repo.Config.BuildSignature(DateTimeOffset.UtcNow);
    var commit = repo.Commit(commitMessage, signature, signature, new CommitOptions() { AllowEmptyCommit = false });
    return $"Commit {commit.Sha} created for author {commit.Author.Name}.";
}

string GitStage(string[] files)
{
    var result = new StringBuilder();
    var repo = new Repository(Environment.CurrentDirectory);

    Commands.Stage(repo, files);

    var status = repo.RetrieveStatus(new StatusOptions() 
    {
        IncludeUntracked = false,
        DetectRenamesInIndex = true,
        DetectRenamesInWorkDir = true,
        Show = StatusShowOption.IndexAndWorkDir
    });

    result.AppendLine("Staged files:");
    foreach(var item in status.Staged)
    {
        result.AppendLine($"- {item.FilePath}");
    }

    return result.ToString();
}

string GitUnstage(string[] files)
{
    var result = new StringBuilder();
    var repo = new Repository(Environment.CurrentDirectory);

    Commands.Unstage(repo, files);

    var status = repo.RetrieveStatus(new StatusOptions()
    {
        IncludeUntracked = false,
        DetectRenamesInIndex = true,
        DetectRenamesInWorkDir = true,
        Show = StatusShowOption.IndexAndWorkDir
    });

    result.AppendLine("Staged files:");
    foreach (var item in status.Staged)
    {
        result.AppendLine($"- {item.FilePath}");
    }

    return result.ToString();
}

string GitStatus()
{
    StringBuilder response = new();
    var repo = new Repository(Environment.CurrentDirectory);
    
    response.AppendLine($"Current branch: {repo.Head.CanonicalName}");

    var status = repo.RetrieveStatus(new StatusOptions() {
        IncludeUntracked = true,
        DetectRenamesInIndex = true,
        DetectRenamesInWorkDir = true,
        Show = StatusShowOption.IndexAndWorkDir
    });

    if(status.Staged?.ToList() is  { Count: > 0 } entries)
    {
        response.AppendLine("Staged for commit:");
        foreach(var item in entries)
        {
            response.AppendLine($" - {item.FilePath}");
        }
    }

    if(status.Modified?.ToList() is  { Count: > 0 } altered)
    {
        response.AppendLine("Modified:");
        foreach(var item in altered)
        {
            response.AppendLine($" - {item.FilePath}");
        }
    }

    if(status.Missing?.ToList() is { Count: > 0 } deleted)
    {
        response.AppendLine("Deleted:");
        foreach(var item in deleted)
        {
            response.AppendLine($" - {item.FilePath}");
        }
    }

    if(status.Untracked?.ToList() is { Count: > 0 } untracked)
    {
        response.AppendLine($"Untracked:");
        foreach(var item in untracked)
        {
            response.AppendLine($" - {item.FilePath}");
        }
    }

    return response.ToString();
}

string RestartSession()
{
    sessionRestarted = true;
    try
    {
        File.Delete(chatLogFilename);
    }
    catch(DirectoryNotFoundException)
    {
        // Don't care.
    }
    return "The session was restarted.";
}

void AddUserMessage(string message)
{
    chatMessages.Add(ChatMessage.FromUser($"[{Now():yyyy-MM-ddTHH:mm:ss}]: {message}"));
}

List<ChatMessage> StartNewChat()
{
    string prompt = $"""
        You are a user interface tool for Git called CapGit-{Environment.MachineName}. You can do a small amount of tasks related to Git.
        The user is named {Environment.UserName}, and the current date is {Now():yyyy-MM-dd}, the time is {Now():HH:mm}. The messages from the user will start with the date and time in ISO 8601 format for when the message was sent.
        """;

    return [ChatMessage.FromSystem(prompt)];
}