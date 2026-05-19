using System.Windows;
using System.Windows.Input;

namespace PilotEars;

public partial class PresetNameDialog : Window
{
    public string PresetName => NameBox.Text.Trim();

    public PresetNameDialog(string title, string prompt, string okText, string cancelText, string initial = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        OkBtn.Content = okText;
        CancelBtn.Content = cancelText;
        NameBox.Text = initial;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PresetName)) return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }
}
