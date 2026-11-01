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
        var pointer = new PointerPanel { Name = "pointer", AccessibleName = "Pointer surface", Location = new Point(270, 92), Size = new Size(230, 90), BackColor = Color.LightGray, TabStop = true };
        Point? draggedFrom = null;
        pointer.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) draggedFrom = e.Location; };
        pointer.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) result.Text = "RightClick";
            else if (draggedFrom is { } start && Math.Abs(e.X-start.X)>40) result.Text = "DragDone";
            draggedFrom = null;
        };
        pointer.MouseDoubleClick += (_, _) => result.Text = "DoubleClick";
        pointer.MouseWheel += (_, e) => result.Text = e.Delta < 0 ? "Wheel:down" : "Wheel:up";
        button.Click += (_, _) => { result.Text = "Saved: " + input.Text; if (args.Length > 0) File.WriteAllText(args[0], input.Text); };
        form.Controls.AddRange(new Control[] { input, button, result, password, toggle, pointer });
        if (args.Contains("--offscreen"))
        {
            form.ShowInTaskbar = false;
            _ = form.Handle;
            foreach (Control control in form.Controls) _ = control.Handle;
            using var owned = new Form { Owner = form, Text = "Hidden owned dialog", ShowInTaskbar = false };
            using var separate = new Form { Text = "Hidden separate window", ShowInTaskbar = false };
            _ = owned.Handle; _ = separate.Handle;
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine(form.Handle.ToInt64()); Console.Out.Flush();
            Console.WriteLine(owned.Handle.ToInt64()+" "+separate.Handle.ToInt64()); Console.Out.Flush();
            Application.Run(new ApplicationContext()); // No Form.Show/Application.Run(form).
            return;
        }
        form.Shown += (_, _) => { Console.OutputEncoding = System.Text.Encoding.UTF8; Console.WriteLine(form.Handle.ToInt64()); Console.Out.Flush(); };
        Application.Run(form);
    }
    private sealed class PointerPanel : Panel
    {
        public PointerPanel() { SetStyle(ControlStyles.Selectable | ControlStyles.StandardDoubleClick, true); }
    }
}
