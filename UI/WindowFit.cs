using System.Windows;

namespace TimeTracker.UI;

// Fenêtres qui s'ouvrent d'elles-mêmes (rappel, question d'agenda, changement de tâche) :
// ShowActivated="False" dans leur XAML, jamais d'Activate(). L'utilisateur est peut-être en
// train de taper ailleurs — le focus volé lui faisait perdre sa saisie (2026-09-30), et une
// Entrée tapée pour son propre texte aurait répondu au bouton par défaut à sa place.
// --uitest le vérifie (AutomaticPopups).

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

    /// <summary>
    /// Coin bas-droit de la zone de travail — et qui y <b>reste</b> quand la fenêtre grandit.
    ///
    /// Une fenêtre en <c>SizeToContent="Height"</c> grandit vers le bas : placée une seule fois
    /// au <c>Loaded</c>, elle débordait dès que du contenu arrivait après coup. C'est ce qui
    /// mettait les boutons de « Tu as changé de tâche ? » hors de l'écran quand la réponse de
    /// l'IA s'affichait (signalé le 2026-09-30). À chaque changement de hauteur, le bas reste
    /// donc calé sur la zone de travail ; si l'utilisateur a déplacé la fenêtre, elle n'est plus
    /// ramenée dans le coin, seulement empêchée de sortir de l'écran.
    ///
    /// Jamais hors de l'écran, non plus, au réveil de veille quand l'affichage n'est pas encore
    /// stabilisé : ces fenêtres sont parfois absentes de la barre des tâches et d'Alt-Tab, hors
    /// écran elles seraient introuvables (HANDOFF §8).
    /// </summary>
    public static void KeepBottomRight(Window window, double gap = 12)
    {
        bool placing = false, movedByUser = false;

        void Place()
        {
            var area = SystemParameters.WorkArea;
            double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            double height = window.ActualHeight > 0 ? window.ActualHeight : 200;
            double maxLeft = Math.Max(area.Left, area.Right - width);
            double maxTop = Math.Max(area.Top, area.Bottom - height);
            placing = true;
            if (movedByUser)
            {
                window.Left = Math.Clamp(window.Left, area.Left, maxLeft);
                window.Top = Math.Clamp(window.Top, area.Top, maxTop);
            }
            else
            {
                window.Left = Math.Clamp(area.Right - width - gap, area.Left, maxLeft);
                window.Top = Math.Clamp(area.Bottom - height - gap, area.Top, maxTop);
            }
            placing = false;
        }

        window.Loaded += (_, _) => Place();
        window.SizeChanged += (_, e) => { if (window.IsLoaded && e.HeightChanged) Place(); };
        window.LocationChanged += (_, _) => { if (window.IsLoaded && !placing) movedByUser = true; };
    }
}
