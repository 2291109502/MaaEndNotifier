using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MaaEndNotify {
    class Plan {
        public Run Report = new Run();
        public DateTime Begin, Limit = DateTime.MaxValue, End;
        public bool QueueSeen;
        public string Id = "", File, Stopped = "";
        public DateTime LastResult;
        public readonly Dictionary<string, DateTime> Terminal = new Dictionary<string,DateTime>();
        public bool AllTerminal { get { return Report.Count > 0 && Report.Tasks.Count == Report.Count && Report.Tasks.All(t => t.Id != null && Terminal.ContainsKey(t.Id)); } }
        public bool Covers(DateTime t) { return t >= Begin && t < Limit && (End == default(DateTime) || t < End.AddSeconds(1)); }
        public void Close(DateTime t, string signal, string reason) { End=t; Report.End=Plans.Show(t); Report.Signal=signal; Report.PlanReason=reason; }
    }
    class Mark { public DateTime At; public string Source, Id; public bool Used; }
    class FrontEvent { public DateTime At; public string Source, Body; public int Index; }
    class Slot { public DateTime At; public string Source, Entry, Name; public int Index; }
    class Launch { public DateTime At; public string Source, Id; public int Count; }
    class Plans {
        public readonly List<Plan> All = new List<Plan>();
        public readonly List<Mark> Marks = new List<Mark>();
        readonly List<Slot> slots = new List<Slot>();
        readonly List<Launch> launches = new List<Launch>();
        readonly Catalog catalog;
        readonly string root;
        public static bool UiLog(string path) {
            return Regex.IsMatch(Path.GetFileName(path), @"^(?:\d{4}-\d\d-\d\d(?:-\d+)?|mxu-web(?:[-_].*)?)\.log$",RegexOptions.IgnoreCase);
        }
        public static string Show(DateTime d) { return d.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture); }
        public static DateTime Stamp(string s) {
            DateTime d;
            return DateTime.TryParseExact(s, new [] {"yyyy-MM-dd HH:mm:ss","yyyy-MM-dd HH:mm:ss.FFF"},
                CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : default(DateTime);
        }
        static readonly Regex Front = new Regex(@"^\[?(\d{4}-\d\d-\d\d[ T]\d\d:\d\d:\d\d)(?:\.\d+)?\]?\s*(?:\[?\w+\]?\s*)?(\[(?:Task|MAA|App)\].*)$",RegexOptions.Compiled);
        static readonly Regex Instance = new Regex(@"^\[Task\]\s*实例\s+(.+?)[:：]\s*(.*)$");
        static readonly Regex Begin = new Regex(@"^开始执行(?:任务\s*[,，]?\s*数量\s*[:：]\s*(\d+)|(\d+)\s*个任务)");
        static readonly Regex Post = new Regex(@"^任务已提交.*?(?:task_ids|taskIds)\s*[:：]\s*\[([\d\s,]*)\]");
        static readonly Regex LaunchLine = new Regex(@"^\[MAA\]\s*启动任务\s*[,，]\s*实例\s*[:：]\s*([^,，\s]+)\s*[,，]\s*任务数\s*[:：]\s*(\d+)");
        static readonly Regex SlotLine = new Regex(@"^\[MAA\]\s*任务\[(\d+)\]\s*[:：]\s*entry=([^,，\s]+)");
        static readonly Regex StopLine = new Regex(@"^(?:用户(?:手动)?(?:停止|取消)(?:了)?(?:运行|任务)|(?:所有)?任务(?:已停止|已取消)|停止任务成功|已停止(?:执行)?任务)");
        static readonly Regex FatalLine = new Regex(@"^(?:任务启动(?:异常|失败)|(?:预任务|前置程序)(?:执行)?(?:异常|失败)|(?:设备|控制器)(?:连接|重连)失败|(?:自动连接|加载资源|资源加载)失败)");
        public Plans(string folder, Catalog catalog, DateTime since) {
            this.catalog=catalog; root=folder;
            var events = new List<FrontEvent>(); string debug=Path.Combine(root,"debug");
            if (!Directory.Exists(debug)) return;
            foreach (string f in Directory.GetFiles(debug,"*.log").Where(UiLog).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)) {
                if (since != DateTime.MinValue && File.GetLastWriteTime(f) < since.AddDays(-1)) continue;
                using (var h = new FileStream(f,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                using (var r = new StreamReader(h,true)) {
                    string line; int index=0;
                    while ((line=r.ReadLine()) != null) {
                        index++;
                        if (line.IndexOf("[Task]",StringComparison.Ordinal)<0 && line.IndexOf("tasks-completed",StringComparison.Ordinal)<0 &&
                            line.IndexOf("[MAA]",StringComparison.Ordinal)<0) continue;
                        var m=Front.Match(line.Length>650 ? line.Substring(0,650) : line);
                        if (!m.Success) continue;
                        // Keep only the optional process basename, never arguments or the full pipeline override.
                        string body=m.Groups[2].Value;
                        if (SlotLine.IsMatch(body)) {
                            var name=SpecialName(line,body);
                            var s=SlotLine.Match(body); body=s.Value+(name.Length>0 ? "|"+name : "");
                        }
                        events.Add(new FrontEvent{At=Stamp(m.Groups[1].Value.Replace('T',' ')),Body=body,Source=f,Index=index});
                    }
                }
            }
            foreach (var e in events.OrderBy(x=>x.At).ThenBy(x=>x.Source,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.Index)) Apply(e);
            foreach (var p in All.Where(x=>x.QueueSeen)) Bind(p);
            // If two UI log files contain the same start (e.g. after export), keep one.
            var keys=new HashSet<string>();
            All.RemoveAll(p=>!keys.Add(p.Report.Key));
        }
        static string SpecialName(string raw, string body) {
            if (!body.Contains("entry=MXU_KILLPROC") && !body.Contains("entry=MXU_LAUNCH")) return "";
            try {
                int start=raw.IndexOf("pipelineOverride=",StringComparison.Ordinal);
                if (start<0) return "";
                foreach (var part in Data.Items(Data.Decode<object>(raw.Substring(start+17)))) {
                    var p=Data.Get(Data.Get(part,"MXU_KILLPROC","MXU_LAUNCH"),"custom_action_param");
                    string file=Data.Str(p,"process_name","program");
                    if (file!="") return Path.GetFileName(file.Replace('/','\\'));
                    if (Object.Equals(Data.Get(p,"kill_self"),true)) return "MaaEnd";
                }
            } catch (ArgumentException) {} catch (InvalidOperationException) {}
            return "";
        }
        Plan Last(string name) { return All.LastOrDefault(p=>p.Report.Instance==name && p.End==default(DateTime) && p.Limit==DateTime.MaxValue); }
        Plan New(FrontEvent e,string name,int count) {
            var p=new Plan{Begin=e.At,File=e.Source};
            p.Report.Instance=name; p.Report.Start=Show(e.At); p.Report.Count=count;
            p.Report.Key=RunKey(name,e.At);
            All.Add(p); return p;
        }
        string RunKey(string name,DateTime start) {
            return Data.Hash(root.TrimEnd('\\').ToUpperInvariant()+"|"+name+"|"+Show(start));
        }
        void Apply(FrontEvent e) {
            var m=Instance.Match(e.Body);
            if (m.Success) {
                var name=m.Groups[1].Value.Trim(); string s=m.Groups[2].Value;
                var p=Last(name); var b=Begin.Match(s);
                if (b.Success) {
                    int count=int.Parse(Data.First(b.Groups[1].Value,b.Groups[2].Value));
                    if (p!=null && p.QueueSeen) { p.Limit=e.At; p=null; }
                    if (p==null) p=New(e,name,count);
                    p.QueueSeen=true; p.Report.Count=count;
                    // The posting phase can follow a long pre-task; bind by its own timestamp.
                    p.Report.Start=Show(e.At); p.Begin=e.At; p.Report.Key=RunKey(name,e.At);
                } else if ((b=Post.Match(s)).Success) {
                    var ids=b.Groups[1].Value.Split(new[]{','},StringSplitOptions.RemoveEmptyEntries).Select(i=>i.Trim()).ToArray();
                    if (p==null) p=New(e,name,ids.Length);
                    p.QueueSeen=true;
                    for(int i=0;i<ids.Length;i++) {
                        while(p.Report.Tasks.Count<=i) p.Report.Tasks.Add(new Subtask());
                        p.Report.Tasks[i].Id=ids[i];
                    }
                } else if (s.StartsWith("执行预任务") || s.StartsWith("自动搜索设备并连接") || s.StartsWith("加载资源")) {
                    if(p!=null && p.QueueSeen) { p.Limit=e.At; p=null; }
                    if(p==null) New(e,name,-1);
                } else if (FatalLine.IsMatch(s) || StopLine.IsMatch(s)) {
                    if (p==null) p=New(e,name,-1);
                    p.QueueSeen=true;
                    p.Close(e.At,FatalLine.IsMatch(s)?"失败":"中止",Data.Clip(Data.Clean(s),480));
                }
                return;
            }
            if ((m=LaunchLine.Match(e.Body)).Success)
                launches.Add(new Launch{At=e.At,Source=e.Source,Id=m.Groups[1].Value,Count=int.Parse(m.Groups[2].Value)});
            else if ((m=SlotLine.Match(e.Body)).Success) {
                var label=e.Body.Split(new[]{'|'},2);
                slots.Add(new Slot{At=e.At,Source=e.Source,Index=int.Parse(m.Groups[1].Value),Entry=m.Groups[2].Value.Split('|')[0],Name=label.Length==2?label[1]:""});
            } else if (e.Body.StartsWith("[App]") && Regex.IsMatch(e.Body,@"\bkind\s*[:=]\s*tasks-completed\b")) {
                m=Regex.Match(e.Body,@"instance(?:Id|_id)\s*[:=]\s*[""']?([a-zA-Z0-9_-]+)");
                Marks.Add(new Mark{At=e.At,Source=e.Source,Id=m.Success?m.Groups[1].Value:""});
            }
        }
        void Bind(Plan p) {
            var loc=launches.Where(l=>l.Source==p.File && Math.Abs((l.At-p.Begin).TotalSeconds)<=5 && l.Count==p.Report.Count).ToList();
            string configId;
            var snapshot=catalog.Selected(p.Report.Instance,p.Report.Count,p.Begin,out configId);
            p.Id=Data.First(loc.Count==1?loc[0].Id:"",configId); p.Report.InstanceId=p.Id;
            if(p.Report.Count<0 && snapshot.Count>0) p.Report.Count=snapshot.Count;
            for (int i=0;i<p.Report.Count;i++) {
                while(p.Report.Tasks.Count<=i) p.Report.Tasks.Add(new Subtask());
                var t=p.Report.Tasks[i];
                var matches=slots.Where(s=>s.Source==p.File && s.Index==i && Math.Abs((s.At-p.Begin).TotalSeconds)<=5 &&
                    All.Count(x=>x.QueueSeen && x.File==p.File && Math.Abs((s.At-x.Begin).TotalSeconds)<=5)==1).ToList();
                Slot slot=matches.Count==1?matches[0]:null;
                var saved=i<snapshot.Count?snapshot[i]:null;
                t.Entry=slot!=null?slot.Entry:saved==null?"":saved.Entry;
                t.Name=saved!=null && saved.Entry==t.Entry?saved.Name:catalog.Name(t.Entry);
                if (slot!=null && slot.Name!="") t.Name += "（"+slot.Name+"）";
                if (String.IsNullOrEmpty(t.Name)) t.Name="第 "+(i+1)+" 个勾选项";
            }
        }
        public void ResolveMarks() {
            foreach(var mark in Marks.Where(m=>!m.Used).OrderBy(m=>m.At)) {
                var possible=All.Where(p=>p.QueueSeen && p.End==default(DateTime) && p.Covers(mark.At) &&
                    (mark.Id=="" || mark.Id==p.Id)).ToList();
                if(possible.Count>1) possible=possible.Where(p=>p.AllTerminal && p.LastResult<mark.At.AddSeconds(1)).ToList();
                if(possible.Count==1) { possible[0].Close(mark.At,"结束",null); mark.Used=true; }
            }
        }
        public void CloseRuns(bool offline, bool processExited, DateTime now) {
            ResolveMarks();
            foreach(var p in All.Where(x=>x.QueueSeen && x.End==default(DateTime))) {
                if(p.AllTerminal && (offline || now-p.LastResult>TimeSpan.FromSeconds(12))) p.Close(p.LastResult,"结束",null);
                else if(p.Limit!=DateTime.MaxValue) p.Close(p.Limit,"待确认","同一实例已启动下一轮，上一轮缺少结束记录。");
                else if(processExited && !offline) p.Close(now,"待确认","MaaEnd 已退出，日志中缺少本轮结束记录。");
                else if(offline) {
                    p.Close(p.Begin,"待确认","这份日志中没有计划结束记录，以下为已能核实的结果。");
                    p.Report.End="未记录";
                }
            }
        }
    }
}
