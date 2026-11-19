using System.Text;
using System.Text.Json;
using static OllamaClient.AgentPrompts;

namespace OllamaClient;

internal static class Program
{
    private const string DefaultUrl = "http://127.0.0.1:11434";
    private const string ReviewExample = """
        public static int CalculateAverage(int[] numbers)
        {
            int sum = 0;
            for (int i = 0; i <= numbers.Length; i++)
                sum += numbers[i];
            return sum / numbers.Length;
        }
        """;

    private static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        if (args is ["--help"])
        {
            Console.WriteLine("Запустите без аргументов для открытия меню или с --models для списка моделей.");
            Console.WriteLine("--web запускает интерфейс по адресу http://127.0.0.1:5080.");
            Console.WriteLine($"OLLAMA_BASE_URL задаёт адрес сервера. По умолчанию {DefaultUrl}.");
            return 0;
        }
        if (args.Length > 0 && args is not ["--models"] && args is not ["--web"])
        {
            Console.Error.WriteLine("Неизвестные аргументы. Используйте --help.");
            return 2;
        }

        var address = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? DefaultUrl;
        if (!Uri.TryCreate(address.TrimEnd('/') + "/", UriKind.Absolute, out var baseUrl)
            || baseUrl.Scheme is not ("http" or "https")
            || baseUrl.Query.Length > 0 || baseUrl.Fragment.Length > 0)
        {
            Console.Error.WriteLine("Задайте адрес HTTP(S) для OLLAMA_BASE_URL, например " + DefaultUrl);
            return 2;
        }

        using var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseProxy = false
        })
        {
            BaseAddress = baseUrl,
            Timeout = TimeSpan.FromMinutes(5)
        };
        var api = new OllamaApiClient(http);
        if (args is ["--web"])
            return await WebInterface.RunAsync(api, baseUrl);

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var models = await api.ListModelsAsync(cancellation.Token);
            Console.WriteLine($"Ollama · {baseUrl}");
            PrintModels(models);
            if (args.Length == 0)
                await RunMenuAsync(api, models, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("\nРабота прервана.");
            return 130;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException
            or IOException or OperationCanceledException)
        {
            PrintError(error);
            if (error is HttpRequestException { StatusCode: null })
                Console.Error.WriteLine($"Проверьте, что Ollama запущен и доступен по адресу {baseUrl}.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static async Task RunMenuAsync(
        OllamaApiClient api, string[] models, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("""

                1. Обновить список моделей
                2. Обычная генерация кода
                3. Потоковая генерация кода
                4. Чат с помощником C#
                5. Потоковое ревью кода
                6. Конвейер генерации и ревью
                0. Выход
                """);
            Console.Write("Выберите действие: ");
            var choice = Console.ReadLine()?.Trim();
            if (choice is null or "0")
                return;

            try
            {
                if (choice == "1")
                {
                    models = await api.ListModelsAsync(cancellationToken);
                    PrintModels(models);
                }
                else if (choice is "2" or "3" or "4" or "5" or "6")
                {
                    if (models.Length == 0)
                    {
                        Console.WriteLine("Загрузите модель через ollama pull и обновите список.");
                        continue;
                    }
                    await RunModeAsync(api, models, choice, cancellationToken);
                }
                else
                {
                    Console.WriteLine("Выберите один из пунктов меню.");
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested
                && error is HttpRequestException or JsonException or IOException or OperationCanceledException)
            {
                PrintError(error);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task RunModeAsync(
        OllamaApiClient api, string[] models, string choice, CancellationToken cancellationToken)
    {
        var developer = new AgentConfig(ChooseModel(models, DeveloperModel), AgentPrompts.Developer);
        var reviewer = new AgentConfig(ChooseModel(models, ReviewerModel), AgentPrompts.Reviewer);

        switch (choice)
        {
            case "2":
            case "3":
                Console.WriteLine($"Модель · {developer.Model}");
                var prompt = ReadPrompt("Напиши метод C# для поиска максимального элемента в массиве целых чисел.");
                if (prompt is null)
                    return;

                Console.WriteLine(choice == "2" ? "Ожидаем полный ответ…" : "Потоковый ответ");
                if (choice == "2")
                    Console.WriteLine(await api.GenerateAsync(developer, prompt, cancellationToken));
                else
                    await PrintStreamAsync(api.GenerateStreamAsync(developer, prompt, cancellationToken));
                break;

            case "4":
                await RunChatAsync(api, developer, cancellationToken);
                break;

            case "5":
                Console.WriteLine($"Ревью · {reviewer.Model}");
                var code = ReadCode();
                if (code is not null)
                {
                    var agent = new CodeReviewAgent(api, reviewer);
                    Console.WriteLine("Результат ревью");
                    await PrintStreamAsync(agent.ReviewAsync(code, cancellationToken));
                }
                break;

            case "6":
                var task = ReadPrompt("Напиши рекурсивный метод C# для вычисления n-го числа Фибоначчи.");
                if (task is null)
                    return;

                var assistant = new CodeAssistantAgent(api, developer);
                var reviewAgent = new CodeReviewAgent(api, reviewer);
                Console.WriteLine($"Шаг 1 · Генерация · {developer.Model}");
                var reviewStarted = false;
                await foreach (var chunk in CodePipeline.RunAsync(assistant, reviewAgent, task, cancellationToken))
                {
                    if (chunk.IsReview && !reviewStarted)
                    {
                        Console.WriteLine($"\n\nШаг 2 · Ревью · {reviewer.Model}");
                        reviewStarted = true;
                    }
                    Console.Write(chunk.Text);
                }
                Console.WriteLine();
                break;
        }
    }

    private static async Task RunChatAsync(
        OllamaApiClient api, AgentConfig config, CancellationToken cancellationToken)
    {
        var assistant = new CodeAssistantAgent(api, config);
        Console.WriteLine($"Чат · {config.Model} · /reset очищает историю, /back возвращает в меню.");

        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("Вы: ");
            var input = Console.ReadLine()?.Trim();
            switch (input)
            {
                case null:
                case "/back":
                    return;
                case "/reset":
                    assistant.Reset();
                    Console.WriteLine("История очищена.");
                    continue;
                case "":
                    continue;
            }

            Console.WriteLine("Ассистент");
            try
            {
                await PrintStreamAsync(assistant.ReplyAsync(input, cancellationToken));
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested
                && error is HttpRequestException or JsonException or IOException or OperationCanceledException)
            {
                PrintError(error);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string ChooseModel(string[] models, string preferred) =>
        models.Contains(preferred, StringComparer.OrdinalIgnoreCase)
            ? preferred
            : models.FirstOrDefault(model => model is DeveloperModel or ReviewerModel) ?? models[0];

    private static string? ReadPrompt(string example)
    {
        Console.WriteLine($"Пример · {example}");
        Console.Write("Запрос (Enter для примера): ");
        var input = Console.ReadLine()?.Trim();
        if (input == "")
        {
            Console.WriteLine($"Запрос · {example}");
            return example;
        }
        return input;
    }

    private static string? ReadCode()
    {
        Console.WriteLine("Вставьте код. Строка с точкой завершает ввод. Точка первой строкой берёт пример.");
        var code = new StringBuilder();
        while (Console.ReadLine() is { } line)
        {
            if (line.Trim() != ".")
            {
                code.AppendLine(line);
                continue;
            }
            if (string.IsNullOrWhiteSpace(code.ToString()))
            {
                Console.WriteLine(ReviewExample);
                return ReviewExample;
            }
            return code.ToString();
        }
        return null;
    }

    private static async Task PrintStreamAsync(IAsyncEnumerable<string> stream)
    {
        try
        {
            await foreach (var chunk in stream)
                Console.Write(chunk);
        }
        finally
        {
            Console.WriteLine();
        }
    }

    private static void PrintModels(string[] models)
    {
        Console.WriteLine(models.Length == 0
            ? "Модели отсутствуют."
            : $"Модели · {string.Join(", ", models)}");
        foreach (var model in new[] { DeveloperModel, ReviewerModel })
        {
            if (!models.Contains(model, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"Для комплекта из методички · ollama pull {model}");
        }
    }

    private static void PrintError(Exception error) => Console.Error.WriteLine(error switch
    {
        OperationCanceledException => "Превышено время ожидания ответа Ollama.",
        JsonException => $"Ollama вернул некорректный JSON. {error.Message}",
        _ => $"Ошибка. {error.Message}"
    });
}
