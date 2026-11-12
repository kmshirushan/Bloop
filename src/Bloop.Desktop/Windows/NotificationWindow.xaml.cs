using System.Windows;
using Bloop.Core.Interfaces;
using Bloop.Core.Personality;

namespace Bloop.Desktop.Windows;

public partial class NotificationWindow : Window
{
    private readonly IPersonalityEngine _personality;

    public NotificationWindow(IPersonalityEngine personality)
    {
        InitializeComponent();
        _personality = personality;
    }

    private void OnHappyClick(object sender, RoutedEventArgs e) =>
        _personality.TryTransition(MascotState.Happy, force: true);

    private void OnSurprisedClick(object sender, RoutedEventArgs e) =>
        _personality.TryTransition(MascotState.Surprised, force: true);

    private void OnCuriousClick(object sender, RoutedEventArgs e) =>
        _personality.TryTransition(MascotState.Curious, force: true);

    private void OnSleepyClick(object sender, RoutedEventArgs e) =>
        _personality.TryTransition(MascotState.Sleepy, force: true);

    public void ReportAnimationAck(string stateName, string? commandId)
    {
        Dispatcher.Invoke(() =>
        {
            AckStatusText.Text = $"Ack: '{stateName}' active ({DateTime.Now:HH:mm:ss})";
        });
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();
}

