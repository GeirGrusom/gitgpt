using LibGit2Sharp;
using OpenAI;
using OpenAI.Builders;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.ObjectModels.SharedModels;
using System.Text;
using System.Text.Json;

namespace gitgpt;

public sealed class ChatCompletion(string apiKey)
{
    private readonly OpenAI.Managers.OpenAIService service = new(new OpenAiOptions { ApiKey = apiKey, DefaultModelId = Models.Gpt_4o });

    public bool ResetRequested { get; private set; }

    private static ToolDefinition DefineRestartTool() => DefineFunctionCall(nameof(RestartSession), "Restarts the chat session.");
    private static ToolDefinition DefineGitStatusTool() => DefineFunctionCall(nameof(GitStatus), "Get the current status of git in the current directory.");
    private static ToolDefinition DefineGitStageTool() => DefineFunctionCall(nameof(GitStage), "Stages files for commit.", b => b.AddParameter("files", PropertyDefinition.DefineArray(PropertyDefinition.DefineString("Name of file to stage. This can use a glob syntax."))));
    private static ToolDefinition DefineGitUnstageTool() => DefineFunctionCall(nameof(GitUnstage), "Unstages files.", b => b.AddParameter("files", PropertyDefinition.DefineArray(PropertyDefinition.DefineString("Name of file to unstage. This can use a glob syntax."))));
    private static ToolDefinition DefineGitCommitTool() => DefineFunctionCall(nameof(GitCommit), "Commits staged changes with the specified commit message.", b => b.AddParameter("commitMessage", PropertyDefinition.DefineString("The commit message to use.")));

    private static ToolDefinition DefineFunctionCall(string name, string? description, Action<FunctionDefinitionBuilder>? action = null)
    {
        var builder = new FunctionDefinitionBuilder(name, description);
        action?.Invoke(builder);
        return ToolDefinition.DefineFunction(builder.Build());
    }

    public async Task DoCompletion(ChatLog chatLog, CancellationToken cancellationToken)
    {
        var request = new ChatCompletionCreateRequest
        {
            Messages = chatLog.ChatMessages,
            Temperature = 0.8f, // Temperature decides "creativity" of output. Temperature of 0 makes output deterministic.
            Tools = [DefineRestartTool(), DefineGitStatusTool(), DefineGitStageTool(), DefineGitUnstageTool(), DefineGitCommitTool()]
        };

        var completion = await service.ChatCompletion.CreateCompletion(request, cancellationToken: cancellationToken);

        // If there were no choices from ChatGPT then nothing to do.
        if (completion is not { Choices.Count: > 0 })
        {
            return;
        }

        // We just pick the first one.
        var choice = completion.Choices[0];

        chatLog.Add(ChatMessage.FromAssistant(choice.Message.Content ?? "", toolCalls: choice.Message.ToolCalls));

        if (choice.Message is { Content: { Length: > 0 } message })
        {
            Console.WriteLine(message);
        }
        if (choice.Message.ToolCalls is { Count: > 0 } tools)
        {
            foreach (var tool in tools)
            {
                var toolCallResponse = ProcessTool(tool);
                chatLog.Add(ChatMessage.FromTool(toolCallResponse, tool.Id!));
            }

            // The agent might not be finished, and it might not have produced a message, so we'll run it back to ChatGPT to see if it wants to do more, or write something.
            await DoCompletion(chatLog, cancellationToken);
        }
    }

    private string ProcessTool(ToolCall tool)
    {
        return tool switch
        {
            { FunctionCall.Name: nameof(RestartSession) } => RestartSession(),
            { FunctionCall.Name: nameof(GitStatus) } => GitStatus(),
            { FunctionCall.Name: nameof(GitStage) } => GitStage([.. ((JsonElement)tool.FunctionCall.ParseArguments()["files"]).EnumerateArray().Select(x => x!.ToString())]),
            { FunctionCall.Name: nameof(GitUnstage) } => GitUnstage([.. ((JsonElement)tool.FunctionCall.ParseArguments()["files"]).EnumerateArray().Select(x => x!.ToString())]),
            { FunctionCall.Name: nameof(GitCommit) } => GitCommit(tool.FunctionCall.ParseArguments()["commitMessage"].ToString()!),
            _ => $"{tool.FunctionCall!.Name} could not be found."
        };
    }

    private static string GitCommit(string commitMessage)
    {
        var repo = new Repository(Environment.CurrentDirectory);
        var signature = repo.Config.BuildSignature(DateTimeOffset.UtcNow);
        var commit = repo.Commit(commitMessage, signature, signature, new CommitOptions() { AllowEmptyCommit = false });
        return $"Commit {commit.Sha} created for author {commit.Author.Name}.";
    }

    private static string GitStage(string[] files)
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
        foreach (var item in status.Staged)
        {
            result.AppendLine($"- {item.FilePath}");
        }

        return result.ToString();
    }

    private static string GitUnstage(string[] files)
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

    private static string GitStatus()
    {
        StringBuilder response = new();
        var repo = new Repository(Environment.CurrentDirectory);

        response.AppendLine($"Current branch: {repo.Head.CanonicalName}");

        var status = repo.RetrieveStatus(new StatusOptions()
        {
            IncludeUntracked = true,
            DetectRenamesInIndex = true,
            DetectRenamesInWorkDir = true,
            Show = StatusShowOption.IndexAndWorkDir
        });

        if (status.Staged?.ToList() is { Count: > 0 } entries)
        {
            response.AppendLine("Staged for commit:");
            foreach (var item in entries)
            {
                response.AppendLine($" - {item.FilePath}");
            }
        }

        if (status.Modified?.ToList() is { Count: > 0 } altered)
        {
            response.AppendLine("Modified:");
            foreach (var item in altered)
            {
                response.AppendLine($" - {item.FilePath}");
            }
        }

        if (status.Missing?.ToList() is { Count: > 0 } deleted)
        {
            response.AppendLine("Deleted:");
            foreach (var item in deleted)
            {
                response.AppendLine($" - {item.FilePath}");
            }
        }

        if (status.Untracked?.ToList() is { Count: > 0 } untracked)
        {
            response.AppendLine($"Untracked:");
            foreach (var item in untracked)
            {
                response.AppendLine($" - {item.FilePath}");
            }
        }

        return response.ToString();
    }

    private string RestartSession()
    {
        this.ResetRequested = true;
        return "The session was restarted.";
    }
}
