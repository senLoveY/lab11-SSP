using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaClient;

internal sealed class OllamaApiClient(HttpClient http)
{
    private const string EmptyAnswer = "Ollama завершил генерацию без текста. Попробуйте уточнить запрос.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string[]> ListModelsAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("api/tags", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ModelList>(cancellationToken);
        return result?.Models?.Select(model => model.Name).ToArray()
            ?? throw new IOException("Ollama не вернул список моделей.");
    }

    public async Task<string> GenerateAsync(
        AgentConfig config, string prompt, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = config.Model,
            prompt,
            system = config.SystemPrompt,
            stream = false,
            options = config.Options
        };

        using var response = await http.PostAsJsonAsync("api/generate", payload, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken);
        if (result is null)
            throw new IOException("Ollama вернул пустой JSON-ответ.");

        CheckForError(result);
        return string.IsNullOrWhiteSpace(result.Response)
            ? throw new IOException(EmptyAnswer)
            : result.Response;
    }

    public IAsyncEnumerable<string> GenerateStreamAsync(
        AgentConfig config, string prompt, CancellationToken cancellationToken) =>
        ReadStreamAsync("api/generate", new
        {
            model = config.Model,
            prompt,
            system = config.SystemPrompt,
            stream = true,
            options = config.Options
        }, cancellationToken);

    public IAsyncEnumerable<string> ChatStreamAsync(
        AgentConfig config, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken) =>
        ReadStreamAsync("api/chat", new
        {
            model = config.Model,
            messages,
            stream = true,
            options = config.Options
        }, cancellationToken);

    private async IAsyncEnumerable<string> ReadStreamAsync<T>(
        string path, T payload, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(http.Timeout);
        var token = deadline.Token;

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        // Read headers first so the console can display each NDJSON fragment as it arrives.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await EnsureSuccessAsync(response, token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        var hasText = false;

        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var chunk = JsonSerializer.Deserialize<OllamaResponse>(line, JsonOptions)
                ?? throw new IOException("Ollama вернул пустую строку NDJSON.");
            CheckForError(chunk);

            var text = chunk.Message?.Content ?? chunk.Response;
            if (!string.IsNullOrEmpty(text))
            {
                hasText |= !string.IsNullOrWhiteSpace(text);
                yield return text;
            }
            if (chunk.Done)
            {
                if (!hasText)
                    throw new IOException(EmptyAnswer);
                yield break;
            }
        }

        throw new IOException("Поток Ollama оборвался до сообщения done.");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            body = JsonSerializer.Deserialize<OllamaResponse>(body, JsonOptions)?.Error ?? body;
        }
        catch (JsonException)
        {
            // A proxy or another service can return an error page instead of Ollama JSON.
        }

        if (body.Length > 400)
            body = body[..400] + "…";
        throw new HttpRequestException($"Ollama вернул HTTP {(int)response.StatusCode}. {body}",
            null, response.StatusCode);
    }

    private static void CheckForError(OllamaResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Error))
            throw new IOException($"Ошибка Ollama. {response.Error}");
    }

    private sealed record ModelList(ModelInfo[]? Models);
    private sealed record ModelInfo(string Name);
    private sealed record OllamaResponse(string? Response, ChatMessage? Message, bool Done, string? Error);
}

internal sealed record ChatMessage(string Role, string Content);

internal sealed record GenerationOptions(
    double Temperature,
    [property: JsonPropertyName("num_predict")] int MaxTokens,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Stop);
