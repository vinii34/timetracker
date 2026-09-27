using System.Windows;

namespace TimeTracker.UI;

/// <summary>
/// Empêche une fenêtre de dépasser l'écran.
///
/// Le poste de développement a un grand écran à 100 % ; celui de l'utilisateur est un portable
/// à <b>150 %</b>, où un écran 1920×1080 ne laisse qu'environ <b>670 points</b> de hauteur utile.
/// La fenêtre Paramètres, en <c>SizeToContent="Height"</c> et <c>NoResize</c>, dépassait donc par
/// le bas : les derniers réglages et les boutons étaient hors de portée, sans même une barre de
/// défilement pour s'en douter.
///
/// ⚠️ Une fenêtre haute doit donc réunir deux choses : ses réglages dans un <c>ScrollViewer</c>,
/// et cet appel. L'un sans l'autre ne suffit pas — sans MaxHeight le ScrollViewer ne défile
/// jamais, sans ScrollViewer la MaxHeight coupe le contenu.
/// </summary>
public static class WindowFit
{
    /// <summary>Marge laissée au cadre de la fenêtre et à la barre de titre.</summary>
    private const double Margin = 40;

    /// <summary>
    /// Hauteur maximale tenable sur l'écran de travail courant. Posée en code plutôt qu'en XAML :
    /// <see cref="SystemParameters.WorkArea"/> est un <c>Rect</c>, qui ne se lie pas à une
    /// <c>MaxHeight</c> sans convertisseur.
    /// </summary>
    public static void LimitToWorkArea(Window window)
    {
        var available = SystemParameters.WorkArea.Height - Margin;
        if (available > 200) window.MaxHeight = available;
    }
}
