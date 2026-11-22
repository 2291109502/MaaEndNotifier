using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MaaEndNotify {
    static class Program {
        internal static readonly string Home=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MaaEndDingTalk");
        [STAThread] static int Main(string[] args) {
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            if(args.Length==3 && args[0]=="--replay") {
                try {
                    var root=Catalog.NormalRoot(args[1]); string dest=Path.GetFullPath(args[2]);
                    var s=new MonitorService(root,dest,DateTime.MinValue,"MaaEnd");
                    int n=s.Scan(true,true,false,DateTime.Now,Console.WriteLine);
                    return n>0?0:2;
                } catch(Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            }
            bool first;
            using(var single=new Mutex(true,@"Local\MaaEndDingTalk.Observer.1",out first)) {
                if(!first) { if(!args.Contains("--tray")) MessageBox.Show("监控程序已经运行，可双击通知区域中的图标打开。","MaaEnd 钉钉通知"); return 0; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                try { Application.Run(new MainWindow(args.Contains("--tray"))); }
                catch(Exception ex) { MessageBox.Show(ex.Message,"MaaEnd 钉钉通知"); return 1; }
            }
            return 0;
        }
    }
    class MainWindow : Form {
        readonly TextBox folder=new TextBox(), hook=new TextBox(), secret=new TextBox(), keyword=new TextBox();
        readonly CheckBox startup=new CheckBox{Text="登录 Windows 后启动",AutoSize=true};
        readonly ListBox feed=new ListBox{Dock=DockStyle.Fill,HorizontalScrollbar=true};
        readonly Label status=new Label{AutoSize=true};
        readonly Button start=new Button(), test=new Button(), replay=new Button();
        readonly Button bundled=new Button{Text="使用随包机器人",AutoSize=true};
        int timeoutMs=10000;
        readonly NotifyIcon tray=new NotifyIcon();
        readonly string cfg=Path.Combine(Program.Home,"settings.json");
        readonly string history=Path.Combine(Program.Home,"history");
        CancellationTokenSource stop;
        Task worker;
        bool quitting, updating, busyReplay;
        Options settings=new Options();
        static Label LabelOf(string s) { return new Label{Text=s,AutoSize=true,Anchor=AnchorStyles.Left}; }
        public MainWindow(bool startHidden) {
            Text="MaaEnd 计划通知"; Icon=SystemIcons.Information; Font=new Font("Microsoft YaHei UI",9);
            StartPosition=FormStartPosition.CenterScreen; MinimumSize=new Size(670,495); Size=new Size(760,530);
            var grid=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=3,RowCount=9};
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for(int i=0;i<8;i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var title=LabelOf("MaaEnd 计划结束通知"); title.Font=new Font(Font.FontFamily,15,FontStyle.Bold);
            grid.Controls.Add(title,0,0);grid.SetColumnSpan(title,3);
            AddRow(grid,1,"MaaEnd 目录",folder);var choose=new Button{Text="选择…",AutoSize=true};
            choose.Click+=(s,e)=>{using(var dlg=new FolderBrowserDialog{Description="选择 MaaEnd 的安装目录或 debug 日志目录"}) if(dlg.ShowDialog(this)==DialogResult.OK) folder.Text=dlg.SelectedPath;}; grid.Controls.Add(choose,2,1);
            AddRow(grid,2,"钉钉 Webhook",hook);grid.SetColumnSpan(hook,2);hook.UseSystemPasswordChar=true;
            AddRow(grid,3,"加签密钥（如有）",secret);grid.SetColumnSpan(secret,2);secret.UseSystemPasswordChar=true;
            AddRow(grid,4,"机器人关键词",keyword);grid.SetColumnSpan(keyword,2);
            grid.Controls.Add(startup,1,5);
            var bar=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true};
            start.Text="保存并开始监控"; start.AutoSize=true; start.Click+=async(s,e)=>{if(stop!=null) await Pause(); else StartMonitor(true);};bar.Controls.Add(start);
            test.Text="发送测试消息";test.AutoSize=true;test.Click+=async(s,e)=>await Test();bar.Controls.Add(test);
            bundled.Enabled=File.Exists(RobotConfig.BundledPath);bundled.Click+=(s,e)=>{try{UseBundled();Log("已载入随包机器人，点击保存后生效。");}catch(Exception ex){Log(Data.Clean(ex.Message));}};bar.Controls.Add(bundled);
            replay.Text="离线分析已有日志";replay.AutoSize=true;replay.Click+=async(s,e)=>await Replay();bar.Controls.Add(replay);
            var open=new Button{Text="打开记录目录",AutoSize=true};open.Click+=(s,e)=>{Directory.CreateDirectory(Program.Home);Process.Start("explorer.exe","\""+Program.Home+"\"");};bar.Controls.Add(open);
            var leave=new Button{Text="退出",AutoSize=true}; leave.Click+=(s,e)=>End();bar.Controls.Add(leave);
            grid.Controls.Add(bar,0,6);grid.SetColumnSpan(bar,3);grid.Controls.Add(status,0,7);grid.SetColumnSpan(status,3);
            grid.Controls.Add(feed,0,8);grid.SetColumnSpan(feed,3);Controls.Add(grid);
            var menu=new ContextMenuStrip();menu.Items.Add("打开监控窗口",null,(s,e)=>Restore());menu.Items.Add("退出监控",null,(s,e)=>End());
            tray.Text=Text;tray.Icon=SystemIcons.Information;tray.ContextMenuStrip=menu;tray.Visible=true;tray.DoubleClick+=(s,e)=>Restore();
            FormClosing+=(s,e)=>{if(!quitting && stop!=null && e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}else {if(stop!=null)stop.Cancel();tray.Visible=false;}};
            Resize+=(s,e)=>{if(WindowState==FormWindowState.Minimized && stop!=null) Hide();};
            startup.CheckedChanged+=(s,e)=>{if(!updating) SetStartup();};
            Shown+=(s,e)=>{
                try {
                    Directory.CreateDirectory(Program.Home);
                    if(File.Exists(cfg)) settings=Data.Decode<Options>(Data.Read(cfg));
                    folder.Text=settings.Folder??""; hook.Text=Options.Open(settings.ProtectedHook);secret.Text=Options.Open(settings.ProtectedSecret);
                    keyword.Text=settings.Keyword??"MaaEnd";
                    timeoutMs=settings.TimeoutMs>0?settings.TimeoutMs:10000;
                    if(String.IsNullOrEmpty(settings.ProtectedHook) && File.Exists(RobotConfig.BundledPath)) UseBundled();
                    updating=true; using(var k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                        startup.Checked=k!=null && k.GetValue("MaaEndDingTalkObserver")!=null;
                    updating=false;
                    if(settings.Enabled) StartMonitor(false); else status.Text="尚未开始监控";
                    if(startHidden && stop!=null) Hide();
                }catch(Exception ex){updating=false;status.Text="读取设置失败，请重新填写。";Log(ex.Message);}
            };
        }
        static void AddRow(TableLayoutPanel grid,int n,string label,TextBox box) { grid.Controls.Add(LabelOf(label),0,n);box.Dock=DockStyle.Fill;grid.Controls.Add(box,1,n); }
        void UseBundled() {
            var config=RobotConfig.Load(RobotConfig.BundledPath);
            hook.Text=config.Hook; secret.Text=config.Secret; keyword.Text=config.Keyword; timeoutMs=config.TimeoutMs;
        }
        void Restore(){Show();WindowState=FormWindowState.Normal;Activate();}
        async void End(){quitting=true; if(stop!=null){stop.Cancel(); if(worker!=null) await worker;}Close();}
        void SetStartup() {
            try {
                using(var k=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
                    if(startup.Checked) k.SetValue("MaaEndDingTalkObserver","\""+Application.ExecutablePath+"\" --tray");
                    else k.DeleteValue("MaaEndDingTalkObserver",false);
                }
            } catch(Exception ex){updating=true;startup.Checked=!startup.Checked;updating=false;Log("登录启动设置失败　"+ex.Message);}
        }
        void Log(string message) {
            if(IsDisposed || Disposing || !IsHandleCreated) return;
            if(InvokeRequired){try{BeginInvoke(new Action<string>(Log),message);}catch(InvalidOperationException){}return;}
            string text=DateTime.Now.ToString("HH:mm:ss")+"  "+message;
            if(feed.Items.Count>0 && ((string)feed.Items[0]).EndsWith("  "+message,StringComparison.Ordinal))return;
            feed.Items.Insert(0,text);while(feed.Items.Count>120)feed.Items.RemoveAt(feed.Items.Count-1);
        }
        void ControlsRunning(bool on){
            start.Text=on?"暂停监控":"保存并开始监控";folder.ReadOnly=hook.ReadOnly=secret.ReadOnly=keyword.ReadOnly=on;
            bundled.Enabled=!on && File.Exists(RobotConfig.BundledPath);
            var g=folder.Parent as TableLayoutPanel;
            if(g!=null)g.GetControlFromPosition(2,1).Enabled=!on;
            status.Text=on?"监控中 · 关闭窗口可收起到通知区域":"已暂停";
        }
        void StartMonitor(bool save) {
            try {
                string r=Catalog.NormalRoot(folder.Text),url=hook.Text.Trim(),sec=secret.Text.Trim(),key=keyword.Text.Trim();
                if(key.Length>80) throw new ArgumentException("请将关键词缩短到 80 字以内。");
                var robot=RobotConfig.FromFields(url,sec,key,timeoutMs);
                var newSettings=new Options{Folder=r,ProtectedHook=Options.Seal(url),ProtectedSecret=Options.Seal(sec),Keyword=key,TimeoutMs=timeoutMs,Enabled=true};
                DateTime since=MonitorService.LoadCutoff(r,Program.Home,DateTime.Now);
                var service=new MonitorService(r,history,since,key);
                if(save) Data.Save(cfg,Data.Encode(newSettings));
                settings=newSettings;stop=new CancellationTokenSource();var cancel=stop.Token;
                ControlsRunning(true);Log("开始监控　"+service.DebugDir);
                worker=Task.WhenAll(Task.Run(()=>{
                    while(!cancel.IsCancellationRequested) {
                        try{service.Tick(Log);}catch(Exception ex){Log("读取日志遇到问题，将重试　"+Data.Clean(ex.Message));}
                        cancel.WaitHandle.WaitOne(1800);
                    }
                }),Task.Run(()=>{
                    SqlQueue queue=null;
                    while(!cancel.IsCancellationRequested) {
                        try {
                            if(queue==null) { var q=Archive.QueueFor(history); Archive.Migrate(history,q,Log); queue=q; }
                            Sender.Flush(queue,robot,Log,null,1);
                        } catch(Exception ex) { Log("读取待发记录遇到问题　"+Data.Clean(ex.Message)); }
                        cancel.WaitHandle.WaitOne(4000);
                    }
                }));
            }catch(Exception ex){Log(Data.Clean(ex.Message));status.Text="尚未开始监控";}
        }
        async Task Pause(){
            if(stop==null)return;
            start.Enabled=false; stop.Cancel(); await worker;stop.Dispose(); stop=null;worker=null;
            settings.Enabled=false;
            try{Data.Save(cfg,Data.Encode(settings));}catch(Exception ex){Log("保存暂停状态失败　"+ex.Message);}
            ControlsRunning(false);start.Enabled=true;
        }
        async Task Test(){
            string url=hook.Text.Trim(),sec=secret.Text.Trim(),key=keyword.Text.Trim();
            test.Enabled=false;
            try { var config=RobotConfig.FromFields(url,sec,key,timeoutMs); await Task.Run(()=>config.Send(key+" MaaEnd　机器人连接测试。"));Log("钉钉接口已接收测试消息，请到群内确认。"); }
            catch(Exception ex){Log("发送测试失败　"+Data.Clean(ex.Message));}finally{test.Enabled=true;}
        }
        async Task Replay(){
            if(busyReplay)return;busyReplay=true;replay.Enabled=false;
            try {
                string root=Catalog.NormalRoot(folder.Text);
                string dest=Path.Combine(Program.Home,"replay",DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                Log("正在分析历史日志，只生成本地记录。");
                int n=await Task.Run(()=>new MonitorService(root,dest,DateTime.MinValue,"").Scan(true,true,false,DateTime.Now,Log));
                Log(n==0?"没有找到可结算的计划。请确认目录中有 MXU 的日期日志和 maafw 日志。":"已生成 "+n+" 份本地记录　"+dest);
                if(n>0) Process.Start("explorer.exe","\""+dest+"\"");
            }catch(Exception ex){Log("分析失败　"+Data.Clean(ex.Message));}finally{busyReplay=false;replay.Enabled=true;}
        }
    }
}
