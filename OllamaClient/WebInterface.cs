using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace OllamaClient;

internal static class WebInterface
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(OllamaApiClient api, Uri baseUrl)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = ["--hostBuilder:reloadConfigOnChange=false"],
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = "wwwroot"
        });
        builder.Configuration["AllowedHosts"] = "127.0.0.1;localhost";
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 5080));

        await using var app = builder.Build();
        MapRoutes(app, api, baseUrl);

        try
        {
            await app.RunAsync();
            return 0;
        }
        catch (Exception error) when (error is IOException or SocketException)
        {
            Console.Error.WriteLine($"Не удалось запустить интерфейс на 127.0.0.1:5080. {error.Message}");
            return 1;
        }
    }

    internal static void MapRoutes(WebApplication app, OllamaApiClient api, Uri baseUrl)
    {
        app.UseFileServer();
        app.MapGet("/lab/models", async (CancellationToken token) =>
        {
            try
            {
                return Results.Ok(new
                {
                    models = await api.ListModelsAsync(token),
                    developer = AgentPrompts.DeveloperModel,
                    reviewer = AgentPrompts.ReviewerModel,
                    address = baseUrl.GetLeftPart(UriPartial.Authority)
                });
            }
            catch (Exception error) when (!token.IsCancellationRequested && IsOllamaError(error))
            {
                return Results.Json(new
                {
                    error = error is HttpRequestException { StatusCode: null }
                        ? "Ollama недоступен. Запусти ollama serve и обнови список моделей."
                        : Describe(error),
                    address = baseUrl.GetLeftPart(UriPartial.Authority)
                }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
        app.MapPost("/lab/run", (HttpContext context, RunRequest input) => RunModeAsync(context, api, input));
    }

    private static async Task RunModeAsync(HttpContext context, OllamaApiClient api, RunRequest input)
    {
        var token = context.RequestAborted;
        var response = context.Response;
        if (input.Mode is not ("generate" or "chat" or "review" or "pipeline")
            || string.IsNullOrWhiteSpace(input.Prompt) || string.IsNullOrWhiteSpace(input.Model)
            || (input.Mode == "pipeline" && string.IsNullOrWhiteSpace(input.ReviewModel)))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsJsonAsync(new { error = "Заполни запрос и выбери модель." }, token);
            return;
        }

        response.ContentType = "application/x-ndjson; charset=utf-8";
        response.Headers.CacheControl = "no-store";

        async Task WriteAsync(WebEvent message)
        {
            await response.WriteAsync(JsonSerializer.Serialize(message, JsonOptions) + "\n", token);
            await response.Body.FlushAsync(token);
        }

        async Task WriteStreamAsync(IAsyncEnumerable<string> stream, string stage = "answer")
        {
            await foreach (var chunk in stream.WithCancellation(token))
                await WriteAsync(new WebEvent("chunk", chunk, stage));
        }

        var developer = new AgentConfig(input.Model, AgentPrompts.Generation);
        var reviewerModel = input.Mode == "pipeline" ? input.ReviewModel! : input.Model;
        var reviewer = new AgentConfig(reviewerModel, AgentPrompts.Reviewer);

        try
        {
            switch (input.Mode)
            {
                case "generate" when !input.Stream:
                    var answer = await api.GenerateAsync(developer, input.Prompt, token);
                    await WriteAsync(new WebEvent("chunk", answer, "answer"));
                    break;
                case "generate":
                    await WriteStreamAsync(api.GenerateStreamAsync(developer, input.Prompt, token));
                    break;
                case "chat":
                    var history = input.History?.TakeLast(3)
                        .Where(turn => !string.IsNullOrWhiteSpace(turn?.User) && !string.IsNullOrWhiteSpace(turn?.Answer))
                        .SelectMany(turn => new[] { new ChatMessage("user", turn!.User), new ChatMessage("assistant", turn.Answer) });
                    var chat = new CodeAssistantAgent(api, new AgentConfig(input.Model, AgentPrompts.Conversation), history);
                    await WriteStreamAsync(chat.ReplyAsync(input.Prompt, token));
                    break;
                case "review":
                    await WriteStreamAsync(new CodeReviewAgent(api, reviewer).ReviewAsync(input.Prompt, token));
                    break;
                case "pipeline":
                    var assistant = new CodeAssistantAgent(api, developer);
                    var review = new CodeReviewAgent(api, reviewer);
                    await foreach (var chunk in CodePipeline.RunAsync(assistant, review, input.Prompt, token))
                        await WriteAsync(new WebEvent("chunk", chunk.Text, chunk.IsReview ? "review" : "draft"));
                    break;
            }
            await WriteAsync(new WebEvent("done"));
        }
        catch (Exception error) when (token.IsCancellationRequested && error is OperationCanceledException or IOException)
        {
            // The browser closed the connection or stopped generation.
        }
        catch (Exception error) when (IsOllamaError(error))
        {
            await WriteAsync(new WebEvent("error", Describe(error)));
        }
    }

    private static bool IsOllamaError(Exception error) =>
        error is HttpRequestException or JsonException or IOException or OperationCanceledException;

    private static string Describe(Exception error) => error switch
    {
        OperationCanceledException => "Превышено время ожидания ответа Ollama.",
        JsonException => "Ollama вернул некорректный JSON.",
        _ => error.Message
    };

    private sealed record RunRequest(
        string? Mode, string? Prompt, string? Model, string? ReviewModel, bool Stream, ChatTurn?[]? History);

    private sealed record ChatTurn(string User, string Answer);
    private sealed record WebEvent(string Type, string? Text = null, string? Stage = null);
}
