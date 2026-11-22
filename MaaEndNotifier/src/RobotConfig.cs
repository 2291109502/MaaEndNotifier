using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MaaEndNotify {
    sealed class RobotConfig {
        internal static string BundledPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"assets","config.json"); } }
        internal string Mode { get { return "dingtalk"; } }
        internal string Hook { get; private set; }
        internal string Secret { get; private set; }
        internal string Keyword { get; private set; }
        internal int TimeoutMs { get; private set; }

        internal static RobotConfig FromFields(string hook,string secret,string keyword,int timeoutMs) {
            if(timeoutMs<1000 || timeoutMs>30000) throw new ArgumentException("机器人超时时间须为 1 到 30 秒。");
            var cfg=new RobotConfig{Hook=(hook??"").Trim(),Secret=secret??"",Keyword=keyword??"",TimeoutMs=timeoutMs};
            if(cfg.Keyword.Length>80 || cfg.Keyword.Any(Char.IsControl)) throw new ArgumentException("机器人关键词须在 80 字以内，且为单行。");
            Uri target=DingTalk.Target(cfg.Hook,"",0);
            Data.AddSensitive(cfg.Hook,cfg.Secret,Uri.UnescapeDataString(target.Query.Substring("?access_token=".Length)));
            return cfg;
        }
        internal static RobotConfig Load(string path) {
            IDictionary<string,object> map;
            try { map=RobotJson.Object(Data.Read(path)); }
            catch(IOException) { throw new InvalidOperationException("无法读取机器人配置文件。"); }
            catch(UnauthorizedAccessException) { throw new InvalidOperationException("当前账户无法读取机器人配置文件。"); }
            catch(ArgumentException) { throw new InvalidOperationException("机器人配置不是有效 JSON。"); }
            catch(InvalidOperationException) { throw new InvalidOperationException("机器人配置不是有效 JSON。"); }
            if(map==null) throw new InvalidOperationException("机器人配置必须是 JSON 对象。");
            var allowed=new[]{"mode","webhook_url","dingtalk_secret","github_secret","keyword","timeout_seconds"};
            if(map.Keys.Except(allowed,StringComparer.Ordinal).Any()) throw new InvalidOperationException("机器人配置含未识别字段，请检查字段名。");
            foreach(var name in allowed.Where(s=>s!="timeout_seconds")) {
                if(map.ContainsKey(name) && !(map[name] is string)) throw new InvalidOperationException(name+" 须为字符串。");
            }
            if(map.ContainsKey("mode") && Data.Str(map,"mode")!="dingtalk") throw new InvalidOperationException("此 MaaEnd 通知程序使用 dingtalk 文本模式，请核对 mode。");
            object v=map.ContainsKey("timeout_seconds")?map["timeout_seconds"]:10;
            if(!(v is int || v is long || v is decimal || v is double)) throw new InvalidOperationException("timeout_seconds 须为 1 到 30 的数字。");
            double seconds=Convert.ToDouble(v,CultureInfo.InvariantCulture);
            if(Double.IsNaN(seconds) || seconds<1 || seconds>30) throw new InvalidOperationException("timeout_seconds 须为 1 到 30 的数字。");
            Data.AddSensitive(Data.Str(map,"github_secret"));
            return FromFields(Data.Str(map,"webhook_url"),Data.Str(map,"dingtalk_secret"),Data.Str(map,"keyword"),(int)Math.Ceiling(seconds*1000));
        }
        internal void Send(string text) { DingTalk.SendVia(this,text,DingTalk.Post); }
    }

    sealed class RobotReply {
        internal int Status;
        internal string Body,RetryAfter;
    }
    sealed class DeliveryException : InvalidOperationException {
        internal readonly bool Permanent;
        internal readonly long RetryAfterMs;
        internal DeliveryException(string safeMessage,bool permanent=false,long retryAfterMs=0) : base(safeMessage) {
            Permanent=permanent; RetryAfterMs=retryAfterMs;
        }
    }

    // JavaScriptSerializer also accepts JavaScript object literals, octal-style numbers and
    // repeated keys. Acknowledgements and credential files must be unambiguous JSON objects.
    sealed class RobotJson {
        static readonly Regex Quoted=new Regex(@"\G""(?:[^""\\\u0000-\u001f]|\\(?:[""\\/bfnrt]|u[0-9a-fA-F]{4}))*""",RegexOptions.Compiled);
        static readonly Regex Scalar=new Regex(@"\G(?:true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)",RegexOptions.Compiled);
        readonly string text;
        int position;
        RobotJson(string text) { this.text=text; }
        static ArgumentException Invalid() { return new ArgumentException("JSON 无效或字段重复。"); }
        internal static IDictionary<string,object> Object(string text) {
            if(String.IsNullOrEmpty(text) || text.Length>65536) throw Invalid();
            var parser=new RobotJson(text);
            parser.Space();
            if(parser.position>=text.Length || text[parser.position]!='{') throw Invalid();
            parser.Value(0); parser.Space();
            if(parser.position!=text.Length) throw Invalid();
            return Data.Map(Data.Decode<object>(text));
        }
        void Space() {
            while(position<text.Length && " \t\r\n".IndexOf(text[position])>=0) position++;
        }
        bool Take(char literal) {
            Space();
            if(position>=text.Length || text[position]!=literal) return false;
            position++; return true;
        }
        Match Word(Regex token) {
            var m=token.Match(text,position); if(!m.Success) throw Invalid();
            position+=m.Length; return m;
        }
        void Value(int depth) {
            Space(); if(depth>64 || position==text.Length) throw Invalid();
            if(Take('{')) {
                if(Take('}')) return;
                var keys=new HashSet<string>(StringComparer.Ordinal);
                do {
                    Space(); string key=Data.Decode<string>(Word(Quoted).Value);
                    if(!keys.Add(key) || !Take(':')) throw Invalid();
                    Value(depth+1);
                } while(Take(','));
                if(!Take('}')) throw Invalid();
            } else if(Take('[')) {
                if(Take(']')) return;
                do { Value(depth+1); } while(Take(','));
                if(!Take(']')) throw Invalid();
            } else if(text[position]=='"') Word(Quoted);
            else Word(Scalar);
        }
    }
}
