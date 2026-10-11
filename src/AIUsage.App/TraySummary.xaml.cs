using System.Windows.Controls;

namespace AIUsage.App;

public partial class TraySummary : UserControl
{
    public TraySummary(MainViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }
    public void SetMode(bool showRemaining) => ModeLabel.Text = showRemaining ? "잔여량 · 초기화까지" : "사용량 · 초기화까지";
}
