using OpenAI;
using LibGit2Sharp;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.Builders;
using System.Text;
using System.Text.Json;
using OpenAI.ObjectModels.SharedModels;

string chatLogFilename = Path.Combine($"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}", "CapGpt", "chatlog.json");
var cmdLine = Environment.GetCommandLineArgs()[1..];

if(cmdLine is ["--reset"])
{
    Console.WriteLine(RestartSession());
    return;
}
if(cmdLine is ["--help"])
{
    Console.WriteLine("""
    Usage: gitgpt <options> [message]
    Options:
      --help    Shows this message
      --reset   Resets the chat session. Use this if ChatGPT refuses to respond, or execute commands.
    """);
    return;
}

CancellationTokenSource cancel = new();
Console.CancelKeyPress += (_, _) => cancel.Cancel();

static DateTimeOffset Now() => TimeProvider.System.GetLocalNow();

var service = new OpenAI.Managers.OpenAIService(new OpenAiOptions {  ApiKey = "apikey here", DefaultModelId = Models.Gpt_4o });

List<ChatMessage> chatMessages = await ReadChatLog();
var cmd = string.Join(" ", cmdLine);

bool sessionRestarted = false;

if(cmd is not {Length: > 0})
{
    return;
}

AddUserMessage(cmd);
await DoCompletion();
await SaveChatLog(chatMessages);

async Task<List<ChatMessage>> ReadChatLog()
{
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
        // The assistant decided to restart the session. No point in saving anything.
        return;
    }
    using var fs = File.Open(chatLogFilename, FileMode.Create);
    await JsonSerializer.SerializeAsync(fs, messages, new JsonSerializerOptions() { WriteIndented = true });
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

ToolDefinition DefineGitCommitTool() => DefineFunctionCall(nameof(GitCommit), "Commits staged changes with the specified commit message.", b =>
     b.AddParameter("commitMessage", PropertyDefinition.DefineString("The commit message to use.")));

ToolDefinition DefineFunctionCall(string name, string? description, Action<FunctionDefinitionBuilder> action)
{
    var builder  = new FunctionDefinitionBuilder(name, description);
    action(builder);
    return ToolDefinition.DefineFunction(builder.Build());
}

async Task DoCompletion()
{
    var completion = await service.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest() { Messages = chatMessages, Temperature = 0.8f, Tools = [DefineRestartTool(), DefineGitStatusTool(), DefineGitStageTool(), DefineGitCommitTool()] }, cancellationToken: cancel.Token);

    if(completion is not { Choices.Count : > 0})
    {
        return;
    }

    var choice = completion.Choices[0];

    chatMessages.Add(ChatMessage.FromAssistant(choice.Message?.Content ?? "", toolCalls: choice.Message!.ToolCalls));

    if(choice.Message is {Content: { Length: > 0} message })
    {
        chatMessages.Add(ChatMessage.FromAssistant(message, toolCalls: choice.Message.ToolCalls));
        Console.WriteLine(message);
    }
    if(choice.Message.ToolCalls is { Count: > 0} tools)
    {
        foreach(var tool in tools)
        {
            var toolCallResponse = ProcessTool(tool);
            chatMessages.Add(ChatMessage.FromTool(toolCallResponse, tool.Id!));
        }

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

    if(status.Missing?.ToList() is {Count: > 0 } deleted)
    {
        response.AppendLine("Deleted:");
        foreach(var item in deleted)
        {
            response.AppendLine($" - {item.FilePath}");
        }
    }

    if(status.Untracked?.ToList() is { Count: > 0} untracked)
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
    catch(FileNotFoundException)
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
        The user is named {Environment.UserName}, and the current date is {Now():yyyy-MM-dd}, the time is {Now():HH:mm}. The messages from the user will start with the current date and time in ISO 8601 format.
        """;

    return [ChatMessage.FromSystem(prompt)];
}