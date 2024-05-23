using OpenAI.ObjectModels.RequestModels;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace gitgpt;

public sealed class ChatLog(string filename, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions defaultSerializerOptions = new () { WriteIndented = true };

    private ImmutableList<ChatMessage> chatMessages = [];

    public ImmutableList<ChatMessage> ChatMessages => chatMessages;

    public void AddUserMessage(string message)
    {
        var now = timeProvider.GetLocalNow();
        chatMessages = chatMessages.Add(ChatMessage.FromUser($"[{now:yyyy-MM-ddTHH:mm:ss}]: {message}"));
    }

    public void Add(ChatMessage message)
    {
        chatMessages = chatMessages.Add(message);
    }

    public void Delete()
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

    public async Task ReadChatLog()
    {
        // We unconditionally create the directory. This function is no-op if the directory already exists.
        Directory.CreateDirectory(Path.GetDirectoryName(filename) ?? throw new InvalidOperationException());
        using var fs = File.Open(filename, FileMode.OpenOrCreate);
        if (fs.Length == 0)
        {
            chatMessages = StartNewChat(timeProvider);
        }
        chatMessages = await JsonSerializer.DeserializeAsync<ImmutableList<ChatMessage>>(fs) ?? [];
    }

    public async Task SaveChatLog()
    {
        using var fs = File.Open(filename, FileMode.Create);
        await JsonSerializer.SerializeAsync(fs, chatMessages, defaultSerializerOptions);
    }

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
