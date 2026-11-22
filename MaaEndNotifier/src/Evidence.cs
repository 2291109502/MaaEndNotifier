using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace MaaEndNotify {
    class Owner { public Plan Plan; public Subtask Task; public DateTime Started; }
    class ChildFailure { public Owner Owner; public string Entry; public DateTime At; }
    class AgentNote { public DateTime At; public string Text; public bool Collector; }
    class Evidence {
        readonly Catalog catalog;
        readonly Dictionary<string,List<Owner>> top = new Dictionary<string,List<Owner>>();
        readonly Dictionary<string,Owner> child = new Dictionary<string,Owner>();
        readonly List<ChildFailure> failures = new List<ChildFailure>();
        readonly Dictionary<Subtask,DateTime> hints = new Dictionary<Subtask,DateTime>();
        readonly Dictionary<Subtask,List<string>> failureHints = new Dictionary<Subtask,List<string>>();
        readonly List<AgentNote> notes = new List<AgentNote>();
        readonly DateTime from, until;
        static readonly Regex Negative = new Regex("fail|timeout|sanity_low_end|insufficient|not_enough|cancel|失败|錯誤|错误|无法|超时|不足|未完成|提前结束",RegexOptions.IgnoreCase|RegexOptions.Compiled);
        static readonly Regex Clock = new Regex(@"^\[?(\d{4}-\d\d-\d\d[ T]\d\d:\d\d:\d\d(?:\.\d{1,3})?)", RegexOptions.Compiled);
        static readonly Regex GzTime = new Regex(@"maafw\.bak\.(\d{4})\.(\d\d)\.(\d\d)-(\d\d)\.(\d\d)\.(\d\d)");
        public Evidence(IEnumerable<Plan> plans,Catalog catalog,string dir,Action<string> log) {
            this.catalog=catalog;
            var list=plans.ToList();
            if(list.Count==0) return;
            from=list.Min(p=>p.Begin).AddSeconds(-1);
            until=list.Max(p=>p.End==default(DateTime)?p.Limit:p.End.AddSeconds(1));
            foreach(var p in list) foreach(var t in p.Report.Tasks.Where(t=>!String.IsNullOrEmpty(t.Id))) {
                List<Owner> v;
                if(!top.TryGetValue(t.Id,out v)) top[t.Id]=v=new List<Owner>();
                v.Add(new Owner{Plan=p,Task=t});
            }
            var files=Directory.GetFiles(dir,"maafw*").Where(f=>Regex.IsMatch(Path.GetFileName(f),@"^maafw(?:\.bak\..+)?\.log(?:\.gz)?$",RegexOptions.IgnoreCase))
                .OrderBy(f=>Path.GetFileName(f).StartsWith("maafw.bak.")?0:1).ThenBy(f=>f,StringComparer.OrdinalIgnoreCase);
            foreach(var f in files) {
                var m=GzTime.Match(Path.GetFileName(f));
                if(m.Success && Plans.Stamp(m.Result("$1-$2-$3 $4:$5:$6")).AddSeconds(2)<from) continue;
                ReadAvailable(f,Framework,log);
            }
            // Agent streams are also mirrored in mxu-tauri, but per-child files rotate independently.
            foreach(string f in Directory.GetFiles(dir,"*.log*").Where(f=>Regex.IsMatch(Path.GetFileName(f),@"^(?:mxu-(?:agent|tauri).*\.log(?:\.bak|\.gz)?|go-service\.log(?:\.gz)?)$",RegexOptions.IgnoreCase)))
                ReadAvailable(f,Agent,log);
            AttachSubtasks();
        }
        static void ReadAvailable(string path,Action<string> fn,Action<string> log) {
            try { ReadLines(path,fn); }
            catch(FileNotFoundException) { } // Renamed during log rotation.
            catch(InvalidDataException) { log("无法完整读取压缩日志　"+Path.GetFileName(path)); }
        }
        static void ReadLines(string path,Action<string> fn) {
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.SequentialScan))
            using(var compressed=path.EndsWith(".gz",StringComparison.OrdinalIgnoreCase)?new GZipStream(file,CompressionMode.Decompress,true):null)
            using(var reader=new StreamReader((Stream)compressed??file,Data.Utf8,true,65536)) {
                string l; while((l=reader.ReadLine())!=null) fn(l);
            }
        }
        static DateTime Time(string line) {
            var m=Clock.Match(line);
            return m.Success?Plans.Stamp(m.Groups[1].Value.Replace('T',' ')):default(DateTime);
        }
        Owner OwnerAt(string id, DateTime at, string entry) {
            List<Owner> xs;
            if(String.IsNullOrEmpty(id)||!top.TryGetValue(id,out xs)) return null;
            Owner chosen=null;
            foreach(var x in xs) {
                if(!x.Plan.Covers(at) || (entry!="" && !String.IsNullOrEmpty(x.Task.Entry) && x.Task.Entry!=entry)) continue;
                if(chosen!=null) return null;
                chosen=x;
            }
            return chosen;
        }
        Owner FindParent(string id,DateTime at) {
            var own=OwnerAt(id,at,""); if(own!=null) return own;
            return child.TryGetValue(id,out own) && own.Plan.Covers(at) ? own : null;
        }
        void Framework(string l) {
            int k=l.IndexOf("[msg=",StringComparison.Ordinal); if(k<0) return;
            int e=l.IndexOf(']',k+5); if(e<0 || e-k>96) return;
            string message=l.Substring(k+5,e-k-5);
            bool task=message.StartsWith("Tasker.Task.",StringComparison.Ordinal);
            bool ctx=message.StartsWith("Context.Task.",StringComparison.Ordinal);
            bool nodeFailed=message=="Node.Action.Failed" || message=="Node.PipelineNode.Failed";
            bool hasFocus=!task && !ctx && l.IndexOf("\""+message+"\"",e,StringComparison.Ordinal)>=0;
            if(!task && !ctx && !nodeFailed && !hasFocus) return;
            var at=Time(l); if(at<from || at>until) return;
            int j=l.IndexOf("[details={",e,StringComparison.Ordinal); if(j<0) return;
            int stop=l.LastIndexOf("}]",StringComparison.Ordinal); if(stop<j+9) return;
            object d;
            try { d=Data.Decode<object>(l.Substring(j+9,stop-j-8)); } catch(ArgumentException) { return; } catch(InvalidOperationException) { return; }
            string id=Data.Str(d,"task_id"), entry=Data.Str(d,"entry"), parent=Data.Str(d,"parent_task_id");
            if(task) {
                var own=OwnerAt(id,at,entry); if(own==null) return;
                var t=own.Task;
                if(String.IsNullOrEmpty(t.Entry) && entry!="") { t.Entry=entry; t.Name=catalog.Name(entry); }
                if(message.EndsWith(".Starting")) { if(t.Status=="等待") t.Status="执行中"; own.Started=at; return; }
                if(!message.EndsWith(".Failed") && !message.EndsWith(".Succeeded") && !message.EndsWith(".Cancelled")) return;
                string why=Data.Str(d,"reason"), text=Data.Clean(Data.Str(d,"error_message","error","errmsg"));
                if(why=="skipped" || why=="not_started") t.Status="已跳过";
                else if(why=="cancelled" || why=="canceled" || message.EndsWith(".Cancelled")) t.Status="提前结束";
                else if(message.EndsWith(".Failed")) t.Status="失败";
                else t.Status="完成";
                t.Reason=t.Status=="完成"?"":Data.First(text,Reason(why));
                own.Plan.Terminal[id]=at; if(at>own.Plan.LastResult) own.Plan.LastResult=at;
                return;
            }
            Owner owner=parent!="" && parent!="0" ? FindParent(parent,at) : null;
            owner=owner??FindParent(id,at);
            if(owner==null) return;
            if(ctx) {
                if(message.EndsWith(".Starting")) child[id]=owner;
                if(message=="Context.Task.Succeeded" && Data.Str(d,"reason")!="cancelled") failures.RemoveAll(x=>x.Owner==owner && x.Entry==entry && x.At<at);
                if(message=="Context.Task.Failed" && entry!="" && !failures.Any(x=>x.Owner==owner && x.Entry==entry && Math.Abs((at-x.At).TotalMilliseconds)<200))
                    failures.Add(new ChildFailure{Owner=owner,Entry=entry,At=at});
                return;
            }
            if(nodeFailed && id==owner.Task.Id) owner.Task.Node=Data.Str(d,"name");
            if(!hasFocus) return;
            string hint=catalog.Focus(d,message); if(hint=="") return;
            owner.Task.Hint=Data.Clip(hint,350); hints[owner.Task]=at;
            object tmpl=Data.Get(Data.Get(d,"focus"),message);
            string raw=tmpl as string??Data.Str(tmpl,"content");
            if(Negative.IsMatch(Data.First(raw,"")+" "+hint) || (nodeFailed && !String.IsNullOrWhiteSpace(raw))) {
                List<string> a; if(!failureHints.TryGetValue(owner.Task,out a)) failureHints[owner.Task]=a=new List<string>();
                if(!a.Contains(hint)) a.Add(hint);
            }
        }
        static string Reason(string why) {
            switch(why) {
                case "cancelled": case "canceled": return "框架记录了提前结束（cancelled）。";
                case "action_failed": return "动作执行失败。";
                case "recognition_timeout": return "识别超时。";
                case "timeout": return "任务超时。";
                case "skipped": case "not_started": return "软件记录此项被跳过。";
                case "unknown": case "": return "软件未记录具体原因。";
                default: return "软件记录　"+Data.Clean(why);
            }
        }
        void Agent(string line) {
            if(line.IndexOf("[telemetry] task failure ",StringComparison.Ordinal)>=0) {
                var logTime=Time(line);
                var m=Regex.Match(line,@"\btask_id=(\d+)\b");
                var own=m.Success?OwnerAt(m.Groups[1].Value,logTime,""):null;
                if(own!=null && !own.Plan.Terminal.ContainsKey(own.Task.Id)) {
                    own.Task.Status="失败"; own.Task.Reason="MXU 已记录该项失败，框架日志缺少具体原因。";
                    own.Plan.Terminal[own.Task.Id]=logTime;
                    if(logTime>own.Plan.LastResult) own.Plan.LastResult=logTime;
                }
                return;
            }
            bool collector=line.IndexOf("FailureCollector subtask failed",StringComparison.Ordinal)>=0;
            bool native=line.IndexOf("][ERR]",StringComparison.Ordinal)>=0 || line.IndexOf("][ERROR]",StringComparison.Ordinal)>=0;
            if(!collector && !native) return;
            DateTime at; string msg;
            if(line.StartsWith("{")) {
                if(!collector) return;
                object d; try{d=Data.Decode<object>(line);}catch(ArgumentException){return;}
                var when=Data.Str(d,"time"); if(when.Length<19) return;
                at=Plans.Stamp(when.Substring(0,19).Replace('T',' ')); msg=Data.Str(d,"task");
            } else {
                at=Time(line);
                string clean=Regex.Replace(line,@"\x1b\[[\d;]*m","");
                if(collector) {
                    var m=Regex.Match(clean,@"\btask=([^\s,]+)"); if(!m.Success) return;
                    msg=m.Groups[1].Value;
                } else {
                    var m=Regex.Match(clean,@"\[(?:ERR|ERROR)\](?:\[[^\]]*\])+\s+(.+?)(?:\s+\[|$)");
                    if(!m.Success) return; msg=ReadableError(Data.Clean(m.Groups[1].Value));
                    var duration=Regex.Match(clean,@"(?:stalled_ms|dwell_ms)=(\d+)");
                    long ms;
                    if(duration.Success && Int64.TryParse(duration.Groups[1].Value,out ms)) msg=msg.TrimEnd('。')+"（约 "+Math.Round(ms/1000.0)+" 秒）。";
                    msg=Data.Clip(msg,340);
                }
            }
            if(at<from || at>until || String.IsNullOrWhiteSpace(msg)) return;
            if(!notes.Any(x=>x.At==at && x.Text==msg)) notes.Add(new AgentNote{At=at,Text=msg,Collector=collector});
        }
        void AttachSubtasks() {
            foreach(var f in failures) {
                // A nested recognition miss is common. The collector explicitly records failed business subitems.
                if(!notes.Any(n=>n.Collector && n.Text==f.Entry && Math.Abs((n.At-f.At).TotalSeconds)<1.25)) continue;
                var t=f.Owner.Task;
                string name=catalog.Name(f.Entry);
                var at=f.At;
                var others=failures.Where(x=>Math.Abs((x.At-at).TotalSeconds)<1.25).Select(x=>x.Owner).Distinct().Count();
                var activeAtTime=top.Values.SelectMany(v=>v).Where(o=>o.Started!=default(DateTime) && o.Started<=at && o.Plan.Covers(at) &&
                    (!o.Plan.Terminal.ContainsKey(o.Task.Id)||o.Plan.Terminal[o.Task.Id]>=at)).Count();
                // Unlabelled native stdout only belongs here if there was a single candidate in this time window.
                var errors=(others==1 && activeAtTime==1)?notes.Where(n=>!n.Collector && Math.Abs((n.At-at).TotalSeconds)<1.25).Select(n=>n.Text).Distinct().ToList():new List<string>();
                string detail=name+"　"+(errors.Count==0?"软件将此子项记为失败。":String.Join(" / ",errors));
                if(!t.Details.Contains(detail)) t.Details.Add(detail);
            }
            foreach(var o in top.Values.SelectMany(x=>x)) {
                var t=o.Task;
                if(t.Status=="完成" && t.Details.Count>0) { t.Status="部分完成"; t.Reason="主任务已结束，软件另有未完成子项记录。"; }
                if(t.Status=="完成" || t.Status=="等待" || t.Status=="执行中") continue;
                List<string> specific;
                if(failureHints.TryGetValue(t,out specific)) {
                    foreach(var hint in specific.Where(x=>!x.StartsWith("$"))) {
                        if(t.Details.Any(d=>d.Contains("　") && hint.StartsWith(d.Substring(0,d.IndexOf('　')),StringComparison.Ordinal))) continue;
                        if(!t.Details.Contains(hint)) t.Details.Add(hint);
                    }
                    if(t.Details.Count==0 && specific.Count>0) t.Details.Add("日志提示键　"+specific.Last());
                } else {
                    DateTime hintTime,end;
                    if(!String.IsNullOrEmpty(t.Hint) && hints.TryGetValue(t,out hintTime) && o.Plan.Terminal.TryGetValue(t.Id,out end) &&
                        (end-hintTime).Duration()<TimeSpan.FromSeconds(4)) t.Details.Add("结束前提示　"+t.Hint);
                }
                if(t.Details.Count==0 && !String.IsNullOrEmpty(t.Node)) t.Details.Add("失败节点　"+t.Node);
                if(t.Details.Count>0 && t.Reason=="软件未记录具体原因。") t.Reason="";
            }
        }
        static string ReadableError(string msg) {
            if(msg.StartsWith("River-fall recovery made no net progress past the timeout",StringComparison.OrdinalIgnoreCase))
                return "落水后的恢复动作超时，位置一直没有进展，软件终止导航。";
            if(msg.StartsWith("Dwell watchdog tripped",StringComparison.OrdinalIgnoreCase) || msg.StartsWith("Agent stayed inside the dwell radius",StringComparison.OrdinalIgnoreCase))
                return "长时间停留在同一小片区域，软件终止导航。";
            return msg;
        }
    }
}
