using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MaaEndNotify {
    static class Data {
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 180 }; }
        internal static string Encode(object o) { return Json().Serialize(o); }
        internal static T Decode<T>(string text) { return Json().Deserialize<T>(text); }
        internal static IDictionary<string, object> Map(object o) { return o as IDictionary<string, object>; }
        internal static object Get(object o, params string[] keys) {
            var map = Map(o);
            if (map != null) foreach (var k in keys) { object v; if (map.TryGetValue(k, out v) && v != null) return v; }
            return null;
        }
        internal static string Str(object o, params string[] keys) {
            var v = Get(o, keys);
            return v == null || v is IDictionary || (v is IEnumerable && !(v is string)) ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        internal static IEnumerable<object> Items(object o) {
            var e = o as IEnumerable;
            return e == null || o is string || o is IDictionary ? Enumerable.Empty<object>() : e.Cast<object>();
        }
        internal static string Read(string path) {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(f, Encoding.UTF8, true)) return r.ReadToEnd();
        }
        internal static void Save(string path, string content) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    var b = Utf8.GetBytes(content); f.Write(b, 0, b.Length); f.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        internal static string Hash(string value) {
            using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Utf8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
        static readonly object sensitiveLock=new object();
        static volatile string[] sensitive=new string[0];
        internal static void AddSensitive(params string[] values) {
            lock(sensitiveLock) {
                sensitive=sensitive.Concat(values).Where(v=>!String.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal).OrderByDescending(v=>v.Length).ToArray();
            }
        }
        internal static string Scrub(string s) {
            if(String.IsNullOrEmpty(s)) return s;
            s=Regex.Replace(s,@"(?i)https?://[^\s<>""']+","[地址已隐藏]");
            foreach(var value in sensitive) s=s.Replace(value,"[已隐藏]");
            s=Regex.Replace(s,@"(?i)\b(access_token|token|(?:dingtalk_|github_)?secret|sign|password)([""']?\s*[=:]\s*[""']?)[^\s&\],}""']+","$1$2***");
            return Regex.Replace(s,@"(?i)\bSEC[a-f0-9]{32,}\b","***");
        }
        internal static string Clean(string s) {
            if (String.IsNullOrWhiteSpace(s)) return "";
            s = Regex.Replace(s, @"<br\s*/?>|</p>|</div>", " ", RegexOptions.IgnoreCase);
            s = WebUtility.HtmlDecode(Regex.Replace(s, @"</?(?:span|div|p|b|i|em|a|strong|font|img|code)(?:\s[^>]*?)?/?>", "", RegexOptions.IgnoreCase));
            s = Regex.Replace(s, @"\x1b\[[0-9;]*[A-Za-z]", "");
            return Regex.Replace(Scrub(s), @"\s+", " ").Trim();
        }
        internal static string Clip(string value, int length) {
            if (value.Length <= length) return value;
            if (Char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length) + "…";
        }
        internal static string First(params string[] names) { return names.FirstOrDefault(x => !String.IsNullOrWhiteSpace(x)) ?? ""; }
    }

    class Options {
        public string Folder { get; set; }
        public string ProtectedHook { get; set; }
        public string ProtectedSecret { get; set; }
        public string Keyword { get; set; }
        public int TimeoutMs { get; set; }
        public bool Enabled { get; set; }
        public static string Seal(string s) {
            return String.IsNullOrEmpty(s) ? "" : Convert.ToBase64String(ProtectedData.Protect(Data.Utf8.GetBytes(s), null, DataProtectionScope.CurrentUser));
        }
        public static string Open(string s) {
            return String.IsNullOrEmpty(s) ? "" : Data.Utf8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser));
        }
    }

    class Subtask {
        public string Name { get; set; }
        public string Entry { get; set; }
        public string Key { get; set; }
        public string Id { get; set; }
        public string Status { get; set; }
        public string Reason { get; set; }
        public string Hint { get; set; }
        public string Node { get; set; }
        public List<string> Details { get; set; }
        public Subtask() { Status = "等待"; Details = new List<string>(); }
    }
    class Run {
        public string Key { get; set; }
        public string Instance { get; set; }
        public string InstanceId { get; set; }
        public string TaskId { get; set; }
        public string DisplayName { get; set; }
        public string Host { get; set; }
        public string TimeZoneId { get; set; }
        public int? ExitCode { get; set; }
        public string Start { get; set; }
        public string End { get; set; }
        public string Signal { get; set; }
        public string PlanReason { get; set; }
        public int Count { get; set; }
        public List<Subtask> Tasks { get; set; }
        public Run() { Tasks = new List<Subtask>(); }
        internal void CaptureHost() {
            if(String.IsNullOrEmpty(Host)) Host=Environment.MachineName;
            if(String.IsNullOrEmpty(TimeZoneId)) TimeZoneId=TimeZoneInfo.Local.Id;
        }
        internal void ScrubForStorage() {
            Instance=Data.Scrub(Instance); DisplayName=Data.Scrub(DisplayName); Host=Data.Scrub(Host);
            PlanReason=Data.Scrub(PlanReason);
            foreach(var t in Tasks) {
                t.Name=Data.Scrub(t.Name); t.Entry=Data.Scrub(t.Entry); t.Node=Data.Scrub(t.Node);
                t.Hint=Data.Scrub(t.Hint); t.Reason=Data.Scrub(t.Reason);
                if(t.Details!=null) t.Details=t.Details.Select(Data.Scrub).ToList();
            }
        }
        TimeZoneInfo Zone() {
            if(String.IsNullOrEmpty(TimeZoneId)) return null;
            try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
            catch(TimeZoneNotFoundException) { return null; }
            catch(InvalidTimeZoneException) { return null; }
        }
        public long? DurationSeconds {
            get {
                DateTime start,end;
                if(!DateTime.TryParseExact(Start,"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out start) ||
                    !DateTime.TryParseExact(End,"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out end)) return null;
                var zone=Zone();
                if(zone!=null) {
                    if(zone.IsInvalidTime(start)||zone.IsInvalidTime(end)||zone.IsAmbiguousTime(start)||zone.IsAmbiguousTime(end)) return null;
                    start=TimeZoneInfo.ConvertTimeToUtc(start,zone); end=TimeZoneInfo.ConvertTimeToUtc(end,zone);
                }
                return end>=start?(long?)(end-start).TotalSeconds:null;
            }
        }
        internal string Verdict() {
            if (Signal == "中止") return "已中止";
            if (Signal == "失败" || Tasks.Any(t => t.Status == "失败" || t.Status == "已跳过" || t.Status == "未执行" || t.Status == "提前结束" || t.Status == "部分完成")) return "未全部完成";
            return Signal == "结束" && Count > 0 && Tasks.Count == Count && Tasks.All(t => t.Status == "完成") ? "全部完成" : "结果待确认";
        }
        internal IEnumerable<string> Lines(bool full) {
            yield return "MaaEnd · " + Data.First(DisplayName,Instance) + " · " + Verdict();
            if(!String.IsNullOrEmpty(Host)) yield return "主机　"+Host;
            yield return "时间　"+Start+" → "+End+"（"+Data.First(TimeZoneId,"日志当地时间，时区未记录")+"）";
            var duration=DurationSeconds;
            if(duration.HasValue) yield return "耗时　"+TimeSpan.FromSeconds(duration.Value).ToString("c",CultureInfo.InvariantCulture);
            if(ExitCode.HasValue) yield return "退出码　"+ExitCode.Value.ToString(CultureInfo.InvariantCulture);
            yield return "本轮选中 " + (Count < 0 ? "数量未知" : Count + " 项") + "，已完成 " + Tasks.Count(t => t.Status == "完成") + " 项";
            if (!String.IsNullOrEmpty(PlanReason)) yield return "计划说明　" + PlanReason;
            foreach (var t in Tasks.Where(t => full || t.Status != "完成")) {
                yield return "• " + t.Name + "〔" + t.Status + "〕" + t.Reason;
                foreach (var detail in t.Details) yield return "    " + detail;
            }
            if (Count > Tasks.Count) yield return "另有 " + (Count - Tasks.Count) + " 个勾选项，日志未提供名称或结果。";
            if (Verdict() == "全部完成") yield return "未完成项　无";
        }
    }
    static class DingTalk {
        internal static Uri Target(string hook, string secret, long timestamp) {
            Uri u;
            if (!Uri.TryCreate(hook, UriKind.Absolute, out u) || u.Scheme != "https" || !u.IsDefaultPort ||
                !String.IsNullOrEmpty(u.UserInfo) || !String.IsNullOrEmpty(u.Fragment) ||
                !u.Host.Equals("oapi.dingtalk.com", StringComparison.OrdinalIgnoreCase) || u.AbsolutePath != "/robot/send")
                throw new ArgumentException("请粘贴钉钉群自定义机器人的完整 HTTPS Webhook。");
            var tokens = u.Query.TrimStart('?').Split('&').Where(p => p.StartsWith("access_token=", StringComparison.Ordinal)).ToArray();
            if (tokens.Length != 1 || !Regex.IsMatch(tokens[0], @"^access_token=[A-Za-z0-9._~-]+$"))
                throw new ArgumentException("Webhook 缺少有效的 access_token。");
            var url = u.GetLeftPart(UriPartial.Path) + "?" + tokens[0];
            if (!String.IsNullOrEmpty(secret)) {
                var stamp = timestamp.ToString(CultureInfo.InvariantCulture);
                using (var h = new HMACSHA256(Data.Utf8.GetBytes(secret))) {
                    var sign = Convert.ToBase64String(h.ComputeHash(Data.Utf8.GetBytes(stamp + "\n" + secret)));
                    url += "&timestamp=" + stamp + "&sign=" + Uri.EscapeDataString(sign);
                }
            }
            return new Uri(url);
        }
        internal static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970,1,1)).TotalMilliseconds; }
        internal static void CheckReply(string json) { CheckResponse(200,json,null); }
        internal static void CheckResponse(int status,string json,string retryAfter) {
            long delay=RetryDelay(retryAfter);
            if(status<200 || status>=300) {
                bool permanent=status>=300 && status<500 && status!=408 && status!=425 && status!=429;
                throw new DeliveryException("钉钉 HTTP "+status.ToString(CultureInfo.InvariantCulture),permanent,delay);
            }
            if(String.IsNullOrWhiteSpace(json) || Data.Utf8.GetByteCount(json)>65536)
                throw new DeliveryException("钉钉响应为空或超出 64 KiB，未确认送达。",false,delay);
            object parsed;
            try { parsed=RobotJson.Object(json); }
            catch(ArgumentException) { throw new DeliveryException("钉钉响应不是有效 JSON。",false,delay); }
            catch(InvalidOperationException) { throw new DeliveryException("钉钉响应不是有效 JSON。",false,delay); }
            var code=Data.Get(parsed,"errcode");
            if(!(code is int || code is long)) throw new DeliveryException("钉钉响应缺少整数 errcode。",false,delay);
            long number=Convert.ToInt64(code,CultureInfo.InvariantCulture);
            if(number==0) return;
            // Never echo the endpoint's free-form response into the queue, even if it contains a secret.
            bool config=number==310000 || number==40014 || number==300001;
            string diagnostic=config?"，请检查机器人的地址、关键词、加签和 IP 白名单。":"，接口未接受消息。";
            throw new DeliveryException("钉钉错误码 "+number.ToString(CultureInfo.InvariantCulture)+diagnostic,config,delay);
        }
        static long RetryDelay(string value) {
            if(String.IsNullOrWhiteSpace(value)) return 0;
            long seconds;
            if(Int64.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out seconds))
                return Math.Min(seconds,253402300799L)*1000;
            DateTimeOffset when;
            if(!DateTimeOffset.TryParseExact(value,"r",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out when)) return 0;
            return Math.Max(0L,(long)Math.Ceiling((when-DateTimeOffset.UtcNow).TotalMilliseconds));
        }
        internal static void Send(string hook, string secret, string body) {
            RobotConfig.FromFields(hook,secret,"",10000).Send(body);
        }
        internal static void SendVia(RobotConfig config,string text,Func<Uri,byte[],int,RobotReply> post) {
            Uri target=Target(config.Hook,config.Secret,Now());
            var bytes=Data.Utf8.GetBytes(Data.Encode(new {msgtype="text",text=new {content=Data.Scrub(text)}}));
            var reply=post(target,bytes,config.TimeoutMs);
            CheckResponse(reply.Status,reply.Body,reply.RetryAfter);
        }
        internal static RobotReply Post(Uri target,byte[] bytes,int timeoutMs) {
            var req = (HttpWebRequest)WebRequest.Create(target);
            req.Method = "POST"; req.ContentType = "application/json; charset=utf-8"; req.AllowAutoRedirect = false;
            req.Timeout=timeoutMs; req.ReadWriteTimeout=timeoutMs; req.ContentLength=bytes.Length;
            var watch=System.Diagnostics.Stopwatch.StartNew();
            try {
                using (var stream = req.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse()) {
                    var reply=new RobotReply{Status=(int)resp.StatusCode,RetryAfter=resp.Headers["Retry-After"]};
                    if(reply.Status<200 || reply.Status>=300) return reply;
                    using(var stream=resp.GetResponseStream())
                    using(var buf=new MemoryStream()) {
                        byte[] block=new byte[4096];
                        while(true) {
                            int remaining=timeoutMs-(int)watch.ElapsedMilliseconds;
                            if(remaining<=0) throw new DeliveryException("读取钉钉响应超时。");
                            if(stream.CanTimeout) stream.ReadTimeout=remaining;
                            int n=stream.Read(block,0,block.Length);
                            if(n==0) break;
                            if(buf.Length+n>65536) throw new DeliveryException("钉钉响应超过 64 KiB。");
                            buf.Write(block,0,n);
                        }
                        try { reply.Body=new UTF8Encoding(false,true).GetString(buf.ToArray()); }
                        catch(DecoderFallbackException) { throw new DeliveryException("钉钉响应不是有效 UTF-8。"); }
                    }
                    return reply;
                }
            } catch (WebException ex) {
                var resp = ex.Response as HttpWebResponse;
                if(resp==null) throw new DeliveryException("连接钉钉失败（"+ex.Status+"）。");
                using(resp) return new RobotReply{Status=(int)resp.StatusCode,RetryAfter=resp.Headers["Retry-After"]};
            } catch(IOException) {
                throw new DeliveryException("读取或写入钉钉连接失败。");
            }
        }
        internal static List<string> Parts(Run run, string keyword) {
            string prefix = (String.IsNullOrWhiteSpace(keyword) || keyword == "MaaEnd" ? "" : Data.Scrub(keyword) + " ") + "MaaEnd 计划汇报 #" + run.Key;
            var parts = new List<string>(); var b = new StringBuilder(); int bytes=0;
            foreach (string raw in run.Lines(false).Select(Data.Scrub)) {
                for(int start=0;start<raw.Length;) {
                    int n=Math.Min(720,raw.Length-start);
                    if(start+n<raw.Length && Char.IsHighSurrogate(raw[start+n-1])) n--;
                    string line=raw.Substring(start,n); int size=Data.Utf8.GetByteCount(line)+2;
                    if(bytes>0 && bytes+size>3600) { parts.Add(b.ToString()); b.Clear(); bytes=0; }
                    b.AppendLine(line); bytes+=size; start+=n;
                }
            }
            if (b.Length != 0) parts.Add(b.ToString());
            for (int i = 0; i < parts.Count; i++) parts[i] = prefix + (parts.Count > 1 ? " (" + (i + 1) + "/" + parts.Count + ")" : "") + "\n" + parts[i].TrimEnd();
            return parts;
        }
    }
}
