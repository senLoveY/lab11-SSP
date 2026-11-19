using System.Runtime.CompilerServices;
using System.Text;

namespace OllamaClient;

internal sealed record AgentConfig(string Model, string SystemPrompt)
{
    public GenerationOptions Options { get; } = new(Temperature: 0.2, MaxTokens: 512);
}

internal static class AgentPrompts
{
    public const string Developer = """
        Ты помощник по программированию на C#. Давай компактные примеры на C# и кратко
        объясняй решение. Учитывай предыдущие реплики. Отвечай на русском языке.
        """;

    public const string Reviewer = """
        Ты проводишь ревью кода C#. Найди конкретные логические ошибки, проблемы с
        граничными случаями и производительностью. Объясни последствия и предложи
        исправленный код. Отвечай кратко на русском языке.
        """;
}

internal sealed class CodeAssistantAgent(OllamaApiClient api, AgentConfig config)
{
    private const int MaxTurns = 3;
    private readonly List<ChatMessage> history = [];

    public void Reset() => history.Clear();

    public async IAsyncEnumerable<string> ReplyAsync(
        string input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var user = new ChatMessage("user", input);
        ChatMessage[] messages = [new("system", config.SystemPrompt), ..history, user];
        var answer = new StringBuilder();

        await foreach (var chunk in api.ChatStreamAsync(config, messages, cancellationToken))
        {
            answer.Append(chunk);
            yield return chunk;
        }

        // Save a complete exchange only after Ollama confirms the end of the stream.
        history.Add(user);
        history.Add(new ChatMessage("assistant", answer.ToString()));
        if (history.Count > MaxTurns * 2)
            history.RemoveRange(0, 2);
    }
}

internal sealed class CodeReviewAgent(OllamaApiClient api, AgentConfig config)
{
    public IAsyncEnumerable<string> ReviewAsync(string code, CancellationToken cancellationToken) =>
        api.GenerateStreamAsync(config, $"Проведи ревью следующего кода C#.\n\n{code}", cancellationToken);
}
