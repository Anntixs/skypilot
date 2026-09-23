using System.Windows;
using SkyPilot.Core.Model;

namespace SkyPilot.App.Views;

public partial class FlightPlanWindow : Window
{
    public FlightPlan? Plan { get; private set; }

    public FlightPlanWindow(FlightPlan? previous, string typeCode, bool connected)
    {
        InitializeComponent();
        var p = previous ?? new FlightPlan { AircraftType = typeCode, Remarks = "/V/" };
        RulesBox.SelectedIndex = p.Rules == FlightRules.Vfr ? 1 : 0;
        TypeBox.Text = p.AircraftType;
        DepBox.Text = p.Departure;
        DestBox.Text = p.Destination;
        AltnBox.Text = p.Alternate;
        DepTimeBox.Text = p.DepartureTime;
        AltBox.Text = p.CruiseAltitude;
        TasBox.Text = p.TrueAirspeed > 0 ? p.TrueAirspeed.ToString() : "";
        EnrouteBox.Text = p.TimeEnroute > TimeSpan.Zero ? p.TimeEnroute.ToString(@"hh\:mm") : "";
        FuelBox.Text = p.FuelOnBoard > TimeSpan.Zero ? p.FuelOnBoard.ToString(@"hh\:mm") : "";
        RouteBox.Text = p.Route;
        RemarksBox.Text = p.Remarks;
        SendButton.IsEnabled = connected;
        if (!connected) ErrorText.Text = "Отправить план можно после подключения к сети.";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (DepBox.Text.Trim().Length != 4 || DestBox.Text.Trim().Length != 4)
        {
            ErrorText.Text = "Укажите ICAO-коды аэропортов вылета и назначения";
            return;
        }
        if (!TryTime(EnrouteBox.Text, out var enroute) || !TryTime(FuelBox.Text, out var fuel))
        {
            ErrorText.Text = "Время в формате ЧЧ:ММ";
            return;
        }
        int.TryParse(TasBox.Text.Trim(), out var tas);
        Plan = new FlightPlan
        {
            Rules = RulesBox.SelectedIndex == 1 ? FlightRules.Vfr : FlightRules.Ifr,
            AircraftType = TypeBox.Text.Trim(),
            Departure = DepBox.Text.Trim(),
            Destination = DestBox.Text.Trim(),
            Alternate = AltnBox.Text.Trim(),
            DepartureTime = DepTimeBox.Text.Trim(),
            CruiseAltitude = AltBox.Text.Trim(),
            TrueAirspeed = tas,
            TimeEnroute = enroute,
            FuelOnBoard = fuel,
            Route = RouteBox.Text.Trim(),
            Remarks = RemarksBox.Text.Trim(),
        };
        DialogResult = true;
    }

    private static bool TryTime(string text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        text = text.Trim();
        if (text.Length == 0) return true;
        var parts = text.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m) || m is < 0 or > 59 || h < 0)
            return false;
        value = new TimeSpan(h, m, 0);
        return true;
    }
}
