using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MaaEndNotify {
    // Names are taken from the installed release, including tasks added after this program was built.
    class Catalog {
        readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, string> entries = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, string> translations = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly string root;
        public Catalog(string folder) {
            root = folder;
            // Readable fallback for a standalone exported debug folder. Installed resources take precedence.
            var labels = new Dictionary<string,string> {
                {"MXU_LAUNCH","启动外部程序"},{"MXU_KILLPROC","结束进程"},{"AutoCollectSchedule","自动采集"},
                {"ProtocolSpaceSchedule","协议空间"},{"ResourceRecycleStationStart","资源回收站"},{"AutoEcoFarmTask","生态农场"},
                {"VisitFriendsMain","拜访好友"},{"DijiangRewards","基建任务"},{"CreditShoppingMain","信用点购物"},
                {"DeliveryJobsMain","转交委托"},{"OutpostTradingSchedule","据点交易"},{"GearAssemblyStart","装备装配"},
                {"DailyRewardStart","日常奖励领取"},{"AutoStockpileMain","自动囤货"},{"AutoStockStapleSchedule","购买稳定物资"},
                {"EnvironmentMonitoringMain","环境监测"},{"TrialOfSwordmancyMain","选剑演武"},{"GiftOperatorMain","赠送礼物"},
                {"AutoCollectWulingCosmagaric2Start","武陵·星门菌 2 号路线"}
            };
            foreach (var p in labels) names[p.Key] = p.Value;
            translations["task.ProtocolSpace.focus.sanity_low_end"] = "触发理智不足结束条件";
            translations["task.AutoCollect.route.WulingCosmagaric2.failed"] = "武陵·星门菌 2 号路线采集失败";
            foreach (var baseDir in new [] { root, Path.Combine(root,"assets") }) {
                foreach (var file in new [] { "locales/interface/zh_cn.json", "locales/interface/zh-CN.json", "locales/pipeline/zh_cn.json", "locales/zh_cn.json" }) {
                    var map = Data.Map(Load(Path.Combine(baseDir,file)));
                    if (map != null) Flatten(map, "");
                }
                var it = Load(Path.Combine(baseDir, "interface.json"));
                var languages = Data.Map(Data.Get(it,"languages"));
                if (languages != null) foreach (var pair in languages.Where(p => p.Key.Replace("-","_").ToLowerInvariant() == "zh_cn")) {
                    var path = pair.Value as string;
                    if (path != null) {
                        var map = Data.Map(Load(Path.Combine(baseDir,path)));
                        if (map != null) Flatten(map,"");
                    }
                }
                Register(it);
                var tasks = Path.Combine(baseDir, "tasks");
                if (Directory.Exists(tasks)) foreach (var file in Directory.GetFiles(tasks,"*.json",SearchOption.AllDirectories)) Register(Load(file));
            }
        }
        static object Load(string path) {
            if (!File.Exists(path)) return null;
            try { return Data.Decode<object>(Data.Read(path)); }
            catch (IOException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        void Flatten(IDictionary<string,object> map, string prefix) {
            foreach (var p in map) {
                var key = prefix + p.Key;
                if (p.Value is string) translations[key] = (string)p.Value;
                else if (Data.Map(p.Value) != null) Flatten(Data.Map(p.Value), key + ".");
            }
        }
        public string Localize(string value) {
            if (String.IsNullOrEmpty(value)) return "";
            string tr;
            return translations.TryGetValue(value.TrimStart('$'), out tr) ? tr : value;
        }
        void Register(object data) {
            foreach (var t in Data.Items(Data.Get(data, "task", "tasks")).Concat(Data.Items(Data.Get(data,"pretask")))) {
                var name = Data.Str(t,"name"); var entry = Data.Str(t,"entry");
                if (entry == "" || name == "") continue;
                string label = Data.Str(t,"label");
                if (label == "" && translations.ContainsKey("task."+name+".label")) label = "$task."+name+".label";
                label = Localize(label); if (label.StartsWith("$")) label = "";
                names[entry] = names[name] = Data.First(label, name);
                entries[name] = entry; entries[entry] = entry;
            }
        }
        public string Entry(string key) { string v; return entries.TryGetValue(key, out v) ? v : key; }
        public string Name(string key) {
            string v;
            if (names.TryGetValue(key,out v) || translations.TryGetValue("task."+key+".label",out v)) return Data.Clean(v);
            return Data.Clean(Localize(key));
        }
        public static string NormalRoot(string input) {
            string path = Path.GetFullPath(input.Trim().Trim('"'));
            if (File.Exists(path)) path = Path.GetDirectoryName(path);
            if (String.Equals(Path.GetFileName(path.TrimEnd('\\','/')), "debug", StringComparison.OrdinalIgnoreCase)) path = Directory.GetParent(path).FullName;
            if (!Directory.Exists(path) || (!Directory.Exists(Path.Combine(path,"debug")) && !File.Exists(Path.Combine(path,"interface.json"))))
                throw new ArgumentException("请选择 MaaEnd 安装目录，或在 MaaEnd 设置中打开的 debug 目录。");
            return path;
        }
        public List<Subtask> Selected(string instance, int expected, DateTime began, out string instanceId) {
            instanceId = ""; var file=Path.Combine(root, "config", "mxu-MaaEnd.json");
            // Later edits to the selection cannot establish which items this older run selected.
            if(!File.Exists(file) || File.GetLastWriteTime(file)>began.AddSeconds(2)) return new List<Subtask>();
            var config = Load(file);
            var collection = Data.Get(config, "instances"); var all = Data.Items(collection).ToList();
            if (Data.Map(collection) != null) all.AddRange(Data.Map(collection).Values);
            var match = all.Where(x => Data.Str(x,"name") == instance || Data.Str(x,"id") == instance).ToList();
            if (match.Count != 1) return new List<Subtask>();
            instanceId = Data.Str(match[0], "id");
            var found = new List<Subtask>();
            foreach (var task in Data.Items(Data.Get(match[0], "tasks","taskList","task"))) {
                // Missing flags are not proof that an item was selected.
                if (!Object.Equals(Data.Get(task,"enabled","checked"), true)) continue;
                string key = Data.Str(task,"taskName","name","entry");
                if (key == "") continue;
                found.Add(new Subtask { Name=Data.Clean(Data.First(Data.Str(task,"customName","alias"),Name(key))),
                    Entry=Entry(Data.First(Data.Str(task,"entry"),key)), Key=Data.Str(task,"id","taskId") });
            }
            // "Run from here", groups and pre-tasks can produce a different queue.
            return expected >= 0 && found.Count != expected ? new List<Subtask>() : found;
        }
        public string Focus(object detail, string message) {
            object focus = Data.Get(detail,"focus");
            object template = Data.Get(focus,message);
            string content = template as string ?? Data.Str(template,"content");
            if (String.IsNullOrEmpty(content)) return "";
            content = Localize(content);
            return Data.Clean(Regex.Replace(content, @"\{([a-zA-Z0-9_.]+)\}", delegate(Match m) {
                object field = detail;
                foreach (var part in m.Groups[1].Value.Split('.')) field = Data.Get(field,part);
                return field == null || Data.Map(field) != null ? m.Value : Convert.ToString(field);
            }));
        }
    }
}
