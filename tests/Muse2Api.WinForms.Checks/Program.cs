using System.Reflection;
using Muse2Api.WinForms;
static class Program
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    [STAThread] static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);Application.EnableVisualStyles();using var context=new ApplicationContext();var code=1;EventHandler? idle=null;
        idle=async(_,_)=>{Application.Idle-=idle;try{await Check();code=0;}catch(Exception ex){Console.Error.WriteLine(ex);}finally{context.ExitThread();}};
        Application.Idle+=idle;Application.Run(context);return code;
    }
    static async Task Check()
    {
        await CheckPreviewIsolation();
        CheckComputerPicker();
        await CheckUpdateSettings();
        CheckConnectPanel();
        await CheckConversationRendering();
        using var transcript=new ChatTranscript {Size=new Size(1000,500)};transcript.CreateControl();
        var viewport=(Panel)Get(transcript,"_viewport");
        var first=transcript.AddUserMessage("Đọc tệp đầu");
        transcript.AddAssistantMessage(string.Join('\n',Enumerable.Range(0,100).Select(i=>"Dòng trả lời đầu "+i)));
        await Task.Delay(80);transcript.ScrollToBottom();
        transcript.AddUserMessage("Đọc tệp thứ hai");var thinking=transcript.AddThinking("Đang đọc tệp thứ hai…");
        for(var i=0;i<8;i++){thinking.AddThought("Đọc đoạn "+i);var tool=transcript.AddToolCall("read_file","second.txt","read_file second.txt");tool.SetResult("Nội dung thứ hai",false);await Task.Delay(20);}
        await Task.Delay(80);
        var offset=-viewport.AutoScrollPosition.Y;
        Console.WriteLine($"offset={offset} first.top={first.Top} expected={16-offset}");
        Require(offset>100,"Fixture never scrolled");Require(Math.Abs(first.Top-(16-offset))<3,"Reflow reset content to the first message despite a scrolled viewport");
        var last=viewport.Controls.Cast<Control>().Last();Require(last.Bottom<=viewport.ClientSize.Height+4,"Latest tool scrolled out of view");
        viewport.AutoScrollPosition=new Point(0,250);
        viewport.GetType().GetMethod("OnScroll",Private)!.Invoke(viewport,new object[]{new ScrollEventArgs(ScrollEventType.ThumbPosition,250)});
        thinking.AddThought("Thông tin mới không được kéo người đang đọc lịch sử đi");await Task.Delay(80);
        Require(Math.Abs(-viewport.AutoScrollPosition.Y-250)<3,"User history reading position was stolen");
        Require(Math.Abs(first.Top-(16+viewport.AutoScrollPosition.Y))<3,"Reading history content shifted to the top");
        Require(Application.OpenForms.Count==0,"Visible window opened");
        Console.WriteLine("PASS: second turn/tool updates remain at bottom; user history position preserved; no visible windows");
    }

    static void CheckConnectPanel()
    {
        var assembly=typeof(MainForm).Assembly;
        var skillsDir=Path.Combine(Path.GetTempPath(),"muse-skills-check-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(skillsDir);
        try
        {
            File.WriteAllText(Path.Combine(skillsDir,"fe.md"),"---\nname: Frontend\nalias: fe\ndescription: Quy tắc giao diện\n---\nLuôn dùng CSS grid.\n");
            var connectType=assembly.GetType("Muse2Api.WinForms.ConnectPanelInfo")!;
            var connect=connectType.GetConstructor(new[]{typeof(string),typeof(string),typeof(GatewayClient)})!.Invoke(new object[]{"http://127.0.0.1:18610","m2a-test-key",new GatewayClient()});
            var formType=assembly.GetType("Muse2Api.WinForms.PreferencesForm")!;
            var ctor=formType.GetConstructor(new[]{typeof(bool),typeof(bool),typeof(bool),typeof(bool),typeof(int),typeof(string),typeof(string),typeof(Func<Task<Muse2Api.UpdateCheckResult>>),typeof(Action<Muse2Api.AvailableUpdate>),typeof(Muse2Api.UpdateCheckResult),typeof(Muse2Api.UpdateFeed),connectType,typeof(string)})!;
            using var form=(Form)ctor.Invoke(new object?[]{true,false,false,false,0,skillsDir,"",null,null,null,Muse2Api.UpdateFeed.Release,connect,skillsDir});
            form.CreateControl();
            var skills=(ListBox)Get(form,"_skillsList");
            Require(skills.Items.Count==1&&skills.Items[0]!.ToString()!.Contains("@fe"),"Skills panel did not list the installed @fe skill");
            var url=(TextBox)Get(form,"_baseUrlBox");
            Require(url.Text=="http://127.0.0.1:18610/v1","OpenAI base URL export is wrong: "+url.Text);
            var key=(TextBox)Get(form,"_apiKeyBox");
            Require(key.Text=="m2a-test-key","API key export did not show the gateway key");
            var accounts=(ListBox)Get(form,"_accountsList");
            Require(accounts.Items.Count==0,"Accounts list should stay empty until the gateway answers");
            Require(Application.OpenForms.Count==0,"Preferences check opened a visible form");
            Console.WriteLine("PASS: preferences show the multi-account panel, OpenAI base URL + key export, and the installed @fe skill; no visible windows");
        }
        finally {try{Directory.Delete(skillsDir,true);}catch{}}
    }

    static async Task CheckConversationRendering()
    {
        var theme=typeof(MainForm).Assembly.GetType("Muse2Api.WinForms.DesktopTheme")!;
        var mode=theme.GetProperty("Mode")!;
        var original=mode.GetValue(null)!;
        using var transcript=new ChatTranscript {Size=new Size(1000,500)};
        transcript.CreateControl();
        var user=transcript.AddUserMessage("Tin nhắn cần đọc rõ ở cả hai chế độ");
        try
        {
            foreach(var name in new[]{"Light","Dark","Light"})
            {
                theme.GetMethod("SetMode")!.Invoke(null,new[]{Enum.Parse(original.GetType(),name)});
                var body=(RichTextBox)Get(user,"_body");
                Require(Contrast(body.ForeColor,body.BackColor)>=4.5,$"Unreadable {name} user message");
                body.Select(0,1);
                Require(Contrast(body.SelectionColor,body.BackColor)>=4.5,$"Unreadable {name} existing character formatting");
                var fresh=transcript.AddUserMessage("Tin mới");
                var freshBody=(RichTextBox)Get(fresh,"_body");
                Require(Contrast(freshBody.ForeColor,freshBody.BackColor)>=4.5,$"Unreadable {name} newly created message");
            }
            var answer=transcript.AddAssistantMessage("**Đã xong.**",stream:true);
            theme.GetMethod("SetMode")!.Invoke(null,new[]{Enum.Parse(original.GetType(),"Dark")});
            var timer=(System.Windows.Forms.Timer)Get(answer,"_streamTimer");
            for(var i=0;i<100&&timer.Enabled;i++)await Task.Delay(20);
            Require(!timer.Enabled&&!(bool)Get(answer,"_streaming"),"Completed answer left a live stream timer");
            var rendered=((RichTextBox)Get(answer,"_body")).Text;
            Require(rendered.TrimEnd('\r','\n')=="Đã xong.","Completed answer left a cursor or unformatted Markdown: "+System.Text.Json.JsonSerializer.Serialize(rendered));
            Require(Application.OpenForms.Count==0,"Rendering check opened a visible form");
            Console.WriteLine("PASS: light/dark user text contrast >=4.5; completed answer formats and stops its cursor/timer; no visible windows");
        }
        finally {theme.GetMethod("SetMode")!.Invoke(null,new[]{original});}
    }

    static double Contrast(Color foreground,Color background)
    {
        static double Channel(byte value){var c=value/255d;return c<=0.04045?c/12.92:Math.Pow((c+0.055)/1.055,2.4);}
        static double Luminance(Color c)=>0.2126*Channel(c.R)+0.7152*Channel(c.G)+0.0722*Channel(c.B);
        var a=Luminance(foreground);var b=Luminance(background);
        return (Math.Max(a,b)+0.05)/(Math.Min(a,b)+0.05);
    }

    static async Task CheckPreviewIsolation()
    {
        var paths=typeof(MainForm).Assembly.GetType("Muse2Api.WinForms.DesktopDataPaths")!;
        var marker=Path.Combine(AppContext.BaseDirectory,"muse-preview.profile");
        var previousMarker=File.Exists(marker)?File.ReadAllBytes(marker):null;
        var previousOverride=Environment.GetEnvironmentVariable("MUSE_DESKTOP_PROFILE_ROOT");
        var isolated=Path.Combine(AppContext.BaseDirectory,"profile-fixture-"+Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(marker,"preview");Environment.SetEnvironmentVariable("MUSE_DESKTOP_PROFILE_ROOT",null);
            var expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Muse2Api.WinForms-Preview-Net10");
            Require((string)paths.GetProperty("Root")!.GetValue(null)! == expected,"Preview exe would use the production profile");
            Require((bool)paths.GetProperty("IsOverride")!.GetValue(null)!,"Preview still imports original accounts automatically");
            Directory.CreateDirectory(isolated);Environment.SetEnvironmentVariable("MUSE_DESKTOP_PROFILE_ROOT",isolated);
            using var form=new MainForm();
            Require(form.Text.Contains("Kiểm thử .NET 10"),"Preview title is not visible to the user");
            Require((string)Get(form,"_dataDirectory")==Path.Combine(isolated,"Gateway"),"Gateway ignored profile override");
            var workspace=Get(form,"_workspace");Require((string)Get(workspace,"_preferences")==Path.Combine(isolated,"agent_workspace.json"),"Workspace ignored profile override");
            var update=(Control)Get(form,"_updateNotice");
            form.CreateControl();
            var available=new Muse2Api.AvailableUpdate("9.0.0",Muse2Api.DesktopUpdates.RepositoryUrl+"/releases/tag/v9.0.0",Muse2Api.UpdateFeed.Release);
            update.GetType().GetMethod("ShowUpdate")!.Invoke(update,new object[]{available});
            foreach(var size in new[]{new Size(900,640),new Size(1280,820)})
            {
                form.Size=size;form.PerformLayout();update.PerformLayout();
                var container=(Control)workspace;
                Require(update.Height>=36,"Update banner collapsed");
                Require(container.Top>=update.Bottom,"Update notification overlaps workspace");
                var open=(Control)Get(update,"_open");var later=(Control)Get(update,"_later");var message=(Control)Get(update,"_message");
                Require(!open.Bounds.IntersectsWith(later.Bounds)&&!message.Bounds.IntersectsWith(open.Bounds),"Update message and actions overlap");
                Require(later.Right<=later.Parent!.ClientSize.Width,"Hide update button clipped");
            }
            update.Visible=false;form.PerformLayout();
            Require(Get(form,"_updateTask") is null,"Offscreen constructor unexpectedly checked GitHub");
            var workspaceControl=(Control)workspace;_ = workspaceControl.Handle;
            foreach(var size in new[]{new Size(900,640),new Size(1280,820)})
            {
                form.Size=size;form.PerformLayout();
                workspace.GetType().GetMethod("ToggleDrawer",Private)!.Invoke(workspace,new object[]{3,true});
                workspaceControl.PerformLayout();
                var preview=(PictureBox)Get(workspace,"_screenPreview");var enabled=(Control)Get(workspace,"_previewEnabled");var status=(Control)Get(workspace,"_previewState");
                preview.Parent!.PerformLayout();
                Require(preview.Width>120&&preview.Height>60,"Screen preview collapsed at supported window size");
                Require(enabled.Bottom<=preview.Top&&preview.Bottom<=status.Top,"Screen preview overlaps controls or status");
                Require(status.Right<=status.Parent!.ClientSize.Width,"Preview status is clipped");
            }
            using(var bitmap=new Bitmap(32,24))
            using(var stream=new MemoryStream())
            {
                bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Png);
                workspace.GetType().GetMethod("SetScreenPreview",Private)!.Invoke(workspace,new object[]{"data:image/png;base64,"+Convert.ToBase64String(stream.ToArray())});
                Require(((PictureBox)Get(workspace,"_screenPreview")).Image is not null,"Local preview did not render frame");
            }
            workspace.GetType().GetMethod("DisableComputer",Private)!.Invoke(workspace,null);
            Require(((PictureBox)Get(workspace,"_screenPreview")).Image is null,"Revoking screen access retained its frame");
            var permissions=(ComboBox)Get(workspace,"_permissions");permissions.SelectedIndex=2;
            var executor=new Muse2Api.Agent.ToolExecutor(isolated);
            var approval=await executor.PrepareAsync(new("run_command",new(){["command"]="echo never executed"}),default);
            var confirm=(Task<bool>)workspace.GetType().GetMethod("ConfirmTool",Private)!.Invoke(workspace,new object[]{approval,CancellationToken.None})!;
            Require(await confirm.WaitAsync(TimeSpan.FromSeconds(3)),"Turbo still waits for command approval");
            permissions.SelectedIndex=1;
            confirm=(Task<bool>)workspace.GetType().GetMethod("ConfirmTool",Private)!.Invoke(workspace,new object[]{approval,CancellationToken.None})!;
            Require(!await confirm.WaitAsync(TimeSpan.FromSeconds(3)),"Read-only mode allowed a command");
            Console.WriteLine("PASS: goal/screen preview fits minimum and normal sizes; revocation clears frames; Turbo approves commands without dialogs; read-only still blocks; no visible windows");
            var queue=(System.Collections.Concurrent.ConcurrentQueue<Muse2Api.Agent.AgentEvent>)Get(workspace,"_events");
            const string notice="Chưa thực hiện đủ thao tác. Thông báo dài chỉ xuất hiện một lần; xem Terminal để biết chi tiết.";
            queue.Enqueue(new("assistant_start","Đang suy nghĩ…"));
            queue.Enqueue(new("context_compacted","Đã nén ngữ cảnh nền"));
            queue.Enqueue(new("incomplete",notice));
            workspace.GetType().GetMethod("DrainEvents",Private)!.Invoke(workspace,null);
            var chat=(ChatTranscript)Get(workspace,"_chat");
            var cards=(List<ChatCard>)Get(chat,"_cards");
            var thinking=cards.OfType<ThinkingCard>().Single();
            Require(!thinking.IsActive&&((Label)Get(thinking,"_title")).Text.Length<60,"Failure duplicated its full notice in the thinking title");
            Require(cards.OfType<NoticeCard>().Count()==1,"Failure notice was duplicated");
            Require(cards.Count==2,"Context compaction added another thinking/status/chat card");
            Require(((RichTextBox)Get(workspace,"_console")).Text.Contains("Đã nén ngữ cảnh nền"),"Context compaction was not logged in Terminal");
            Require(((RichTextBox)Get(workspace,"_console")).Text.Contains(notice),"Failure details lost from terminal");
            var nativeComputer=(Muse2Api.Computer.DesktopComputer)Get(workspace,"_computer");
            nativeComputer.BeginTurn();
            var windowSource=nativeComputer.GetType().GetProperty("WindowSource",Private)!;
            windowSource.SetValue(nativeComputer,(Func<IReadOnlyList<Muse2Api.Computer.DesktopWindow>>)(()=>throw new InvalidOperationException("Read-only must not enumerate or auto-authorize windows")));
            var denied=await nativeComputer.ExecuteAsync(new("computer_inspect",new()),default).WaitAsync(TimeSpan.FromSeconds(3));
            Require(denied.Text.StartsWith("error: user declined")&&nativeComputer.Target is null,"Screen tools bypassed read-only mode");
            windowSource.SetValue(nativeComputer,null);
            var longAnswer=chat.AddAssistantMessage(new string('x',2000),stream:true);chat.CompletePendingResponse();
            Require(!((System.Windows.Forms.Timer)Get(longAnswer,"_streamTimer")).Enabled,"Old answer kept typing into the next turn");
            var fixtureSession=new Muse2Api.Agent.ChatSession {Workspace=isolated,Events=new(){
                new("user","Kiểm tra tệp"),new("tool_start","npm test","run_command"),new("tool_result","exit_code=1\nstdout:\n1 failed","run_command"),new("incomplete","Test chưa đạt"),
                new("user","Chờ tiến trình"),new("tool_start","poll abc","process_status"),new("tool_result","running=True\noutput (latest 32000 chars):\n","process_status"),
                new("assistant","```json\n{\"status\":\"needs_input\",\"summary\":\"Bạn chọn thư mục nào?\"}\n```"),new("waiting_input","Chờ bạn trả lời")}};
            var sessionStore=(Muse2Api.Agent.ChatSessionStore)Get(workspace,"_store");await sessionStore.SaveAsync(fixtureSession);
            workspace.GetType().GetField("_tools",Private)!.SetValue(workspace,executor);
            await ((Task)workspace.GetType().GetMethod("LoadSession",Private)!.Invoke(workspace,new object[]{fixtureSession.Id})!);
            var restored=((List<ChatCard>)Get(chat,"_cards")).OfType<ToolCallCard>().ToArray();
            Require(restored.Length==2,"History tool order changed");
            Require(((Label)Get(restored[0],"_status")).Text.Contains("Thất bại"),"History changed a failed tool into success");
            var runningStatus=(Label)Get(restored[1],"_status");
            Require(runningStatus.Text.Contains("Đang chạy")&&runningStatus.Width>=TextRenderer.MeasureText(runningStatus.Text,runningStatus.Font).Width,"Running process was marked complete or clipped");
            var finalAnswer=((List<ChatCard>)Get(chat,"_cards")).OfType<ChatMessageCard>().Last();
            Require(((RichTextBox)Get(finalAnswer,"_body")).Text.Contains("Bạn chọn thư mục nào?")&&!((RichTextBox)Get(finalAnswer,"_body")).Text.Contains("needs_input"),"History exposed planner JSON instead of its question");
            Console.WriteLine("PASS: read-only screen request blocked before auto-selection; old streaming answer finishes before next turn; replay preserves failed/running tool state and readable questions");
            Require(!form.Visible,"A test form became visible");
            Console.WriteLine("PASS: preview defaults to a separate profile, all desktop paths honor override, no visible forms");
            Console.WriteLine("PASS: update banner fits minimum/normal sizes, does not overlap chat, and construction sends no update request");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MUSE_DESKTOP_PROFILE_ROOT",previousOverride);
            if(previousMarker is null)File.Delete(marker);else File.WriteAllBytes(marker,previousMarker);
            if(Directory.Exists(isolated))Directory.Delete(isolated,true);
        }
    }
    static async Task CheckUpdateSettings()
    {
        var type=typeof(MainForm).Assembly.GetType("Muse2Api.WinForms.UpdateSettingsControl")!;
        var calls=0;var opened=0;
        var available=new Muse2Api.AvailableUpdate("9.0.0",Muse2Api.DesktopUpdates.RepositoryUrl+"/commits/main/",Muse2Api.UpdateFeed.Source);
        Func<Task<Muse2Api.UpdateCheckResult>> check=async()=>{calls++;await Task.Delay(30);return new(Muse2Api.UpdateStatus.Available,"Có mã nguồn mới để kiểm tra.",available,true);};
        Action<Muse2Api.AvailableUpdate> open=_=>opened++;
        using var control=(Control)Activator.CreateInstance(type,new object?[]{check,open,null,Muse2Api.UpdateFeed.Source})!;
        control.Size=new Size(440,180);control.CreateControl();control.PerformLayout();
        var button=(Control)Get(control,"_check");
        var click=button.GetType().GetMethod("OnClick",Private)!;
        click.Invoke(button,new object[]{EventArgs.Empty});click.Invoke(button,new object[]{EventArgs.Empty});
        await Task.Delay(100);
        Require(calls==1&&button.Enabled,"Manual update click was duplicated or stayed disabled");
        Require(((Label)Get(control,"_status")).Text=="Có mã nguồn mới để kiểm tra.","Manual update result was not displayed");
        var openButton=(Control)Get(control,"_open");
        openButton.GetType().GetMethod("OnClick",Private)!.Invoke(openButton,new object[]{EventArgs.Empty});
        Require(opened==1,"Update link action was not routed to the host");
        Require(openButton.Text=="Xem mã nguồn","Source updates show the wrong action");
        Require(Application.OpenForms.Count==0,"Update settings check opened a window");
        Console.WriteLine("PASS: manual update check stays responsive, prevents duplicate clicks, shows source instructions, no browser/window opened");
    }
    static object Get(object o,string name)=>o.GetType().GetField(name,Private)!.GetValue(o)!;
    static void CheckComputerPicker()
    {
        var type=typeof(MainForm).Assembly.GetType("Muse2Api.WinForms.ComputerScopeForm")!;
        var listed=0;
        Func<IReadOnlyList<Muse2Api.Computer.DesktopWindow>> source=()=>{listed++;return new[]{new Muse2Api.Computer.DesktopWindow(11,101,"Trang thử nghiệm",1,"chrome"),new Muse2Api.Computer.DesktopWindow(22,102,"Ghi chú",2,"notepad")};};
        using var form=(Form)Activator.CreateInstance(type,new object[]{source})!;
        form.CreateControl();
        var search=(TextBox)Get(form,"_search");var windows=(ListBox)Get(form,"_windows");var allow=(Control)Get(form,"_allow");
        Require(windows.Items.Count==2&&!allow.Enabled,"Picker silently preselected a window");
        search.Text="CHROME";Require(windows.Items.Count==1&&listed==1,"Picker search failed or re-enumerated desktop on every keystroke");
        windows.SelectedIndex=0;Require(allow.Enabled,"Cannot authorize the selected window");
        search.Text="not-a-window";Require(windows.Items.Count==0&&!allow.Enabled,"Empty filter kept a stale target authorized");
        search.Text="";
        foreach(var size in new[]{new Size(640,520),new Size(900,650)})
        {
            form.Size=size;form.PerformLayout();
            Require(windows.Height>=50,"Window list lost usable height: client="+form.ClientSize+" rows="+string.Join(",",((TableLayoutPanel)windows.Parent!).GetRowHeights())+" list="+windows.Bounds);
            Require(allow.Right<=allow.Parent!.ClientSize.Width,"Picker permission action clipped");
            Require(windows.Bottom<=windows.Parent!.ClientSize.Height,"Window list escaped the layout");
        }
        Require(!form.Visible&&Application.OpenForms.Count==0,"Picker check opened a visible window");
        Console.WriteLine("PASS: screen picker search/selection/empty list/minimum layout; fake windows only, no visible UI");
    }
    static void Require(bool condition,string text){if(!condition)throw new Exception(text);}
}
