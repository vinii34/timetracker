using System.Windows;

namespace TimeTracker.UI;

/// <summary>
/// Choix d'une période libre pour l'export — le tableau de bord n'exporte que le jour ou la
/// semaine affichés, alors qu'une timesheet se remplit parfois au mois ou après des congés.
/// Ne fait que renvoyer les bornes et le format : l'export lui-même reste au tableau de bord.
/// </summary>
public partial class ExportRangeWindow : Window
{
    /// <summary>Bornes incluses et format (« csv » ou « xlsx »).</summary>
    public sealed record Choice(DateTime From, DateTime ToInclusive, string Format);

    private Choice? _choice;

    internal ExportRangeWindow(DateTime from, DateTime toInclusive)
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        FromPicker.SelectedDate = from.Date;
        ToPicker.SelectedDate = toInclusive.Date;
        Loaded += (_, _) => Activate();
    }

    /// <summary>Ouvre le choix en modal ; null si annulé.</summary>
    public static Choice? Ask(Window? owner, DateTime from, DateTime toInclusive)
    {
        var win = new ExportRangeWindow(from, toInclusive);
        if (owner != null && owner.IsLoaded) win.Owner = owner;
        else win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.ShowDialog();
        return win._choice;
    }

    private void ThisMonth_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Now.Date;
        Set(new DateTime(today.Year, today.Month, 1), today);
    }

    private void LastMonth_Click(object sender, RoutedEventArgs e)
    {
        var first = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1).AddMonths(-1);
        Set(first, first.AddMonths(1).AddDays(-1));
    }

    private void Last30_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Now.Date;
        Set(today.AddDays(-29), today);
    }

    private void Set(DateTime from, DateTime to)
    {
        FromPicker.SelectedDate = from;
        ToPicker.SelectedDate = to;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Csv_Click(object sender, RoutedEventArgs e) => Confirm("csv");

    private void Xlsx_Click(object sender, RoutedEventArgs e) => Confirm("xlsx");

    private void Confirm(string format)
    {
        if (FromPicker.SelectedDate is not DateTime from || ToPicker.SelectedDate is not DateTime to)
        {
            ErrorText.Text = "Choisis les deux dates.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (to < from)
        {
            ErrorText.Text = "La date de fin est avant la date de début.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        _choice = new Choice(from.Date, to.Date, format);
        Close();
    }
}
