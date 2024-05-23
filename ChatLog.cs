using OpenAI.ObjectModels.RequestModels;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Text.Json;

namespace gitgpt;

public sealed record ChatLog(ImmutableList<ChatMessage> ChatMessages)
{
    private static readonly JsonSerializerOptions defaultSerializerOptions = new () { WriteIndented = true };

    [Pure]
    public ChatLog AddUserMessage(string message, TimeProvider timeProvider)
    {
        var now = timeProvider.GetLocalNow();
        return new ChatLog(ChatMessages.Add(ChatMessage.FromUser($"[{now:yyyy-MM-ddTHH:mm:ss}]: {message}")));
    }

    [Pure]
    public ChatLog Add(ChatMessage message)
    {
        return new ChatLog(ChatMessages.Add(message));
    }

    public static void Delete(string filename)
    {
        try
        {
            File.Delete(filename);
        }
        catch (DirectoryNotFoundException)
        {
            // Don't care.
        }
    }

    [Pure]
    public static async Task<ChatLog> ReadChatLogAsync(string filename, TimeProvider timeProvider)
    {
        // We unconditionally create the directory. This function is no-op if the directory already exists.
        Directory.CreateDirectory(Path.GetDirectoryName(filename) ?? throw new InvalidOperationException());
        using var fs = File.Open(filename, FileMode.OpenOrCreate);
        if (fs.Length == 0)
        {
            return new ChatLog(StartNewChat(timeProvider));
        }
        return new ChatLog(await JsonSerializer.DeserializeAsync<ImmutableList<ChatMessage>>(fs) ?? []);
    }

    public async Task SaveChatLogAsync(string filename)
    {
        using var fs = File.Open(filename, FileMode.Create);
        await JsonSerializer.SerializeAsync(fs, ChatMessages, defaultSerializerOptions);
    }

    [Pure]
    private static ImmutableList<ChatMessage> StartNewChat(TimeProvider timeProvider)
    {
        var now = timeProvider.GetLocalNow();
        string prompt = $"""
        You are a user interface tool for Git called CapGit-{Environment.MachineName}. You can do a small amount of tasks related to Git.
        The user is named {Environment.UserName}, and the current date is {now:yyyy-MM-dd}, the time is {now:HH:mm}. The messages from the user will start with the date and time in ISO 8601 format for when the message was sent.
        """;

        return [ChatMessage.FromSystem(prompt)];
    }
}
