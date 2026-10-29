namespace Muse2Api.WinForms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--prepare-browser")
        {
            // Child-process mode used by BrowserSetup.InstallAsync: install the
            // bundled Chromium through Playwright and exit with its result.
            // Without this branch the child would open the main window and the
            // parent would wait forever for it to exit.
            return Muse2Api.DesktopProfile.InstallBrowser();
        }
        if (args.Length >= 1 && args[0] == "--render-check")
        {
            ApplicationConfiguration.Initialize();
            var outDir = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(outDir);

            using var form = new Form
            {
                Size = new Size(1000, 720),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20000, -20000),
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None
            };
            var transcript = new ChatTranscript { Dock = DockStyle.Fill };
            form.Controls.Add(transcript);
            form.CreateControl();
            transcript.CreateControl();

            transcript.AddUserMessage("Hãy đọc project và cải tiến giao diện giúp tôi.");

            var thinking = transcript.AddThinking("Đang suy nghĩ…");
            thinking.AddThought("Khởi động phân tích dự án muse2api-dotnet");
            thinking.AddThought("Đọc cấu trúc file và kiểm tra các component giao diện");
            thinking.Complete("Đã suy nghĩ xong trong 1.8s");

            var tool1 = transcript.AddToolCall("read_file", "src/Muse2Api.WinForms/AgentWorkspaceControl.cs", "read_file path=src/Muse2Api.WinForms/AgentWorkspaceControl.cs");
            tool1.SetResult("namespace Muse2Api.WinForms;\npublic sealed partial class AgentWorkspaceControl : UserControl { ... }", isError: false);

            var tool2 = transcript.AddToolCall("run_command", null, "dotnet test tests/Muse2Api.Tests/Muse2Api.Tests.csproj");
            tool2.SetResult("Passed! - Failed: 0, Passed: 196, Skipped: 7", isError: false);

            transcript.AddAssistantMessage("Tôi đã nâng cấp giao diện theo phong cách Codex hiện đại:\n- **Khối Thinking:** Hiển thị thời gian và các bước suy nghĩ có thể thu gọn.\n- **Hội thoại Tool:** Tương tác giữa Muse và công cụ được biểu diễn qua thẻ trực quan (đọc tệp, sửa code, chạy lệnh).\n- **Nhả chữ (Typewriter):** Phản hồi mượt mà theo thời gian thực.\n- **Không chồng chéo:** Các nút và bảng điều khiển được căn lề chuẩn xác, không còn lỗi bóng chữ.", stream: false);

            form.Size = new Size(1000, 720);
            transcript.Reflow();

            // Validate non-overlapping bounds in conversation
            var typeField = typeof(ChatTranscript).GetField("_cards", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (typeField?.GetValue(transcript) is List<ChatCard> cards)
            {
                for (var i = 0; i < cards.Count; i++)
                {
                    Console.WriteLine($"CARD_{i}: {cards[i].GetType().Name} Bounds={cards[i].Bounds}");
                    for (var j = i + 1; j < cards.Count; j++)
                    {
                        if (cards[i].Bounds.IntersectsWith(cards[j].Bounds))
                        {
                            Console.WriteLine($"OVERLAP_ERROR between Card {i} and {j}!");
                            return 4;
                        }
                    }
                }
            }

            form.Visible = true;
            void RenderTree(Control control, Point origin, Bitmap target)
            {
                if (!control.Visible) return;
                if (control.Parent is SplitContainer s && s.Panel2Collapsed && control == s.Panel2) return;
                foreach (Control child in control.Controls)
                {
                    if (!child.Visible) continue;
                    if (child.Parent is SplitContainer sc && sc.Panel2Collapsed && child == sc.Panel2) continue;
                    var childOrigin = new Point(origin.X + child.Left, origin.Y + child.Top);
                    try
                    {
                        if (child.Width > 0 && child.Height > 0)
                        {
                            using var cbmp = new Bitmap(child.Width, child.Height);
                            child.DrawToBitmap(cbmp, new Rectangle(0, 0, child.Width, child.Height));
                            using var g = Graphics.FromImage(target);
                            g.DrawImage(cbmp, childOrigin);
                        }
                    }
                    catch { }
                    RenderTree(child, childOrigin, target);
                }
            }

            using var bmp = new Bitmap(1000, 720);
            using (var g = Graphics.FromImage(bmp)) { g.Clear(DesktopTheme.Background); }
            RenderTree(form, Point.Empty, bmp);
            var previewPath = Path.Combine(outDir, "codex_chat_preview.png");
            bmp.Save(previewPath, System.Drawing.Imaging.ImageFormat.Png);

            try
            {
                using var mainForm = new MainForm
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-20000, -20000),
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    Size = new Size(1280, 820)
                };
                mainForm.CreateControl();
                mainForm.Visible = true;
                using var mainBmp = new Bitmap(1280, 820);
                using (var g = Graphics.FromImage(mainBmp))
                {
                    g.Clear(DesktopTheme.Background);
                }
                RenderTree(mainForm, Point.Empty, mainBmp);
                var mainPreviewPath = Path.Combine(outDir, "main_window_preview.png");
                mainBmp.Save(mainPreviewPath, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("MAIN_RENDER_OK: " + mainPreviewPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("MAIN_RENDER_WARN: " + ex.Message);
            }

            // Scroll regression: with more cards than the viewport can show, the
            // transcript must stay pinned to the bottom as content keeps growing.
            for (var i = 0; i < 30; i++) transcript.AddToolCall("run_command", null, "dotnet test").SetResult("ok", isError: false);
            var growing = transcript.AddAssistantMessage("Dòng đầu của câu trả lời dài.", stream: false);
            transcript.ScrollToBottom();
            Application.DoEvents();
            if (!transcript.IsNearBottom()) { Console.WriteLine("SCROLL_ERROR: not at bottom after ScrollToBottom"); return 5; }
            transcript.UpdateMessage(growing, string.Join("\n", Enumerable.Range(1, 60).Select(n => $"Dòng nội dung số {n} để câu trả lời vượt chiều cao khung chat.")));
            Application.DoEvents();
            if (!transcript.IsNearBottom()) { Console.WriteLine("SCROLL_ERROR: view did not follow growing content"); return 5; }
            Console.WriteLine("SCROLL_CHECK_OK");

            Console.WriteLine("RENDER_CHECK_OK: " + previewPath);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--check-installation")
        {
            using var log = new Muse2Api.RequestLog();
            var bundled = Path.Combine(AppContext.BaseDirectory, ".playwright");
            var ready = Muse2Api.Computer.DesktopComputer.IsRuntimeAvailable && File.Exists(Path.Combine(bundled, "package", "cli.js"))
                && Directory.Exists(Path.Combine(bundled, "node"))
                && Directory.EnumerateFiles(Path.Combine(bundled, "node"), "node.exe", SearchOption.AllDirectories).Any();
            Console.WriteLine(ready ? "Muse installation OK" : "Muse browser or desktop automation component is missing");
            return ready ? 0 : 3;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
