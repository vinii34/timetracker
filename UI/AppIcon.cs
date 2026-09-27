using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
// System.Drawing et System.Windows.Media portent les mêmes noms : ici c'est GDI+ qui dessine.
using Color = System.Drawing.Color;
using Pen = System.Drawing.Pen;

namespace TimeTracker.UI;

/// <summary>
/// L'icône de l'application, dessinée une fois et servie sous les deux formes dont WPF et
/// WinForms ont besoin : un <see cref="Icon"/> pour la barre système, un
/// <see cref="ImageSource"/> pour <c>Window.Icon</c>.
///
/// Dessinée plutôt qu'embarquée en <c>.ico</c> : le projet n'est pas un dépôt git, un binaire
/// perdu serait irrécupérable. Le dessin reste donc la seule source.
/// </summary>
public static class AppIcon
{
    /// <summary>
    /// Icône des fenêtres. Sans elle, Windows affiche un cadre vide dans la barre des tâches
    /// et dans Alt-Tab — aucune fenêtre XAML ne posait <c>Icon</c>.
    /// </summary>
    public static ImageSource Image { get; } = CreateImage();

    /// <summary>Icône de la barre système. WinForms la veut en <see cref="Icon"/>.</summary>
    public static Icon CreateTrayIcon()
    {
        using var bmp = Draw(32);
        IntPtr handle = bmp.GetHicon();
        return (Icon)Icon.FromHandle(handle).Clone();
    }

    /// <summary>
    /// Passe par un PNG en mémoire plutôt que par <c>CreateBitmapSourceFromHBitmap</c> :
    /// pas de handle GDI à libérer, et la transparence est conservée telle quelle.
    /// </summary>
    private static ImageSource CreateImage()
    {
        using var bmp = Draw(64);   // 64 px : Windows réduit proprement, l'inverse baverait
        using var stream = new MemoryStream();
        bmp.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;   // libère le flux dès la fin de l'init
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();                                 // partagée entre toutes les fenêtres
        return image;
    }

    /// <summary>Horloge simple, dessinée proportionnellement à la taille demandée.</summary>
    private static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float unit = size / 32f;
        float inset = 2 * unit;
        float diameter = size - 2 * inset;

        using var face = new SolidBrush(Color.FromArgb(0x2D, 0x7D, 0xD2));
        using var rim = new Pen(Color.White, 2 * unit);
        g.FillEllipse(face, inset, inset, diameter, diameter);
        g.DrawEllipse(rim, inset, inset, diameter, diameter);

        using var hands = new Pen(Color.White, 2 * unit);
        float center = size / 2f;
        g.DrawLine(hands, center, center, center, 7 * unit);        // aiguille verticale
        g.DrawLine(hands, center, center, 23 * unit, center);       // aiguille horizontale
        return bmp;
    }
}
