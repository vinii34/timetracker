using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TimeTracker.Core.Services.Ai;

/// <summary>Plomberie commune : sérialisation, envoi, lecture d'une erreur lisible.</summary>
internal static class AiHttp
{
    public static async Task<JsonDocument> PostAsync(HttpRequestMessage request, object body,
                                                     CancellationToken cancellation)
    {
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await AiProviderFactory.Http.SendAsync(request, cancellation);
        var text = await response.Content.ReadAsStringAsync(cancellation);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} — {ErrorMessage(text)}",
                                           null, response.StatusCode);
        return JsonDocument.Parse(text);
    }

    /// <summary>
    /// Message d'erreur sur une ligne. Les trois fournisseurs répondent
    /// <c>{"error": {"message": "…"}}</c> ; le corps entier, JSON indenté compris, prenait sept
    /// lignes du journal pour un 503 « high demand » (2026-09-29).
    /// </summary>
    internal static string ErrorMessage(string body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error))
                message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)
                    ? m.GetString()
                    : error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        }
        catch (JsonException) { /* corps non JSON : on garde le texte brut */ }

        var oneLine = string.Join(' ', (message ?? body).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length > 200 ? oneLine[..200] + "…" : oneLine;
    }

    /// <summary>Surcharge passagère (429, 500, 502, 503, 504) : un nouvel essai a du sens.</summary>
    public static bool IsTransient(Exception ex) =>
        ex is HttpRequestException { StatusCode: { } code }
        && (int)code is 429 or 500 or 502 or 503 or 504;
}

/// <summary>API Gemini (AI Studio) : <c>models/{model}:generateContent</c>.</summary>
public sealed class GeminiProvider : IAiProvider
{
    private readonly string _apiKey;
    private readonly string _model;

    public GeminiProvider(string apiKey, string model) { _apiKey = apiKey; _model = model; }

    public string Name => $"Gemini / {_model}";

    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellation)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent");
        request.Headers.Add("x-goog-api-key", _apiKey);
        using var doc = await AiHttp.PostAsync(request, new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0.2 }
        }, cancellation);

        return doc.RootElement.GetProperty("candidates")[0]
                  .GetProperty("content").GetProperty("parts")[0]
                  .GetProperty("text").GetString() ?? "";
    }
}

/// <summary>
/// Protocole OpenAI <c>chat/completions</c> : OpenAI, mais aussi Mistral, Groq, OpenRouter,
/// et les serveurs <b>locaux</b> Ollama / LM Studio — la seule façon d'avoir un assistant sans
/// que rien ne sorte du poste.
/// </summary>
public sealed class OpenAiCompatibleProvider : IAiProvider
{
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;

    public OpenAiCompatibleProvider(string baseUrl, string apiKey, string model)
    {
        _baseUrl = baseUrl; _apiKey = apiKey; _model = model;
    }

    public string Name => $"OpenAI-compatible ({_baseUrl}) / {_model}";

    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellation)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
        if (_apiKey.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var doc = await AiHttp.PostAsync(request, new
        {
            model = _model,
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0.2
        }, cancellation);

        return doc.RootElement.GetProperty("choices")[0]
                  .GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}

/// <summary>API Anthropic <c>v1/messages</c>.</summary>
public sealed class AnthropicProvider : IAiProvider
{
    private readonly string _apiKey;
    private readonly string _model;

    public AnthropicProvider(string apiKey, string model) { _apiKey = apiKey; _model = model; }

    public string Name => $"Anthropic / {_model}";

    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellation)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        using var doc = await AiHttp.PostAsync(request, new
        {
            model = _model,
            max_tokens = 2048,
            messages = new[] { new { role = "user", content = prompt } }
        }, cancellation);

        var sb = new StringBuilder();
        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
            if (block.TryGetProperty("text", out var text)) sb.Append(text.GetString());
        return sb.ToString();
    }
}
