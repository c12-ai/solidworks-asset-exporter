using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

class RepairLocalModels
{
    static readonly string Folder = @"C:\Users\C12\Desktop\（部分）layout_all";
    static Dictionary<string, Dictionary<string,string>> Changes = new Dictionary<string, Dictionary<string,string>>();
    static void Set(string file, string key, string value) {
        if (!Changes.ContainsKey(file)) Changes[file] = new Dictionary<string,string>();
        Changes[file][key] = value;
    }
    static int Main() {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        foreach (var name in new [] {"25ml离心管盒支架", "25ml移液管", "25ml移液管盒", "1.5ml离心管", "1.5ml离心管架", "50ml离心管", "50ml离心管架", "BSC内部组件安装底板", "桌子 枪头盒-1000uL", "锥形瓶底板", "125ml锥形瓶底座", "培养箱底板", "1000ml圆形瓶", "水浴锅"})
            Set(name + ".SLDPRT", "asset_version", "2");
        foreach (var name in new [] {"1000ml圆形瓶", "1.5ml离心管", "50ml离心管", "500ml圆形瓶", "移液枪头-1000uL"})
            Set(name + ".SLDPRT", "accepts_interfaces", "细胞样本");
        Set("BSC内部组件安装底板.SLDPRT", "accepts_interfaces", "锥形瓶底板;1.5ml离心管架;50ml离心管架;培养基瓶底座;枪头盒;电动助吸器支架;移液器支架");
        Set("BSC内部组件安装底板.SLDPRT", "connection_interface", "");
        Set("移液器支架总装.SLDASM", "class", "station");
        Set("末端工具套装-移液器.SLDASM", "quick_changer_side", "");
        Set("装配体10.SLDASM", "设计目的", "冰箱内存放样本瓶的置物组件");
        Set("BSC.SLDASM", "设计目的", "生物安全柜主体，为内部操作组件提供安装空间和防护");
        Set("水浴沥水组件.SLDASM", "设计目的", "用于水浴后样本瓶的放置和沥水");
        // Published metadata changes require a new asset version too.
        foreach (var pair in Changes) pair.Value["asset_version"] = "2";
        string backup = Path.Combine(Folder, "ModelPropertyBackup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backup);
        foreach (var pair in Changes) File.Copy(Path.Combine(Folder, pair.Key), Path.Combine(backup, pair.Key), false);
        Console.WriteLine("BACKUP=" + backup);
        SldWorks app = null;
        try {
            app = Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application")) as SldWorks;
            app.Visible = false;
            foreach (var pair in Changes) {
                string path = Path.Combine(Folder, pair.Key);
                int errors = 0, warnings = 0;
                var doc = app.OpenDoc6(path, path.EndsWith("SLDASM") ? 2 : 1, 1, "", ref errors, ref warnings) as ModelDoc2;
                if (doc == null || errors != 0 || doc.IsOpenedReadOnly()) throw new Exception("Cannot edit " + path + " errors=" + errors);
                var manager = doc.Extension.get_CustomPropertyManager("");
                foreach (var change in pair.Value) {
                    manager.Add3(change.Key, 30, change.Value, 2);
                    string raw, resolved; bool wasResolved, linked;
                    manager.Get6(change.Key, false, out raw, out resolved, out wasResolved, out linked);
                    if (raw != change.Value) throw new Exception("Property readback failed: " + path + " / " + change.Key);
                    Console.WriteLine(pair.Key + " | " + change.Key + "=" + raw);
                }
                if (!doc.Save3(1, ref errors, ref warnings) || errors != 0) throw new Exception("Save failed: " + path + " errors=" + errors);
                app.CloseDoc(doc.GetTitle());
            }
            Console.WriteLine("SAVED_FILES=" + Changes.Count);
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (app != null) app.ExitApp(); }
    }
}
