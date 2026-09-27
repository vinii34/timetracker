using System.Net.Http;
using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services.Ai;

/// <summary>
/// Un modèle de langage interrogeable en une question / une réponse. Trois fournisseurs
/// derrière la même interface, pour ne pas lier l'application à une clé Gemini : ce qui
/// compte pour l'utilisateur est de choisir <i>où</i> partent ses noms de tâches — y compris
/// nulle part, avec un modèle local qui parle le protocole OpenAI (Ollama, LM Studio).
///
/// ⚠️ C'est le seul endroit de l'application d'où quelque chose peut sortir du poste. Rien ne
/// passe par ici sans que l'utilisateur ait activé l'assistant <b>et</b> cliqué un bouton.
/// </summary>
public interface IAiProvider
{
    /// <summary>Nom lisible, journalisé (« Gemini / gemini-2.5-flash »).</summary>
    string Name { get; }

    /// <summary>Envoie une question, renvoie le texte de la réponse. Lève sur toute erreur réseau ou API.</summary>
    Task<string> CompleteAsync(string prompt, CancellationToken cancellation);
}

/// <summary>Fabrique et valeurs par défaut par fournisseur.</summary>
public static class AiProviderFactory
{
    public const string Gemini = "gemini";
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";

    /// <summary>Client partagé : un HttpClient par appel épuiserait les sockets.</summary>
    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public static IReadOnlyList<(string Id, string Label)> Providers { get; } = new[]
    {
        (Gemini, "Google Gemini (clé AI Studio)"),
        (OpenAi, "Compatible OpenAI (OpenAI, Mistral, Ollama, LM Studio…)"),
        (Anthropic, "Anthropic Claude")
    };

    /// <summary>
    /// Modèles par défaut : les moins chers de chaque maison. Pour Gemini, la page des limites
    /// de l'utilisateur (2026-09-17) donne 500 requêtes/jour aux « Flash Lite » contre 20 aux
    /// « Flash » : c'est le Lite qui convient à un usage à la demande.
    /// </summary>
    public static string DefaultModel(string provider) => provider switch
    {
        OpenAi => "gpt-4o-mini",
        Anthropic => "claude-haiku-4-5-20251001",
        _ => "gemini-3.5-flash-lite"
    };

    public static string DefaultBaseUrl(string provider) => provider switch
    {
        OpenAi => "https://api.openai.com/v1",
        _ => ""
    };

    /// <summary>Construit le fournisseur des réglages. Lève si la configuration est inutilisable.</summary>
    public static IAiProvider Create(AppSettings settings) =>
        Create(settings.AiProvider, settings.AiApiKey, settings.AiModel, settings.AiBaseUrl);

    public static IAiProvider Create(string provider, string apiKey, string model, string baseUrl)
    {
        model = string.IsNullOrWhiteSpace(model) ? DefaultModel(provider) : model.Trim();
        apiKey = apiKey.Trim();
        switch (provider)
        {
            case Gemini:
                if (apiKey.Length == 0) throw new InvalidOperationException("Clé API Gemini manquante.");
                return new GeminiProvider(apiKey, model);
            case OpenAi:
                baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl(provider) : baseUrl.Trim().TrimEnd('/');
                // Pas de clé exigée : un serveur local (Ollama) n'en demande pas.
                return new OpenAiCompatibleProvider(baseUrl, apiKey, model);
            case Anthropic:
                if (apiKey.Length == 0) throw new InvalidOperationException("Clé API Anthropic manquante.");
                return new AnthropicProvider(apiKey, model);
            default:
                throw new InvalidOperationException($"Fournisseur inconnu : « {provider} ».");
        }
    }
}
