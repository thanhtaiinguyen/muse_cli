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
        CheckPreviewIsolation();
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

    static void CheckPreviewIsolation()
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
            Require(!form.Visible,"A test form became visible");
            Console.WriteLine("PASS: preview defaults to a separate profile, all desktop paths honor override, no visible forms");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MUSE_DESKTOP_PROFILE_ROOT",previousOverride);
            if(previousMarker is null)File.Delete(marker);else File.WriteAllBytes(marker,previousMarker);
            if(Directory.Exists(isolated))Directory.Delete(isolated,true);
        }
    }
    static object Get(object o,string name)=>o.GetType().GetField(name,Private)!.GetValue(o)!;
    static void Require(bool condition,string text){if(!condition)throw new Exception(text);}
}
