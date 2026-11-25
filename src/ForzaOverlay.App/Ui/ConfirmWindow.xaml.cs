using System.Windows;

namespace ForzaOverlay.App.Ui;

/// <summary>
/// 通用二次确认窗。外观照 <see cref="UpdateWindow"/>，与设置页的浅色风格一致。
/// 破坏性操作统一走这里，不用系统 MessageBox（原生样式与本程序不一致）。
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string heading, string message)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = heading;
        MessageText.Text = message;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
