using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Source d'indices de réunion, interrogée périodiquement par <see cref="MeetingDetector"/>.
///
/// L'interface existe surtout pour rendre la machine à états testable : on ne peut pas
/// déclencher une vraie réunion Teams depuis <c>--selftest</c>, alors on y injecte une sonde
/// scriptée.
/// </summary>
public interface IMeetingProbe
{
    /// <summary>Nom court, journalisé dans le diagnostic (« micro », « fenêtres »).</summary>
    string Name { get; }

    /// <summary>Indices visibles à cet instant. Liste vide = rien détecté. Ne doit pas lever.</summary>
    IReadOnlyList<MeetingSignal> Poll();
}
