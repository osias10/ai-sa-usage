using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIUsage.Core;

namespace AIUsage.App;

public sealed class DesktopLoginInteraction : ILoginInteraction
{
    public Task OpenBrowserAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new InvalidOperationException("로그인 주소가 올바르지 않습니다.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
    public async Task<string?> RequestInputAsync(string title, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new Window { Title = $"AI SA · {title}", Width = 500, SizeToContent = SizeToContent.Height, MinHeight = 210, MaxHeight = 650, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true };
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
            var explanation = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18), LineHeight = 20 };
            explanation.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(explanation);
            var input = new TextBox { MinHeight = 38, MaxLength = 4096 };
            panel.Children.Add(input);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 75, Margin = new Thickness(0, 0, 8, 0) };
            var confirm = new Button { Content = "계속", IsDefault = true, MinWidth = 75, Style = (Style)Application.Current.FindResource("PrimaryButton") };
            confirm.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
            buttons.Children.Add(cancel); buttons.Children.Add(confirm); panel.Children.Add(buttons); dialog.Content = panel;
            dialog.Loaded += (_, _) => Keyboard.Focus(input);
            using var registration = cancellationToken.Register(() => dialog.Dispatcher.BeginInvoke(() => { if (dialog.IsVisible) dialog.Close(); }));
            return dialog.ShowDialog() == true ? input.Text.Trim() : null;
        });
    }
}
