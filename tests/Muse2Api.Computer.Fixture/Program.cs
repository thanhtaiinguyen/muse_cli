namespace Muse2Api.Computer.Fixture;

public sealed class FixtureMarker;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var form = new Form { Text = "Muse · cửa sổ kiểm thử tự động", Size = new Size(620, 360), StartPosition = FormStartPosition.CenterScreen };
        var input = new TextBox { Name = "testInput", AccessibleName = "Test input", Location = new Point(30, 40), Width = 500, Text = "initial" };
        var button = new Button { Name = "apply", Text = "Apply", Location = new Point(30, 95), Width = 130 };
        var result = new Label { Name = "result", Text = "Ready", AutoSize = true, Location = new Point(30, 155) };
        var password = new TextBox { Name = "password", Text = "not-for-automation", UseSystemPasswordChar = true, Location = new Point(30, 215), Width = 200 };
        var toggle = new CheckBox { Name = "option", Text = "Enable feature", Location = new Point(270, 215), Width = 160 };
        button.Click += (_, _) => { result.Text = "Saved: " + input.Text; if (args.Length > 0) File.WriteAllText(args[0], input.Text); };
        form.Controls.AddRange(new Control[] { input, button, result, password, toggle });
        form.Shown += (_, _) => { Console.OutputEncoding = System.Text.Encoding.UTF8; Console.WriteLine(form.Handle.ToInt64()); Console.Out.Flush(); };
        Application.Run(form);
    }
}
