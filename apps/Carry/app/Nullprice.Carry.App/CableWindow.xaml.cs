using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;
using Nullprice.Carry.Core;

namespace Nullprice.Carry.App;

public partial class CableWindow : Window
{
    private readonly CableTransferService _service = new();
    private CancellationTokenSource? _cancellation;

    public CableWindow()
    {
        InitializeComponent();
        CodeBox.Text = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        AddressBox.Text = string.Join("  or  ", CableTransferService.GetLocalAddresses());
        PathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    private bool IsSending => SendMode.IsChecked == true;

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AddressLabel.Text = IsSending ? "Receiver IP" : "This laptop's IP";
        AddressBox.IsReadOnly = !IsSending;
        AddressBox.Text = IsSending ? "" :
            string.Join("  or  ", CableTransferService.GetLocalAddresses());
        PathLabel.Text = IsSending ? "Completed Carry package to send" : "Save incoming package to";
        PathBox.Text = IsSending ? "" : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        InstructionText.Text = IsSending
            ? "On the new laptop, start Receive first. Enter its IP address and 6-digit code here."
            : "Connect both laptops by Ethernet cable or to the same router. Start Receive here first.";
        StartButton.Content = IsSending ? "Send package" : "Start receiving";
        ResetProgress();
    }

    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = IsSending ? "Choose a completed Carry package" : "Choose where to save the incoming package",
            Multiselect = false
        };
        if (picker.ShowDialog(this) == true)
            PathBox.Text = picker.FolderName;
    }

    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(PathBox.Text))
        {
            MessageBox.Show("Choose an existing folder first.", "Carry",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (CodeBox.Text.Length != 6 || !CodeBox.Text.All(char.IsDigit))
        {
            MessageBox.Show("Enter the same 6-digit code on both laptops.", "Carry",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<CableProgress>(UpdateProgress);
        try
        {
            if (IsSending)
            {
                await _service.SendPackageAsync(PathBox.Text, AddressBox.Text, CodeBox.Text,
                    progress, _cancellation.Token);
                MessageBox.Show("The package reached the new laptop.", "Carry",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var received = await _service.ReceivePackageAsync(PathBox.Text, CodeBox.Text,
                    progress, _cancellation.Token);
                MessageBox.Show(
                    $"Transfer received:\n{received}\n\nReturn to Carry and choose Restore on new laptop.",
                    "Carry", MessageBoxButton.OK, MessageBoxImage.Information);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{received}\"")
                    { UseShellExecute = true });
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Transfer stopped";
            CurrentItemText.Text = ex.Message;
            MessageBox.Show(ex.Message, "Direct transfer stopped",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            SetBusy(false);
        }
    }

    private void UpdateProgress(CableProgress value)
    {
        StatusText.Text = value.Stage;
        CurrentItemText.Text = value.CurrentItem;
        if (value.TotalBytes > 0)
        {
            TransferProgress.IsIndeterminate = false;
            TransferProgress.Value = Math.Min(100, value.BytesCompleted * 100d / value.TotalBytes);
            ProgressText.Text = $"{value.FilesCompleted:N0} / {value.TotalFiles:N0} files • " +
                $"{FormatSize(value.BytesCompleted)} / {FormatSize(value.TotalBytes)}";
        }
        else
        {
            TransferProgress.IsIndeterminate = value.Stage is "Waiting";
            ProgressText.Text = "";
        }
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = !busy;
        SendMode.IsEnabled = !busy;
        ReceiveMode.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        AddressBox.IsEnabled = !busy;
        CodeBox.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResetProgress()
    {
        StatusText.Text = "Ready";
        CurrentItemText.Text = "";
        ProgressText.Text = "";
        TransferProgress.IsIndeterminate = false;
        TransferProgress.Value = 0;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}
