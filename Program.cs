using OpenAI;
using LibGit2Sharp;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.Builders;
using OpenAI.ObjectModels.SharedModels;

CancellationTokenSource cancel = new();
Console.CancelKeyPress += (_, _) => cancel.Cancel();

static DateTimeOffset Now() => TimeProvider.System.GetLocalNow();

var service = new OpenAI.Managers.OpenAIService(new OpenAiOptions {  ApiKey = "apikey here", DefaultModelId = Models.Gpt_4o });

string chatLogFilename = Path.Combine($"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}", "CapGpt", "chatlog.json");
List<ChatMessage> chatMessages = await ReadChatLog();
var cmd = Environment.CommandLine;

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
    return await System.Text.Json.JsonSerializer.DeserializeAsync<List<ChatMessage>>(fs) ?? [];
}

async Task SaveChatLog(List<ChatMessage> messages)
{
    if(sessionRestarted)
    {
        // The assistant decided to restart the session. No point in saving anything.
        return;
    }
    using var fs = File.Open(chatLogFilename, FileMode.Create);
    await System.Text.Json.JsonSerializer.SerializeAsync(fs, messages);
}

ToolDefinition DefineRestartTool()
{
    var fun = new FunctionDefinitionBuilder(nameof(RestartSession), "Restarts the chat session.");
    return ToolDefinition.DefineFunction(fun.Build());
}

async Task DoCompletion()
{
    var completion = await service.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest() { Messages = chatMessages, Temperature = 0.8f, Tools = [DefineRestartTool()] }, cancellationToken: cancel.Token);

    if(completion is not { Choices.Count : > 0})
    {
        return;
    }

    var choice = completion.Choices[0];

    if(choice.Message is {Content: { Length: > 0} message })
    {
        chatMessages.Add(ChatMessage.FromAssistant(message));
        Console.WriteLine(message);
    }
    if(choice.Message.ToolCalls is { Count: > 0} tools)
    {
        foreach(var tool in tools)
        {
            var toolCallResponse = ProcessTool(tool);
            chatMessages.Add(ChatMessage.FromTool(toolCallResponse, tool.Id!));
            Console.WriteLine($"{tool.FunctionCall!.Name}: {toolCallResponse}");
        }

        await DoCompletion();
    }
}

string ProcessTool(ToolCall tool)
{
    if(tool is { FunctionCall.Name: nameof(RestartSession) })
    {
        return RestartSession();
    }
    return "(Tool could not be found)";
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