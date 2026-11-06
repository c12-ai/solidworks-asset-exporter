# 接口候选库与只读属性模板

适用于 v1.1.0-rc3（文件版本 1.1.0.2）及兼容接口库格式 1.0 的插件。本次是接口库与模板补充包，不需要更换插件 DLL。

## 旧属性保留

已经填好的 `connection_interface`、`accepts_interfaces` 不要删除。插件编辑的是这两个原有文件级自定义属性；打开窗口会读取原值，导出继续读取它们。候选库更新不会改模型、迁移旧名称或自动增加资产版本。

## 命名方式

采用 `类别.规格[.关键尺寸][.厂商或型号]`，例如用户指定的 `试管.15ml.D20`。容量统一使用 `ml` 或 `ul`，尺寸默认 mm，`D` 表示事先约定的圆形定位/承载部位直径。可追加 `H120` 或 `底座L80xW60` 等字段，但这些只是你确认的尺寸定义，不是插件测量结果。

候选库包含 103 个基础名称，覆盖试管、离心管、冻存管、锥形瓶、培养瓶/皿、试剂瓶、储液槽、孔板、PCR、枪头/盒、移液管、架/底座、托盘/载台、快换及工具安装。`A型`/`B型` 是可自行定义的接口系列，不是厂商通用标准。

这些是录入候选，不是经过验证的兼容性数据库：相同容量或孔数的不同厂家可能具有不同外形、口径和定位结构。采用基础名称前应确认它在项目内代表同一种实际接口；有差异时增加尺寸或型号，分别命名。没有尺寸的候选没有暗含默认直径。`试管.15ml.D20` 是命名示例，不声明所有 15 ml 试管直径均为 20 mm。

父级 `accepts_interfaces` 中的值必须与子级 `connection_interface` 完全一致，包括大小写和单位写法。旧名称照常有效；不能只给一侧改名。多个接受接口在模型中使用英文分号分隔，插件窗口中通过勾选操作。容量仍由 `slots_num` 表达；接口名不建立槽位分配或空位确认。

## 使用

1. 关闭并重新打开“承载关系 → 接口属性 / 建议…”窗口；向上接口下拉框可输入中文类别前缀进行自动补全。
2. 选择已有候选，或直接输入符合项目实际规格的新名称。也可用“添加新接口选项”把自定义名称加入当前窗口，随后选择/勾选它。
3. 先保存子资产的连接接口，再选父资产生成接受接口建议并确认保存。保存成功的新名称会追加到本机接口库。

## 模板修改范围

零件 `part.prtprp`、装配 `asem.asmprp` 的两个接口控件使用 `ReadOnly="True"`，标题附“插件维护”。属性键、文件级 `ApplyTo="Global"`、原有默认值和其他控件保持原样。

只读作用于自定义属性任务窗格的模板控件，不是模型权限锁。SW 的“文件 → 属性”窗口和 API 仍能编辑原属性；Robot 等尚不支持在承载关系窗口中编辑的对象，可使用该原生入口。保留属性并显示只读值，避免模板和插件之间反复手工同步。

## 应用补充包

`Install-InterfaceOptions.ps1` 直接修改现有模板的两个控件，保留模板中的其他定制。默认仅预览，指定 `-Apply` 才实际更新。候选名称合并到当前 Windows 用户的 `%APPDATA%\SolidWorksAssetExporter\interfaces.json`，保留旧名称和原有大小写；不会修改 `SLDPRT`/`SLDASM` 或 SW 注册表设置。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-InterfaceOptions.ps1 -TemplateDirectory "C:\C12_SolidWorksTemplate\自定义属性"
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-InterfaceOptions.ps1 -TemplateDirectory "C:\C12_SolidWorksTemplate\自定义属性" -Apply
```

更新前备份所有将改写的文件，输出备份目录及 `manifest.json`；其中记录原路径、原哈希和是否原本存在。接口库使用与插件一致的互斥文件锁及临时文件替换，模板按文件替换；若中途失败，已成功更新的文件会保留改动，按备份清单恢复即可，不承诺跨文件原子更新。

刷新方式：关闭接口编辑窗口并重新打开；自定义属性任务窗格重新选择模板或按 F5 刷新。模板按文档类型显示的机制见 [SOLIDWORKS 官方说明](https://help.solidworks.com/2022/english/SolidWorks/oh_propertytabbuilder/t_tab_builder_creating_tab.htm)。
